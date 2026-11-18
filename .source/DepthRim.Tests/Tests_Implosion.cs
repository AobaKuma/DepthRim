using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 水中內爆（Docs/水中內爆_實作計畫.md §8.1）：判定、燙傷轉換、氣泡曲線、依水深縮放、粒子與音量的範圍；排除名單與燙傷 Def 的內容。
static partial class Test
{
    static void ImplosionMathTests()
    {
        // 判定
        Check(ImplosionMath.ShouldImplode(false, true, true, true, 0f), "open water implodes");
        Check(ImplosionMath.ShouldImplode(false, true, true, false, 0.8f), "a compartment at 80% implodes");
        Check(ImplosionMath.ShouldImplode(false, true, true, false, ImplosionMath.MinLevel), "the threshold itself implodes");
        Check(!ImplosionMath.ShouldImplode(false, true, true, false, 0.5f), "a half-flooded compartment explodes normally");
        Check(!ImplosionMath.ShouldImplode(true, true, true, true, 1f), "exempt damage types explode normally");
        Check(!ImplosionMath.ShouldImplode(false, false, true, true, 1f), "explosions without visuals are left alone");
        Check(!ImplosionMath.ShouldImplode(false, true, false, true, 1f), "maps without water explode normally");
        Check(ImplosionMath.MinLevel == FloodingMath.SubmergedLevelStanding, "the threshold is the standing submerged line (0.75)");

        // 燙傷轉換
        Check(ImplosionMath.ConvertsToScald(isFlameDef: true, hasFlameWorker: true, keepDamageType: false, alreadyScald: false), "Flame becomes scald");
        Check(ImplosionMath.ConvertsToScald(false, true, false, false), "a modded flame-worker damage becomes scald");
        Check(!ImplosionMath.ConvertsToScald(false, false, false, false), "Bomb / tox stay as they are");
        Check(!ImplosionMath.ConvertsToScald(true, true, true, false), "keepDamageType keeps the flame");
        Check(!ImplosionMath.ConvertsToScald(false, false, false, true), "scald is not converted again");

        // 氣泡曲線
        float period = ImplosionMath.Period(ImplosionMath.Energy(50, 3f), DepthTier.Shallow);
        Check(period > 0.4f && period < 0.7f, $"a 3-radius bomb in the shallows pulses in about half a second ({period})");
        float rebound = ImplosionMath.ReboundRatio(DepthTier.Shallow);
        Check(ImplosionMath.BubbleScale(0f, period, rebound) == 0f, "the bubble starts at zero");
        Near(ImplosionMath.BubbleScale(period * ImplosionMath.ExpandFraction, period, rebound), 1f, "the bubble peaks at the end of the expansion");
        Near(ImplosionMath.BubbleScale(period, period, rebound), 0f, "the bubble has collapsed at one period");
        Near(ImplosionMath.BubbleScale(period * (1f + ImplosionMath.ReboundDurationFactor / 2f), period, rebound), rebound, "the rebound peaks at the rebound ratio");
        Near(ImplosionMath.BubbleScale(ImplosionMath.TotalBubbleSeconds(period), period, rebound), 0f, "the bubble is gone at the end");
        Check(ImplosionMath.BubbleScale(ImplosionMath.TotalBubbleSeconds(period) + 0.01f, period, rebound) == 0f, "nothing after the end");
        float max = 0f;
        bool nonNegative = true;
        for (int i = 0; i <= 200; i++)
        {
            float s = ImplosionMath.BubbleScale(ImplosionMath.TotalBubbleSeconds(period) * i / 200f, period, rebound);
            nonNegative &= s >= 0f;
            max = Math.Max(max, s);
        }
        Check(nonNegative && max <= 1.0001f, $"the curve stays within 0..1 (max {max})");
        Check(ImplosionMath.BubbleAlpha(period * 0.5f, period) > ImplosionMath.BubbleAlpha(period * 1.2f, period), "the rebound is fainter than the first pulse");
        Check(ImplosionMath.BubbleAlpha(ImplosionMath.TotalBubbleSeconds(period), period) == 0f && ImplosionMath.BubbleAlpha(0f, period) == 0f, "the bubble is invisible before and after");
        float ring = ImplosionMath.RingSeconds(period);
        Check(ring < period * ImplosionMath.ExpandFraction, "the shock ring outruns the bubble");
        Near(ImplosionMath.RingRadius(ring, period, 3f), 3f * 1.6f, "the ring reaches 1.6x the blast radius");
        Check(ImplosionMath.RingRadius(ring * 0.5f, period, 3f) > 3f * 1.6f * 0.5f, "the ring slows down as it spreads");
        Check(ImplosionMath.RingAlpha(ring * 0.1f, period) > ImplosionMath.RingAlpha(ring * 0.9f, period) && ImplosionMath.RingAlpha(ring, period) == 0f, "the ring fades out as it spreads");
        Near(ImplosionMath.RimBrightness(period, period), 1f, "the rim is brightest at the collapse");
        Check(ImplosionMath.RimBrightness(0f, period) == 0f, "the rim is dark at the start");

        // 越深：氣泡越小、越快、反彈越弱
        DepthTier[] tiers = (DepthTier[])Enum.GetValues(typeof(DepthTier));
        for (int i = 1; i < tiers.Length; i++)
        {
            Check(ImplosionMath.MaxBubbleRadius(3f, tiers[i]) < ImplosionMath.MaxBubbleRadius(3f, tiers[i - 1]), $"{tiers[i]} bubble is smaller than {tiers[i - 1]}");
            Check(ImplosionMath.Period(150f, tiers[i]) < ImplosionMath.Period(150f, tiers[i - 1]), $"{tiers[i]} collapses faster than {tiers[i - 1]}");
            Check(ImplosionMath.ReboundRatio(tiers[i]) < ImplosionMath.ReboundRatio(tiers[i - 1]), $"{tiers[i]} rebounds less than {tiers[i - 1]}");
        }
        Check(ImplosionMath.MaxBubbleRadius(0.1f, DepthTier.Seabed) >= 0.6f, "tiny explosions still show a bubble");
        float big = ImplosionMath.Period(ImplosionMath.Energy(550, 9f), DepthTier.Shallow);
        Check(big <= 1.2f && ImplosionMath.Period(1f, DepthTier.Seabed) >= 0.25f, $"the period stays within 0.25..1.2 s ({big})");

        // 粒子、音量、震動
        Check(ImplosionMath.MaxParticles(0.5f) == 6 && ImplosionMath.MaxParticles(20f) == 60, "particle count is capped at 6..60");
        Check(ImplosionMath.CellParticleChance(20f) < ImplosionMath.CellParticleChance(2f), "big explosions throw fewer particles per cell");
        Check(ImplosionMath.CellParticleChance(1f) <= 1f, "the per-cell chance is a probability");
        Check(ImplosionMath.VolumeFactor(0f) == 0.6f && ImplosionMath.VolumeFactor(100f) == 1.4f, "volume stays within 0.6..1.4");
        Check(ImplosionMath.PitchFactor(9f, DepthTier.Seabed) < ImplosionMath.PitchFactor(2f, DepthTier.Shallow), "bigger and deeper sounds lower");
        Check(ImplosionMath.PitchFactor(100f, DepthTier.Seabed) == 0.7f, "pitch has a floor");
        Near(ImplosionMath.Shake(3f, 1f, 10f), 4f * 3f * ImplosionMath.ShakeFactor / 10f, "shake follows the vanilla formula times the water factor");
        Check(ImplosionMath.Shake(3f, 1f, 0f) == 0f, "no shake without a camera distance");

        // 泡沫團（參考影片：氣泡塌成一團白色泡沫，之後慢慢上浮散掉）
        float col = ImplosionMath.ColumnSeconds(false);
        Check(ImplosionMath.FoamScale(period * 0.5f, period, col) == 0f && ImplosionMath.FoamAlpha(period * 0.5f, period, col) == 0f, "no foam while the bubble expands");
        Near(ImplosionMath.FoamScale(period, period, col), 0.45f, "the bubble collapses into foam a bit under half its size");
        Check(ImplosionMath.FoamScale(period + col, period, col) > ImplosionMath.FoamScale(period, period, col), "the foam swells while it lingers");
        Check(ImplosionMath.FoamAlpha(period + col * 0.3f, period, col) > 0.8f, "the foam is dense right after the collapse");
        Check(ImplosionMath.FoamAlpha(period + col * 0.8f, period, col) < ImplosionMath.FoamAlpha(period + col * 0.3f, period, col) && ImplosionMath.FoamAlpha(period + col, period, col) == 0f, "the foam fades out by the end of the column");
        Check(ImplosionMath.BubbleAlpha(period * 0.99f, period) < ImplosionMath.BubbleAlpha(period * 0.5f, period) * 0.5f, "the gas bubble hands over to the foam at the collapse");
        Check(ImplosionMath.FoamRise(period + 1f, period) > 0f && ImplosionMath.FoamRise(period * 0.5f, period) == 0f, "the foam drifts up only after the collapse");

        // 光照：暗水裡調暗但看得見；晃動在崩塌時最大、平常很小
        // 混進水的混濁：清水裡原色、最混濁也只混 65%（還看得見），不透明度最多少 35%
        Check(ImplosionMath.MurkBlend(0f) == 0f && ImplosionMath.MurkBlend(1f) == 0.65f && ImplosionMath.MurkBlend(5f) == 0.65f, "murk blend runs 0..0.65 with turbidity");
        Near(ImplosionMath.MurkAlpha(1f, 1f), 0.65f, "the murkiest water still leaves the implosion two-thirds opaque");
        Check(ImplosionMath.MurkAlpha(0.8f, 0f) == 0.8f, "clear water keeps the implosion's opacity");
        Check(ImplosionMath.MurkLightness(0f, 0.05f, 0.6f) == 0f && ImplosionMath.MurkLightness(1f, 0.05f, 0.6f) == 1f, "murk colour runs from deep to water colour with light");
        float calm = 0f, collapse = 0f;
        for (int i = 0; i < 100; i++)
        {
            float t = period * 0.3f * i / 100f;
            calm = Math.Max(calm, Math.Abs(ImplosionMath.Wobble(t, period, 1f)));
            collapse = Math.Max(collapse, Math.Abs(ImplosionMath.Wobble(period * 0.9f + period * 0.2f * i / 100f, period, 1f)));
        }
        Check(calm <= 0.05f && collapse > calm && collapse <= 0.11f, $"the surface wobbles slightly, most at the collapse ({calm:F3} vs {collapse:F3})");
        Check(ImplosionMath.Wobble(0.2f, period, 0f) != ImplosionMath.Wobble(0.2f, period, 2f), "each implosion wobbles in its own phase");

        // 往中心吸的粒子：崩塌時剛好抵達；崩塌後丟的也至少飛一下
        Near(ImplosionMath.InwardSpeed(2f, 0.1f, 0.5f) * ImplosionMath.SecondsToCollapse(0.1f, 0.5f), 2f, "an inward particle reaches the centre at the collapse");
        Check(ImplosionMath.SecondsToCollapse(0.9f, 0.5f) >= 0.08f, "late particles still get a short flight");
        Check(ImplosionMath.InwardSpeed(-1f, 0f, 0.5f) == 0f, "no negative speed");
        // 氣泡柱與混濁水團
        Check(ImplosionMath.ColumnSeconds(true) > ImplosionMath.ColumnSeconds(false), "scald bubbles longer (the water boils)");
        Check(ImplosionMath.ColumnIntervalTicks(true, false) < ImplosionMath.ColumnIntervalTicks(false, false), "scald bubbles more densely");
        Check(ImplosionMath.ColumnIntervalTicks(false, true) == 2 * ImplosionMath.ColumnIntervalTicks(false, false), "simplified implosions bubble half as often");
        int columnBubbles = (int)(ImplosionMath.ColumnSeconds(true) * 60f) / ImplosionMath.ColumnIntervalTicks(true, false);
        Check(columnBubbles <= 60, $"a scald column stays within the particle budget ({columnBubbles})");
        Check(ImplosionMath.MurkCount(0.5f) == 2 && ImplosionMath.MurkCount(30f) == 8, "murk puffs are capped at 2..8");
    }

