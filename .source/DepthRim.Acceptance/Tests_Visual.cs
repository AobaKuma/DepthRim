using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>水體網格與著色器（SectionLayer_WaterVolume、DepthRim_WaterVolume.frag.glsl）。</summary>
    public static class Tests_Visual
    {
        private const int Update = MapComponent_Flooding.UpdateInterval;

        private static WaitTicks Updates(int n) => new WaitTicks(Update * n + 5);

        /// <summary>重建並取得 cell 所在區塊的艙室層網格（頂點、顏色）。</summary>
        private static LayerSubMesh WaterMesh(Map map, IntVec3 cell) => Mesh<SectionLayer_WaterVolume>(map, cell);

        /// <summary>重建並取得 cell 所在區塊的外海層網格。</summary>
        private static LayerSubMesh OuterMesh(Map map, IntVec3 cell) => Mesh<SectionLayer_WaterOuter>(map, cell);

        /// <summary>
        /// 圖層上真正有幾何的 submesh。`SectionLayer.ClearSubMeshes` 只是把頂點清空、**不會**移除
        /// submesh 本身，而 `GetSubMesh(material)` 是「同材質就重用，否則新增」，所以換過材質的區塊
        /// 會留下一份空的舊 submesh；`subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0)` 很可能拿到的是上一個層級的空殼
        /// （它的 material 還是舊的），V6／V8 從前就是這樣誤判的。
        /// </summary>
        private static LayerSubMesh LiveMesh(SectionLayer layer)
            => layer?.subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0);

        private static LayerSubMesh Mesh<T>(Map map, IntVec3 cell) where T : SectionLayer
        {
            Section section = map.mapDrawer.SectionAt(cell);
            SectionLayer layer = section.GetLayer(typeof(T));
            layer.Regenerate();
            return LiveMesh(layer);
        }

        /// <summary>網格裡左下角落在 cell 的四邊形的四個頂點顏色（找不到時為 null）。</summary>
        private static Color32[] QuadAt(LayerSubMesh sm, IntVec3 cell)
        {
            if (sm == null) { return null; }
            for (int i = 0; i + 3 < sm.verts.Count; i += 4)
            {
                Vector3 v = sm.verts[i];
                if (Mathf.RoundToInt(v.x) == cell.x && Mathf.RoundToInt(v.z) == cell.z)
                {
                    return new[] { sm.colors[i], sm.colors[i + 1], sm.colors[i + 2], sm.colors[i + 3] };
                }
            }
            return null;
        }

        public static IEnumerable<Wait> V1_ShaderLoaded(TestContext ctx)
        {
            foreach (ShaderTypeDef def in new[] { DepthRimDefOf.DepthRim_WaterVolume, DepthRimDefOf.DepthRim_WaterOuter })
            {
                ctx.Check(def.Shader != null && def.Shader != ShaderDatabase.DefaultShader, $"{def.defName} compiled (not the Cutout fallback)");
            }
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            IntVec3 open = DepthRimFixture.OpenWaterCell();
            ctx.Require(open.IsValid, "found an open-water cell");
            ctx.Check(DepthRimFixture.Flooding.IsOpenAt(open), "open-water flag set for the cell");

            LayerSubMesh outer = OuterMesh(map, open);
            ctx.Require(outer != null && outer.verts.Count > 0, "open water section has an outer-layer mesh");
            ctx.Check(outer.material.shader == DepthRimDefOf.DepthRim_WaterOuter.Shader, "outer mesh uses the outer shader");
            ctx.Check(outer.verts.Count % 4 == 0 && outer.tris.Count == outer.verts.Count / 4 * 6 && outer.colors.Count == outer.verts.Count, "one coloured quad per covered cell");
            ctx.Check(outer.verts.All(v => v.y > AltitudeLayer.FogOfWar.AltitudeFor()), "outer layer drawn above fog altitude");
            ctx.Check(QuadAt(outer, open) != null, "open-water cell is covered by the outer layer");
            ctx.Check(QuadAt(WaterMesh(map, open), open) == null, "open water is not drawn again by the compartment layer");

            // 開放水域的每個區塊都應該有外海層網格
            int sections = 0, covered = 0;
            for (int x = 8; x < map.Size.x; x += 17)
            {
                for (int z = 8; z < map.Size.z; z += 17)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (!DepthRimFixture.Flooding.IsOpenAt(c)) { continue; }
                    sections++;
                    Section section = map.mapDrawer.SectionAt(c);
                    SectionLayer layer = section.GetLayer(typeof(SectionLayer_WaterOuter));
                    bool pending = layer.Dirty || (section.dirtyFlags & (ulong)DepthRimDefOf.DepthRim_Water) != 0;
                    if (layer.subMeshes.Any(m => m.finalized && m.verts.Count > 0) || pending) { covered++; }
                }
            }
            ctx.Check(sections > 0 && covered == sections, $"every open-water section has (or will rebuild) an outer mesh ({covered}/{sections})");
            Current.Game.CurrentMap = map;
            CameraJumper.TryJump(open, map);
            Find.CameraDriver.SetRootSize(14f);
            yield return new WaitFrames(60);
            ctx.Screenshot("open_water");
            Find.CameraDriver.SetRootSize(40f);
            yield return new WaitFrames(60);
            ctx.Screenshot("open_water_wide");
        }

        public static IEnumerable<Wait> V2_CompartmentEdges(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            CellRect ring = DepthRimFixture.ClearOceanArea(7);
            List<Thing> walls = DepthRimFixture.BuildWallRing(ring);
            CellRect inner = ring.ContractedBy(1);
            yield return Updates(1);

            // 抽到一半：排水泵開著，水位降到 40–60% 時關掉
            DepthRimFixture.Spawn(ThingDefOf.PowerConduit, inner.CenterCell + IntVec3.North, map);
            CompFloodPump pump = DepthRimFixture.Spawn(DepthRimFixture.BilgePump, inner.CenterCell, map).TryGetComp<CompFloodPump>();
            WaitUntil half = new WaitUntil(() => DepthRimFixture.Flooding.LevelAt(inner.CenterCell) <= 0.6f, 4000);
            yield return half;
            ctx.Require(!half.TimedOut, "compartment pumped down to about half");
            pump.parent.TryGetComp<CompFlickable>().DoFlick();
            yield return Updates(1);
            float level = DepthRimFixture.Flooding.LevelAt(inner.CenterCell);
            ctx.Note($"compartment level {level:P0}");

            LayerSubMesh sm = WaterMesh(map, inner.CenterCell);
            Color32[] center = QuadAt(sm, inner.CenterCell + IntVec3.East);
            ctx.Require(center != null, "half-flooded compartment cell has a quad");
            ctx.Check(center.All(c => Mathf.Abs(c.a / 255f - level) < 0.02f), $"interior vertices carry the compartment level ({string.Join(",", center.Select(c => c.a))})");
            // B 階段起 g 通道是光照值（不再是開放水域旗標）
            float expectLight = FloodingMath.LightOf(map.glowGrid.VisualGlowAt(inner.CenterCell));
            ctx.Check(center.All(c => Mathf.Abs(c.g / 255f - expectLight) < 0.08f), $"compartment vertices carry the light value ({center[0].g} vs {expectLight:0.000})");
            ctx.Check(center.All(c => c.r == 0), "interior cell has no shore");

            // 角落格：左下頂點三格是牆 → 岸邊 0.75；而且牆外淹滿的水不會把它拉高
            IntVec3 cornerCell = new IntVec3(inner.minX, 0, inner.minZ);
            Color32[] corner = QuadAt(WaterMesh(map, cornerCell), cornerCell); // 角落可能在另一個區塊
            ctx.Require(corner != null, "corner cell has a quad");
            ctx.Check(Mathf.Abs(corner[0].r / 255f - 0.75f) < 0.02f, $"inner corner vertex is 75% shore ({corner[0].r})");
            ctx.Check(corner.All(c => Mathf.Abs(c.a / 255f - level) < 0.02f), "outside water does not bleed into the compartment's corner");

            // 抽乾後艙室內不應該有任何四邊形
            pump.parent.TryGetComp<CompFlickable>().DoFlick();
            WaitUntil dry = new WaitUntil(() => DepthRimFixture.Flooding.LevelAt(inner.CenterCell) <= 0.002f, 4000);
            yield return dry;
            ctx.Require(!dry.TimedOut, "compartment drained");
            yield return new WaitFrames(2);
            bool anyInside = inner.Cells.Any(c => QuadAt(WaterMesh(map, c), c) != null);
            ctx.Check(!anyInside, "a dry compartment has no water quads");

            // 外海層：艙室內部與它的牆都被裁掉；牆外緊鄰的格子往艙室淡出
            bool outerInside = ring.Cells.Any(c => QuadAt(OuterMesh(map, c), c) != null);
            ctx.Check(!outerInside, "the outer layer cuts out the compartment and its walls");
            // 牆外緊鄰的格子必須是「開放水域」才會被外海層蓋到——只有緊鄰艙室的岩體會例外，
            // 而這裡是牆外第二圈，不算緊鄰。清空區外圍在有些地圖上是岩體，所以沿牆往外找
            // 最近的開放水域，而不是寫死 ring.minZ - 1（那樣會因為地圖長相而整條測試 ABORT）。
            IntVec3 outside = IntVec3.Invalid;
            bool adjacentToWall = false;
            for (int d = 1; d <= 6 && !outside.IsValid; d++)
            {
                foreach (IntVec3 c in ring.ExpandedBy(d).EdgeCells)
                {
                    if (!c.InBounds(map) || ring.Contains(c) || c.Fogged(map) || !map.Flooding().IsOpenAt(c)) { continue; }
                    outside = c;
                    adjacentToWall = d == 1;
                    break;
                }
            }
            if (!outside.IsValid)
            {
                ctx.Note("no open water within 6 cells outside the wall on this map; the fade-out check is skipped");
            }
            else
            {
                Color32[] outer = QuadAt(OuterMesh(map, outside), outside);
                ctx.Require(outer != null, $"cell just outside the wall is covered by the outer layer ({outside})");
                if (adjacentToWall)
                {
                    ctx.Check(outer.Any(c => c.r > 0), $"outer water fades towards the compartment (r > 0 on the wall side, {outside})");
                }
                else
                {
                    ctx.Note($"nearest open water outside the wall is {outside}, not adjacent, so the fade-out check is skipped");
                }
                ctx.Check(outer.All(c => c.b == 0), $"explored cell has no fog weight ({outside})");
            }

            // 半淹狀態的截圖
            pump.parent.TryGetComp<CompFlickable>().DoFlick();
            Thing wall = walls.First(w => w.Position.z == ring.minZ && w.Position.x == inner.CenterCell.x);
            wall.HitPoints = wall.MaxHitPoints / 5;
            yield return Updates(4);
            CameraJumper.TryJump(new GlobalTargetInfo(inner.CenterCell, map));
            Find.CameraDriver.SetRootSize(9f);
            yield return new WaitFrames(30);
            yield return new WaitRealtime(() => false, 2f); // 切換地圖後等畫面真的畫出來再截圖
            ctx.Check(Find.CurrentMap == map, "camera is on the ocean map for the screenshot");
            ctx.Screenshot("compartment_leaking");
            // 截圖在之後的幀才擷取；測試一結束清理就會切回地表地圖，所以多等幾幀
            yield return new WaitFrames(10);
            Find.CameraDriver.SetRootSize(24f);
        }

        private static bool AllNeighborsFogged(IntVec3 c, Map map)
        {
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = c + GenAdj.AdjacentCells[i];
                if (n.InBounds(map) && !n.Fogged(map)) { return false; }
            }
            return true;
        }

        private static bool AnyNeighborFogged(IntVec3 c, Map map)
        {
            for (int i = 0; i < 8; i++)
            {
                IntVec3 n = c + GenAdj.AdjacentCells[i];
                if (n.InBounds(map) && n.Fogged(map)) { return true; }
            }
            return false;
        }

        /// <summary>迷霧之上的外海層：迷霧格一律覆蓋、不帶地形資訊；揭開迷霧後重建。</summary>
        public static IEnumerable<Wait> V3_OuterOverFog(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            int fogged = map.AllCells.Count(c => c.Fogged(map));
            ctx.Note($"fogged cells: {fogged} of {map.cellIndices.NumGridCells}");
            ctx.Require(fogged > 0, "ocean map has fog (rock interiors)");

            // 四周都是迷霧的格子：不論底下是岩體還是封閉洞穴，頂點都一樣（只有迷霧權重）
            List<IntVec3> deep = map.AllCells.Where(c => c.Fogged(map) && AllNeighborsFogged(c, map)).Take(2000).ToList();
            ctx.Require(deep.Count > 0, "found cells deep inside the fog");
            List<IntVec3> samples = new List<IntVec3>();
            IntVec3 rock = deep.FirstOrDefault(c => c.Filled(map));
            IntVec3 cave = deep.FirstOrDefault(c => !c.Filled(map));
            if (deep.Any(c => c.Filled(map))) { samples.Add(rock); }
            if (deep.Any(c => !c.Filled(map))) { samples.Add(cave); } else { ctx.Note("no fogged open cave cell; only rock checked"); }
            foreach (IntVec3 c in samples)
            {
                string kind = c.Filled(map) ? "rock" : "cave";
                Color32[] q = QuadAt(OuterMesh(map, c), c);
                ctx.Check(q != null, $"fogged {kind} cell is covered");
                if (q != null)
                {
                    ctx.Check(q.All(v => v.r == 0 && v.g == 0 && v.b == 255 && v.a == 0), $"fogged {kind} cell carries only fog weight, no light and no sky ({q[0]})");
                }
            }

            // 迷霧邊緣：迷霧權重介於 0 和 1 之間（柔和過渡）
            List<IntVec3> edges = map.AllCells.Where(c => !c.Fogged(map) && DepthRimFixture.Flooding.IsOpenAt(c) && AnyNeighborFogged(c, map)).Take(1).ToList();
            if (edges.Count > 0)
            {
                IntVec3 edge = edges[0];
                Color32[] q = QuadAt(OuterMesh(map, edge), edge);
                ctx.Check(q != null && q.Any(v => v.b > 0 && v.b < 255), "fog edge vertices blend between explored and fogged");
                Current.Game.CurrentMap = map;
                CameraJumper.TryJump(edge, map);
                Find.CameraDriver.SetRootSize(12f);
                yield return new WaitFrames(60);
                ctx.Screenshot("fog_edge");
            }
            else
            {
                ctx.Note("no explored open-water cell next to fog found");
            }

            // 揭開一塊迷霧後重建：那一格四周仍是迷霧，但本格已探索，頂點的迷霧權重不再是 255
            IntVec3 target = deep[0];
            map.fogGrid.Unfog(target);
            yield return new WaitFrames(5);
            Color32[] after = QuadAt(OuterMesh(map, target), target);
            ctx.Check(!target.Fogged(map), "target cell unfogged");
            if (after != null)
            {
                ctx.Check(after.All(v => v.b < 255), $"unfogged cell loses full fog weight ({after[0]})");
            }
            else
            {
                ctx.Note("unfogged cell is cut out of the outer layer (explored compartment or wall)");
            }
        }

        // ==================================================================
        //  實作指南 B / B2 / C / D / E 的驗收（V4–V8）
        // ==================================================================

        /// <summary>和外海層頂點、暗處提示完全同一個光照定義（含日光，見 FloodingMath.CombineLight）。</summary>
        private static float Lighting(Map map, IntVec3 c)
        {
            MapComponent_Flooding flooding = map.Flooding();
            WaterVisualTierDef def = flooding == null ? null : WaterVisualTierDef.For(flooding.RenderTier);
            return WaterLight.At(map, c, def?.ambientReach ?? 1f);
        }

        /// <summary>
        /// 沒有燈的地方會讀到的值＝日光 × 該層級的穿透率。白天開放水域本來就「有光」，
        /// 所以「暗」只能是相對於這個基準（見指南 D9）。
        /// </summary>
        private static float Ambient(Map map)
        {
            MapComponent_Flooding flooding = map.Flooding();
            WaterVisualTierDef def = flooding == null ? null : WaterVisualTierDef.For(flooding.RenderTier);
            return Mathf.Clamp01(map.skyManager.CurSkyGlow) * (def?.ambientReach ?? 1f);
        }

        private static SectionLayer_WaterOuter OuterLayer(Map map, IntVec3 cell)
            => (SectionLayer_WaterOuter)map.mapDrawer.SectionAt(cell).GetLayer(typeof(SectionLayer_WaterOuter));

        private static SectionLayer_WaterVolume VolumeLayer(Map map, IntVec3 cell)
            => (SectionLayer_WaterVolume)map.mapDrawer.SectionAt(cell).GetLayer(typeof(SectionLayer_WaterVolume));

        /// <summary>整張地圖沒有其他光源的開放水域格（立燈測試要有乾淨的起點）。白天有日光，所以「暗」是相對於 <see cref="Ambient"/>。</summary>
        private static List<IntVec3> DarkOpenWaterCells(Map map, MapComponent_Flooding flooding)
        {
            List<IntVec3> cells = new List<IntVec3>();
            // 「暗」一律即時重讀日光基準（Ambient）：V4 在黎明執行，CurSkyGlow 會在測試中途上升，
            // 快取一次基準會讓後面的斷言莫名其妙地紅（實測 0.067 → 0.079）。
            foreach (IntVec3 c in map.AllCells)
            {
                if (!c.Standable(map) || !flooding.IsOpenAt(c) || Lighting(map, c) > Ambient(map) + 0.01f)
                {
                    continue;
                }
                cells.Add(c);
            }
            return cells;
        }

        /// <summary>
        /// 找一個暗的開放水域格，<paramref name="neighbour"/> 是從它出發、落在另一個區塊的偏移（3 格），
        /// 用來驗證燈光跨區塊的重建（實作指南 B.1 第 4 點）。
        /// 偏移刻意選 3 格而不是更遠：`Verse.Glow.ComputeGlowGridsJob.SetGlowFromDist` 的強度是
        /// `Lerp(1 - d/R, 1/d², 0.4)`（R = 立燈的 12），而且 `d` 從燈自己的格子就開始算 1，
        /// 所以 11 格處是 `0.4/144 = 0.003` → `214 × 0.003` 取整後剛好是 0，量不到東西。
        /// </summary>
        private static IntVec3 FindDarkOpenWater(Map map, MapComponent_Flooding flooding, List<IntVec3> dark, out IntVec3 neighbour)
        {
            neighbour = IntVec3.Invalid;
            IntVec3 origin = DepthRimFixture.Shaft.exit.Position;
            IntVec3[] offsets =
            {
                new IntVec3(3, 0, 0), new IntVec3(-3, 0, 0), new IntVec3(0, 0, 3), new IntVec3(0, 0, -3)
            };
            foreach (IntVec3 c in dark.OrderBy(c => c.DistanceToSquared(origin)))
            {
                Section section = map.mapDrawer.SectionAt(c);
                foreach (IntVec3 offset in offsets)
                {
                    IntVec3 n = c + offset;
                    if (!n.InBounds(map) || map.mapDrawer.SectionAt(n) == section || !flooding.IsOpenAt(n))
                    {
                        continue;
                    }
                    // 兩格都必須本來就只有日光：附近 6 格內不能有別的光源（例如洞穴植物），量測才乾淨
                    bool clean = Lighting(map, n) <= Ambient(map) + 0.01f;
                    if (!clean) { continue; }
                    foreach (IntVec3 near in GenRadial.RadialCellsAround(c, 6f, true))
                    {
                        if (near.InBounds(map) && Lighting(map, near) > Ambient(map) + 0.01f) { clean = false; break; }
                    }
                    if (!clean) { continue; }
                    neighbour = offset;
                    return c;
                }
            }
            return IntVec3.Invalid;
        }

        private static Pawn SpawnPawnWithSuit(Map map, IntVec3 cell, out Apparel suit)
        {
            Pawn pawn = DepthRimFixture.SpawnColonist(map, cell);
            suit = (Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit));
            pawn.apparel.Wear(suit, dropReplacedApparel: false);
            return pawn;
        }

        /// <summary>
        /// 放一盞會亮的立燈。-quicktest 的殖民地沒有電網，而原版立燈要通電才會註冊進 <see cref="GlowGrid"/>
        /// （`CompGlower.ShouldBeLitNow` 會問 `CompPowerTrader.PowerOn`），所以旁邊補一截電線；
        /// 配合 `DebugSettings.unlimitedPower` 就有無限的發電量（F2 的排水泵也是靠同一招通電）。
        /// </summary>
        private static Thing SpawnLamp(Map map, IntVec3 cell)
        {
            Thing lamp = DepthRimFixture.Spawn(ThingDefOf.StandingLamp, cell, map);
            IntVec3 conduit = cell + IntVec3.North;
            if (!conduit.InBounds(map) || !conduit.Standable(map)) { conduit = cell + IntVec3.South; }
            if (!conduit.InBounds(map) || !conduit.Standable(map)) { conduit = cell; }
            DepthRimFixture.Spawn(ThingDefOf.PowerConduit, conduit, map);
            return lamp;
        }

        /// <summary>第一個符合條件的格子；找不到時回傳 <see cref="IntVec3.Invalid"/>（default(IntVec3) 是合法座標，不能拿來當哨兵）。</summary>
        private static IntVec3 Pick(List<IntVec3> cells, Func<IntVec3, bool> predicate)
        {
            foreach (IntVec3 c in cells)
            {
                if (predicate(c)) { return c; }
            }
            return IntVec3.Invalid;
        }

        /// <summary>V4（B.1、B.3、B.5）：光照寫入頂點、跨區塊重建、迷霧不洩漏、潛水服頭燈、效能。</summary>
        public static IEnumerable<Wait> V4_Lighting(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            Current.Game.CurrentMap = map;
            yield return Updates(1);

            // 白天淺海的日光就是 1.0（見指南 D9），燈的貢獻會被日光完全蓋掉、量不出來，
            // 所以這裡固定用海床（ambientReach 0.12）當「暗水」的背景，測完再切回自動。
            flooding.debugTierOverride = DepthTier.Seabed;
            flooding.MapComponentUpdate();
            yield return new WaitFrames(5);

            List<IntVec3> dark = DarkOpenWaterCells(map, flooding);
            ctx.Require(dark.Count > 20, $"found dark open water ({dark.Count} cells)");
            IntVec3 lampCell = FindDarkOpenWater(map, flooding, dark, out IntVec3 neighbourOffset);
            ctx.Require(lampCell.IsValid && neighbourOffset.IsValid, "found a dark cell whose lamp light crosses a section edge");
            IntVec3 neighbour = lampCell + neighbourOffset;
            CameraJumper.TryJump(lampCell, map);
            Find.CameraDriver.SetRootSize(24f);
            yield return new WaitFrames(20);

            // 白天開放水域本來就有日光（見指南 D9），所以「沒有燈」= 只讀到 Ambient
            float ambient = Ambient(map);
            ctx.Note($"daylight on the water at tier {flooding.RenderTier}: {ambient:0.000}");
            ctx.Check(Lighting(map, lampCell) <= Ambient(map) + 0.01f && Lighting(map, neighbour) <= Ambient(map) + 0.01f,
                $"both sample cells carry only daylight before the lamp ({Lighting(map, lampCell):0.000} / {Lighting(map, neighbour):0.000})");

            // 1. 立燈：燈下夠亮，遠處只剩日光
            SpawnLamp(map, lampCell);
            yield return Updates(1);
            yield return new WaitFrames(15);
            float atLamp = Lighting(map, lampCell);
            float atNeighbour = Lighting(map, neighbour);
            ctx.Check(atLamp >= 0.6f, $"B.5: the cell under the lamp reads at least 0.6 light ({atLamp:0.000})");
            ctx.Check(atNeighbour > Ambient(map) + 0.02f, $"the lamp light reaches 3 cells into the neighbouring section ({atNeighbour:0.000})");
            IntVec3 farCell = lampCell + new IntVec3(0, 0, 24);
            if (!farCell.InBounds(map)) { farCell = lampCell - new IntVec3(0, 0, 24); }
            if (farCell.InBounds(map))
            {
                float far = Lighting(map, farCell);
                ctx.Check(far <= Ambient(map) + 0.01f, $"B.5: 24 cells away from the lamp is back to daylight only ({far:0.000})");
            }

            SectionLayer_WaterOuter lampLayer = OuterLayer(map, lampCell);
            lampLayer.Regenerate();
            Color32[] lampQuad = QuadAt(lampLayer.subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0), lampCell);
            ctx.Require(lampQuad != null, "the lamp cell is covered by the outer layer");
            ctx.Check(lampQuad.Max(c => c.g) / 255f >= 0.5f, $"B.1: outer-layer vertices carry the lamp light ({lampQuad.Max(c => c.g) / 255f:0.000})");

            // 2. 燈照範圍跨到相鄰區塊：那個區塊自己重建過（不是靠這裡手動 Regenerate）
            Section neighbourSection = map.mapDrawer.SectionAt(neighbour);
            SectionLayer_WaterOuter neighbourLayer = (SectionLayer_WaterOuter)neighbourSection.GetLayer(typeof(SectionLayer_WaterOuter));
            bool pending = neighbourLayer.Dirty || (neighbourSection.dirtyFlags & (ulong)MapMeshFlagDefOf.GroundGlow) != 0;
            ctx.Check(!pending, "B.1: the neighbouring section is not left pending (the light triggered its rebuild)");
            Color32[] neighbourQuad = QuadAt(neighbourLayer.subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0), neighbour);
            ctx.Check(neighbourQuad != null, "the neighbouring section has a mesh at the lit cell");
            if (neighbourQuad != null)
            {
                float litVertex = neighbourQuad.Max(c => c.g) / 255f;
                ctx.Check(litVertex > 0.02f, $"B.1: the neighbouring section was rebuilt with the new light ({litVertex:0.000}, {neighbourQuad.Max(c => c.g)}/255)");
            }

            // 3. 迷霧格：即使底下有燈，頂點光照仍為 0
            IntVec3 fogged = IntVec3.Invalid;
            foreach (IntVec3 c in map.AllCells)
            {
                if (c.Fogged(map) && c.Standable(map) && SectionLayer_WaterOuter.Includes(c, map, flooding)) { fogged = c; break; }
            }
            if (!fogged.IsValid)
            {
                ctx.Note("no standable fogged cell found; the fog-under-lamp check is skipped (V3 still covers fogged vertices)");
            }
            else
            {
                SpawnLamp(map, fogged);
                yield return Updates(1);
                yield return new WaitFrames(10);
                ctx.Check(Lighting(map, fogged) > Ambient(map) + 0.1f, $"the lamp under the fog really lights the glow grid ({Lighting(map, fogged):0.000}, daylight {ambient:0.000})");
                Color32[] fogQuad = QuadAt(OuterMesh(map, fogged), fogged);
                ctx.Require(fogQuad != null, "the fogged cell is covered by the outer layer");
                ctx.Check(fogQuad.All(c => c.g == 0), "B.1: a fogged vertex carries no light even with a lamp under it");
            }

            // 4. 潛水服頭燈（B.3）
            IntVec3 suitCell = Pick(dark, c => c.DistanceTo(lampCell) > 25 && c.DistanceTo(fogged) > 25);
            ctx.Require(suitCell.IsValid, "found a dark cell for the diving suit");
            Pawn wearer = SpawnPawnWithSuit(map, suitCell, out Apparel suit);
            yield return new WaitTicks(SuitLampUpdater.Interval * 2 + 2);
            // 光照格是每「幀」重算的（GlowGrid.GlowGridUpdate_First），不是每 tick；
            // Ultrafast 下一次 WaitTicks 可能只跨一兩幀，所以要另外等幾幀才讀得到新光源。
            yield return new WaitFrames(3);
            CompSuitLamp lamp = suit.TryGetComp<CompSuitLamp>();
            ctx.Require(lamp != null, "the diving suit has the head lamp comp");
            ctx.Check(lamp.Lit, "the head lamp is registered while the wearer is in the water");
            // 走路中的 pawn 會在兩次掃描之間移動，而光照格又是每幀才算的（GlowGrid.GlowGridUpdate_First），
            // 所以不能拿「pawn 現在的格子」當探針——那樣量到的常常是落後一格的 0.345。
            // 以 Comp 真正註冊的格子為準，最多等 8 幀讓光照追上（見指南 §D D7）。
            IntVec3 lit = IntVec3.Invalid;
            for (int i = 0; i < 8; i++)
            {
                SuitLampUpdater.Update(map);
                lit = lamp.RegisteredCell;
                if (Lighting(map, lit) >= 0.5f) { break; }
                yield return new WaitFrames(1);
            }
            ctx.Check(Lighting(map, lit) >= 0.5f, $"B.5: the head lamp lights the wearer's cell (registered {lit}, light {Lighting(map, lit):0.000})");

            IntVec3 first = wearer.Position;
            // moved 也要離那兩盞立燈夠遠：否則「脫掉潛水服後變暗」會量到立燈的光而不是頭燈的殘光。
            IntVec3 moved = Pick(dark, c => c.DistanceTo(first) > 3 && c.DistanceTo(first) < 30
                && c.DistanceTo(lampCell) > 20 && c.DistanceTo(fogged) > 20);
            ctx.Require(moved.IsValid, "found a cell to move the suited colonist to");
            wearer.DeSpawn();
            GenSpawn.Spawn(wearer, moved, map, WipeMode.Vanish);
            yield return new WaitTicks(SuitLampUpdater.Interval * 2 + 2);
            IntVec3 litAfterMove = IntVec3.Invalid;
            for (int i = 0; i < 8; i++)
            {
                SuitLampUpdater.Update(map);
                litAfterMove = lamp.RegisteredCell;
                if (Lighting(map, litAfterMove) >= 0.5f) { break; }
                yield return new WaitFrames(1);
            }
            ctx.Check(Lighting(map, litAfterMove) >= 0.5f, $"the head lamp follows the wearer (registered {litAfterMove}, light {Lighting(map, litAfterMove):0.000})");
            ctx.Check(litAfterMove.DistanceTo(first) > 3, $"the head lamp really moved with the wearer ({litAfterMove} vs {first})");
            ctx.Check(Lighting(map, first) <= Ambient(map) + 0.01f, $"the cell the wearer left goes dark again ({Lighting(map, first):0.000})");

            suit.Destroy(DestroyMode.Vanish);
            yield return new WaitTicks(SuitLampUpdater.Interval * 2 + 2);
            yield return new WaitFrames(3);
            // 印出殘光值：這條從前失敗時沒有數字，只能猜是立燈還是頭燈沒關。
            // 探針用「最後註冊的格子」：pawn 可能還在走，但那才是燈真的待過的位置。
            IntVec3 wasLit = lamp.RegisteredCell;
            ctx.Check(Lighting(map, wasLit) <= Ambient(map) + 0.01f, $"B.5: taking the suit off removes the light (leftover {Lighting(map, wasLit):0.000}, daylight {ambient:0.000})");
            ctx.Check(!lamp.Lit, "the comp knows the lamp is off");

            // 5. 效能（B.5）
            SectionLayer_WaterOuter perf = OuterLayer(map, lampCell);
            perf.Regenerate();
            Stopwatch sw = Stopwatch.StartNew();
            const int runs = 5;
            for (int i = 0; i < runs; i++) { perf.Regenerate(); }
            sw.Stop();
            float ms = (float)(sw.Elapsed.TotalMilliseconds / runs);
            ctx.Note($"B.5: one outer-layer section rebuild = {ms:F3} ms (limit 2 ms)");
            ctx.Check(ms <= 2f, $"B.5: a single outer section rebuild stays under 2 ms ({ms:F3} ms)");

            // 10 名穿潛水服的殖民者：頭燈掃一次的成本（搬移會另外觸發光照與區塊重建）
            List<Pawn> wearers = new List<Pawn>();
            foreach (IntVec3 c in dark.OrderBy(c => c.DistanceToSquared(suitCell)))
            {
                if (wearers.Count >= 10) { break; }
                if (c.DistanceTo(suitCell) > 40 || c.DistanceTo(lampCell) < 14) { continue; }
                wearers.Add(SpawnPawnWithSuit(map, c, out _));
            }
            yield return new WaitTicks(SuitLampUpdater.Interval * 2 + 2);
            int litCount = 0;
            foreach (Pawn p in wearers) { if (Lighting(map, p.Position) >= 0.5f) { litCount++; } }
            ctx.Check(litCount == wearers.Count, $"every suited colonist in the water is lit ({litCount}/{wearers.Count})");
            SuitLampUpdater.Update(map);
            Stopwatch suitWatch = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) { SuitLampUpdater.Update(map); }
            suitWatch.Stop();
            float suitMs = (float)(suitWatch.Elapsed.TotalMilliseconds / 5.0);
            ctx.Note($"B.5: one head-lamp scan for {wearers.Count} suited colonist(s) = {suitMs:F3} ms");
            ctx.Check(suitMs <= 2f, $"B.5: scanning the head lamps stays cheap ({suitMs:F3} ms)");

            Current.Game.CurrentMap = map;
            CameraJumper.TryJump(lampCell, map);
            Find.CameraDriver.SetRootSize(10f);
            yield return new WaitFrames(30);
            ctx.Screenshot("lamp_in_water");
            CameraJumper.TryJump(wearer.Position, map);
            yield return new WaitFrames(30);
            ctx.Screenshot("suit_lamp");
            Find.CameraDriver.SetRootSize(24f);
            flooding.debugTierOverride = null;
            flooding.MapComponentUpdate();
            yield return new WaitFrames(3);
        }

        /// <summary>V9（P2-10）深淵地板：地形不畫、背景層有網格、離崖權重、水位固定 100%、Pawn 走不進去。</summary>
        public static IEnumerable<Wait> V9_Abyss(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            Current.Game.CurrentMap = map;
            yield return Updates(1);

            // 著色器證據：.Shader 是惰性解析（Waylith 載入 ShaderDef 時不編譯），讀它才會真的去找引擎著色器。
            // 只看 != DefaultShader 不夠——解析失敗會回一個 proxy，所以連名稱一起印出來比對。
            ShaderTypeDef abyssShaderDef = DefDatabase<ShaderTypeDef>.GetNamedSilentFail("DepthRim_Abyss");
            ctx.Require(abyssShaderDef != null, "P2-10: the abyss ShaderTypeDef exists");
            Shader abyssShader = abyssShaderDef.Shader;
            ctx.Require(abyssShader != null, "P2-10: the abyss shader resolved");
            ctx.Check(abyssShader.name != ShaderDatabase.DefaultShader.name && abyssShader != ShaderDatabase.DefaultShader,
                $"P2-10: the abyss shader is not the default fallback (name '{abyssShader.name}', default '{ShaderDatabase.DefaultShader.name}')");

            // 用暗層級，燈的貢獻才量得出來
            flooding.debugTierOverride = DepthTier.Seabed;
            flooding.MapComponentUpdate();
            yield return new WaitFrames(5);

            // 找一塊「整個 11x11 都是已探索、可站立的開放水域」的區域。只挑一格不算數：
            // 邊角可能落在岩體或迷霧裡，深淵就挖不滿（實測會變成 88/121，中央也就不再是中央）。
            IntVec3 origin = DepthRimFixture.Shaft.exit.Position;
            CellRect hole = default;
            bool haveHole = false;
            int best = int.MaxValue;
            for (int cx = 6; cx < map.Size.x - 6; cx += 3)
            {
                for (int cz = 6; cz < map.Size.z - 6; cz += 3)
                {
                    IntVec3 candidate = new IntVec3(cx, 0, cz);
                    int d = candidate.DistanceToSquared(origin);
                    if (d <= 400 || d >= best) { continue; }
                    CellRect rect = CellRect.CenteredOn(candidate, 5);
                    bool ok = true;
                    foreach (IntVec3 c in rect)
                    {
                        if (!c.InBounds(map) || c.Fogged(map) || !c.Standable(map) || !flooding.IsOpenAt(c))
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (!ok) { continue; }
                    best = d;
                    hole = rect;
                    haveHole = true;
                }
            }
            ctx.Require(haveHole, "found a clean 11x11 open-water area for the abyss");
            IntVec3 center = hole.CenterCell;

            int carved = 0;
            foreach (IntVec3 c in hole)
            {
                map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_Abyss);
                carved++;
            }
            ctx.Check(carved == 121, $"carved the whole 11x11 abyss at {center} ({carved} cells)");
            map.mapDrawer.WholeMapChanged(MapMeshFlagDefOf.Terrain);
            yield return new WaitFrames(5);
            flooding.MapComponentUpdate();

            ctx.Check(DepthRimDefOf.DepthRim_Abyss.dontRender, "P2-10: the terrain layer does not draw the abyss");
            ctx.Check(!center.Standable(map), "P2-10: pawns cannot stand in the abyss");
            ctx.Check(flooding.LevelAt(center) > 0.99f, $"P2-10: abyss water level stays full ({flooding.LevelAt(center):0.000})");
            // 計畫 §2.7.4：「外海層照常覆蓋，所以深淵上方也有一層水」。
            // 只驗水位不夠（水位是遊戲資料，不是渲染覆蓋），所以直接驗開放水域旗標。
            ctx.Check(flooding.IsOpenAt(center), "P2-10: the abyss still counts as open water (the outer layer covers it)");

            SectionLayer backdrop = map.mapDrawer.SectionAt(center).GetLayer(typeof(SectionLayer_AbyssBackdrop));
            ctx.Require(backdrop != null, "P2-10: the abyss backdrop layer exists");
            backdrop.Regenerate();
            ctx.Check(LiveMesh(backdrop)?.material?.shader == abyssShader,
                "P2-10: the backdrop mesh uses the abyss shader");
            Color32[] centreQuad = QuadAt(LiveMesh(backdrop), center);
            ctx.Require(centreQuad != null, $"P2-10: the abyss centre has a backdrop quad ({center})");

            // 崖邊那一格：離崖 1 格，權重應該比中央大
            IntVec3 rim = new IntVec3(hole.minX, 0, center.z);
            SectionLayer rimLayer = map.mapDrawer.SectionAt(rim).GetLayer(typeof(SectionLayer_AbyssBackdrop));
            rimLayer.Regenerate();
            Color32[] rimQuad = QuadAt(LiveMesh(rimLayer), rim);
            ctx.Require(rimQuad != null, $"P2-10: the rim cell has a backdrop quad ({rim})");
            byte rimWeight = rimQuad.Max(c => c.r);
            byte centreWeight = centreQuad.Max(c => c.r);
            ctx.Check(rimWeight > centreWeight,
                $"P2-10: the rim carries more weight than the centre ({rimWeight} vs {centreWeight})");
            ctx.Check(centreWeight == 0, $"P2-10: the centre is past the rim range ({centreWeight})");

            // 迷霧格不畫：深淵不該洩漏迷霧底下的資訊
            bool anyFogged = false;
            foreach (IntVec3 c in hole)
            {
                if (c.InBounds(map) && c.Fogged(map)) { anyFogged = true; break; }
            }
            ctx.Check(!anyFogged, "P2-10: the carved area was explored (no fog to leak)");

            // 計畫 §2.7.5 的完成指標：「放一盞燈在崖邊，只有崖壁附近變亮、深淵中央維持黑暗」。
            // 這張地圖是永夜（沒有日光），崖邊的海床原本也是暗的——燈是唯一能造出對比的光源，
            // 少了它就只會拍到「黑深淵 vs 黑海水」，前兩輪的截圖就是這樣看不出來。
            IntVec3 lampCell = rim + new IntVec3(-1, 0, 0);
            if (!lampCell.InBounds(map) || !lampCell.Standable(map))
            {
                lampCell = center + new IntVec3(-6, 0, 0);
            }
            if (lampCell.InBounds(map) && lampCell.Standable(map))
            {
                SpawnLamp(map, lampCell);
                yield return Updates(1);
                yield return new WaitFrames(15);
                backdrop.Regenerate();
                rimLayer.Regenerate();
                Color32[] rimLit = QuadAt(LiveMesh(rimLayer), rim);
                Color32[] centreDark = QuadAt(LiveMesh(backdrop), center);
                ctx.Require(rimLit != null && centreDark != null, "both abyss quads survive the lamp rebuild");
                ctx.Check(rimLit.Max(c => c.g) > centreDark.Max(c => c.g),
                    $"P2-10: the rim is lit while the centre stays dark ({rimLit.Max(c => c.g)} vs {centreDark.Max(c => c.g)})");
                // 計畫 §2.7.6 的嚴格版：「只有靠崖的那幾格光照值大於 0」。
                // 立燈半徑 12、中央離燈約 6 格，一定會有溢出，所以斷言用「中央不到崖邊一半」，
                // 並把期望值印出來——否則「整個深淵都發亮」也會通過。
                ctx.Check(centreDark.Max(c => c.g) * 2 < rimLit.Max(c => c.g),
                    $"P2-10: the centre is less than half the rim ({centreDark.Max(c => c.g)} vs {rimLit.Max(c => c.g)})");
                // 這行要印「純環境光」就必須只算天空光×深淵穿透率；
                // 直接拿 WaterLight.At 會含燈的溢出，標籤會說謊（實測就是 55 而不是 19）。
                float skyOnly = Mathf.Clamp01(map.skyManager.CurSkyGlow) * SectionLayer_AbyssBackdrop.AmbientReach;
                ctx.Note($"P2-10: ambient-only light at the centre = {FloodingMath.ToByte(skyOnly)}/255 (combined {centreDark.Max(c => c.g)}/255)");
            }
            else
            {
                ctx.Note($"no standable cell outside the abyss rim at {lampCell}; the lamp-at-rim check is skipped");
            }

            // 近景對準**崖邊**：讓 1..4 格的漸層帶與燈照亮的崖邊填滿畫面。
            // 照片時把微粒暫時關掉（著色器參數，不影響任何斷言），免得雜訊蓋住漸層。
            Material abyssMaterial = LiveMesh(backdrop)?.material;
            abyssMaterial?.SetFloat("_ParticleDensity", 0f);
            // 相機定位＋等待＋截圖交給 ScreenshotOn（P2-1 的輔助方法），
            // 但螢幕座標要在**相機定位之後**才印，所以先手動定位、印座標，再交給輔助方法截圖。
            CameraJumper.TryJump(rim, map);
            Find.CameraDriver.SetRootSize(10f);
            yield return new WaitFrames(30);
            // 把深淵在**畫面上的矩形與關鍵點**印出來：沒有這些座標，外部量測只能靠猜，
            // 很容易把畫面左側的 UI 或燈池當成崖邊（實際發生過一次，造成誤判 8.2×）。
            ReportAbyssScreenRect(ctx, hole, rim, center);
            ctx.Screenshot("abyss_rim");
            abyssMaterial?.SetFloat("_ParticleDensity", 0.35f);
            // 全景對準整塊深淵：燈在崖邊，深淵的黑在另一側
            foreach (Wait wait in ctx.ScreenshotOn(map, center, 26f, "abyss_wide", 25)) { yield return wait; }

            flooding.debugTierOverride = null;
            flooding.MapComponentUpdate();
            yield return new WaitFrames(3);
        }

        /// <summary>
        /// 把深淵在**畫面上**的位置印成 Note。外部像素量測需要精確座標：沒有它就只能靠掃描線猜，
        /// 很容易把左側 UI 邊框或燈池當成崖邊（實際發生過一次，造成誤判 8.2×）。
        /// 座標是 y 往上、原點在左下角，與 `Screen` 一致。
        /// </summary>
        private static void ReportAbyssScreenRect(TestContext ctx, CellRect hole, IntVec3 rim, IntVec3 center)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                ctx.Note("P2-10: no main camera; the screen rect is unavailable");
                return;
            }
            Vector3 min = cam.WorldToScreenPoint(new Vector3(hole.minX, 0f, hole.minZ));
            Vector3 max = cam.WorldToScreenPoint(new Vector3(hole.maxX + 1, 0f, hole.maxZ + 1));
            Vector3 rimPoint = cam.WorldToScreenPoint(new Vector3(rim.x + 0.5f, 0f, rim.z + 0.5f));
            Vector3 centrePoint = cam.WorldToScreenPoint(new Vector3(center.x + 0.5f, 0f, center.z + 0.5f));
            ctx.Note($"P2-10: screenshot {Screen.width}x{Screen.height}; abyss rect x {Mathf.RoundToInt(min.x)}..{Mathf.RoundToInt(max.x)}" +
                     $" y {Mathf.RoundToInt(min.y)}..{Mathf.RoundToInt(max.y)};" +
                     $" rim ({Mathf.RoundToInt(rimPoint.x)},{Mathf.RoundToInt(rimPoint.y)})" +
                     $" centre ({Mathf.RoundToInt(centrePoint.x)},{Mathf.RoundToInt(centrePoint.y)})");
        }

        /// <summary>
        /// V10（P2-8）效能壓力測試：一次大量物件時仍守住預算。
        /// 只量「重建／計算」的成本，不含 GPU 繪製——GPU 的部分由 V7 的 FPS 比較負責。
        /// </summary>
        public static IEnumerable<Wait> V10_Performance(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            Current.Game.CurrentMap = map;
            yield return Updates(1);

            // 1) 20 盞立燈，分佈在 20 個不同的區塊：受影響區塊的重建總時間 ≤ 20 ms
            List<IntVec3> dark = DarkOpenWaterCells(map, flooding);
            ctx.Require(dark.Count > 500, $"found dark water for the stress test ({dark.Count} cells)");
            List<IntVec3> lampCells = new List<IntVec3>();
            HashSet<Section> usedSections = new HashSet<Section>();
            foreach (IntVec3 c in dark)
            {
                if (lampCells.Count >= 20) { break; }
                Section section = map.mapDrawer.SectionAt(c);
                if (!usedSections.Add(section)) { continue; }
                lampCells.Add(c);
            }
            ctx.Require(lampCells.Count >= 20, $"picked 20 lamp cells in 20 different sections ({lampCells.Count})");
            foreach (IntVec3 c in lampCells) { SpawnLamp(map, c); }
            yield return Updates(1);
            yield return new WaitFrames(10);
            Stopwatch watch = Stopwatch.StartNew();
            foreach (IntVec3 c in lampCells) { OuterLayer(map, c).Regenerate(); }
            watch.Stop();
            float lampMs = (float)watch.Elapsed.TotalMilliseconds;
            ctx.Note($"P2-8: 20 lit outer-layer sections rebuilt in {lampMs:F2} ms (budget 20 ms)");
            ctx.Check(lampMs <= 20f, $"P2-8: 20 lamp-lit sections rebuild within 20 ms ({lampMs:F2} ms)");

            // 2) 30 名己方 Pawn 的暗處提示計算 ≤ 0.5 ms
            List<Pawn> pawns = new List<Pawn>();
            IntVec3 lampCell = lampCells[0];
            foreach (IntVec3 c in dark)
            {
                if (pawns.Count >= 30) { break; }
                if (c.DistanceTo(lampCell) < 8) { continue; }
                pawns.Add(DepthRimFixture.SpawnColonist(map, c));
            }
            ctx.Require(pawns.Count >= 30, $"spawned 30 pawns for the hint stress test ({pawns.Count})");
            yield return new WaitFrames(5);
            int hinted = 0;
            Stopwatch hintWatch = Stopwatch.StartNew();
            for (int i = 0; i < pawns.Count; i++)
            {
                if (MapComponent_DarkWaterHints.HintAlphaFor(pawns[i], flooding) > 0f) { hinted++; }
            }
            hintWatch.Stop();
            float hintMs = (float)hintWatch.Elapsed.TotalMilliseconds;
            ctx.Note($"P2-8: 30 pawn hint checks took {hintMs:F3} ms (budget 0.5 ms), {hinted} of them hinted");
            ctx.Check(hintMs <= 0.5f, $"P2-8: 30 pawn hint checks stay under 0.5 ms ({hintMs:F3} ms)");
            ctx.Check(hinted > 0, $"P2-8: the stress-test pawns really are in dark water ({hinted}/30 hinted)");

            // 3) 深淵背景：單一區塊重建 ≤ 1 ms
            IntVec3 abyssCentre = lampCells[lampCells.Count - 1] + new IntVec3(0, 0, 8);
            if (!abyssCentre.InBounds(map)) { abyssCentre = lampCells[lampCells.Count - 1] - new IntVec3(0, 0, 8); }
            CellRect hole = CellRect.CenteredOn(abyssCentre, 2); // 5x5
            int carved = 0;
            foreach (IntVec3 c in hole)
            {
                if (!c.InBounds(map)) { continue; }
                map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_Abyss);
                carved++;
            }
            ctx.Require(carved > 0, $"carved a 5x5 abyss for the backdrop timing ({carved} cells)");
            map.mapDrawer.WholeMapChanged(MapMeshFlagDefOf.Terrain);
            yield return new WaitFrames(3);
            SectionLayer backdrop = map.mapDrawer.SectionAt(abyssCentre).GetLayer(typeof(SectionLayer_AbyssBackdrop));
            ctx.Require(backdrop != null, "the abyss backdrop layer exists");
            backdrop.Regenerate();
            Stopwatch abyssWatch = Stopwatch.StartNew();
            const int abyssRuns = 5;
            for (int i = 0; i < abyssRuns; i++) { backdrop.Regenerate(); }
            abyssWatch.Stop();
            float abyssMs = (float)(abyssWatch.Elapsed.TotalMilliseconds / abyssRuns);
            ctx.Note($"P2-8: one abyss backdrop section rebuild = {abyssMs:F3} ms (budget 1 ms)");
            ctx.Check(abyssMs <= 1f, $"P2-8: one abyss backdrop section rebuild stays under 1 ms ({abyssMs:F3} ms)");
        }

        /// <summary>V5（B2）：暗處的己方提示、敵方沒有提示、開燈後消失、艙室裡不畫。</summary>
        public static IEnumerable<Wait> V5_DarkWaterHints(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            Current.Game.CurrentMap = map;
            yield return Updates(1);

            // 提示是給「暗水」用的：白天淺海有日光就不再是暗水（見指南 D9），
            // 所以這裡把層級固定成海床（ambientReach 0.12）才測得到提示。
            flooding.debugTierOverride = DepthTier.Seabed;
            flooding.MapComponentUpdate();
            yield return new WaitFrames(5);

            List<IntVec3> dark = DarkOpenWaterCells(map, flooding);
            ctx.Require(dark.Count > 10, $"found dark open water ({dark.Count} cells)");
            IntVec3 cell = dark.OrderBy(c => c.DistanceToSquared(DepthRimFixture.Shaft.exit.Position)).First();
            CameraJumper.TryJump(cell, map);
            Find.CameraDriver.SetRootSize(12f);
            yield return new WaitFrames(15);

            WaterVisualTierDef def = WaterVisualTierDef.For(flooding.RenderTier);
            ctx.Require(def != null, $"the {flooding.RenderTier} water visual tier Def exists");
            ctx.Note($"hint test runs at tier {flooding.RenderTier}, daylight on the water {Ambient(map):0.000}");
            float turbidity = FloodingMath.WaterAlpha(Lighting(map, cell), def.clearAlpha, def.murkAlpha,
                WaterTierMaterials.LightMin, WaterTierMaterials.LightMax);
            float expect = FloodingMath.HintAlpha(turbidity, MapComponent_DarkWaterHints.Threshold, MapComponent_DarkWaterHints.MaxAlpha);
            Pawn colonist = DepthRimFixture.SpawnColonist(map, cell);
            yield return new WaitFrames(3);
            float alpha = MapComponent_DarkWaterHints.HintAlphaFor(colonist, flooding);
            ctx.Check(alpha > 0.05f, $"B2: a colonist in dark water gets a hint ({alpha:0.00})");
            ctx.Check(Mathf.Abs(alpha - expect) < 0.02f, $"B2: the hint alpha uses the shader's turbidity ({alpha:0.00} vs {expect:0.00})");
            ctx.Check(alpha <= MapComponent_DarkWaterHints.MaxAlpha, "B2: never brighter than the maximum");

            // 敵方沒有提示
            Faction hostile = Find.FactionManager.AllFactionsListForReading
                .FirstOrDefault(f => !f.IsPlayer && !f.Hidden && f.HostileTo(Faction.OfPlayer));
            if (hostile == null)
            {
                ctx.Note("no hostile faction in this world; enemy check skipped");
            }
            else
            {
                Pawn enemy = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Villager, hostile,
                    forceGenerateNewPawn: true, canGeneratePawnRelations: false, mustBeCapableOfViolence: false, colonistRelationChanceFactor: 0f));
                IntVec3 enemyCell = cell + IntVec3.East;
                if (!enemyCell.Standable(map)) { enemyCell = cell + IntVec3.North; }
                GenSpawn.Spawn(enemy, enemyCell, map, WipeMode.Vanish);
                yield return new WaitFrames(3);
                ctx.Check(MapComponent_DarkWaterHints.HintAlphaFor(enemy, flooding) == 0f, "B2: an enemy in the same water gets no hint");
                enemy.Destroy(DestroyMode.Vanish);
            }

            // 開燈 → 提示消失
            SpawnLamp(map, cell);
            yield return Updates(1);
            yield return new WaitFrames(12);
            ctx.Check(Lighting(map, cell) > 0.6f, $"the lamp lights the colonist's cell ({Lighting(map, cell):0.000})");
            ctx.Check(MapComponent_DarkWaterHints.HintAlphaFor(colonist, flooding) == 0f, "B2: under a lamp the hint disappears");

            yield return new WaitFrames(20);
            ctx.Screenshot("dark_water_hint_off");
            Find.CameraDriver.SetRootSize(30f);
            yield return new WaitFrames(20);
            ctx.Screenshot("dark_water_hint_wide");
            Find.CameraDriver.SetRootSize(12f);
            flooding.debugTierOverride = null;
            flooding.MapComponentUpdate();
            yield return new WaitFrames(3);
        }

        /// <summary>V6（C.2、C.3、C.6）：每層級一份材質；除錯切換後整張地圖改用新材質。</summary>
        public static IEnumerable<Wait> V6_TierMaterials(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            Current.Game.CurrentMap = map;
            yield return Updates(1);

            ctx.Check(WaterVisualTierDef.AllTiersDefined(), "C.1: all four water visual tiers are defined");
            ctx.Check(flooding.DepthTier == DepthTier.Shallow && flooding.RenderTier == DepthTier.Shallow, "the ice ocean base renders as the shallows");
            IntVec3 cell = DepthRimFixture.OpenWaterCell();
            ctx.Require(cell.IsValid, "found an open-water cell");
            CameraJumper.TryJump(cell, map);
            Find.CameraDriver.SetRootSize(18f);
            yield return new WaitFrames(10);

            SectionLayer_WaterOuter outer = OuterLayer(map, cell);
            outer.Regenerate();
            ctx.Check(outer.subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0)?.material == WaterTierMaterials.Outer(DepthTier.Shallow), "C.2: open water uses the shallow outer material");
            Material shallow = WaterTierMaterials.Outer(DepthTier.Shallow);
            WaterVisualTierDef shallowDef = WaterVisualTierDef.For(DepthTier.Shallow);
            ctx.Check(Mathf.Abs(shallow.GetFloat("_ClearAlpha") - shallowDef.clearAlpha) < 0.001f, $"C.2: _ClearAlpha comes from the Def ({shallow.GetFloat("_ClearAlpha")})");
            ctx.Check(Mathf.Abs(shallow.GetFloat("_MurkAlpha") - shallowDef.murkAlpha) < 0.001f, $"C.2: _MurkAlpha comes from the Def ({shallow.GetFloat("_MurkAlpha")})");
            ctx.Check(Mathf.Abs(shallow.GetFloat("_RippleStrength") - shallowDef.rippleStrength) < 0.001f, "C.2: _RippleStrength comes from the Def");
            ctx.Check(Mathf.Abs(shallow.GetFloat("_LightMin") - WaterTierMaterials.LightMin) < 0.001f, "C.2: _LightMin is the shared constant");

            // 除錯切換到深海：整張地圖標記重建，材質跟著換
            flooding.debugTierOverride = DepthTier.Deep;
            // 層級變化是 MapComponentUpdate 每幀偵測的；這裡直接叫一次，才不會和 mapDrawer
            // （它一重建完就把 dirtyFlags 清掉）賽跑，否則「整張地圖被標記」永遠讀不到。
            flooding.MapComponentUpdate();
            Section far = map.mapDrawer.SectionAt(new IntVec3(map.Size.x / 2, 0, map.Size.z / 2));
            ctx.Check((far.dirtyFlags & (ulong)DepthRimDefOf.DepthRim_Water) != 0, "C.6: switching the tier marks the whole map for rebuild");
            ctx.Check(flooding.RenderTier == DepthTier.Deep, "C.6: the debug override changes the render tier");
            yield return new WaitFrames(5);
            cameraFollow(cell, map);
            outer.Regenerate();
            ctx.Check(outer.subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0)?.material == WaterTierMaterials.Outer(DepthTier.Deep), "C.6: after the override the section uses the deep material");
            Material deep = WaterTierMaterials.Outer(DepthTier.Deep);
            WaterVisualTierDef deepDef = WaterVisualTierDef.For(DepthTier.Deep);
            ctx.Check(Mathf.Abs(deep.GetFloat("_MurkAlpha") - deepDef.murkAlpha) < 0.001f, $"C.5: deep _MurkAlpha ({deep.GetFloat("_MurkAlpha")})");
            ctx.Check(deep.GetFloat("_RippleStrength") == 0f, "C.5: deep sea skips the surface ripples");
            ctx.Check(ColorEqual(deep.color, deepDef.waterColor), $"C.5: deep sea water colour ({deep.color})");

            int sampled = 0;
            int matched = 0;
            int empty = 0;
            List<string> bad = new List<string>();
            foreach (IntVec3 c in map.AllCells)
            {
                if (!flooding.IsOpenAt(c) || c.Fogged(map)) { continue; }
                SectionLayer layer = map.mapDrawer.SectionAt(c).GetLayer(typeof(SectionLayer_WaterOuter));
                layer.Regenerate();
                LayerSubMesh live = layer.subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0);
                // 這一塊本來就沒畫水（例如整塊被岩體或艙室吃掉）：沒有水材質可言，不列入
                if (live == null) { empty++; continue; }
                // 注意順序：先看夠了沒再計數。從前是 sampled++ 之後才 break，
                // 於是第 41 塊「被算進 sampled 卻沒被檢查」，永遠呈現 40/41 的假失敗。
                if (sampled >= 40) { break; }
                sampled++;
                if (live.material == WaterTierMaterials.Outer(DepthTier.Deep)) { matched++; continue; }
                if (bad.Count < 3)
                {
                    string dump = string.Join(", ", layer.subMeshes
                        .Select(sm => $"{Label(sm.material)}({(sm.verts == null ? 0 : sm.verts.Count)})").ToArray());
                    bad.Add($"cell {c}: live={Label(live.material)}, submeshes=[{dump}]");
                }
            }
            ctx.Check(sampled > 0 && matched == sampled,
                $"C.6: every sampled open-water section uses the deep material ({matched}/{sampled}, {empty} without water geometry)"
                + (bad.Count == 0 ? "" : " — " + string.Join(" | ", bad.ToArray())));
            yield return new WaitFrames(20);
            ctx.Screenshot("tier_deep_override");

            // 切回自動
            flooding.debugTierOverride = null;
            yield return new WaitFrames(5);
            outer.Regenerate();
            ctx.Check(outer.subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0)?.material == WaterTierMaterials.Outer(DepthTier.Shallow), "auto puts the shallow material back");
            ctx.Check(flooding.RenderTier == flooding.DepthTier, "auto follows the map tier again");
        }

        private static bool ColorEqual(Color a, Color b)
            => Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

        /// <summary>把材質講成人看得懂的名字（材質本身沒有設 name，失敗訊息才看得出是哪一層級的）。</summary>
        private static string Label(Material m)
        {
            if (m == null) { return "null"; }
            foreach (DepthTier t in Enum.GetValues(typeof(DepthTier)))
            {
                if (m == WaterTierMaterials.Outer(t)) { return "outer-" + t; }
                if (m == WaterTierMaterials.Volume(t)) { return "volume-" + t; }
            }
            return string.IsNullOrEmpty(m.name) ? "other" : m.name;
        }

        private static void cameraFollow(IntVec3 cell, Map map)
        {
            if (Find.CurrentMap != map) { Current.Game.CurrentMap = map; }
            CameraJumper.TryJump(cell, map);
        }

        /// <summary>V7（D.5）：四個層級各拍近景與全景，並記錄每個層級的 FPS。截圖取「已探索」的開放水域。</summary>
        public static IEnumerable<Wait> V7_TierScreenshots(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            Current.Game.CurrentMap = map;
            yield return Updates(1);

            IntVec3 origin = DepthRimFixture.Shaft.exit.Position;
            IntVec3 cell = IntVec3.Invalid;
            int best = int.MaxValue;
            foreach (IntVec3 c in map.AllCells)
            {
                if (!c.Standable(map) || c.Fogged(map) || !flooding.IsOpenAt(c)) { continue; }
                int d = c.DistanceToSquared(origin);
                if (d < best) { best = d; cell = c; }
            }
            ctx.Require(cell.IsValid, $"found an explored open-water cell ({cell})");
            CameraJumper.TryJump(cell, map);
            // 一張近景要有的燈（比較「開燈看得到懸浮物」與「關燈只剩生物光」）
            IntVec3 lamp = cell + new IntVec3(2, 0, 0);
            if (lamp.InBounds(map) && lamp.Standable(map)) { SpawnLamp(map, lamp); }
            yield return new WaitFrames(20);

            float shallowFps = 0f;
            float deepFps = 0f;
            foreach (DepthTier tier in Enum.GetValues(typeof(DepthTier)))
            {
                flooding.debugTierOverride = tier;
                map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
                Find.CameraDriver.SetRootSize(9f);
                yield return new WaitFrames(35);
                int frames = Time.frameCount;
                float started = Time.realtimeSinceStartup;
                yield return new WaitRealtime(() => false, 1.0f);
                float fps = (Time.frameCount - frames) / Mathf.Max(0.001f, Time.realtimeSinceStartup - started);
                ctx.Note($"{tier}: {fps:F1} fps at camera size 9");
                if (tier == DepthTier.Shallow) { shallowFps = fps; }
                if (tier == DepthTier.Deep) { deepFps = fps; }
                ctx.Screenshot("tier_" + tier + "_near");
                Find.CameraDriver.SetRootSize(38f);
                yield return new WaitFrames(25);
                ctx.Screenshot("tier_" + tier + "_wide");
            }
            flooding.debugTierOverride = null;
            Find.CameraDriver.SetRootSize(24f);
            if (shallowFps > 0f)
            {
                float ratio = deepFps / shallowFps;
                ctx.Note($"D.3: deep sea runs at {ratio:P0} of the shown fps of the shallows (target: no more than 10% slower)");
                // 幀率在這個環境（無 vsync、Ultrafast 模擬）本身就有 ±20% 的雜訊，所以只擋明顯的退化
                ctx.Check(ratio >= 0.8f, $"D.3: the deep sea does not cost a large amount of frame rate ({ratio:P0})");
                ctx.Check(ratio >= 0.9f, $"D.3: deep sea within 10% of the shallows ({ratio:P0}; see the note for the raw numbers)");
            }
            else
            {
                ctx.Note("could not measure the shallows fps");
            }
        }

        /// <summary>V8（E.3）：半淹的艙室在四個層級下顏色不同，而且都沒有海洋雪或生物光。</summary>
        public static IEnumerable<Wait> V8_CompartmentTiers(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            CellRect ring = DepthRimFixture.ClearOceanArea(7);
            DepthRimFixture.BuildWallRing(ring);
            CellRect inner = ring.ContractedBy(1);
            yield return Updates(1);

            DepthRimFixture.Spawn(ThingDefOf.PowerConduit, inner.CenterCell + IntVec3.North, map);
            CompFloodPump pump = DepthRimFixture.Spawn(DepthRimFixture.BilgePump, inner.CenterCell, map).TryGetComp<CompFloodPump>();
            WaitUntil half = new WaitUntil(() => DepthRimFixture.Flooding.LevelAt(inner.CenterCell) <= 0.6f, 4000);
            yield return half;
            ctx.Require(!half.TimedOut, "compartment pumped down to about half");
            pump.parent.TryGetComp<CompFlickable>().DoFlick();
            yield return Updates(1);

            Current.Game.CurrentMap = map;
            CameraJumper.TryJump(inner.CenterCell, map);
            Find.CameraDriver.SetRootSize(11f);
            yield return new WaitFrames(10);

            SectionLayer_WaterVolume volume = VolumeLayer(map, inner.CenterCell);
            Material shallowVolume = WaterTierMaterials.Volume(DepthTier.Shallow);
            // 艙室層的 D 階段效果由 Def 的 compartmentEffects 決定（二期 P2-6）。
            // 生物光與霧氣在艙室著色器裡沒有函式，所以那兩個屬性不該存在；
            // 海洋雪有函式，但密度由 Def 決定（沒開啟就是 0）。
            ctx.Check(!shallowVolume.HasProperty("_BiolumDensity"), "E.1: the compartment shader has no bioluminescence");
            ctx.Check(!shallowVolume.HasProperty("_HazeStrength"), "E.1: the compartment shader has no sediment haze");
            ctx.Check(shallowVolume.HasProperty("_SnowDensity"), "P2-6: the compartment shader supports marine snow");
            ctx.Check(WaterTierMaterials.Volume(DepthTier.Shallow).GetFloat("_SnowDensity") == 0f, "P2-6: the shallows leave compartment snow off");
            ctx.Check(WaterTierMaterials.Volume(DepthTier.Thermocline).GetFloat("_SnowDensity") == 0f, "P2-6: the thermocline leaves compartment snow off");
            ctx.Check(WaterTierMaterials.Volume(DepthTier.Deep).GetFloat("_SnowDensity") > 0f, "P2-6: the deep sea shows compartment snow");
            ctx.Check(WaterTierMaterials.Volume(DepthTier.Seabed).GetFloat("_SnowDensity") > 0f, "P2-6: the seabed shows compartment snow");

            foreach (DepthTier tier in Enum.GetValues(typeof(DepthTier)))
            {
                flooding.debugTierOverride = tier;
                map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
                yield return new WaitFrames(8);
                volume.Regenerate();
                LayerSubMesh sm = volume.subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0);
                ctx.Require(sm != null, $"{tier}: the half-flooded compartment still has water");
                WaterVisualTierDef def = WaterVisualTierDef.For(tier);
                ctx.Check(sm.material == WaterTierMaterials.Volume(tier), $"E.1: the compartment layer uses the {tier} material");
                ctx.Check(ColorEqual(sm.material.color, def.waterColor), $"E.1: {tier} compartment water colour ({sm.material.color})");
                ctx.Check(Mathf.Abs(sm.material.GetFloat("_RippleScale") - def.rippleScale) < 0.001f, $"E.1: {tier} compartment ripple scale comes from the Def");
                ctx.Check(Mathf.Abs(sm.material.GetFloat("_MurkAlpha") - def.murkAlpha) < 0.001f, $"E.1: {tier} compartment murkiness comes from the Def");
                yield return new WaitFrames(15);
                ctx.Screenshot("compartment_tier_" + tier);
            }
            flooding.debugTierOverride = null;
            Find.CameraDriver.SetRootSize(24f);
        }
    }
}
