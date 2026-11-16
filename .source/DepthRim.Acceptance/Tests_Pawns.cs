using System.Collections.Generic;
using System.Linq;
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
            ctx.Check(Mathf.Approximately(pawn.GetStatValue(DepthRimDefOf.DepthRim_PressureResistance), 2f), $"diving suit gives pressure resistance 2 ({pawn.GetStatValue(DepthRimDefOf.DepthRim_PressureResistance)})");
            ctx.Check(air.Props.capacityTicks == 36 * GenDate.TicksPerHour, $"diving suit tank lasts 36 hours ({air.Props.capacityTicks.ToStringTicksToPeriod()})");
            ctx.Check(air.Props.capacityTicks / air.Props.refillPerTick <= 3000f, "a full refill still takes about 1.2 hours");

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

        /// <summary>
        /// 原版真空服（Odyssey 補丁）：頭盔配真空服身體才能在水下呼吸 12 小時；只戴頭盔不行；
        /// 不給水壓抗性（淺海夠用）；跳幫隊員已經穿齊真空服就不換成潛水服。
        /// </summary>
        public static IEnumerable<Wait> P12_Vacsuit(TestContext ctx)
        {
            ThingDef helmetDef = ThingDef.Named("Apparel_VacsuitHelmet");
            ThingDef suitDef = ThingDef.Named("Apparel_Vacsuit");
            ctx.Require(helmetDef.GetCompProperties<CompProperties_DivingAir>() != null, "vacsuit helmet patched with an air supply");
            ctx.Check(suitDef.GetCompProperties<CompProperties_DivingAir>() == null, "the vacsuit body has no tank of its own");

            DepthRimFixture.EnsureOcean();
            IntVec3 cell = DepthRimFixture.OpenWaterCell();
            ctx.Require(cell.IsValid, "found an open-water cell");
            Pawn pawn = DepthRimFixture.SpawnColonist(DepthRimFixture.Ocean, cell);
            foreach (Apparel a in pawn.apparel.WornApparel.ToList()) { pawn.apparel.Remove(a); a.Destroy(); }
            Apparel helmet = (Apparel)ThingMaker.MakeThing(helmetDef, GenStuff.DefaultStuffFor(helmetDef));
            pawn.apparel.Wear(helmet, dropReplacedApparel: false);
            ctx.Check(CompDivingAir.WornBy(pawn) == null, "helmet alone is not sealed");
            ctx.Check(!FloodingPawnEffects.CanBreatheUnderwater(pawn), "cannot breathe underwater with the helmet alone");

            Apparel suit = (Apparel)ThingMaker.MakeThing(suitDef, GenStuff.DefaultStuffFor(suitDef));
            pawn.apparel.Wear(suit, dropReplacedApparel: false);
            CompDivingAir air = CompDivingAir.WornBy(pawn);
            ctx.Require(air != null && air.parent == helmet, "helmet + vacsuit: the helmet's tank is used");
            ctx.Check(air.Props.capacityTicks == 12 * GenDate.TicksPerHour, $"vacsuit air lasts 12 hours ({air.Props.capacityTicks.ToStringTicksToPeriod()})");
            ctx.Check(FloodingPawnEffects.CanBreatheUnderwater(pawn), "can breathe underwater in a full vacsuit");
            ctx.Check(pawn.GetStatValue(DepthRimDefOf.DepthRim_PressureResistance) < 0.01f, "vacsuit gives no water pressure resistance");

            float full = air.Air;
            yield return Updates(3);
            ctx.Check(Severity(pawn, DepthRimDefOf.DepthRim_Drowning) == 0f, "no drowning in the shallows in a vacsuit");
            ctx.Check(Severity(pawn, DepthRimDefOf.DepthRim_Barotrauma) == 0f, "no barotrauma in the shallows");
            ctx.Check(air.Air < full, $"vacsuit air is consumed underwater ({full} -> {air.Air})");

            BoardingUtility.GiveDivingGear(pawn);
            ctx.Check(!pawn.apparel.WornApparel.Any(a => a.def == DepthRimFixture.DivingSuit) && pawn.apparel.Wearing(helmet) && pawn.apparel.Wearing(suit),
                "a boarder already in a full vacsuit is not given a diving suit");

            pawn.apparel.Remove(suit);
            suit.Destroy();
            ctx.Check(CompDivingAir.WornBy(pawn) == null, "taking off the vacsuit body unseals the helmet");
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

        /// <summary>
        /// 潛水服穿著貼圖走 apparel.renderNodeProperties：每種體型都只有一個自訂節點，
        /// 共用 Thing/DiveSuit/DiveSuit 的三方向貼圖與遮罩，沒有原版的 _體型 衣物節點；染色只改遮罩紅色區域。四個方向各拍一張。
        /// </summary>
        public static IEnumerable<Wait> P11_DivingSuitGraphic(TestContext ctx)
        {
            foreach (string dir in new[] { "north", "east", "south" })
            {
                ctx.Check(ContentFinder<Texture2D>.Get("Thing/DiveSuit/DiveSuit_" + dir, reportFailure: false) != null, $"DiveSuit_{dir} texture loaded");
                ctx.Check(ContentFinder<Texture2D>.Get("Thing/DiveSuit/DiveSuit_" + dir + "m", reportFailure: false) != null, $"DiveSuit_{dir}m mask loaded");
            }

            Map map = DepthRimFixture.Home;
            BodyTypeDef[] bodies = { BodyTypeDefOf.Thin, BodyTypeDefOf.Male, BodyTypeDefOf.Female, BodyTypeDefOf.Hulk, BodyTypeDefOf.Fat };
            // 前兩件不染色（原圖），後三件染色：只有遮罩紅色區域會變色
            Color?[] dyes = { null, null, new Color(0.85f, 0.45f, 0.1f), new Color(0.2f, 0.45f, 0.8f), new Color(0.35f, 0.6f, 0.3f) };
            IntVec3 origin = DepthRimFixture.Pad.CenterCell + new IntVec3(-4, 0, -4);
            var pawns = new List<Pawn>();
            for (int i = 0; i < bodies.Length; i++)
            {
                IntVec3 cell = origin + new IntVec3(i * 2, 0, 0);
                DepthRimFixture.ClearCell(map, cell, TerrainDefOf.Concrete);
                Pawn pawn = DepthRimFixture.SpawnColonist(map, cell);
                pawn.story.bodyType = bodies[i];
                Apparel suit = (Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit));
                if (dyes[i].HasValue) { suit.SetColor(dyes[i].Value); }
                pawn.apparel.Wear(suit, dropReplacedApparel: false);
                pawn.Drawer.renderer.SetAllGraphicsDirty();
                pawns.Add(pawn);
            }
            Current.Game.CurrentMap = map;
            yield return new WaitFrames(10);

            foreach (Pawn pawn in pawns)
            {
                var suitNodes = new List<PawnRenderNode>();
                var vanillaNodes = new List<PawnRenderNode>();
                Collect(pawn.Drawer.renderer.renderTree.rootNode, n =>
                {
                    if (n.apparel?.def != DepthRimFixture.DivingSuit) { return; }
                    (n is PawnRenderNode_Apparel ? vanillaNodes : suitNodes).Add(n);
                });
                string body = pawn.story.bodyType.defName;
                ctx.Check(suitNodes.Count == 1, $"{body}: one diving suit render node ({suitNodes.Count})");
                ctx.Check(vanillaNodes.Count == 0, $"{body}: no vanilla body-type apparel node ({vanillaNodes.Count})");
                Graphic g = suitNodes.Count == 1 ? suitNodes[0].GraphicFor(pawn) : null;
                ctx.Check(g is Graphic_Multi && g.path == "Thing/DiveSuit/DiveSuit", $"{body}: shared Graphic_Multi ({g?.GetType().Name} {g?.path})");
                Material mat = g?.MatSouth;
                ctx.Check(mat != null && mat.shader == ShaderDatabase.CutoutComplex && mat.GetTexture(ShaderPropertyIDs.MaskTex) != null, $"{body}: masked CutoutComplex material ({mat?.shader?.name})");
                Apparel worn = suitNodes.Count == 1 ? suitNodes[0].apparel : null;
                ctx.Check(worn != null && g.color.IndistinguishableFrom(worn.DrawColor) && g.colorTwo == Color.white, $"{body}: primary = suit color {worn?.DrawColor}, secondary white ({g?.color} / {g?.colorTwo})");
            }

            // 暫停再轉向：徵召待命的工作每 tick 都會把朝向轉回去
            ctx.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            string[] names = { "north", "east", "south", "west" };
            foreach (Rot4 rot in new[] { Rot4.South, Rot4.East, Rot4.North, Rot4.West })
            {
                foreach (Pawn pawn in pawns) { pawn.Rotation = rot; }
                foreach (Wait w in ctx.ScreenshotOn(map, origin + new IntVec3(4, 0, 0), 11f, "suit_" + names[rot.AsInt], 40)) { yield return w; }
            }
            // 截圖晚一點才寫檔：最後一張拍完前不能拉遠鏡頭
            yield return new WaitFrames(60);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            ctx.allowPause = false;
            Find.CameraDriver.SetRootSize(24f);
        }

        private static void Collect(PawnRenderNode node, System.Action<PawnRenderNode> visit)
        {
            if (node == null) { return; }
            visit(node);
            if (node.children == null) { return; }
            foreach (PawnRenderNode child in node.children) { Collect(child, visit); }
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
