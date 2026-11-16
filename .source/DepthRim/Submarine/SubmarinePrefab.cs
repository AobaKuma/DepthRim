using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified.Structures;
using LudeonTK;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 預製潛艇：用 FFF 結構（<see cref="FFF_StructureDef"/>，<c>1.7/Defs/Submarine/Submarine_Prefabs.xml</c>）一次生成一艘能直接出航的潛艇。
    /// 給驗收測試、除錯動作與之後的開局內容（<see cref="GenStep_PrefabSubmarine"/>）共用。
    ///
    /// <para>FFF 的 <see cref="FFF_StructureUtility.Generate"/> 之外補上潛艇需要的幾件事：</para>
    /// <list type="bullet">
    /// <item>船體平台格蓋屋頂（不依賴 FFF 屋頂任務在旋轉後的座標）。只蓋平台格：遊戲內匯出的潛艇不是長方形，佔地四角是深淵。</item>
    /// <item>船體平台格設成乾的（<see cref="MapComponent_Flooding.MarkDry"/>），不然水位系統會把新艙室當成已經淹水。</item>
    /// <item>通知 DMSE 重算龍骨網路。</item>
    /// </list>
    /// 迷霧交給 FFF：玩家派系的結構整塊揭開，其他只重算占地內（<see cref="FFF_StructureUtility.RecomputeFog"/>）。
    /// 平台蓋在海床上時，原本的海床由 <see cref="MapComponent_SeabedMemory"/> 記住（它掛在 SetTerrain 上，FFF 鋪地形也會經過）。
    /// </summary>
    public static class SubmarinePrefab
    {
        public const string DefaultDefName = "DepthRim_Prefab_PatrolSubmarine";

        public static FFF_StructureDef Default => DefDatabase<FFF_StructureDef>.GetNamedSilentFail(DefaultDefName);

        /// <summary>以 <paramref name="center"/> 為中心、轉向 <paramref name="rot"/> 時實際佔的格子。</summary>
        public static CellRect Footprint(FFF_StructureDef def, IntVec3 center, Rot4 rot)
        {
            return FFF_StructureUtility.FootprintAt(def ?? Default, center, rot);
        }

        /// <summary>能不能生成在這裡：佔地（外加一圈）在地圖內、地圖可以出航、佔地內沒有玩家建築。</summary>
        public static bool CanSpawnAt(Map map, IntVec3 center, Rot4 rot, out string reason, FFF_StructureDef def = null)
        {
            def = def ?? Default;
            reason = null;
            if (def == null)
            {
                reason = "missing structure def " + DefaultDefName;
                return false;
            }
            if (map == null)
            {
                reason = "no map";
                return false;
            }
            if (!TransferEnvironment.AllowsTransfer(map))
            {
                reason = "this map's layer does not allow submarine voyages";
                return false;
            }
            CellRect rect = Footprint(def, center, rot);
            if (!rect.ExpandedBy(1).InBounds(map))
            {
                reason = "too close to the map edge";
                return false;
            }
            foreach (IntVec3 c in rect)
            {
                Building b = c.GetFirstBuilding(map);
                if (b != null && b.Faction == Faction.OfPlayer)
                {
                    reason = "overlaps " + b.LabelShort;
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 生成預製潛艇，回傳佔地。<paramref name="duringMapGen"/> 為 true 時（地圖生成步驟裡）不強制重建電網，
        /// 原因見 <see cref="FFF_StructureUtility.Generate"/> 的說明。
        /// </summary>
        public static CellRect Spawn(Map map, IntVec3 center, Rot4 rot, Faction faction = null, FFF_StructureDef def = null, bool duringMapGen = false)
        {
            def = def ?? Default;
            faction = faction ?? Faction.OfPlayer;
            CellRect rect = Footprint(def, center, rot);

            FFF_StructureUtility.Generate(def, center, map, faction, rot, reconnectPower: !duringMapGen);

            List<IntVec3> hull = HullCells(map, rect);
            foreach (IntVec3 c in hull)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }
            map.Flooding()?.MarkDry(hull);
            int groups = LinkKeels(map, rect);
            if (groups > 1)
            {
                Log.Warning($"[DepthRim] Prefab submarine {def.defName}: its keel frames form {groups} separate hulls (some are out of link range); only the part the helm stands on can leave.");
            }
            MapComponent_PileNetwork.For(map)?.MarkDirty();
            // 原版的電網只在遊戲進行時每幾十 tick 開幾台，新遊戲一開始是暫停的：艦橋與推進器一直斷電，航行計畫確認不了。
            // 地圖生成期間電網還沒建好，等長時間事件結束再開。
            if (duringMapGen)
            {
                LongEventHandler.ExecuteWhenFinished(() => PowerUp(map, rect));
            }
            else
            {
                PowerUp(map, rect);
            }
            return rect;
        }

        /// <summary>
        /// 佔地內想開著（開關開、沒故障）的用電建築直接通電，只要它所在的電網撐得住（儲電加上淨發電夠用）。
        /// 撐不住的留給原版電網慢慢處理。回傳通電的台數。
        /// </summary>
        public static int PowerUp(Map map, CellRect rect)
        {
            if (map == null || map.Disposed)
            {
                return 0;
            }
            // 剛生成完電網可能還沒建好（原版每個畫面開頭才重建）：先建好
            map.powerNetManager.UpdatePowerNetsAndConnections_First();
            int count = 0;
            HashSet<Thing> seen = new HashSet<Thing>();
            foreach (IntVec3 c in rect.ClipInsideMap(map))
            {
                foreach (Thing t in c.GetThingList(map))
                {
                    if (!seen.Add(t))
                    {
                        continue;
                    }
                    CompPowerTrader power = t.TryGetComp<CompPowerTrader>();
                    if (power == null || power.PowerOn || power.PowerNet == null || !FlickUtility.WantsToBeOn(t) || t.IsBrokenDown())
                    {
                        continue;
                    }
                    PowerNet net = power.PowerNet;
                    if (net.CurrentStoredEnergy() + net.CurrentEnergyGainRate() + power.EnergyOutputPerTick < 0f)
                    {
                        continue;
                    }
                    power.PowerOn = true;
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// 把佔地內的龍骨框架用鋼梁連起來（直接完工，不用材料）。DMSE 的龍骨要有鋼梁連結才算同一艘船，
        /// 而 FFF 結構（包括遊戲內匯出的）不記錄鋼梁，生成後每座龍骨各自獨立，出港時只會帶走艦橋那一座覆蓋的船體。
        /// 照距離由近到遠連（最小生成樹），遵守 DMSE 的連結規則（距離、每座的連結數上限）。回傳連完之後還剩幾群（1 = 全部連在一起）。
        /// </summary>
        public static int LinkKeels(Map map, CellRect rect)
        {
            List<CompStructuralPile> piles = rect.ClipInsideMap(map).Cells
                .SelectMany(c => c.GetThingList(map)).Distinct()
                .Select(t => t.TryGetComp<CompStructuralPile>()).Where(p => p != null).ToList();
            if (piles.Count <= 1)
            {
                return piles.Count;
            }
            int[] group = Enumerable.Range(0, piles.Count).ToArray();
            int Find(int i) => group[i] == i ? i : (group[i] = Find(group[i]));
            // 已經有的連結（例如之後 FFF 開始記錄鋼梁）先算進去
            for (int i = 0; i < piles.Count; i++)
            {
                for (int j = i + 1; j < piles.Count; j++)
                {
                    if (piles[i].Links.Contains(piles[j].parent))
                    {
                        group[Find(i)] = Find(j);
                    }
                }
            }
            List<(int a, int b, float d)> pairs = new List<(int, int, float)>();
            for (int i = 0; i < piles.Count; i++)
            {
                for (int j = i + 1; j < piles.Count; j++)
                {
                    pairs.Add((i, j, piles[i].parent.Position.DistanceToSquared(piles[j].parent.Position)));
                }
            }
            foreach ((int a, int b, float _) in pairs.OrderBy(p => p.d))
            {
                if (Find(a) == Find(b) || !PileLinkUtility.CanOrderLink(piles[a], piles[b].parent, out _))
                {
                    continue;
                }
                PileLinkUtility.CompleteLink(piles[a], piles[b]);
                group[Find(a)] = Find(b);
            }
            return Enumerable.Range(0, piles.Count).Select(Find).Distinct().Count();
        }

        /// <summary>佔地內鋪了船體平台的格子（潛艇真正的船體）。</summary>
        public static List<IntVec3> HullCells(Map map, CellRect rect)
        {
            List<IntVec3> cells = new List<IntVec3>();
            foreach (IntVec3 c in rect.ClipInsideMap(map))
            {
                if (c.GetTerrain(map)?.affordances?.Contains(DepthRimDefOf.DepthRim_HullPlatform) == true)
                {
                    cells.Add(c);
                }
            }
            return cells;
        }

        /// <summary>
        /// 挖船塢：清掉範圍內所有能摧毀的東西、拿掉屋頂、鋪成深淵（DMSE 的虛空地形，潛艇離港後留下的就是它）。
        /// 船塢周圍若是岩體，挖完的邊緣是陡崖，和玩家自己在海床上挖的船塢一樣。
        /// </summary>
        public static void CarveShipyard(Map map, CellRect yard)
        {
            ClearArea(map, yard);
            foreach (IntVec3 c in yard.ClipInsideMap(map))
            {
                map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_Abyss);
            }
        }

        /// <summary>
        /// 直接停在海床上：只清掉範圍內能摧毀的東西（岩塊、海藻、礦物）與屋頂，地形不動。
        /// 船體平台鋪上去時，被蓋掉的海床由 <see cref="MapComponent_SeabedMemory"/> 記住，離港後還原。
        /// </summary>
        public static void ClearArea(Map map, CellRect area)
        {
            foreach (IntVec3 c in area.ClipInsideMap(map))
            {
                foreach (Thing t in c.GetThingList(map).ListFullCopy())
                {
                    if (t.def.destroyable && !(t is Pawn))
                    {
                        t.Destroy(DestroyMode.Vanish);
                    }
                }
                map.roofGrid.SetRoof(c, null);
            }
        }

        [DebugAction("DepthRim", "Spawn prefab submarine", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void DebugSpawn()
        {
            Map map = Find.CurrentMap;
            IntVec3 center = UI.MouseCell();
            if (!CanSpawnAt(map, center, Rot4.North, out string reason))
            {
                Messages.Message("Cannot spawn prefab submarine: " + reason, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            Spawn(map, center, Rot4.North);
        }
    }

    /// <summary>
    /// 地圖生成步驟：在地圖上放一艘預製潛艇（之後開局內容用）。預設先在中央挖一個深淵坑當船塢，船體平台就像玩家自己蓋在船塢上一樣，
    /// 離港後留下深淵；<see cref="carveShipyard"/> 為 false 時直接蓋在海床上（離港後海床由 <see cref="MapComponent_SeabedMemory"/> 還原）。
    /// </summary>
    public class GenStep_PrefabSubmarine : GenStep
    {
        public FFF_StructureDef structure;

        public Rot4 rot = Rot4.North;

        public bool carveShipyard = true;

        /// <summary>船塢比船體每邊多幾格深淵。</summary>
        public int shipyardMargin = 2;

        public override int SeedPart => 0x5B9EFAB;

        public override void Generate(Map map, GenStepParams parms)
        {
            FFF_StructureDef def = structure ?? SubmarinePrefab.Default;
            if (def == null)
            {
                Log.Error("[DepthRim] GenStep_PrefabSubmarine: no structure def");
                return;
            }
            IntVec3 center = map.Center;
            if (carveShipyard)
            {
                SubmarinePrefab.CarveShipyard(map, SubmarinePrefab.Footprint(def, center, rot).ExpandedBy(shipyardMargin));
            }
            SubmarinePrefab.Spawn(map, center, rot, Faction.OfPlayer, def, duringMapGen: true);
        }
    }
}