    static void ContentImplosion()
    {
        // 排除名單：只有 EMP、煙霧、眩暈、消防泡沫（2026-11-18 決定）
        XDocument patch = XDocument.Load(Path.Combine(ModRoot, "1.7", "Patches", "Implosion_DamageDefs.xml"));
        string[] exempt = patch.Root.Elements("Operation")
            .Select(op => (string)op.Element("xpath"))
            .Select(x => x.Substring(x.IndexOf("defName=\"", StringComparison.Ordinal) + 9).TrimEnd(']', '"'))
            .ToArray();
        Check(exempt.OrderBy(s => s).SequenceEqual(new[] { "EMP", "Extinguish", "Smoke", "Stun" }), $"exempt damage types are EMP, Smoke, Stun, Extinguish ({string.Join(",", exempt)})");
        foreach (XElement op in patch.Root.Elements("Operation"))
        {
            XElement li = op.Element("match")?.Element("value")?.Element("li");
            Check((string)li?.Attribute("Class") == "DepthRim.UnderwaterImplosionExtension" && (string)li.Element("exempt") == "true", $"{(string)op.Element("xpath")} adds an exempt extension");
        }

        // 燙傷：繼承 Flame，但不用火焰 worker（不會起火）
        XElement scald = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "Implosion", "Implosion_Damage.xml")).Root.Elements("DamageDef")
            .Single(e => (string)e.Element("defName") == "DepthRim_Scald");
        Check((string)scald.Attribute("ParentName") == "Flame", "scald inherits Flame (Burn hediff, Heat armor)");
        Check((string)scald.Element("workerClass") == "DamageWorker_AddInjury", "scald uses the plain injury worker, so it starts no fires");
        Check(scald.Element("modExtensions").Elements("li").Any(li => (string)li.Element("scald") == "true"), "scald is marked as scald");
        Check(!typeof(Verse.DamageWorker_Flame).IsAssignableFrom(typeof(Verse.DamageWorker_AddInjury)), "AddInjury is not a flame worker");

        // 粒子：四個 FleckDef 都在；畫在兩種水層（艙室 2950、外海 3177）之上（顏色由程式混進水色）；模組自己的貼圖存在
        XElement[] flecks = XDocument.Load(Path.Combine(ModRoot, "1.7", "Defs", "Implosion", "Implosion_Effects.xml")).Root.Elements("FleckDef").ToArray();
        foreach (string name in new[] { "DepthRim_ImplosionFlash", "DepthRim_ImplosionInward", "DepthRim_ImplosionBubble", "DepthRim_ImplosionMurk" })
        {
            XElement def = flecks.SingleOrDefault(e => (string)e.Element("defName") == name);
            if (!Check(def != null, $"{name} is defined")) { continue; }
            XElement[] graphics = def.Element("graphicData") != null ? new[] { def.Element("graphicData") } : def.Element("randomGraphics").Elements("li").ToArray();
            foreach (XElement g in graphics)
            {
                Check((int?)g.Element("renderQueue") > 3177, $"{name} draws over the water layers (and is tinted into the murk in code)");
                string tex = (string)g.Element("texPath");
                if (tex.StartsWith("DepthRim/"))
                {
                    Check(File.Exists(Path.Combine(ModRoot, "Textures", tex + ".png")), $"{name}: texture {tex} exists");
                }
            }
        }
        foreach (string warp in new[] { "DepthRim_ImplosionWarpOut", "DepthRim_ImplosionWarpIn" })
        {
            XElement def = flecks.SingleOrDefault(e => (string)e.Element("defName") == warp);
            Check(def != null && (string)def.Element("graphicData")?.Element("shaderType") == "DepthRim_ImplosionWarp", $"{warp} uses the copy of the vanilla psychic distortion shader");
        }
        Near(ImplosionMath.WarpInSeconds(1f), 0.75f, "a long pulse warps in a quarter second before the collapse");
        Check(ImplosionMath.WarpInSeconds(0.5f) == 0.5f * ImplosionMath.ExpandFraction, "a short pulse warps in no earlier than the bubble peak");
        Check(ImplosionAssets.RenderQueue > 3177, $"the bubble, ring and foam draw over the water layers ({ImplosionAssets.RenderQueue})");
        foreach (string tex in new[] { "ShockRing", "Foam" })
        {
            Check(File.Exists(Path.Combine(ModRoot, "Textures", "DepthRim", "Implosion", tex + ".png")), $"bubble renderer texture {tex} exists");
        }
    }
}
