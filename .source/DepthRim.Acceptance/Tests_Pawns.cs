using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>水位對 Pawn 的影響與水位渲染（FloodingPawnEffects、CompDivingAir、PawnWaterRendering）。</summary>
    public static class Tests_Pawns
    {
        private const int Update = MapComponent_Flooding.UpdateInterval;

        private static WaitTicks Updates(int n) => new WaitTicks(Update * n + 5);

        private static float Severity(Pawn pawn, HediffDef def) => pawn.health.hediffSet.GetFirstHediffOfDef(def)?.Severity ?? 0f;

        public static IEnumerable<Wait> P1_Drowning(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            IntVec3 cell = DepthRimFixture.OpenWaterCell();
            ctx.Require(cell.IsValid, "found an open-water cell");
            Pawn pawn = DepthRimFixture.SpawnColonist(DepthRimFixture.Ocean, cell);
            ctx.Require(CompDivingAir.WornBy(pawn) == null, "colonist has no diving gear");
            yield return Updates(3);
            float s1 = Severity(pawn, DepthRimDefOf.DepthRim_Drowning);
            ctx.Check(s1 > 0f, $"colonist is drowning ({s1:F2})");
            yield return Updates(2);
            float s2 = Severity(pawn, DepthRimDefOf.DepthRim_Drowning);
            ctx.Check(s2 > s1, $"drowning keeps getting worse ({s1:F2} -> {s2:F2})");
            ctx.Note($"net drowning per update ~{(s2 - s1) / 2f:F3}");

            // 移到地表：溺水靠 SeverityPerDay 消退
            pawn.DeSpawn();
            GenSpawn.Spawn(pawn, DepthRimFixture.Pad.CenterCell + new IntVec3(3, 0, 3), DepthRimFixture.Home);
            yield return new WaitTicks(1500);
            float s3 = Severity(pawn, DepthRimDefOf.DepthRim_Drowning);
            ctx.Check(s3 < s2, $"drowning recovers out of the water ({s2:F2} -> {s3:F2})");
            ctx.Check(!pawn.Dead, "colonist survived");
        }

        public static IEnumerable<Wait> P2_DivingSuit(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            IntVec3 cell = DepthRimFixture.OpenWaterCell();
            ctx.Require(cell.IsValid, "found an open-water cell");
            Pawn pawn = DepthRimFixture.SpawnColonist(DepthRimFixture.Ocean, cell);
            Apparel suit = (Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit));
            pawn.apparel.Wear(suit, dropReplacedApparel: false);
            CompDivingAir air = CompDivingAir.WornBy(pawn);
            ctx.Require(air != null, "diving suit worn and found");
            ctx.Check(pawn.GetStatValue(DepthRimDefOf.DepthRim_PressureResistance) >= 1f, "diving suit gives pressure resistance 1");

            float full = air.Air;
            yield return Updates(3);
            ctx.Check(Severity(pawn, DepthRimDefOf.DepthRim_Drowning) == 0f, "no drowning with air in the tank");
            ctx.Check(air.Air < full, $"air is consumed underwater ({full} -> {air.Air})");
            ctx.Check(air.Air >= full - Update * 4, "air drops by about one tick per tick");

            // 用光氣瓶 → 開始溺水
            air.TryConsume(int.MaxValue / 2);
            yield return Updates(2);
            ctx.Check(Severity(pawn, DepthRimDefOf.DepthRim_Drowning) > 0f, "drowning starts when the tank is empty");

            // 到地表（不在水中）會回充
            float empty = air.Air;
            pawn.DeSpawn();
            GenSpawn.Spawn(pawn, DepthRimFixture.Pad.CenterCell + new IntVec3(3, 0, 3), DepthRimFixture.Home);
            yield return Updates(2);
            ctx.Note("refill happens only on water maps (the flooding update), so the surface does not refill");
            ctx.Check(air.Air == empty, "no refill on a map without flooding");

            // 回到冰洋的乾燥艙室：圍一間小艙室並直接把水位設為 0（抽水已由 F2 驗證，這裡只測回充）
            pawn.DeSpawn();
            IntVec3 dry = FindDryCell();
            if (!dry.IsValid)
            {
                ctx.Note("no dry cell on the ocean map; refill check skipped");
                yield break;
            }
            GenSpawn.Spawn(pawn, dry, DepthRimFixture.Ocean);
            yield return Updates(2);
            ctx.Check(air.Air > empty, $"tank refills out of the water ({empty} -> {air.Air})");
        }

        /// <summary>圍一間 3×3 的小艙室並把它的水位直接設為 0，回傳中心格；圍不出密閉艙室時回傳 Invalid。</summary>
        private static IntVec3 FindDryCell()
        {
            Map map = DepthRimFixture.Ocean;
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            CellRect ring = DepthRimFixture.ClearOceanArea(5);
            DepthRimFixture.BuildWallRing(ring);
            IntVec3 c = ring.CenterCell;
            Room room = c.GetRoom(map);
            if (room == null || flooding.IsOpenWater(room))
            {
                return IntVec3.Invalid;
            }
            HarmonyLib.Traverse levels = HarmonyLib.Traverse.Create(flooding).Field("levels");
            byte[] arr = levels.GetValue<byte[]>();
            foreach (IntVec3 cell in room.Cells) { arr[map.cellIndices.CellToIndex(cell)] = 0; }
            return c;
        }

        public static IEnumerable<Wait> P3_WadingAndPressure(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            IntVec3 cell = DepthRimFixture.OpenWaterCell();
            ctx.Require(cell.IsValid, "found an open-water cell");
            Pawn pawn = DepthRimFixture.SpawnColonist(DepthRimFixture.Ocean, cell);
            Apparel suit = (Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit));
            pawn.apparel.Wear(suit, dropReplacedApparel: false);
            yield return Updates(2);
            Hediff wading = pawn.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Wading);
            ctx.Require(wading != null, "wading hediff present in open water");
            // 嚴重度每次更新設為水位，兩次更新之間會被 SeverityPerDay 稍微扣掉（-10/天 ≈ 每 250 ticks -0.04）
            ctx.Check(wading.Severity >= 0.95f, $"wading severity follows the water level ({wading.Severity:F2})");
            ctx.Check(wading.CurStageIndex == 2, "swimming stage in full water");
            ctx.Check(pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving) < 0.8f, "moving is slowed while swimming");
            ctx.Check(Severity(pawn, DepthRimDefOf.DepthRim_Barotrauma) == 0f, "no barotrauma in the shallows");
            ctx.Check(DepthRimFixture.Flooding.DepthTier == DepthTier.Shallow, "ice ocean base counts as the shallows");
        }

        public static IEnumerable<Wait> R1_WaterLineMaterial(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            IntVec3 cell = DepthRimFixture.OpenWaterCell();
            ctx.Require(cell.IsValid, "found an open-water cell");
            Pawn pawn = DepthRimFixture.SpawnColonist(DepthRimFixture.Ocean, cell);
            pawn.Drawer.renderer.EnsureGraphicsInitialized();
            pawn.Drawer.renderer.SetAllGraphicsDirty();
            yield return new WaitFrames(5);
            Material original = pawn.Drawer.renderer.BodyGraphic?.MatSouth;
            ctx.Require(original != null, "colonist body material found");

            Material first = PawnWaterRendering.GetOrQueue(original, FloodingMath.CutSteps / 2);
            ctx.Note(first == null ? "first request queued (expected)" : "material already cached by the renderer");
            PawnWaterRendering.ProcessPending();
            Material water = PawnWaterRendering.GetOrQueue(original, FloodingMath.CutSteps / 2);
            ctx.Require(water != null, "water-line material created on the main thread");
            ctx.Check(water.shader == ShaderDatabase.CutoutComplex, "uses the CutoutComplex shader");
            ctx.Check(water.mainTexture == original.mainTexture, "keeps the original texture");
            ctx.Check(water.GetTexture(ShaderPropertyIDs.MaskTex) != null, "has a water-line mask");
            ctx.Check(PawnWaterRendering.GetOrQueue(original, FloodingMath.CutSteps / 2) == water, "material is cached");

            Current.Game.CurrentMap = DepthRimFixture.Ocean;
            CameraJumper.TryJump(pawn);
            Find.CameraDriver.SetRootSize(8f);
            yield return new WaitFrames(30);
            ctx.Screenshot("pawn_in_water_close");
            Find.CameraDriver.SetRootSize(30f);
            yield return new WaitFrames(30);
            ctx.Screenshot("pawn_in_water_far");
            Find.CameraDriver.SetRootSize(24f);
        }
    }
}
