using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>下潛能力 M6（Docs/海岸船舶與潛水鐘_機制設計.md §2.1、§2.2、§2.2b、§2.2c、§2.4）。</summary>
    public static class Tests_DiveCapability
    {
        /// <summary>
        /// C6：海面上一艘 9×9 的船（龍骨、艦橋，下方的海床比淺海深）。檢查：沒有壓載不能下潛；蓋了壓載但甲板上有人也不行；
        /// 人下船後可以下潛。潛航中：壓載座數與換層時間（拆掉壓載 → 不能往下、換層 4 倍慢）；朝外的一般牆讓船殼只撐淺海，
        /// 但往下仍然可以（不再是硬上限），比船殼深時先跳確認視窗；在超載深度時檢視面板與警示顯示超載；
        /// 在較深處按上浮 → 往上爬、到淺海自動上浮回海面。最後還原。
        /// </summary>
        public static IEnumerable<Wait> C6_DiveCapability(TestContext ctx)
        {
            PlanetLayer ocean = DepthRimFixture.OceanLayer;
            ctx.Require(ocean != null, "the ocean layer exists");
            PlanetTile below = PlanetTile.Invalid, above = PlanetTile.Invalid;
            for (int id = 0; id < ocean.TilesCount && !below.Valid; id += 3)
            {
                PlanetTile t = new PlanetTile(id, ocean);
                if (!OceanRouting.Navigable(t) || WorldObject_Submarine.SeafloorAt(t) < DepthTier.Thermocline || Find.WorldObjects.AnyWorldObjectAt(t))
                {
                    continue;
                }
                PlanetTile up = OceanWorldView.TileAbove(t);
                if (!up.Valid || Find.WorldObjects.AnyWorldObjectAt(up) || OceanWorldView.TileBelow(up, ocean) != t)
                {
                    continue;
                }
                IceConditions.Info ice = IceConditions.At(up);
                if (ice.Frozen || ice.land || ice.kind == IceKind.Shelf)
                {
                    continue;
                }
                below = t;
                above = up;
            }
            ctx.Require(below.Valid, "found an open sea tile over water deeper than the shallows");
            ctx.Note($"surface {above}, ocean {below} (seafloor {WorldObject_Submarine.SeafloorAt(below)})");

            Map viewing = Find.CurrentMap;
            bool skipBefore = SubmarineSurfacing.skipCutscenes;
            SubmarineSurfacing.skipCutscenes = true;
            OverboardHazard.debugSeaFactor = 1f;
            Pawn sailor = null;
            WorldObject_Submarine sub = null;
            try
            {
                Map sea = DebugActions_SeaSurface.CreateAt(above);
                ctx.Require(sea != null, "a sea-surface site");
                Current.Game.CurrentMap = sea;
                TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
                CellRect deck = CellRect.CenteredOn(sea.Center, 4);
                foreach (IntVec3 c in deck) { sea.terrainGrid.SetTerrain(c, platform); }
                Thing Build(ThingDef def, IntVec3 at, Map map)
                {
                    Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? ThingDefOf.Steel : null);
                    t.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(t, at, map, Rot4.North);
                    return t;
                }
                IntVec3 center = deck.CenterCell;
                Build(ThingDef.Named("DepthRim_Keel"), center, sea);
                Thing helm = Build(ThingDef.Named("DepthRim_Helm"), center + new IntVec3(-2, 0, 3), sea);
                sailor = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                GenSpawn.Spawn(sailor, center + new IntVec3(0, 0, -3), sea);
                MapComponent_PileNetwork.For(sea).MarkDirty();
                yield return new WaitTicks(5);

                // ── 壓載是唯一的硬門檻 ──
                string block = SubmarineSurfacing.DiveBlockReason(helm, out _, out _);
                ctx.Check(block == "DepthRim.Ballast.None".Translate().ToString(), $"no ballast tanks: cannot dive ({block})");
                ctx.Check(SubmarineNavigation.SurfacedOptions(helm).Where(o => o.Label.StartsWith("DepthRim.Nav.DiveTo".Translate(DepthTier.Shallow.Label()))).All(o => o.Disabled), "the dive options are greyed out");
                Thing tank = Build(DepthRimDefOf.DepthRim_BallastTank, center + new IntVec3(3, 0, -3), sea);
                ctx.Check(Ballast.TanksFor(helm, out int deckCells) == 1 && deckCells == deck.Area, $"the tank counts on the keel's hull ({deckCells} cells)");

                // ── 甲板清空 ──
                block = SubmarineSurfacing.DiveBlockReason(helm, out _, out _);
                ctx.Check(block != null && block.Contains(sailor.LabelShortCap), $"a colonist on the open deck stops the dive ({block})");
                ctx.Check(DeckClearing.IsOpenDeck(sailor.Position, sea), "an unroofed hull cell is open deck");
                sailor.DeSpawn();
                block = SubmarineSurfacing.DiveBlockReason(helm, out _, out _);
                ctx.Require(block == null, $"with ballast and a clear deck the ship can dive ({block})");

                HashSet<WorldObject_Submarine> subsBefore = new HashSet<WorldObject_Submarine>(Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>());
                SubmarineSurfacing.Dive(helm);
                yield return new WaitUntil(() => (sub = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>().FirstOrDefault(s => !subsBefore.Contains(s) && s.HasMap)) != null, 600);
                ctx.Require(sub != null && sub.Tile == below, "the ship dived into the ocean below");
                Map subMap = sub.Map;
                yield return new WaitTicks(5);

                // ── 壓載與換層時間 ──
                ctx.Check(sub.BallastTanks(out int hullCells) == 1 && hullCells == deck.Area, $"one tank aboard for {hullCells} hull cells");
                ctx.Check(sub.TicksPerTier == DiveMath.TicksPerTier, $"enough ballast: an hour per tier ({sub.TicksPerTier})");
                Thing subTank = subMap.listerThings.ThingsOfDef(DepthRimDefOf.DepthRim_BallastTank).FirstOrDefault();
                ctx.Require(subTank != null, "the tank came along");
                IntVec3 tankAt = subTank.Position;
                subTank.Destroy();
                ctx.Check(sub.CanDiveTo(DepthTier.Thermocline) == DiveMath.DiveBlock.NoBallast, "without ballast it cannot go deeper");
                ctx.Check(sub.CanDiveTo(DepthTier.Shallow) == DiveMath.DiveBlock.AlreadyThere, "(it is already in the shallows)");
                ctx.Check(sub.TicksPerTier == (int)(DiveMath.TicksPerTier * BallastMath.MaxSlowdown), $"and every tier takes four times as long ({sub.TicksPerTier})");
                ctx.Check(sub.GetInspectString().Contains("DepthRim.Ballast.InspectNone".Translate()), "the inspect pane says there is no ballast");
                Build(DepthRimDefOf.DepthRim_BallastTank, tankAt, subMap);

                // ── 耐壓是警告，不是門檻 ──
                List<IntVec3> cells = TransferLandingUtility.HullCells(subMap).ToList();
                int minX = cells.Min(c => c.x);
                IntVec3 edge = cells.Where(c => c.x == minX).OrderBy(c => System.Math.Abs(c.z - subMap.Center.z)).First();
                Thing wall = Build(ThingDefOf.Wall, edge, subMap);
                yield return new WaitTicks(2);
                ctx.Check(sub.HullRating(out Building weakest) == DepthTier.Shallow && weakest == wall, "a plain wall facing the sea rates the hull for the shallows");
                ctx.Check(sub.CanDiveTo(DepthTier.Thermocline) == DiveMath.DiveBlock.None, "but going deeper is still allowed");
                bool proceeded = false;
                AcceptanceDriver.KeepWindowOpen = typeof(Dialog_MessageBox);
                HullWarning.ConfirmThen(subMap, DepthTier.Shallow, DepthTier.Thermocline, () => proceeded = true);
                yield return new WaitFrames(2);
                Dialog_MessageBox box = Find.WindowStack.WindowOfType<Dialog_MessageBox>();
                ctx.Check(box != null && !proceeded, "deeper than the hull: a confirmation comes first");
                ctx.Check(box != null && box.text.ToString().Contains(wall.LabelCap) && box.text.ToString().Contains(DepthTier.Thermocline.Label()), "it names the target depth and the weakest piece");
                box?.Close(doCloseSound: false);
                AcceptanceDriver.KeepWindowOpen = null;
                HullWarning.ConfirmThen(subMap, DepthTier.Thermocline, DepthTier.Shallow, () => proceeded = true);
                ctx.Check(proceeded && Find.WindowStack.WindowOfType<Dialog_MessageBox>() == null, "rising needs no confirmation");

                // ── 超載 ──
                sub.InitDepth(DepthTier.Thermocline);
                yield return new WaitTicks(2);
                ctx.Check(sub.HullOverloaded, "in the thermocline with a shallows-rated hull it is overloaded");
                ctx.Check(sub.GetInspectString().Contains("DepthRim.HullWarn.Inspect".Translate(DepthTier.Shallow.Label())), "the inspect pane says the hull is overloaded");
                ctx.Check(new Alert_HullOverloaded().GetReport().active, "the hull-overloaded alert is up");

                // ── 從較深處上浮：往上爬、到淺海自動上浮 ──
                string up = SubmarineSurfacing.SurfaceBlockReason(sub, out _, fromDepth: true);
                ctx.Check(up == null, $"Surface is offered from the thermocline ({up})");
                ctx.Check(SubmarineSurfacing.SurfaceBlockReason(sub, out _) != null, "(an immediate surface still needs the shallows)");
                SubmarineSurfacing.SurfaceOrAscend(sub);
                ctx.Check(sub.surfaceWhenShallow && sub.targetTier == DepthTier.Shallow, "it heads for the shallows and will surface there");
                sub.nextDiveTick = Find.TickManager.TicksGame + 1;
                WorldObject_Submarine rising = sub;
                yield return new WaitUntil(() => rising.Destroyed, 600);
                ctx.Check(rising.Destroyed, $"after rising to the shallows it surfaced ({rising.cruiseTier})");
                Map surfaced = Find.WorldObjects.MapParentAt(above)?.Map;
                ctx.Check(surfaced != null && surfaced.listerThings.ThingsOfDef(DepthRimDefOf.DepthRim_BallastTank).Any(), "the hull, ballast and all, floats on the sea again");
                sub = null;
            }
            finally
            {
                AcceptanceDriver.KeepWindowOpen = null;
                SubmarineSurfacing.skipCutscenes = skipBefore;
                OverboardHazard.debugSeaFactor = null;
                Current.Game.CurrentMap = viewing;
                if (sailor != null && !sailor.Destroyed) { if (sailor.Spawned) { sailor.DeSpawn(); } sailor.Destroy(); }
                if (sub != null && !sub.Destroyed)
                {
                    if (sub.HasMap) { Current.Game.DeinitAndRemoveMap(sub.Map, false); }
                    if (!sub.Destroyed) { sub.Destroy(); }
                }
                foreach (MapParent p in Find.WorldObjects.AllWorldObjects.OfType<MapParent>().Where(p => p.Tile == below || p.Tile == above).ToList())
                {
                    if (p.HasMap) { Current.Game.DeinitAndRemoveMap(p.Map, false); }
                    if (!p.Destroyed) { p.Destroy(); }
                }
            }
            yield return new WaitFrames(2);
        }
    }
}
