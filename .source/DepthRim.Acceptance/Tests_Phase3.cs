using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DepthRim.Acceptance
{
    /// <summary>
    /// 使用者回報的問題（2026-11-01）：長時間遊玩後外海層出現整塊 17×17 的明暗差（V14）、
    /// 沒有潛水能力的殖民者會走進水裡（水密門，V15）、進水點看不出來、半淹的艙室不明顯（V14／V15）。
    /// </summary>
    public static class Tests_Phase3
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        private static Color32[] QuadAt(SectionLayer layer, IntVec3 cell)
        {
            LayerSubMesh sm = layer?.subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0);
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

        private static Color32[] AllColors(SectionLayer layer)
        {
            LayerSubMesh sm = layer?.subMeshes.FirstOrDefault(s => s.verts != null && s.verts.Count > 0);
            return sm == null ? new Color32[0] : sm.colors.ToArray();
        }

        private static void SetRoomLevel(Map map, Room room, float level)
        {
            byte[] arr = HarmonyLib.Traverse.Create(DepthRimFixture.Flooding).Field("levels").GetValue<byte[]>();
            byte b = FloodingMath.ToByte(level);
            foreach (IntVec3 c in room.Cells) { arr[map.cellIndices.CellToIndex(c)] = b; }
            map.mapDrawer.MapMeshDirty(room.Cells.First(), DepthRimDefOf.DepthRim_Water);
        }

        /// <summary>
        /// V14：外海層的頂點不含日光（日光由材質的 <c>_SkyLight</c> 每幀乘上），所以同一個區塊在白天重建和在晚上重建，
        /// 網格完全一樣——不會再出現「不同時間重建的區塊明暗不一」。另外檢查半淹艙室的可見度參數，並拍一張有燈的半淹艙室。
        /// </summary>
        public static IEnumerable<Wait> V14_SkyLightNotBaked(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            CellRect ring = DepthRimFixture.ClearOceanArea(7);
            DepthRimFixture.BuildWallRing(ring);
            CellRect inner = ring.ContractedBy(1);
            Room room = inner.CenterCell.GetRoom(map);
            ctx.Require(room != null && !flooding.IsOpenWater(room), "sealed compartment");
            yield return Updates(1);

            // 牆圈外圍（清出來的）開放水域格；洞穴地圖可能有天然岩頂，所以把它和四周的屋頂拿掉（只動牆圈外，不影響艙室）
            // 拿掉屋頂後那一區就有露天格，依定義變成開放水域（不要求它事先就是開放水域：清出來的區域可能被岩體圍住）
            IntVec3 sky = ring.ExpandedBy(2).EdgeCells.FirstOrDefault(c => c.InBounds(map) && !c.Fogged(map) && c.Standable(map)
                && GenAdj.AdjacentCellsAndInside.All(d => (c + d).InBounds(map) && !ring.Contains(c + d)));
            if (sky.IsValid && sky != IntVec3.Zero)
            {
                foreach (IntVec3 d in GenAdj.AdjacentCellsAndInside) { map.roofGrid.SetRoof(sky + d, null); }
                yield return Updates(1);
            }
            ctx.Require(sky.IsValid && sky != IntVec3.Zero && flooding.IsOpenAt(sky), "an unroofed open-water cell with an unroofed neighbourhood");
            SectionLayer outer = map.mapDrawer.SectionAt(sky).GetLayer(typeof(SectionLayer_WaterOuter));

            float realGlow = map.skyManager.CurSkyGlow;
            map.skyManager.ForceSetCurSkyGlow(1f);
            outer.Regenerate();
            Color32[] day = AllColors(outer);
            Color32[] dayQuad = QuadAt(outer, sky);
            WaterTierMaterials.UpdateSkyLight(map);
            float daySky = WaterTierMaterials.Outer(flooding.RenderTier).GetFloat("_SkyLight");

            map.skyManager.ForceSetCurSkyGlow(0f);
            outer.Regenerate();
            Color32[] night = AllColors(outer);
            WaterTierMaterials.UpdateSkyLight(map);
            float nightSky = WaterTierMaterials.Outer(flooding.RenderTier).GetFloat("_SkyLight");
            map.skyManager.ForceSetCurSkyGlow(realGlow);

            ctx.Require(day.Length > 0, $"outer section has vertices ({day.Length})");
            ctx.Check(day.Length == night.Length && day.Zip(night, (a, b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a).All(x => x),
                "the outer mesh is identical whether it was rebuilt at noon or at midnight (sky light is not baked)");
            ctx.Require(dayQuad != null, "the unroofed cell has an outer quad");
            ctx.Check(dayQuad.All(c => c.a == 255), $"unroofed cell: vertex alpha = full sky exposure ({string.Join(",", dayQuad.Select(c => c.a))})");
            // 左下頂點共用的四格（左下、下、左、本格）：g 必須等於這四格的人造光平均，和日光無關
            IntVec3[] corner0 = { sky + new IntVec3(-1, 0, -1), sky + new IntVec3(0, 0, -1), sky + new IntVec3(-1, 0, 0), sky };
            float[] art = corner0.Where(c => c.InBounds(map)).Select(c => c.Fogged(map) ? 0f : WaterLight.Artificial(map, c)).ToArray();
            byte expectG = FloodingMath.ToByte(FloodingMath.AverageLight(art, art.Length));
            ctx.Check(Mathf.Abs(dayQuad[0].g - expectG) <= 2, $"vertex g is the artificial light only ({dayQuad[0].g} vs {expectG}), even at full daylight");
            float reach = WaterVisualTierDef.For(flooding.RenderTier).ambientReach * DepthRimModSettings.Current.ambientScale;
            ctx.Check(Mathf.Abs(daySky - Mathf.Clamp01(reach)) < 0.01f, $"_SkyLight follows the sky at noon ({daySky:0.000} = reach {reach:0.000})");
            ctx.Check(nightSky == 0f, $"_SkyLight is 0 at midnight ({nightSky:0.000})");

            IntVec3 roofed = map.AllCells.FirstOrDefault(c => c.Roofed(map) && !c.Fogged(map) && flooding.IsOpenAt(c) && GenAdj.AdjacentCellsAndInside.All(d => (c + d).InBounds(map) && (c + d).Roofed(map)));
            if (roofed.IsValid && roofed != IntVec3.Zero)
            {
                SectionLayer roofedLayer = map.mapDrawer.SectionAt(roofed).GetLayer(typeof(SectionLayer_WaterOuter));
                roofedLayer.Regenerate();
                Color32[] q = QuadAt(roofedLayer, roofed);
                ctx.Check(q != null && q.All(c => c.a == 0), "roofed open water (a cave): no sky exposure");
            }
            else
            {
                ctx.Note("no fully roofed, explored open-water cell; cave sky check skipped");
            }

            // 半淹艙室的可見度：材質帶有最低覆蓋與水線泡沫
            Material vol = WaterTierMaterials.Volume(flooding.RenderTier);
            ctx.Check(vol.GetFloat("_MinAlpha") >= 0.4f, $"compartment material has a minimum opacity ({vol.GetFloat("_MinAlpha"):0.00})");
            ctx.Check(vol.GetFloat("_FoamStrength") > 0f, $"compartment material has a waterline foam ({vol.GetFloat("_FoamStrength"):0.00})");

            SetRoomLevel(map, room, 0.5f);
            Thing lamp = DepthRimFixture.Spawn(ThingDefOf.StandingLamp, inner.CenterCell + IntVec3.West, map);
            DepthRimFixture.Spawn(ThingDefOf.PowerConduit, inner.CenterCell + IntVec3.West + IntVec3.North, map);
            yield return Updates(1);
            yield return new WaitFrames(10);
            SectionLayer volLayer = map.mapDrawer.SectionAt(inner.CenterCell).GetLayer(typeof(SectionLayer_WaterVolume));
            volLayer.Regenerate();
            Color32[] vq = QuadAt(volLayer, inner.CenterCell + IntVec3.East);
            ctx.Note($"half-flooded compartment: level {flooding.LevelAt(inner.CenterCell):P0}, volume quad {(vq == null ? "none" : string.Join(",", vq.Select(c => $"({c.r},{c.g},{c.b},{c.a})")))}, visible={volLayer.Visible}, submeshes={volLayer.subMeshes.Count(m => m.verts.Count > 0)}");
            ctx.Check(vq != null && vq.All(c => Mathf.Abs(c.a / 255f - 0.5f) < 0.05f), "the half-flooded compartment has water quads at 50%");
            // 北牆正面的水帶：牆格南段，高度 = 正面高度 × 水位，頂邊帶水線標記
            IntVec3 northWall = new IntVec3(inner.CenterCell.x, 0, inner.maxZ + 1);
            SectionLayer faceLayer = map.mapDrawer.SectionAt(inner.CenterCell + new IntVec3(0, 0, inner.maxZ - inner.CenterCell.z)).GetLayer(typeof(SectionLayer_WaterVolume));
            faceLayer.Regenerate();
            LayerSubMesh faceMesh = faceLayer.subMeshes.FirstOrDefault(m => m.verts.Count > 0);
            int face = -1;
            for (int i = 0; faceMesh != null && i + 3 < faceMesh.verts.Count; i += 4)
            {
                if (Mathf.RoundToInt(faceMesh.verts[i].x) == northWall.x && Mathf.Abs(faceMesh.verts[i].z - northWall.z) < 0.001f && faceMesh.colors[i].b == SectionLayer_WaterVolume.FaceBottomB)
                {
                    face = i;
                    break;
                }
            }
            ctx.Require(face >= 0, "the north wall's front face has a water band");
            float bandHeight = faceMesh.verts[face + 1].z - faceMesh.verts[face].z;
            float expectBand = SectionLayer_WaterVolume.WallFaceDepth * flooding.LevelAt(inner.CenterCell);
            ctx.Check(Mathf.Abs(bandHeight - expectBand) < 0.01f, $"band height follows the water level ({bandHeight:0.000} vs {expectBand:0.000})");
            ctx.Check(faceMesh.colors[face + 1].b == SectionLayer_WaterVolume.FaceTopB && faceMesh.colors[face + 2].b == SectionLayer_WaterVolume.FaceTopB, "the band's top edge is marked as the waterline");
            IntVec3 southWall = new IntVec3(inner.CenterCell.x, 0, inner.minZ - 1);
            bool southBand = false;
            for (int i = 0; faceMesh != null && i + 3 < faceMesh.verts.Count; i += 4)
            {
                if (Mathf.RoundToInt(faceMesh.verts[i].x) == southWall.x && Mathf.Abs(faceMesh.verts[i].z - southWall.z) < 0.001f && faceMesh.colors[i].b != 0) { southBand = true; }
            }
            ctx.Check(!southBand, "no band on the south wall (its face is not visible from inside)");

            LayerSubMesh live = volLayer.subMeshes.FirstOrDefault(m => m.verts.Count > 0);
            Material want = WaterTierMaterials.Volume(flooding.RenderTier);
            ctx.Note($"volume submesh material {(live?.material == null ? "null" : live.material.name)} alive={(live?.material != null)} same={ReferenceEquals(live?.material, want)} minAlpha={(live?.material != null ? live.material.GetFloat("_MinAlpha") : -1f):0.00} murk={(live?.material != null ? live.material.GetFloat("_MurkAlpha") : -1f):0.00} clear={(live?.material != null ? live.material.GetFloat("_ClearAlpha") : -1f):0.00} tier={flooding.RenderTier} override={flooding.debugTierOverride}");
            ctx.Check(ReferenceEquals(live?.material, want), "the compartment is drawn with the current volume material");
            foreach (Wait w in ctx.ScreenshotOn(map, inner.CenterCell, 9f, "half_flooded_lit", 40)) { yield return w; }
            yield return new WaitFrames(20);
            ctx.Check(lamp.Spawned, "lamp stays spawned for the screenshot");
        }

        /// <summary>
        /// V15：水密門擋住「乾處、沒有潛水能力、沒被徵召」的殖民者走進淹水的一側（含原版可達性），
        /// 徵召、穿潛水服、已經在水裡的人可以通過；受損的水密門滲水較少，進水點會噴出水花。
        /// </summary>
        public static IEnumerable<Wait> V15_WatertightDoorAndLeaks(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            CellRect ring = DepthRimFixture.ClearOceanArea(7);
            List<Thing> walls = DepthRimFixture.BuildWallRing(ring);
            CellRect inner = ring.ContractedBy(1);
            IntVec3 doorCell = new IntVec3(inner.CenterCell.x, 0, ring.minZ);
            walls.First(w => w.Position == doorCell).Destroy(DestroyMode.Vanish);
            Building_WatertightDoor door = (Building_WatertightDoor)DepthRimFixture.Spawn(DepthRimDefOf.DepthRim_WatertightDoor, doorCell, map, null, Faction.OfPlayer);
            Room room = inner.CenterCell.GetRoom(map);
            ctx.Require(room != null && !flooding.IsOpenWater(room), "compartment with a watertight door is sealed");
            SetRoomLevel(map, room, 0f);
            IntVec3 outside = doorCell + IntVec3.South;
            ctx.Require(flooding.IsOpenAt(outside) || outside.GetRoom(map) != room, "the door leads outside");

            ctx.Check(door.FloodedSide, $"a newly built door knows its flooded side at once ({door.MaxSideLevel():P0})");
            yield return Updates(1);
            SetRoomLevel(map, room, 0f);
            ctx.Check(door.FloodedSide, $"door still sees the flooded side after a water update ({door.MaxSideLevel():P0})");
            // 殖民者在門狀態確定之後才放進去，而且立刻檢查（不讓它有時間閒逛）
            Pawn dry = DepthRimFixture.SpawnColonist(map, inner.CenterCell);
            dry.drafter.Drafted = false;
            ctx.Check(!door.PawnCanOpen(dry), "a dry colonist without a suit will not open it");
            ctx.Check(!map.reachability.CanReach(dry.Position, outside, PathEndMode.OnCell, TraverseParms.For(dry)),
                "vanilla reachability: the dry colonist cannot path into the water");
            ctx.Check(map.reachability.CanReach(dry.Position, inner.CenterCell + IntVec3.North, PathEndMode.OnCell, TraverseParms.For(dry)),
                "the colonist can still move around inside");

            dry.drafter.Drafted = true;
            ctx.Check(door.PawnCanOpen(dry), "drafted: the player's order wins");
            dry.drafter.Drafted = false;

            Pawn diver = DepthRimFixture.SpawnColonist(map, inner.CenterCell + IntVec3.East);
            diver.drafter.Drafted = false;
            Apparel suit = (Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit));
            diver.apparel.Wear(suit, dropReplacedApparel: false);
            ctx.Check(FloodingPawnEffects.CanBreatheUnderwater(diver), "the diver can breathe underwater");
            ctx.Check(door.PawnCanOpen(diver), "a colonist with a diving suit may go out");
            map.reachability.ClearCache();
            ctx.Check(map.reachability.CanReach(diver.Position, outside, PathEndMode.OnCell, TraverseParms.For(diver)), "vanilla reachability: the diver can path outside");

            Pawn swimmer = DepthRimFixture.SpawnColonist(map, outside);
            swimmer.drafter.Drafted = false;
            ctx.Check(door.PawnCanOpen(swimmer), "a colonist already in the water may come back in");

            ctx.Check(!door.Notify_WaterUpdated(), "no state change while nothing changes");
            string inspect = door.GetInspectString();
            ctx.Check(inspect.Contains(door.MaxSideLevel().ToStringPercent()), $"inspect string shows the flooded side ({inspect.Replace('\n', ' ')})");

            // 受損：滲水且有水花
            door.HitPoints = Mathf.Max(1, door.MaxHitPoints / 10);
            Current.Game.CurrentMap = map;
            Find.CameraDriver.JumpToCurrentMapLoc(doorCell);
            yield return Updates(1);
            ctx.Check(flooding.InflowCells.Contains(doorCell), "the damaged watertight door is an inflow cell");
            int before = flooding.LeakEffectsSpawned;
            yield return new WaitTicks(MapComponent_Flooding.LeakEffectInterval * 4 + 2);
            ctx.Check(flooding.LeakEffectsSpawned > before, $"leak spray appears at the inflow point ({flooding.LeakEffectsSpawned - before})");
            float level = flooding.LevelAt(inner.CenterCell);
            float wallLeak = FloodingMath.LeakVolume(door.HitPoints, door.MaxHitPoints, flooding.DepthTier, 1f, 0f, MapComponent_Flooding.UpdateInterval / 60f);
            ctx.Note($"compartment level after leaking {level:P1}; an ordinary wall at the same damage would add {wallLeak:0.00} cells per update");
            foreach (Wait w in ctx.ScreenshotOn(map, doorCell, 8f, "watertight_door_leak", 30)) { yield return w; }
            yield return new WaitFrames(20);
        }
    }
}
