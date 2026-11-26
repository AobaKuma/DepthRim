using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 在同一張地圖上把一具活的船體原地旋轉 90° 的倍數（靠岸 M3，設計稿 §6.1 的表）：
    /// <list type="bullet">
    /// <item>地形（底層、地板）、屋頂逐格座標變換；舊位置鋪回這張地圖的「空」（<see cref="SeaLevels.VoidTerrain"/>）。</item>
    /// <item>東西（建築、物品、Pawn、藍圖、框架）先全部移出地圖（建築用 <see cref="DestroyMode.WillReplace"/>：DMSE 的龍骨連結、施工單、材料原樣保留），
    /// 再放到轉過的位置；能轉的建築朝向跟著轉，非方形又不能轉的只能轉 180°（<see cref="DockingMath.RotatedPlacement"/>）。</item>
    /// <item>儲存區、種植區等區域逐格搬；船上的指派（拆除、採收等）取消。</item>
    /// <item>艙室水位與艙壓照對應的格子搬。</item>
    /// </list>
    /// 地基（舷梯的臨時甲板）不搬：旋轉前舷梯必須收起（呼叫端負責）。
    /// 只在不會撞到別的東西的地圖上用（船自己的地圖：開放海面上只有這具船體）。
    /// </summary>
    public static class HullRotator
    {
        /// <summary>這具船體轉 <paramref name="turns"/> 次能不能轉：非方形又不能轉的建築擋住奇數次。回傳擋住的東西（可以轉時 null）。</summary>
        public static Thing Blocker(Map map, ICollection<IntVec3> cells, int turns)
        {
            if (((turns % 4) + 4) % 2 == 0)
            {
                return null;
            }
            foreach (Thing t in ThingsOn(map, cells))
            {
                if (t.def.size.x != t.def.size.z && !t.def.rotatable && !(t is Pawn))
                {
                    return t;
                }
            }
            return null;
        }

        private static List<Thing> ThingsOn(Map map, ICollection<IntVec3> cells)
        {
            HashSet<Thing> seen = new HashSet<Thing>();
            List<Thing> list = new List<Thing>();
            foreach (IntVec3 c in cells)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(map))
                {
                    if (seen.Add(t))
                    {
                        list.Add(t);
                    }
                }
            }
            return list;
        }

        private struct CellData
        {
            public TerrainDef top;
            public TerrainDef under;
            public RoofDef roof;
            public Zone zone;
        }

        /// <summary>
        /// 區域逐格搬到新位置。原版的區域少到零格就會被刪掉，新舊位置又會重疊，所以分幾輪：
        /// 先把空著的新格加進去，再拿掉不要的舊格（每個區域至少留一格），重複到沒有變化；最後把殘留的舊格拿掉。
        /// </summary>
        private static void MoveZones(Map map, HashSet<IntVec3> source, Dictionary<IntVec3, CellData> data, Dictionary<IntVec3, IntVec3> mapping)
        {
            Dictionary<Zone, HashSet<IntVec3>> targets = new Dictionary<Zone, HashSet<IntVec3>>();
            foreach (KeyValuePair<IntVec3, CellData> kv in data)
            {
                if (kv.Value.zone == null)
                {
                    continue;
                }
                if (!targets.TryGetValue(kv.Value.zone, out HashSet<IntVec3> set))
                {
                    set = new HashSet<IntVec3>();
                    targets[kv.Value.zone] = set;
                }
                set.Add(mapping[kv.Key]);
            }
            for (int pass = 0; pass < 6; pass++)
            {
                bool changed = false;
                foreach (KeyValuePair<Zone, HashSet<IntVec3>> kv in targets)
                {
                    foreach (IntVec3 t in kv.Value)
                    {
                        if (map.zoneManager.ZoneAt(t) == null)
                        {
                            kv.Key.AddCell(t);
                            changed = true;
                        }
                    }
                }
                foreach (KeyValuePair<Zone, HashSet<IntVec3>> kv in targets)
                {
                    foreach (IntVec3 s in kv.Key.Cells.Where(c => source.Contains(c) && !kv.Value.Contains(c)).ToList())
                    {
                        if (kv.Key.Cells.Count > 1)
                        {
                            kv.Key.RemoveCell(s);
                            changed = true;
                        }
                    }
                }
                if (!changed)
                {
                    break;
                }
            }
            foreach (KeyValuePair<Zone, HashSet<IntVec3>> kv in targets)
            {
                foreach (IntVec3 s in kv.Key.Cells.Where(c => source.Contains(c) && !kv.Value.Contains(c)).ToList())
                {
                    kv.Key.RemoveCell(s);
                }
            }
        }

        /// <summary>
        /// 以 <paramref name="pivot"/> 為中心順時針轉 <paramref name="turns"/> 次。回傳轉好後的船體格（失敗時 null，什麼都不動）。
        /// </summary>
        public static HashSet<IntVec3> Rotate(Map map, ICollection<IntVec3> cells, IntVec3 pivot, int turns)
        {
            turns = ((turns % 4) + 4) % 4;
            HashSet<IntVec3> source = new HashSet<IntVec3>(cells);
            if (turns == 0)
            {
                return source;
            }
            Dictionary<IntVec3, IntVec3> mapping = source.ToDictionary(c => c, c => DockingMath.RotateCell(c, pivot, turns));
            if (mapping.Values.Any(c => !c.InBounds(map)))
            {
                return null;
            }
            if (Blocker(map, source, turns) != null)
            {
                return null;
            }

            // 東西：先算好新位置，任何一個放不下就整個放棄
            List<Thing> things = ThingsOn(map, source);
            Dictionary<Thing, (IntVec3 pos, Rot4 rot)> placements = new Dictionary<Thing, (IntVec3, Rot4)>();
            foreach (Thing t in things)
            {
                IntVec2 size = t is Pawn ? IntVec2.One : t.def.size;
                bool rotatable = t is Pawn || t.def.rotatable;
                if (!DockingMath.RotatedPlacement(t.Position, t.Rotation, size, rotatable, pivot, turns, out IntVec3 pos, out Rot4 rot))
                {
                    return null;
                }
                placements[t] = (pos, rot);
            }

            // 逐格資料
            Dictionary<IntVec3, CellData> data = new Dictionary<IntVec3, CellData>();
            foreach (IntVec3 c in source)
            {
                data[c] = new CellData
                {
                    top = map.terrainGrid.TopTerrainAt(c),
                    under = map.terrainGrid.UnderTerrainAt(c),
                    roof = map.roofGrid.RoofAt(c),
                    zone = map.zoneManager.ZoneAt(c),
                };
                foreach (Designation d in map.designationManager.AllDesignationsAt(c).ToList())
                {
                    map.designationManager.RemoveDesignation(d);
                }
            }
            foreach (Thing t in things)
            {
                map.designationManager.RemoveAllDesignationsOn(t);
            }

            // 移出東西：Pawn 先移出（照常），再重新收集其他東西——Pawn 移出時會放下搬運中的東西，放下的堆會和地上的同種堆合併，
            // 先收集的清單裡就會有已經被併掉（銷毀）的堆（2026-11-21 log：Steel 被重複移出、以 0 堆疊數重生）。
            // 其他用 WillReplace，DMSE 的龍骨連結不會斷。
            Dictionary<Pawn, bool> drafted = new Dictionary<Pawn, bool>();
            foreach (Pawn p in things.OfType<Pawn>().Where(p => p.Spawned).ToList())
            {
                drafted[p] = p.Drafted;
                p.DeSpawn();
            }
            List<Thing> pawnsMoved = things.Where(t => t is Pawn).ToList();
            things = ThingsOn(map, source).Where(t => !(t is Pawn)).ToList();
            foreach (Thing t in things)
            {
                if (!placements.ContainsKey(t))
                {
                    // Pawn 放下的東西（1×1 物品，一定轉得過去）
                    IntVec2 size = t.def.size;
                    if (DockingMath.RotatedPlacement(t.Position, t.Rotation, size, t.def.rotatable, pivot, turns, out IntVec3 pos, out Rot4 rot))
                    {
                        placements[t] = (pos, rot);
                    }
                }
            }
            things = things.Where(t => placements.ContainsKey(t)).ToList();
            foreach (Thing t in things)
            {
                if (!t.Destroyed && t.Spawned)
                {
                    t.DeSpawn(DestroyMode.WillReplace);
                }
            }
            things.AddRange(pawnsMoved);

            // 清空舊格、鋪新格
            TerrainDef empty = SeaLevels.VoidTerrain(map);
            foreach (IntVec3 c in source)
            {
                map.roofGrid.SetRoof(c, null);
                map.terrainGrid.SetTerrain(c, empty);
            }
            foreach (KeyValuePair<IntVec3, IntVec3> kv in mapping)
            {
                CellData d = data[kv.Key];
                IntVec3 to = kv.Value;
                if (d.under != null)
                {
                    map.terrainGrid.SetTerrain(to, d.under);
                }
                if (d.top != null)
                {
                    map.terrainGrid.SetTerrain(to, d.top);
                }
                if (d.roof != null)
                {
                    map.roofGrid.SetRoof(to, d.roof);
                }
            }
            MoveZones(map, source, data, mapping);

            // 放回東西：建築先放（地板、電線等），Pawn 最後
            foreach (Thing t in things.OrderBy(t => t is Pawn ? 2 : t.def.category == ThingCategory.Building ? 0 : 1))
            {
                if (t.Destroyed || t.Spawned)
                {
                    continue;
                }
                (IntVec3 pos, Rot4 rot) = placements[t];
                GenSpawn.Spawn(t, pos, map, rot, WipeMode.Vanish, respawningAfterLoad: false);
                if (t is Pawn p && drafted.TryGetValue(p, out bool wasDrafted) && wasDrafted && p.drafter != null)
                {
                    p.drafter.Drafted = true;
                }
            }

            map.GetComponent<MapComponent_Flooding>()?.RemapLevels(mapping);
            map.GetComponent<MapComponent_PressureChambers>()?.RemapCells(mapping);
            FloodingUtility.WaterChanged(map);
            MapComponent_PileNetwork.For(map)?.MarkDirty();
            return new HashSet<IntVec3>(mapping.Values);
        }
    }
}
