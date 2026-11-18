using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>海洋基因（Docs/基因_Biotech設計.md）：需要 Biotech。</summary>
    public static class Tests_Genes
    {
        private const int Update = MapComponent_Flooding.UpdateInterval;

        private static WaitTicks Updates(int n) => new WaitTicks(Update * n + 5);

        private static float Severity(Pawn pawn, HediffDef def) => pawn.health.hediffSet.GetFirstHediffOfDef(def)?.Severity ?? 0f;

        private static GeneDef Gene(string name) => DefDatabase<GeneDef>.GetNamed(name);

        private static Pawn WithGenes(Pawn pawn, params string[] genes)
        {
            foreach (string g in genes)
            {
                pawn.genes.AddGene(Gene(g), true);
            }
            DepthRimGenes.Invalidate(pawn);
            return pawn;
        }

        private static IntVec3 HomeCell(int dx, int dz) => DepthRimFixture.Pad.CenterCell + new IntVec3(dx, 0, dz);

        /// <summary>定義載入、水下呼吸、耐壓參數。</summary>
        public static IEnumerable<Wait> GN1_BreathingAndPressure(TestContext ctx)
        {
            ctx.Require(ModsConfig.BiotechActive, "Biotech is active");
            var marine = DefDatabase<GeneDef>.AllDefs.Where(g => g.defName.StartsWith("DepthRim_")).ToList();
            ctx.Check(marine.Count >= 25, $"marine genes loaded ({marine.Count})");
            ctx.Check(marine.All(g => g.Icon != null && g.Icon != BaseContent.BadTex), "every marine gene has an icon");
            ctx.Check(new[] { "DepthRim_Mariner", "DepthRim_Deepdiver", "DepthRim_Abyssal" }.All(x => DefDatabase<XenotypeDef>.GetNamedSilentFail(x) != null), "three xenotypes loaded");
            ctx.Check(DefDatabase<XenotypeDef>.GetNamed("DepthRim_Deepdiver").AllGenes.Any(g => g.defName == "DarkVision"), "deepdiver gets the vanilla dark vision gene");
            ctx.Check(Gene("VacuumResistance_Total").GetModExtension<GeneExtension_DepthRim>()?.drowningFactor == 0.35f, "Odyssey breathless counts as long breath");
            ctx.Check(ThoughtDef.Named("SoakingWet").nullifyingGenes?.Contains(Gene("DepthRim_Waterproof")) == true, "waterproof skin nullifies soaking wet");

            DepthRimFixture.EnsureOcean();
            IntVec3 cell = DepthRimFixture.OpenWaterCell();
            ctx.Require(cell.IsValid, "found an open-water cell");
            Map ocean = DepthRimFixture.Ocean;
            Pawn plain = DepthRimFixture.SpawnColonist(ocean, cell);
            Pawn breath = WithGenes(DepthRimFixture.SpawnColonist(ocean, cell), "DepthRim_LongBreath");
            Pawn gills = WithGenes(DepthRimFixture.SpawnColonist(ocean, cell), "DepthRim_Gills");
            ctx.Check(DepthRimGenes.Of(gills).BreathesWater && FloodingPawnEffects.CanBreatheUnderwater(gills), "gills breathe underwater");
            ctx.Check(!FloodingPawnEffects.CanBreatheUnderwater(plain), "a baseliner does not");
            yield return Updates(4);
            float d0 = Severity(plain, DepthRimDefOf.DepthRim_Drowning);
            float d1 = Severity(breath, DepthRimDefOf.DepthRim_Drowning);
            float d2 = Severity(gills, DepthRimDefOf.DepthRim_Drowning);
            ctx.Check(d0 > 0f, $"baseliner drowns ({d0:F3})");
            ctx.Check(d1 > 0f && d1 < d0 * 0.6f, $"long breath drowns much slower ({d1:F3} vs {d0:F3})");
            ctx.Check(d2 == 0f, "gills never drown");

            // 鰓的人穿潛水服也不耗氣
            Apparel suit = (Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit));
            gills.apparel.Wear(suit, dropReplacedApparel: false);
            CompDivingAir air = CompDivingAir.WornBy(gills);
            float before = air.Air;
            yield return Updates(2);
            ctx.Check(air.Air >= before, $"gills use no tank air ({before} -> {air.Air})");

            // 耐壓：抗性統計、容許差
            Pawn fragile = WithGenes(DepthRimFixture.SpawnColonist(DepthRimFixture.Home, HomeCell(2, 2)), "DepthRim_PressureFragile");
            Pawn adapted = WithGenes(DepthRimFixture.SpawnColonist(DepthRimFixture.Home, HomeCell(3, 2)), "DepthRim_PressureAdapted");
            Pawn immune = WithGenes(DepthRimFixture.SpawnColonist(DepthRimFixture.Home, HomeCell(4, 2)), "DepthRim_PressureImmune");
            ctx.Check(Mathf.Approximately(adapted.GetStatValue(DepthRimDefOf.DepthRim_PressureResistance), 2f), "deep-adapted: pressure resistance 2 without a suit");
            ctx.Check(Mathf.Approximately(immune.GetStatValue(DepthRimDefOf.DepthRim_PressureResistance), 3f), "abyssal body: pressure resistance 3");
            ctx.Check(Mathf.Approximately(DepthRimGenes.PressureOf(fragile).tolerance, 0.5f) && DepthRimGenes.PressureOf(fragile).maxLevel == 2, "fragile profile read from the gene");
            Hediff_PressureAdaptation hf = PressureAdaptationUtility.Ensure(fragile, 1f);
            Hediff_PressureAdaptation hi = PressureAdaptationUtility.Ensure(immune, 3f);
            hf.SetState(0f, 1f);
            hi.SetState(0f, 3f);
            ctx.Check(hf.Mismatch > 0.4f, $"fragile: one level off is already uncomfortable ({hf.Mismatch:F2})");
            ctx.Check(hi.Mismatch == 0f, "abyssal body: surface to seabed is fine");
            hf.SetState(2f, 3f);
            ctx.Check(hf.TicksUntilComfortable == PressureAdaptationMath.Never, "fragile on the seabed: never comfortable");
            ctx.Check(!hf.TipStringExtra.NullOrEmpty(), "tooltip renders with gene info");
            fragile.health.RemoveHediff(hf);
            immune.health.RemoveHediff(hi);
        }

        /// <summary>暈船：基準、海腳、易暈船；想法；離開暴露後恢復。</summary>
        public static IEnumerable<Wait> GN2_Seasickness(TestContext ctx)
        {
            Map home = DepthRimFixture.Home;
            Pawn plain = DepthRimFixture.SpawnColonist(home, HomeCell(-2, -2));
            Pawn legs = WithGenes(DepthRimFixture.SpawnColonist(home, HomeCell(-3, -2)), "DepthRim_SeaLegs");
            Pawn prone = WithGenes(DepthRimFixture.SpawnColonist(home, HomeCell(-4, -2)), "DepthRim_SeasickProne");
            ctx.Check(legs.GetStatValue(DepthRimDefOf.DepthRim_SeasicknessSensitivity) == 0f, "sea legs: sensitivity 0");
            ctx.Check(Mathf.Approximately(prone.GetStatValue(DepthRimDefOf.DepthRim_SeasicknessSensitivity), 2f), "seasick-prone: sensitivity 200%");
            ctx.Check(SeasicknessExposure.Of(home) == 0f, "no sea motion on a land map");
            SeasicknessExposure.debugExposure = 1f;
            try
            {
                WaitUntil added = new WaitUntil(() => plain.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_Seasick)
                    && prone.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_Seasick), 1000);
                yield return added;
                ctx.Require(!added.TimedOut, "the scanner adds seasickness where the sea moves");
                ctx.Check(!legs.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_Seasick), "sea legs never get it");
                var hp = (Hediff_Seasick)plain.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Seasick);
                var hr = (Hediff_Seasick)prone.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Seasick);
                hp.Advance(30000);
                hr.Advance(30000);
                ctx.Check(hp.Severity >= SeasicknessMath.SeasickSeverity && hp.Severity < SeasicknessMath.SevereSeverity, $"baseliner in a storm: seasick ({hp.Severity:F2})");
                ctx.Check(hr.Severity >= SeasicknessMath.SevereSeverity, $"seasick-prone: severe ({hr.Severity:F2})");
                ctx.Check(hp.Visible, "visible once queasy");
                ThoughtState state = ThoughtDef.Named("DepthRim_Seasick").Worker.CurrentState(plain);
                ctx.Check(state.Active && state.StageIndex == 1, $"seasick thought at the seasick stage ({state.StageIndex})");
            }
            finally
            {
                SeasicknessExposure.debugExposure = null;
            }
            var h = (Hediff_Seasick)plain.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_Seasick);
            h.Advance(15000);
            ctx.Check(h.Severity < SeasicknessMath.QueasySeverity, $"recovers on steady ground ({h.Severity:F3})");
            ctx.Check(!h.Visible, "hidden again");
        }

        /// <summary>畏光、頭燈模式、生物發光、鰓乾燥、船員種族、沉船基因包。</summary>
        public static IEnumerable<Wait> GN3_LightAndCrew(TestContext ctx)
        {
            Map home = DepthRimFixture.Home;
            IntVec3 lit = HomeCell(6, -3);
            Pawn blind = WithGenes(DepthRimFixture.SpawnColonist(home, lit), "DepthRim_PhotophobiaIntense");
            Pawn mild = WithGenes(DepthRimFixture.SpawnColonist(home, HomeCell(7, -3)), "DepthRim_Photophobia");
            Pawn plain = DepthRimFixture.SpawnColonist(home, HomeCell(8, -3));
            int handle = home.glowGrid.RegisterCustomGlower(lit, 4f, new ColorInt(Color.white), 2.5f);
            try
            {
                yield return new WaitTicks(Gene_DepthRim.Interval + 20);
                float glow = home.glowGrid.GroundGlowAt(lit);
                ctx.Check(glow >= 0.99f, $"overlit test cell ({glow:F2})");
                ctx.Check(blind.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_Dazzled), "light-blind: dazzled in bright light");
                ctx.Check(ThoughtDef.Named("DepthRim_BrightLight").Worker.CurrentState(blind).StageIndex == 1, "light-blind: blinding light thought");
                var affecter = Gene("DepthRim_PhotophobiaIntense").conditionalStatAffecters[0];
                ctx.Check(affecter.Applies(StatRequest.For(blind)), "the bright-light stat condition applies");
            }
            finally
            {
                home.glowGrid.DeregisterCustomGlower(handle);
            }
            yield return new WaitTicks(Gene_DepthRim.Interval + 20);
            ctx.Check(home.glowGrid.GroundGlowAt(lit) < 0.99f || home.skyManager.CurSkyGlow >= 0.99f, "lamp removed");
            if (home.glowGrid.GroundGlowAt(lit) < MarineGeneMath.LightBlindGlow)
            {
                ctx.Check(!blind.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_Dazzled), "dazzle goes away in the dark");
            }
            else
            {
                ctx.Note("daytime: the test cell stays bright, dazzle removal not checked");
            }

            // 頭燈自動模式
            CompSuitLamp LampOn(Pawn p)
            {
                Apparel suit = (Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit));
                p.apparel.Wear(suit, dropReplacedApparel: false);
                return suit.GetComp<CompSuitLamp>();
            }
            ctx.Check(LampOn(plain).Resolved(plain) == CompSuitLamp.LampMode.White, "auto lamp: white for a baseliner");
            ctx.Check(LampOn(mild).Resolved(mild) == CompSuitLamp.LampMode.Dim, "auto lamp: dim red for the photophobic");
            CompSuitLamp blindLamp = LampOn(blind);
            ctx.Check(blindLamp.Resolved(blind) == CompSuitLamp.LampMode.Off, "auto lamp: off for the light-blind");
            blindLamp.Mode = CompSuitLamp.LampMode.White;
            ctx.Check(blindLamp.Resolved(blind) == CompSuitLamp.LampMode.White, "a chosen mode overrides auto");

            // 生物發光：水下地圖上亮，離開地圖就關
            DepthRimFixture.EnsureOcean();
            IntVec3 water = DepthRimFixture.OpenWaterCell();
            Pawn glow1 = WithGenes(DepthRimFixture.SpawnColonist(DepthRimFixture.Ocean, water), "DepthRim_Bioluminescent", "DepthRim_Gills");
            Gene_Bioluminescent glowGene = glow1.genes.GenesListForReading.OfType<Gene_Bioluminescent>().FirstOrDefault();
            ctx.Require(glowGene != null, "bioluminescent gene uses its own class");
            yield return new WaitTicks(80);
            ctx.Check(glowGene.Lit, "glows on the ocean map");
            glow1.DeSpawn();
            ctx.Check(!glowGene.Lit, "light removed when leaving the map");

            // 鰓乾燥：在地表放著會慢慢乾
            Pawn gills = WithGenes(DepthRimFixture.SpawnColonist(home, HomeCell(-6, 4)), "DepthRim_Gills");
            if (home.weatherManager.RainRate > 0.1f)
            {
                ctx.Note("raining on the home map: gill drying not checked");
            }
            else
            {
                yield return new WaitTicks(Gene_DepthRim.Interval * 3 + 20);
                ctx.Check(Severity(gills, DepthRimDefOf.DepthRim_GillDrying) > 0f, "gills dry out on land");
            }

            // 船員種族
            int converted = 0;
            for (int i = 0; i < 20; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, null, forceGenerateNewPawn: true, canGeneratePawnRelations: false, forcedXenotype: XenotypeDefOf.Baseliner));
                MarineXenotypes.TryAssignCrew(p);
                if (p.genes.Xenotype == MarineXenotypes.Mariner || p.genes.Xenotype == MarineXenotypes.Deepdiver)
                {
                    converted++;
                    ctx.Check(p.genes.HasActiveGene(Gene(p.genes.Xenotype == MarineXenotypes.Mariner ? "DepthRim_SeaLegs" : "DepthRim_Gills")), $"converted crew has the xenotype's genes ({p.genes.XenotypeLabel})");
                }
                p.Discard(true);
            }
            ctx.Check(converted > 0 && converted < 20, $"some baseliner crew become mariners or deepdivers ({converted}/20)");

            // 沉船基因包
            Genepack pack = null;
            for (int i = 0; i < 40 && pack == null; i++)
            {
                pack = MarineXenotypes.TryMakeShipwreckGenepack() as Genepack;
            }
            ctx.Require(pack != null, "shipwrecks can hold a genepack");
            var packed = pack.GeneSet.GenesListForReading;
            ctx.Check(packed.Count >= 1 && packed.Count <= 2, $"one or two genes ({packed.Count})");
            ctx.Check(packed.All(g => g.displayCategory?.defName == "DepthRim_Marine" && g.biostatArc == 0), "only non-archite marine genes");
            pack.Destroy();
        }
    }
}
