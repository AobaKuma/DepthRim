using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>安全繩錨點（Docs/安全繩錨點_機制設計.md）：航行中與水面惡劣海況。</summary>
    public static class Tests_RopeAnchor
    {
        /// <summary>
        /// A1：同 S2 的船塢與起飛（<see cref="Tests_Submarine.A1_RopeAnchorVoyage"/> 呼叫這裡）。航行中在甲板上裝一個錨點，
        /// 沒屋頂的甲板放三名殖民者：最近的兩人扣上、第三人在第一次落海檢查時失蹤；扣著的人有「掛在安全繩上」；
        /// 走回艙室的人解開；停船時全部解開、回到航行再扣上；拆掉錨點後剩下的人在下一個 tick 失蹤。
        /// </summary>
        internal static IEnumerable<Wait> Voyage(TestContext ctx, WorldObject_Submarine sub, IntVec3 subCenter, List<IntVec3> cabin)
        {
            Map map = sub.Map;
            yield return new WaitTicks(VoyageMath.ThrustCheckInterval + 10);
            ctx.Require(VoyageOverboard.Underway(sub), $"under way with thrust ({sub.phase}, thrust {sub.ThrustFraction:P0})");
            ctx.Require(cabin.Count > 0, "the shipyard has a sealed compartment");
            MapComponent_RopeTethers comp = MapComponent_RopeTethers.For(map);
            ctx.Require(comp != null, "the submarine map has the safety-line component");
            ctx.Check(map.listerThings.ThingsOfDef(DepthRimDefOf.DR_RopeAnchor).Count == 0, "no anchors yet");
            int earlyBefore = comp.EarlyOuts;

            // 錨點：甲板西側
            IntVec3 anchorCell = subCenter + new IntVec3(-3, 0, 0);
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(DepthRimDefOf.DR_RopeAnchor, anchorCell, Rot4.North, map, godMode: true).Accepted, "an anchor can be placed on the hull platform");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(DepthRimDefOf.DR_RopeAnchor, subCenter + new IntVec3(-8, 0, 0), Rot4.North, map, godMode: true).Accepted, "but not on the abyss");
            Building_RopeAnchor anchor = (Building_RopeAnchor)DepthRimFixture.Spawn(DepthRimDefOf.DR_RopeAnchor, anchorCell, map, faction: Faction.OfPlayer);
            ctx.Require(anchor != null && anchor.Spawned, "the anchor is on deck");
            ctx.Check(map.listerThings.ThingsOfDef(DepthRimDefOf.DR_RopeAnchor).Contains(anchor), "the anchor is in listerThings (no own registry)");

            // 三名殖民者在沒屋頂的甲板上：距離錨點 1、√5、√17 格
            Pawn near = DepthRimFixture.SpawnColonist(map, subCenter + new IntVec3(-4, 0, 0));
            Pawn mid = DepthRimFixture.SpawnColonist(map, subCenter + new IntVec3(-4, 0, 2));
            Pawn far = DepthRimFixture.SpawnColonist(map, subCenter + new IntVec3(-4, 0, -4));
            Pawn inside = DepthRimFixture.SpawnColonist(map, cabin[0]);
            // 甲板淹在水裡：沒穿潛水服的人會溺水倒地，倒地的人不能自己扣繩（2026-11-17 第二次執行就這樣失敗過）
            foreach (Pawn p in new[] { near, mid, far })
            {
                p.apparel.Wear((Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit)), dropReplacedApparel: false);
            }
            ctx.Check(new[] { near, mid, far }.All(VoyageOverboard.IsOutside) && !VoyageOverboard.IsOutside(inside), "three on the open deck, one in the compartment");
            yield return new WaitTicks(VoyageOverboard.CheckInterval + 20);

            ctx.Check(anchor.TetheredCount == 2 && RopeTether.IsTethered(near) && RopeTether.IsTethered(mid), $"the two nearest are clipped on ({anchor.TetheredCount})");
            ctx.Check(!far.Spawned && !far.Dead && far.IsWorldPawn(), $"the third, with no free line, was swept away (spawned {far.Spawned})");
            ctx.Check(inside.Spawned && !RopeTether.IsTethered(inside), "the one in the compartment stays aboard, unclipped");
            ctx.Check(near.Spawned && mid.Spawned && near.Map == map && mid.Map == map, "the clipped two are still aboard");
            ctx.Check(near.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_TetheredInCurrent), "hanging in the current slows them down");
            ctx.Check(comp.Hazard == HazardKind.Voyage, "the hazard is the voyage");
            ctx.Check(comp.EarlyOuts == earlyBefore, "scans under way do not take the no-hazard early out");
            ctx.Check(near.GetGizmos().Any(g => g is Command_Action c && c.defaultLabel == "DepthRim.Rope.Untie".Translate()), "a clipped pawn has the unclip command");
            ctx.Check(anchor.GetInspectString().Contains(near.LabelShortCap), "the anchor names who is clipped on");
            Current.Game.CurrentMap = map;
            foreach (Wait w in ctx.ScreenshotOn(map, anchorCell, 8f, "rope_voyage", 30)) { yield return w; }

            // 遮罩快取：沒有東西變時不重算；蓋一道牆後重算，牆後面拉不到
            int rebuilds = anchor.coverage.Rebuilds;
            yield return new WaitTicks(MapComponent_RopeTethers.ScanInterval * 2);
            ctx.Check(anchor.coverage.Rebuilds == rebuilds, $"the cell mask is cached between scans ({anchor.coverage.Rebuilds - rebuilds} rebuilds)");
            IntVec3 wallCell = anchorCell + new IntVec3(0, 0, -1);
            Thing wall = DepthRimFixture.Spawn(ThingDef.Named("DepthRim_PressureHullWall"), wallCell, map);
            anchor.coverage.EnsureFresh(map, anchor.Position);
            ctx.Check(anchor.coverage.Rebuilds == rebuilds + 1, "a new wall invalidates the mask");
            ctx.Check(!anchor.coverage.Covers(anchorCell + new IntVec3(0, 0, -3)) && anchor.coverage.Covers(anchorCell + new IntVec3(0, 0, 3)), "cells behind the wall are out of reach");
            wall.Destroy();

            // 走回艙室：解開，位子讓出來
            mid.Position = cabin[cabin.Count - 1];
            mid.Notify_Teleported();
            yield return new WaitTicks(MapComponent_RopeTethers.ScanInterval + 5);
            ctx.Check(!RopeTether.IsTethered(mid) && anchor.TetheredCount == 1, "walking back inside unclips the line");

            // 停船（視為已抵達）：沒有風險，全部解開；回到航行再扣上
            TransferPhase saved = sub.phase;
            sub.phase = TransferPhase.Arrived;
            yield return new WaitTicks(MapComponent_RopeTethers.ScanInterval + 5);
            ctx.Check(comp.Hazard == HazardKind.None && comp.TetheredCount == 0 && anchor.TetheredCount == 0, "stopped: every line is unclipped");
            ctx.Check(comp.EarlyOuts > earlyBefore, "and scans take the no-hazard early out");
            sub.phase = saved;
            yield return new WaitTicks(VoyageMath.ThrustCheckInterval + MapComponent_RopeTethers.ScanInterval + 10);
            ctx.Check(VoyageOverboard.Underway(sub), $"under way again ({sub.phase}, thrust {sub.ThrustFraction:P0})");
            ctx.Check(RopeTether.IsTethered(near), $"and clipped back on (downed {near.Downed}, outside {VoyageOverboard.IsOutside(near)}, hazard {comp.Hazard})");
            ctx.Note($"average scan {comp.ScanStopwatchTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Mathf.Max(1, comp.Scans):F3} ms over {comp.Scans} scans");

            // 拆掉錨點：繩子斷開，下一個 tick 落海
            anchor.Destroy(DestroyMode.Deconstruct);
            ctx.Check(!RopeTether.IsTethered(near), "destroying the anchor parts the line");
            yield return new WaitTicks(3);
            ctx.Check(!near.Spawned && near.IsWorldPawn(), $"and the one left outside is swept away at once (spawned {near.Spawned})");
            ctx.Check(inside.Spawned && mid.Spawned, "the two inside stay aboard");

            foreach (Pawn p in new[] { near, far })
            {
                if (p.IsWorldPawn()) { Find.WorldPawns.RemoveAndDiscardPawnViaGC(p); }
            }
        }

        /// <summary>
        /// A2：海面地圖的水面層、固定天氣倍率 2.5（惡劣海況）、每次擲骰都中：浮船上的錨點扣上旁邊的人，
        /// 他被浪打倒（暈眩）但沒失蹤；船另一頭繩子拉不到的人被浪捲走並收到信；警報列出沒扣繩的人；
        /// 倍率降到 1.5 時繩子解開、警報消失；地表基地沒有落海風險。
        /// </summary>
        public static IEnumerable<Wait> A2_RopeAnchorRoughSea(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            List<Pawn> lostPawns = new List<Pawn>();
            try
            {
                OverboardHazard.debugSeaFactor = 1.2f;
                IntVec3 c = sea.Center;
                CellRect hull = new CellRect(c.x - 12, c.z - 2, 25, 5);
                Tests_SeaSurface.LayHull(sea, hull);
                IntVec3 anchorCell = new IntVec3(hull.minX + 2, 0, c.z);
                Building_RopeAnchor anchor = (Building_RopeAnchor)DepthRimFixture.Spawn(DepthRimDefOf.DR_RopeAnchor, anchorCell, sea, faction: Faction.OfPlayer);
                Pawn clipped = DepthRimFixture.SpawnColonist(sea, anchorCell + new IntVec3(3, 0, 1));
                Pawn loose = DepthRimFixture.SpawnColonist(sea, new IntVec3(hull.maxX - 1, 0, c.z));
                yield return new WaitTicks(MapComponent_RopeTethers.ScanInterval + 5);
                MapComponent_RopeTethers comp = MapComponent_RopeTethers.For(sea);
                MapComponent_RoughSeaOverboard rough = sea.GetComponent<MapComponent_RoughSeaOverboard>();
                ctx.Require(comp != null && rough != null, "the sea level has the safety-line and rough-sea components");
                ctx.Check(comp.Hazard == HazardKind.None && anchor.TetheredCount == 0, "calm sea: no lines clipped");
                ctx.Check(OverboardHazard.HazardOf(DepthRimFixture.Home) == HazardKind.None, "the land base has no overboard hazard");

                OverboardHazard.debugSeaFactor = 2.5f;
                yield return new WaitTicks(MapComponent_RopeTethers.ScanInterval + 5);
                ctx.Check(comp.Hazard == HazardKind.RoughSea, "a storm at the surface is rough sea");
                ctx.Check(RopeTether.IsTethered(clipped) && !RopeTether.IsTethered(loose), "the one near the anchor is clipped, the one at the far end is not");
                ctx.Check(comp.UntetheredOutside.Contains(loose), "the far one is on the untethered list");
                Alert_UntetheredOutside alert = new Alert_UntetheredOutside();
                ctx.Check(alert.GetReport().AllCulprits.Any(t => t.Thing == loose), "the alert points at them");
                Current.Game.CurrentMap = sea;
                foreach (Wait w in ctx.ScreenshotOn(sea, anchorCell + new IntVec3(2, 0, 0), 8f, "rope_rough_sea", 30)) { yield return w; }

                int letters = Find.LetterStack.LettersListForReading.Count(l => l.Label == "DepthRim.RoughSea.OverboardLabel".Translate());
                MapComponent_RoughSeaOverboard.debugAlwaysHit = true;
                int knockdowns = rough.Knockdowns;
                yield return new WaitTicks(MapComponent_RoughSeaOverboard.CheckInterval + 10);
                MapComponent_RoughSeaOverboard.debugAlwaysHit = false;
                ctx.Check(clipped.Spawned && clipped.Map == sea && rough.Knockdowns > knockdowns, $"the clipped one is knocked down by the wave but stays ({rough.Knockdowns - knockdowns} knockdowns)");
                ctx.Check(!loose.Spawned && loose.IsWorldPawn(), $"the loose one is swept away (spawned {loose.Spawned})");
                lostPawns.Add(loose);
                int lettersAfter = Find.LetterStack.LettersListForReading.Count(l => l.Label == "DepthRim.RoughSea.OverboardLabel".Translate());
                ctx.Check(lettersAfter == letters + 1, $"one swept-away letter ({letters} -> {lettersAfter})");

                // 平均間隔：靠海邊比甲板中央危險
                ctx.Check(rough.NearOpenSea(new IntVec3(hull.minX, 0, c.z)) && !rough.NearOpenSea(new IntVec3(c.x, 0, c.z)), "the hull edge is near open sea, the middle is not");

                OverboardHazard.debugSeaFactor = 1.5f;
                yield return new WaitTicks(MapComponent_RopeTethers.ScanInterval + 5);
                ctx.Check(comp.Hazard == HazardKind.None && !RopeTether.IsTethered(clipped), "the sea calms down: lines unclipped");
                ctx.Check(!new Alert_UntetheredOutside().GetReport().active, "and the alert clears");
            }
            finally
            {
                OverboardHazard.debugSeaFactor = null;
                MapComponent_RoughSeaOverboard.debugAlwaysHit = false;
            }
            foreach (Pawn p in lostPawns)
            {
                if (p.IsWorldPawn()) { Find.WorldPawns.RemoveAndDiscardPawnViaGC(p); }
            }
            Tests_SeaSurface.RemoveSeaMaps(sea);
            yield return new WaitTicks(5);
        }
    }
}
