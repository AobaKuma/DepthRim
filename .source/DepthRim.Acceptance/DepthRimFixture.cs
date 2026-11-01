using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>驗收測試共用的準備、生成與清理（比照 DMSE.BVRAcceptance 的 BVRFixture）。</summary>
    public static class DepthRimFixture
    {
        public const string TestSaveName = "DepthRim_Acceptance";

        // ---- defs ----
        public static ThingDef IceShaft => ThingDef.Named("DepthRim_IceShaft");
        public static ThingDef IceShaftBottom => ThingDef.Named("DepthRim_IceShaftBottom");
        public static ThingDef BilgePump => ThingDef.Named("DepthRim_BilgePump");
        public static ThingDef DivingSuit => ThingDef.Named("DepthRim_DivingSuit");

        // ---- world ----
        public static Map Home => Find.Maps.FirstOrDefault(m => m.IsPlayerHome && m.Tile.LayerDef == PlanetLayerDefOf.Surface) ?? Find.CurrentMap;
        public static PlanetLayer OceanLayer => Find.WorldGrid.FirstLayerOfDef(DepthRimDefOf.DepthRim_Ocean);

        /// <summary>地表上清空、鋪好的測試區（放升降井）。</summary>
        public static CellRect Pad { get; private set; }

        /// <summary>E1 經由升降井生成的冰洋地圖；後面的測試都在這裡進行。</summary>
        public static Map Ocean { get; private set; }

        public static Building_IceShaft Shaft { get; private set; }

        private static readonly List<Thing> spawned = new List<Thing>();

        private static readonly List<IntVec3> roofed = new List<IntVec3>();

        // ==================================================================
        //  One-time game preparation
        // ==================================================================

        public static void PrepareGameOnce()
        {
            // 測試期間沒有隨機事件
            Find.Storyteller.storytellerComps.Clear();
            DebugSettings.unlimitedPower = true;

            Map map = Home;
            Pad = new CellRect(20, 20, 12, 12).ClipInsideMap(map);
            foreach (IntVec3 c in Pad.ExpandedBy(1).ClipInsideMap(map))
            {
                ClearCell(map, c, TerrainDefOf.Concrete);
            }
            // 測試區若在山裡：岩體挖掉後，上方的山頂失去支撐會坍塌，坍下來的岩石會把底下的東西（升降井）整個抹掉——
            // E1 偶發「shaft spawned=False」就是這個。先拿掉整塊測試區（含外圈支撐範圍）的屋頂。
            foreach (IntVec3 c in Pad.ExpandedBy(8).ClipInsideMap(map))
            {
                if (c.Roofed(map) && !c.Filled(map)) { map.roofGrid.SetRoof(c, null); }
            }
            foreach (IntVec3 c in Pad.ExpandedBy(1).ClipInsideMap(map))
            {
                map.roofGrid.SetRoof(c, null);
            }
        }

        public static void OnGameReloaded()
        {
            Find.Storyteller.storytellerComps.Clear();
            DebugSettings.unlimitedPower = true;
            Ocean = Find.Maps.FirstOrDefault(m => m.Tile.LayerDef == DepthRimDefOf.DepthRim_Ocean);
        }

        // ==================================================================
        //  Ice shaft / ocean map
        // ==================================================================

        /// <summary>在地表測試區放一座升降井並生成井下的冰洋地圖（重複呼叫時沿用）。</summary>
        public static Map EnsureOcean()
        {
            if (Ocean != null && Ocean.Parent != null && Ocean.Parent.HasMap && Shaft != null && Shaft.Spawned)
            {
                return Ocean;
            }
            Map home = Home;
            // 測試世界的開局地點是隨機的，正下方可能是深海生態區（有水壓、會挖深淵）。既有的測試都假設井底是淺海，
            // 所以把正下方那一格固定成極地冰蓋（只改測試世界的格子資料）。
            PlanetTile below = OceanLayer.GetClosestTile_NewTemp(home.Tile);
            if (below.Valid)
            {
                below.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("DepthRim_PolarIceCap");
            }
            IntVec3 cell = Pad.CenterCell;
            ClearCell(home, cell, TerrainDefOf.Concrete);
            Shaft = (Building_IceShaft)GenSpawn.Spawn(ThingMaker.MakeThing(IceShaft), cell, home, WipeMode.Vanish);
            Shaft.SetFaction(Faction.OfPlayer);
            Ocean = Shaft.GetOtherMap();
            return Ocean;
        }

        public static MapComponent_Flooding Flooding => Ocean?.GetComponent<MapComponent_Flooding>();

        /// <summary>
        /// 在冰洋地圖上清出一塊區域：移除岩體、鋪成碎石，並在 rect 範圍蓋上人造屋頂（冰洋地圖的「密閉」需要屋頂）。
        /// 回傳的區域避開地圖邊緣與井底出口。
        /// </summary>
        public static CellRect ClearOceanArea(int size)
        {
            Map map = Ocean;
            IntVec3 exit = Shaft.exit.Position;
            // 出口右上方，留 6 格間距
            int x = Math.Min(exit.x + 6, map.Size.x - size - 12);
            int z = Math.Min(exit.z + 6, map.Size.z - size - 12);
            CellRect rect = new CellRect(x, z, size, size);
            foreach (IntVec3 c in rect.ExpandedBy(2).ClipInsideMap(map))
            {
                ClearCell(map, c, TerrainDefOf.Gravel);
            }
            // 只在牆圈範圍內蓋屋頂：外圍沒有支撐，蓋了會坍塌砸傷測試用的殖民者
            foreach (IntVec3 c in rect)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
                roofed.Add(c);
            }
            return rect;
        }

        /// <summary>沿 rect 外框蓋一圈鋼牆，圍出 rect.ContractedBy(1) 的密閉艙室。</summary>
        public static List<Thing> BuildWallRing(CellRect rect)
        {
            List<Thing> walls = new List<Thing>();
            foreach (IntVec3 c in rect.EdgeCells)
            {
                walls.Add(Spawn(ThingDefOf.Wall, c, Ocean, ThingDefOf.Steel));
            }
            return walls;
        }

        public static Thing Spawn(ThingDef def, IntVec3 cell, Map map, ThingDef stuff = null, Faction faction = null)
        {
            if (stuff == null && def.MadeFromStuff) { stuff = GenStuff.DefaultStuffFor(def); }
            Thing thing = ThingMaker.MakeThing(def, stuff);
            if (thing.def.CanHaveFaction) { thing.SetFaction(faction ?? Faction.OfPlayer); }
            thing = GenSpawn.Spawn(thing, cell, map, WipeMode.Vanish);
            spawned.Add(thing);
            return thing;
        }

        /// <summary>產生一名殖民者並放到指定格子，徵召以免亂跑。</summary>
        public static Pawn SpawnColonist(Map map, IntVec3 cell)
        {
            Pawn pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: false, colonistRelationChanceFactor: 0f));
            GenSpawn.Spawn(pawn, cell, map, WipeMode.Vanish);
            if (pawn.drafter != null) { pawn.drafter.Drafted = true; }
            spawned.Add(pawn);
            return pawn;
        }

        /// <summary>冰洋地圖上一個碰到地圖邊緣的開放水域可站立格。</summary>
        public static IntVec3 OpenWaterCell()
        {
            Map map = Ocean;
            MapComponent_Flooding flooding = Flooding;
            foreach (IntVec3 c in map.AllCells.OrderBy(c => c.DistanceToSquared(Shaft.exit.Position)))
            {
                if (!c.Standable(map)) { continue; }
                Room room = c.GetRoom(map);
                if (room != null && !room.IsDoorway && flooding.IsOpenWater(room)) { return c; }
            }
            return IntVec3.Invalid;
        }

        public static void ClearCell(Map map, IntVec3 c, TerrainDef terrain)
        {
            if (!c.InBounds(map)) { return; }
            foreach (Thing t in map.thingGrid.ThingsListAt(c).ToList())
            {
                if (t is Pawn || t is MapPortal) { continue; }
                // 不可摧毀的東西（蒸氣噴泉等）留在原地：原版會噴
                // 「Tried to destroy non-destroyable thing」ERROR，把 Player.log 弄髒。
                if (!t.def.destroyable) { continue; }
                if (!t.Destroyed) { t.Destroy(DestroyMode.Vanish); }
            }
            map.terrainGrid.SetTerrain(c, terrain);
            map.fogGrid.Unfog(c);
            map.snowGrid?.SetDepth(c, 0f);
        }

        // ==================================================================
        //  Cleanup
        // ==================================================================

        public static void CleanupAfterTest()
        {
            foreach (Thing t in spawned)
            {
                if (!t.Destroyed) { t.Destroy(DestroyMode.Vanish); }
            }
            spawned.Clear();
            // 牆已移除：先拿掉測試蓋的屋頂，避免下一個測試期間坍塌
            if (Ocean != null && Ocean.Parent != null && Ocean.Parent.HasMap)
            {
                foreach (IntVec3 c in roofed) { Ocean.roofGrid.SetRoof(c, null); }
            }
            roofed.Clear();
            // 殘留檢查（二期 P2-1）：除錯層級覆寫與頭燈追蹤清單都是**跨測試**的狀態，
            // 上一個測試忘了清，下一個測試就會量到別人的光或材質。這裡主動清掉而不是只偵測，
            // 並且在有殘留時記一行——記不出來的話，殘留會變成「偶爾怪怪的」而永遠查不到。
            ResetFloodingState();
            CheckNoLeftoverRoofs();
            if (Find.CurrentMap != Home) { Current.Game.CurrentMap = Home; }
        }

        /// <summary>
        /// 測試留下的屋頂會失去支撐而坍塌（計畫 §1.4 第 3 點的「屋頂坍塌」噪音）。
        /// 這裡在清理後**數一次**還剩幾格人造屋頂：0 才算乾淨。
        /// 用計數而不是看信件，是因為信件內容會變、而且信件可能被玩家關掉；
        /// 屋頂格數是客觀狀態，能直接指出「還有東西沒收」。
        /// </summary>
        public static void CheckNoLeftoverRoofs()
        {
            if (Ocean == null || Ocean.Parent == null || !Ocean.Parent.HasMap)
            {
                return;
            }
            int leftover = 0;
            foreach (IntVec3 c in Ocean.AllCells)
            {
                RoofDef roof = Ocean.roofGrid.RoofAt(c);
                if (roof != null && roof != RoofDefOf.RoofRockThick && roof != RoofDefOf.RoofRockThin)
                {
                    leftover++;
                }
            }
            if (leftover > 0)
            {
                Log.Warning("[DepthRim.Acceptance] " + leftover
                    + " constructed roof cell(s) survived the test cleanup on map " + Ocean.uniqueID
                    + "; they will collapse and spam letters.");
            }
        }

        /// <summary>把每張水下地圖的除錯覆寫與頭燈追蹤清掉（二期 P2-1）。</summary>
        public static void ResetFloodingState()
        {
            if (Find.Maps == null)
            {
                return;
            }
            for (int i = 0; i < Find.Maps.Count; i++)
            {
                MapComponent_Flooding flooding = Find.Maps[i].Flooding();
                if (flooding == null)
                {
                    continue;
                }
                // **先**讓生產端的修剪邏輯跑一次再量測：清單是在下一輪 15 ticks 才自我修復的，
                // 而我們在測試結束的同一幀檢查，會把「裝備已銷毀、但還沒被修剪」的條目誤報成殘留
                // （實測 P2／P3／V4 每次都各報 1／1／10 筆，全是假陽性）。
                // 這樣做也順便驗證真正的修剪路徑，而不是在這裡另外寫一份判斷。
                SuitLampUpdater.Update(Find.Maps[i]);
                if (flooding.debugTierOverride != null)
                {
                    Log.Warning("[DepthRim.Acceptance] leftover debugTierOverride on map "
                        + Find.Maps[i].uniqueID + "; clearing it after the test.");
                    flooding.debugTierOverride = null;
                    flooding.MapComponentUpdate();
                }
                if (flooding.TrackedSuitLampCount > 0)
                {
                    Log.Warning("[DepthRim.Acceptance] leftover suit lamps on map "
                        + Find.Maps[i].uniqueID + " (" + flooding.TrackedSuitLampCount + "); releasing them.");
                    flooding.ReleaseSuitLamps();
                }
            }
        }

        public static void DeleteTestSave()
        {
            try
            {
                string path = GenFilePaths.FilePathForSavedGame(TestSaveName);
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch (Exception) { }
        }
    }
}
