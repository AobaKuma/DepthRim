using System;
using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 靠岸 M3（Docs/海岸船舶與潛水鐘_機制設計.md §5）：停在海岸殖民地旁邊那一格海上的船，由玩家在殖民地地圖上選泊位（位置＋朝向），
    /// 船體轉到選的朝向、搬進殖民地的海灣。沒有演出（M4）。
    /// <list type="bullet">
    /// <item>能靠岸：船停在海面上（沒在航行）、在龍骨網路上、艦橋有電、兩艘船的龍骨沒連在一起、有能運作的水面推進器、
    /// 船上的舷梯已收起、旁邊一格是玩家的原版地表殖民地。</item>
    /// <item>搬法：船不在自己的船地圖上（例如和平台共用一個海面地點）時，先搬到同一格的一張新船地圖；在那裡原地旋轉
    /// （<see cref="HullRotator"/>：開放海面上只有這具船體，不會撞到東西），再用 <see cref="ShipSeparationUtility.RelocateHull"/> 平移進殖民地。
    /// 不在船體上的人（海裡游的）一起放到甲板上。船地圖與船物件移除。</item>
    /// <item>泊位存在殖民地地圖上（<see cref="MapComponent_Berths"/>，以船名為鍵）：位置、轉了幾次、從哪一格海來（出港時優先從那一格出去）。</item>
    /// <item>靠好後，船上的舷梯接得到岸的自動伸出。</item>
    /// </list>
    /// </summary>
    public static class Docking
    {
        /// <summary>這一格旁邊的玩家原版地表殖民地或停泊點（沒有時 null）。</summary>
        public static Map AdjacentColony(PlanetTile seaTile)
        {
            if (!seaTile.Valid)
            {
                return null;
            }
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(seaTile, neighbors);
            return Find.Maps.FirstOrDefault(m => IsDockableMap(m) && neighbors.Contains(m.Tile));
        }

        /// <summary>能靠岸的地圖：玩家的原版地表殖民地（包括靠岸時建立的停泊點，見 <see cref="BeginAtCoast"/>）。</summary>
        public static bool IsDockableMap(Map m)
        {
            return m != null && SeaLevels.IsVanillaSurface(m) && m.IsPlayerHome;
        }

        /// <summary>
        /// 2026-11-22：能靠的海岸格（不限殖民地）：地表的陸地格（不是海、不是無法通行的山）、旁邊至少一格船能浮的海，
        /// 而且上面沒有別的世界物件，或者是玩家的殖民地。
        /// </summary>
        public static bool IsCoastTarget(PlanetTile tile)
        {
            if (!tile.Valid || tile.LayerDef != PlanetLayerDefOf.Surface || SurfaceRouting.Navigable(tile))
            {
                return false;
            }
            Tile t = tile.Tile;
            if (t == null || t.WaterCovered || t.hilliness == Hilliness.Impassable)
            {
                return false;
            }
            MapParent parent = Find.WorldObjects.MapParentAt(tile);
            if (parent != null)
            {
                if (!parent.HasMap || !IsDockableMap(parent.Map))
                {
                    return false;
                }
            }
            else if (Find.WorldObjects.AnyWorldObjectAt(tile))
            {
                return false;
            }
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(tile, neighbors);
            return neighbors.Any(SurfaceRouting.Navigable);
        }

        /// <summary>海岸格上已有的可靠岸地圖（還沒有時 null）。</summary>
        public static Map CoastMap(PlanetTile coast)
        {
            MapParent parent = Find.WorldObjects.MapParentAt(coast);
            return parent != null && parent.HasMap && IsDockableMap(parent.Map) ? parent.Map : null;
        }

        /// <summary>
        /// 靠到 <paramref name="coast"/> 這一格海岸：已有殖民地就直接選泊位；沒有就照原版奧德賽重力船降落在空地的做法
        /// （<c>GravshipUtility.ArriveNewMap</c>）：在那一格建立玩家的新殖民地（停泊點，名字「X的停泊點」）、生成地圖，再選泊位。
        /// 殖民地不會因為還沒有人而被移除；取消選泊位時，沒有人也沒有建築的停泊點直接撤掉（<see cref="RemoveIfUnused"/>）。
        /// 船要停在那一格旁邊的海上。
        /// </summary>
        public static void BeginAtCoast(Thing helm, PlanetTile coast)
        {
            Map existing = CoastMap(coast);
            if (existing != null)
            {
                Begin(helm, existing);
                return;
            }
            string block = ShipBlockReason(helm, out _);
            if (block == null && (helm.Map == null || !Find.WorldGrid.IsNeighbor(helm.Map.Tile, coast) || !IsCoastTarget(coast)))
            {
                block = "DepthRim.Dock.NoColony".Translate();
            }
            if (block != null)
            {
                Messages.Message(block, helm, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            LongEventHandler.QueueLongEvent(delegate
            {
                Settlement site = SettleUtility.AddNewHome(coast, Faction.OfPlayer);
                site.Name = "DepthRim.Dock.LandingSite".Translate(SurfaceVoyage.ShipNameFor(helm)).CapitalizeFirst();
                site.namedByPlayer = true;
                GameComponent_SurfaceSail.Instance?.MarkLandingSite(site);
                Map map = GetOrGenerateMapUtility.GetOrGenerateMap(coast, Find.World.info.initialMapSize, site.def);
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    if (helm.Spawned && Find.Maps.Contains(map))
                    {
                        Begin(helm, map);
                    }
                });
            }, "GeneratingMap", doAsynchronously: false, null);
        }

        /// <summary>這張地圖是靠岸時建立的停泊點、而且上面沒有玩家的人、建築或船（可以撤掉）。</summary>
        public static bool UnusedLandingSite(Map map)
        {
            return map != null && map.Parent != null && GameComponent_SurfaceSail.Instance?.IsLandingSite(map.Parent) == true
                   && map.mapPawns.FreeColonistsSpawnedCount == 0 && !map.mapPawns.AnyPawnBlockingMapRemoval
                   && !map.listerBuildings.allBuildingsColonist.Any();
        }

        /// <summary>取消靠岸：剛建立、還沒用到的停泊點直接撤掉（不留被遺棄的據點）。</summary>
        public static void RemoveIfUnused(Map map)
        {
            if (!UnusedLandingSite(map) || Find.CurrentMap == map)
            {
                return;
            }
            MapParent parent = map.Parent;
            GameComponent_SurfaceSail.Instance?.UnmarkLandingSite(parent);
            Current.Game.DeinitAndRemoveMap(map, false);
            if (!parent.Destroyed)
            {
                parent.Destroy();
            }
        }

        /// <summary>
        /// 船從停泊點下水離開後：停泊點上沒有人也沒有建築就照原版奧德賽重力船離開的做法放棄這張地圖
        /// （<c>MapParent.Abandon(wasGravshipLaunch: true)</c>）。
        /// </summary>
        public static void AbandonIfUnused(Map map)
        {
            if (!UnusedLandingSite(map) || Find.CurrentMap == map)
            {
                return;
            }
            GameComponent_SurfaceSail.Instance?.UnmarkLandingSite(map.Parent);
            map.Parent.Abandon(wasGravshipLaunch: true);
        }

        /// <summary>
        /// 從這座艦橋靠岸不能做的原因（可以時 null）。<paramref name="target"/> 給了的話停進那張地圖（同一格的海面地點，例如有平台的那張；
        /// 2026-11-21 航行抵達有地圖的海面地點時也要選泊位），否則停進旁邊的玩家海岸殖民地。
        /// </summary>
        public static string BlockReason(Thing helm, out SurfaceVoyage.Plan plan, out Map colony, Map target = null)
        {
            plan = null;
            colony = null;
            Map map = helm?.Map;
            if (map == null || !SeaLevels.IsSeaLevel(map))
            {
                return "DepthRim.Sail.NotAtSea".Translate();
            }
            if (SurfaceVoyage.ShipOf(helm) is WorldObject_SurfaceShip ship && ship.underWay)
            {
                return "DepthRim.Sail.StopFirst".Translate();
            }
            colony = target ?? AdjacentColony(map.Tile);
            if (colony == null || colony == map || !Find.Maps.Contains(colony))
            {
                return "DepthRim.Dock.NoColony".Translate();
            }
            return ShipBlockReason(helm, out plan);
        }

        /// <summary>船本身能不能靠岸（不看靠哪裡；可以時 null）。</summary>
        public static string ShipBlockReason(Thing helm, out SurfaceVoyage.Plan plan)
        {
            plan = null;
            Map map = helm?.Map;
            if (map == null || !SeaLevels.IsSeaLevel(map))
            {
                return "DepthRim.Sail.NotAtSea".Translate();
            }
            if (SurfaceVoyage.ShipOf(helm) is WorldObject_SurfaceShip ship && ship.underWay)
            {
                return "DepthRim.Sail.StopFirst".Translate();
            }
            plan = SurfaceVoyage.PlanFor(helm);
            if (plan == null || plan.footprint.Count == 0)
            {
                return "DepthRim.Surface.NotOnHull".Translate();
            }
            CompPowerTrader power = helm.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                return "DepthRim.Launch.NoPower".Translate();
            }
            HashSet<IntVec3> footprint = plan.footprint;
            // 同一個龍骨網路上可以有多座艦橋（母船載子船），不再擋
            // 潛水鐘沒收回不能走（§7.7）
            string bellLock = DiveBellLock.Reason(map, plan.footprint);
            if (bellLock != null)
            {
                return bellLock;
            }
            if (plan.thrust <= 0f)
            {
                return "DepthRim.Sail.NoThruster".Translate();
            }
            if (SurfaceVoyage.GangwaysOut(map, footprint).Count > 0)
            {
                return "DepthRim.Launch.GangwayOut".Translate();
            }
            return null;
        }

        public static FloatMenuOption DockOption(Thing helm)
        {
            string block = BlockReason(helm, out _, out Map colony);
            string label = colony != null ? "DepthRim.Dock.Option".Translate(colony.Parent.LabelCap).ToString() : "DepthRim.Dock.OptionNoColony".Translate().ToString();
            FloatMenuOption option = new FloatMenuOption(label, () => Begin(helm), SubmarineIcons.Anchoring, Color.white);
            if (block != null)
            {
                option.Label = label + " (" + block + ")";
                option.Disabled = true;
            }
            return option;
        }

        /// <summary>開始選泊位：切到殖民地地圖、選上 <see cref="Designator_DockShip"/>。</summary>
        public static void Begin(Thing helm, Map target = null)
        {
            string block = BlockReason(helm, out SurfaceVoyage.Plan plan, out Map colony, target);
            if (block != null)
            {
                Messages.Message(block, helm, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            GameComponent_SubmarinePlacement.Request(new Designator_DockShip(helm, colony, plan.footprint.ToList()), colony);
        }

        /// <summary>
        /// 靠岸：船體轉 <paramref name="turns"/> 次、外框中心放到 <paramref name="berthCenter"/>。在長時間事件裡做，回傳是否開始。
        /// </summary>
        public static bool Dock(Thing helm, int turns, IntVec3 berthCenter, Map target = null)
        {
            string block = BlockReason(helm, out SurfaceVoyage.Plan plan, out Map colony, target);
            if (block != null)
            {
                Messages.Message(block, helm, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            Map origin = helm.Map;
            PlanetTile seaTile = origin.Tile;
            HashSet<IntVec3> footprint = new HashSet<IntVec3>(plan.footprint);
            string shipName = SurfaceVoyage.ShipNameFor(helm);
            bool viewing = Find.CurrentMap == origin || Find.CurrentMap == colony;
            MapComponent_Berths.Berth previous = colony.GetComponent<MapComponent_Berths>()?.For(shipName);
            LongEventHandler.QueueLongEvent(delegate
            {
                // 1. 船自己的地圖（開放海面上只有這具船體）
                Map shipMap = origin;
                HashSet<IntVec3> cells = footprint;
                bool own = origin.Parent is WorldObject_SurfaceShip && origin.listerBuildings.allBuildingsColonist.All(b => footprint.Contains(b.Position));
                if (!own)
                {
                    WorldObject_SurfaceShip temp = (WorldObject_SurfaceShip)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_SurfaceShip);
                    temp.Tile = seaTile;
                    temp.SetFaction(Faction.OfPlayer);
                    temp.shipName = shipName;
                    Find.WorldObjects.Add(temp);
                    shipMap = MapGenerator.GenerateMap(origin.Size, temp, temp.def.mapGenerator);
                    ShipSeparationUtility.RelocateHull(origin, shipMap, cells, IntVec3.Zero);
                }
                // 2. 原地旋轉
                IntVec3 pivot = DockingMath.Pivot(cells);
                HashSet<IntVec3> rotated = HullRotator.Rotate(shipMap, cells, pivot, turns);
                if (rotated == null)
                {
                    Messages.Message("DepthRim.Dock.CannotRotate".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }
                IntVec3 offset = berthCenter - CellRect.FromCellList(rotated).CenterCell;
                Action finish = () => LongEventHandler.QueueLongEvent(
                    () => FinishDock(shipMap, own, colony, rotated, offset, turns, seaTile, shipName, viewing), "DepthRim.Dock.Docking", false, null);
                // 3. 演出（M4）：擷取轉好的船體，在殖民地上從外海開進泊位；最後一幀才真的搬進來
                if (SkipShow())
                {
                    finish();
                    return;
                }
                HashSet<IntVec3> berth = new HashSet<IntVec3>(rotated.Select(c => c + offset));
                bool shortVersion = previous != null && previous.center == berthCenter && previous.turns == turns;
                LongEventHandler.ExecuteWhenFinished(() => Find.WindowStack.Add(MakeShow(colony, berth, finish, shipMap, rotated, departing: false, shortVersion)));
            }, "DepthRim.Dock.Docking", doAsynchronously: false, null);
            return true;
        }

        /// <summary>不播演出：測試開關、模組設定「簡化進出港」、原版關掉重力船過場、沒有 Odyssey（擷取要用它的工具）。</summary>
        public static bool SkipShow()
        {
            return SubmarineSurfacing.skipCutscenes || DepthRimModSettings.Current.simplifiedHarbor || !Prefs.GravshipCutscenes || !ModsConfig.OdysseyActive;
        }

        /// <summary>
        /// 演出：進場路線（同 <see cref="DockingMath.Plan"/>，泊位格本身算水）、停好後的船頭、舷靠或錨泊（船體旁邊貼著陸地＝舷靠，碰墊那一側朝陸地）。
        /// <paramref name="berth"/> 是泊位在 <paramref name="colony"/> 上的格子；<paramref name="captureCells"/> 是 <paramref name="captureMap"/> 上要擷取的船體格。
        /// </summary>
        public static HarborCutscene MakeShow(Map colony, HashSet<IntVec3> berth, Action onComplete, Map captureMap, HashSet<IntVec3> captureCells, bool departing, bool shortVersion)
        {
            CellRect box = CellRect.FromCellList(berth);
            Vector3 center3 = box.CenterVector3;
            Vector2 center = new Vector2(center3.x, center3.z);
            bool[] water = Designator_DockShip.WaterMask(colony);
            int w = colony.Size.x;
            DockingMath.Approach approach = DockingMath.Plan(w, colony.Size.z, (x, z) => water[z * w + x], berth, box.Width, box.Height);
            List<Vector2> path = new List<Vector2>();
            if (approach.Ok)
            {
                foreach (IntVec3 c in approach.path.Count > 0 ? DockingMath.Corners(approach.path) : approach.corners)
                {
                    path.Add(new Vector2(c.x + 0.5f, c.z + 0.5f));
                }
            }
            if (path.Count == 0)
            {
                path.Add(center);
            }
            float heading = HarborShowMath.FinalHeading(box.Width, box.Height, path[path.Count - 1], center);
            Vector2 fender = LandSide(colony, berth);
            return new HarborCutscene(colony, center3, onComplete, captureMap, captureCells, path, heading, fender != Vector2.zero, shortVersion, departing, fender);
        }

        /// <summary>船體外緣哪一邊貼著陸地最多（舷靠時碰墊在那一邊）；都沒貼著陸地（錨泊）時 zero。</summary>
        public static Vector2 LandSide(Map map, HashSet<IntVec3> berth)
        {
            int best = 0;
            Vector2 side = Vector2.zero;
            for (int i = 0; i < 4; i++)
            {
                IntVec3 dir = GenAdj.CardinalDirections[i];
                int n = 0;
                foreach (IntVec3 c in berth)
                {
                    if (berth.Contains(c + dir))
                    {
                        continue;
                    }
                    for (int k = 1; k <= 2; k++)
                    {
                        IntVec3 x = c + dir * k;
                        if (x.InBounds(map) && !Building_Gangway.IsWaterForGangway(x, map) && !berth.Contains(x))
                        {
                            n++;
                            break;
                        }
                    }
                }
                if (n > best)
                {
                    best = n;
                    side = new Vector2(dir.x, dir.z);
                }
            }
            return side;
        }

        /// <summary>靠岸的最後一步（演出結束那一刻）：平移進殖民地、游泳的人上甲板、移除船地圖、泊位、舷梯、信件。</summary>
        private static void FinishDock(Map shipMap, bool own, Map colony, HashSet<IntVec3> rotated, IntVec3 offset, int turns, PlanetTile seaTile, string shipName, bool viewing)
        {
            {
                ShipSeparationUtility.RelocateHull(shipMap, colony, rotated, offset);
                HashSet<IntVec3> berth = new HashSet<IntVec3>(rotated.Select(c => c + offset));
                IntVec3 center = CellRect.FromCellList(berth).CenterCell;
                // 不在船體上的人（游泳的）放上甲板
                foreach (Pawn p in shipMap.mapPawns.AllPawnsSpawned.ToList())
                {
                    if (!CellFinder.TryFindRandomCellNear(center, colony, 30, x => x.Standable(colony) && berth.Contains(x), out IntVec3 spot)
                        && !CellFinder.TryFindRandomCellNear(center, colony, 30, x => x.Standable(colony), out spot))
                    {
                        spot = center;
                    }
                    p.DeSpawn();
                    GenSpawn.Spawn(p, spot, colony);
                }
                MapComponent_PileNetwork.For(colony)?.MarkDirty();
                // 4. 船地圖與船物件移除（共用的海面地點留著，裡面還有別的東西）。
                // 這次事件裡才生成的暫時船地圖還沒有畫面（MapDrawer），當場移除會丟例外：它已經空了，交給海面地點的規則下一個 tick 自己移除
                MapParent shipParent = shipMap.Parent;
                if (own && shipParent is WorldObject_SurfaceShip)
                {
                    Current.Game.DeinitAndRemoveMap(shipMap, false);
                    if (!shipParent.Destroyed)
                    {
                        shipParent.Destroy();
                    }
                }
                // 5. 泊位、舷梯
                colony.GetComponent<MapComponent_Berths>()?.Record(shipName, center, turns, seaTile);
                int extended = 0;
                foreach (Building_Gangway g in colony.listerThings.AllThings.OfType<Building_Gangway>().Where(g => berth.Contains(g.Position)).ToList())
                {
                    if (g.CanExtend(out _) && g.Extend())
                    {
                        extended++;
                    }
                }
                if (viewing)
                {
                    Current.Game.CurrentMap = colony;
                    CameraJumper.TryJump(center, colony);
                }
                string text = "DepthRim.Dock.LetterText".Translate(shipName, colony.Parent.LabelCap).ToString();
                if (extended == 0 && SeaLevels.IsVanillaSurface(colony))
                {
                    text += "\n\n" + "DepthRim.Dock.NoGangwayAshore".Translate().ToString();
                }
                Find.LetterStack.ReceiveLetter("DepthRim.Dock.LetterLabel".Translate(), text, LetterDefOf.PositiveEvent, new TargetInfo(center, colony));
            }
        }
    }

    /// <summary>殖民地地圖上記下的泊位（以船名為鍵；設計稿 §5.5：玩家選的就存下來，不重算）。</summary>
    public class MapComponent_Berths : MapComponent
    {
        public sealed class Berth : IExposable
        {
            public string shipName;
            public IntVec3 center;
            public int turns;
            public int seaTileId = -1;

            public void ExposeData()
            {
                Scribe_Values.Look(ref shipName, "shipName");
                Scribe_Values.Look(ref center, "center");
                Scribe_Values.Look(ref turns, "turns");
                Scribe_Values.Look(ref seaTileId, "seaTileId", -1);
            }
        }

        private List<Berth> berths = new List<Berth>();

        public MapComponent_Berths(Map map) : base(map)
        {
        }

        public IReadOnlyList<Berth> All => berths;

        public Berth For(string shipName) => shipName.NullOrEmpty() ? null : berths.FirstOrDefault(b => b.shipName == shipName);

        public void Record(string shipName, IntVec3 center, int turns, PlanetTile seaTile)
        {
            Berth b = For(shipName);
            if (b == null)
            {
                b = new Berth { shipName = shipName };
                berths.Add(b);
            }
            b.center = center;
            b.turns = turns;
            b.seaTileId = seaTile.Valid ? seaTile.tileId : -1;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref berths, "berths", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                berths ??= new List<Berth>();
                berths.RemoveAll(b => b == null);
            }
        }
    }

    /// <summary>
    /// 選泊位（設計稿 §5.1）：船體輪廓跟著滑鼠（滑鼠格＝轉好後外框的中心），Q／E 轉 90°；逐格白色可停、紅色受阻，
    /// 滑鼠旁寫第一個失敗原因；算得出進場路線時畫成淡虛線。舷梯接不到岸不擋，只在滑鼠旁提示。
    /// </summary>
    public class Designator_DockShip : Designator
    {
        private static readonly Color ValidStripes = ColorLibrary.White.WithAlpha(0.5f);

        private static readonly Color InvalidStripes = ColorLibrary.Red.WithAlpha(0.5f);

        private readonly Thing helm;

        private readonly Map colony;

        private readonly List<IntVec3> cells;

        private readonly IntVec3 pivot;

        private int turns;

        private bool[] water;

        private float selectedAt;

        // 快取：同一格、同一朝向不重算
        private IntVec3 cachedMouse = IntVec3.Invalid;
        private int cachedTurns = -1;
        private readonly List<IntVec3> valid = new List<IntVec3>();
        private readonly List<IntVec3> invalid = new List<IntVec3>();
        private AcceptanceReport report = true;
        private DockingMath.Approach approach;
        private bool gangwayReaches;

        private bool docked;

        public Designator_DockShip(Thing helm, Map colony, List<IntVec3> cells)
        {
            this.helm = helm;
            this.colony = colony;
            this.cells = cells;
            pivot = DockingMath.Pivot(cells);
            MapComponent_Berths.Berth last = colony.GetComponent<MapComponent_Berths>()?.For(SurfaceVoyage.ShipNameFor(helm));
            turns = last?.turns ?? 0;
            defaultLabel = "DepthRim.Dock.Label".Translate();
            defaultDesc = "DepthRim.Dock.Desc".Translate();
            useMouseIcon = false;
        }

        public int Turns => turns;

        public override bool AlwaysDoGuiControls => true;

        public override void Selected()
        {
            base.Selected();
            selectedAt = Time.realtimeSinceStartup;
            water = WaterMask(colony);
            cachedMouse = IntVec3.Invalid;
        }

        /// <summary>殖民地地圖上船能浮的格子：原版海水（帶 DepthRim_HullBase）、沒有船體平台、沒有建築。</summary>
        public static bool[] WaterMask(Map map)
        {
            bool[] mask = new bool[map.Size.x * map.Size.z];
            foreach (IntVec3 c in map.AllCells)
            {
                mask[c.z * map.Size.x + c.x] = HullWater(c, map) && c.GetEdifice(map) == null;
            }
            return mask;
        }

        public static bool HullWater(IntVec3 c, Map map)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            TerrainDef t = map.terrainGrid.TerrainAt(c);
            return t != null && t.affordances != null && t.affordances.Contains(DefDatabase<TerrainAffordanceDef>.GetNamed("DepthRim_HullBase"))
                   && !MapComponent_SeabedMemory.IsPlatform(t);
        }

        /// <summary>轉 <paramref name="t"/> 次後、外框中心在 <paramref name="center"/> 的泊位格。</summary>
        public List<IntVec3> BerthCells(IntVec3 center, int t)
        {
            List<IntVec3> rotated = DockingMath.RotateCells(cells, pivot, t);
            IntVec3 anchor = CellRect.FromCellList(rotated).CenterCell;
            IntVec3 offset = center - anchor;
            return rotated.Select(c => c + offset).ToList();
        }

        /// <summary>泊位檢查（§5.2）：每格能停、旋轉合法、有迴轉空間與進場路線。同時填好白／紅格與路線。</summary>
        public AcceptanceReport Evaluate(IntVec3 center, int t)
        {
            valid.Clear();
            invalid.Clear();
            approach = null;
            gangwayReaches = false;
            water ??= WaterMask(colony);
            List<IntVec3> berth = BerthCells(center, t);
            AcceptanceReport result = true;
            foreach (IntVec3 d in berth)
            {
                AcceptanceReport r = d.InBounds(colony) ? TransferLandingUtility.IsValidLandingCell(d, colony) : (AcceptanceReport)"DepthRim.Dock.OutOfBounds".Translate().ToString();
                if (r.Accepted && !HullWater(d, colony))
                {
                    r = "DepthRim.Dock.NotWater".Translate();
                }
                if (r.Accepted)
                {
                    valid.Add(d);
                }
                else
                {
                    invalid.Add(d);
                    if (result.Accepted)
                    {
                        result = r;
                    }
                }
            }
            if (!result.Accepted)
            {
                return result;
            }
            Thing blocker = helm.Spawned ? HullRotator.Blocker(helm.Map, cells, t) : null;
            if (blocker != null)
            {
                invalid.AddRange(valid);
                valid.Clear();
                return "DepthRim.Dock.CannotTurn".Translate(blocker.LabelCap, blocker.def.size.x, blocker.def.size.z);
            }
            CellRect box = CellRect.FromCellList(berth);
            HashSet<IntVec3> berthSet = new HashSet<IntVec3>(berth);
            int w = colony.Size.x;
            approach = DockingMath.Plan(w, colony.Size.z, (x, z) => water[z * w + x], berthSet, box.Width, box.Height);
            if (!approach.Ok)
            {
                invalid.AddRange(valid);
                valid.Clear();
                return approach.failure == DockingMath.PlanFailure.NoTurningRoom ? "DepthRim.Dock.NoTurningRoom".Translate() : "DepthRim.Dock.NoRoute".Translate();
            }
            gangwayReaches = GangwayCouldReachShore(berthSet);
            return true;
        }

        /// <summary>泊位外緣往外 6 格（舷梯最長）內有沒有岸（不是水、能走）。只是提示，不擋。</summary>
        private bool GangwayCouldReachShore(HashSet<IntVec3> berth)
        {
            const int reach = 6;
            foreach (IntVec3 c in berth)
            {
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 dir = GenAdj.CardinalDirections[i];
                    if (berth.Contains(c + dir))
                    {
                        continue;
                    }
                    for (int k = 1; k <= reach + 1; k++)
                    {
                        IntVec3 x = c + dir * k;
                        if (!x.InBounds(colony) || berth.Contains(x))
                        {
                            break;
                        }
                        if (Building_Gangway.IsLanding(x, colony))
                        {
                            return true;
                        }
                        if (!Building_Gangway.IsGap(x, colony))
                        {
                            break;
                        }
                    }
                }
            }
            return false;
        }

        private void Refresh()
        {
            IntVec3 mouse = UI.MouseCell();
            if (mouse == cachedMouse && turns == cachedTurns)
            {
                return;
            }
            cachedMouse = mouse;
            cachedTurns = turns;
            report = Evaluate(mouse, turns);
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 loc)
        {
            if (Find.CurrentMap != colony || helm.Destroyed || !helm.Spawned || Time.realtimeSinceStartup - selectedAt <= 0.3f)
            {
                return false;
            }
            return Evaluate(loc, turns);
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            int t = turns;
            docked = true;
            Find.DesignatorManager.Deselect();
            Docking.Dock(helm, t, c, colony);
        }

        /// <summary>
        /// 玩家自己切到別張地圖造成的取消不搶鏡頭。
        /// </summary>
        public override void Deselected()
        {
            base.Deselected();
            if (docked || helm == null || helm.Destroyed || !helm.Spawned)
            {
                return;
            }
            Map seaMap = helm.Map;
            string label = SurfaceVoyage.ShipNameFor(helm);
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                if (helm.Destroyed || !helm.Spawned || Find.CurrentMap != colony)
                {
                    Docking.RemoveIfUnused(colony);
                    return;
                }
                Current.Game.CurrentMap = seaMap;
                IntVec3 center = cells.Count > 0 ? CellRect.FromCellList(cells).CenterCell : seaMap.Center;
                CameraJumper.TryJump(center, seaMap);
                Messages.Message("DepthRim.Dock.Cancelled".Translate(label, colony.Parent.LabelCap), new TargetInfo(center, seaMap), MessageTypeDefOf.NeutralEvent, historical: false);
                // 剛為這次靠岸建立、還沒用到的停泊點撤掉
                Docking.RemoveIfUnused(colony);
            });
        }

        public override void SelectedProcessInput(Event ev)
        {
            base.SelectedProcessInput(ev);
            if (KeyBindingDefOf.Designator_RotateRight.KeyDownEvent)
            {
                turns = (turns + 1) % 4;
                ev.Use();
            }
            else if (KeyBindingDefOf.Designator_RotateLeft.KeyDownEvent)
            {
                turns = (turns + 3) % 4;
                ev.Use();
            }
        }

        public override void SelectedUpdate()
        {
            if (helm.Destroyed || !helm.Spawned)
            {
                Find.DesignatorManager.Deselect();
                return;
            }
            if (Find.CurrentMap != colony)
            {
                return;
            }
            Refresh();
            GenDraw.DrawFieldEdges(valid, ColorLibrary.White);
            GenDraw.DrawFieldEdges(invalid, ColorLibrary.Red);
            GenDraw.DrawDiagonalStripes(valid, ValidStripes);
            GenDraw.DrawDiagonalStripes(invalid, InvalidStripes);
            if (approach != null && approach.Ok)
            {
                DrawDashed(approach.corners);
            }
        }

        /// <summary>路線預覽：淡淡的虛線（每格畫一半）。</summary>
        private static void DrawDashed(List<IntVec3> corners)
        {
            float y = AltitudeLayer.MetaOverlays.AltitudeFor();
            for (int i = 0; i + 1 < corners.Count; i++)
            {
                Vector3 a = corners[i].ToVector3Shifted().WithY(y);
                Vector3 b = corners[i + 1].ToVector3Shifted().WithY(y);
                float len = (b - a).magnitude;
                if (len < 0.01f)
                {
                    continue;
                }
                Vector3 dir = (b - a) / len;
                for (float s = 0f; s < len; s += 1f)
                {
                    GenDraw.DrawLineBetween(a + dir * s, a + dir * Mathf.Min(s + 0.5f, len), SimpleColor.White, 0.15f);
                }
            }
        }

        public override void DrawMouseAttachments()
        {
            string rotate = "DepthRim.Dock.RotateHint".Translate(KeyBindingDefOf.Designator_RotateLeft.MainKeyLabel, KeyBindingDefOf.Designator_RotateRight.MainKeyLabel);
            if (!report.Accepted)
            {
                GenUI.DrawMouseAttachment(null, report.Reason + "\n" + rotate, 0f, default(Vector2), null, ColorLibrary.RedReadable, drawTextBackground: true, new Color(0f, 0f, 0f, 0.5f));
            }
            else if (!gangwayReaches)
            {
                GenUI.DrawMouseAttachment(null, "DepthRim.Dock.GangwayWontReach".Translate() + "\n" + rotate, 0f, default(Vector2), null, ColorLibrary.Yellow, drawTextBackground: true, new Color(0f, 0f, 0f, 0.5f));
            }
            else
            {
                GenUI.DrawMouseAttachment(null, rotate, 0f, default(Vector2), null, null, drawTextBackground: true, new Color(0f, 0f, 0f, 0.5f));
            }
        }
    }
}
