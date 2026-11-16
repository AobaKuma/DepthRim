using System;

namespace DepthRim
{
    /// <summary>冰型（設計稿 Docs/潛艇破冰出水_機制設計.md §3.1）。</summary>
    public enum IceKind : byte
    {
        /// <summary>沒有冰（開放海面）：可以常規上浮。</summary>
        None,
        /// <summary>一年冰（季節性海冰）。</summary>
        FirstYear,
        /// <summary>多年冰（原版海冰生態區）。</summary>
        MultiYear,
        /// <summary>冰棚（冰原）：頂不開。</summary>
        Shelf
    }

    /// <summary>頂冰的判定結果（§4.4）。</summary>
    public enum IceBreachResult : byte
    {
        /// <summary>冰比破冰結構的上限厚：不能嘗試。</summary>
        TooThick,
        /// <summary>R &lt; 0.75：頂不動。</summary>
        Stuck,
        /// <summary>0.75 ≤ R &lt; 1：冰被頂裂但沒穿。</summary>
        Partial,
        /// <summary>1 ≤ R &lt; 1.5：出水。</summary>
        Surfaced,
        /// <summary>R ≥ 1.5：猛烈出水。</summary>
        Violent
    }

    public struct IceBreachOutcome
    {
        public IceBreachResult result;
        public float ratio;
        public float breakForce;
        public float liftLoad;
        public float buoyancy;
        public float damagePerFrame;

        public bool Success => result == IceBreachResult.Surfaced || result == IceBreachResult.Violent;
    }

    /// <summary>
    /// 破冰出水的純計算（可單元測試；設計稿 §3、§4、§7.2）。單位：長度 m、力 N、應力 MPa、溫度 °C、時間為遊戲天。
    /// RimWorld 一年 60 天，冰凍度日換算成真實日當量（× 365/60），冰厚才和真實海冰同一個量級。
    /// </summary>
    public static class IceBreachMath
    {
        /// <summary>一格幾公尺（只影響裂紋長度與碎冰帶寬度）。</summary>
        public const float CellMeters = 2f;

        /// <summary>海水冰點（和 <c>OceanTemperature.FreezingPoint</c> 一致）。</summary>
        public const float FreezingPoint = -1.9f;

        public const float RealDaysPerGameDay = 365f / 60f;

        public const float DaysPerTwelfth = 5f;

        public const float LebedevCoefficient = 0.0133f;

        public const float LebedevExponent = 0.58f;

        /// <summary>融化度日每 °C·日融掉幾公尺。</summary>
        public const float MeltPerDegreeDay = 0.005f;

        public const float MaxIceThickness = 3.5f;

        public const float YoungsModulus = 5e9f;

        public const float PoissonRatio = 0.33f;

        public const float SeawaterDensity = 1025f;

        public const float IceDensity = 917f;

        public const float Gravity = 9.81f;

        /// <summary>Kerr 點載荷穿透係數。</summary>
        public const float BreakCoefficient = 3f;

        /// <summary>船體正上方真正被抬起的冰的比例。</summary>
        public const float LiftFraction = 0.3f;

        /// <summary>每格船體的儲備浮力（公噸）。</summary>
        public const float ReserveBuoyancyTonnesPerCell = 2.4f;

        /// <summary>部分開裂後一天內，冰的抗彎厚度打幾折。</summary>
        public const float CrackedFactor = 0.7f;

        public const float StuckBelow = 0.75f;

        public const float ViolentFrom = 1.5f;

        /// <summary>破冰結構受損的基準（每 2 MN 的 P_break 120 點）。</summary>
        public const float BaseDamage = 120f;

        public const float DamageForcePerUnit = 2e6f;

        /// <summary>下潛時船體四周的冰不能比這厚（m），不然船被凍住。</summary>
        public const float FrozenInThickness = 0.4f;

        /// <summary>重新結冰：薄於這個是開放水、到下一個門檻之前是新冰，再厚是完整冰。</summary>
        public const float NewIceFrom = 0.1f;

        public const float SolidIceFrom = 0.3f;

        /// <summary>裂紋在累積這麼多冰凍度日之後癒合。</summary>
        public const float CrackHealDegreeDays = 50f;

        // ── 冰厚 ─────────────────────────────────────────────────────────

        /// <summary>Lebedev 經驗式：累積冰凍度日（°C·真實日）→ 冰厚。</summary>
        public static float LebedevThickness(float freezingDegreeDays)
        {
            if (freezingDegreeDays <= 0f)
            {
                return 0f;
            }
            return LebedevCoefficient * (float)Math.Pow(freezingDegreeDays, LebedevExponent);
        }

        /// <summary>
        /// 一年冰的厚度：從一年裡最暖的那個十二分之一年的下一段累加到 <paramref name="currentTwelfth"/>（含），
        /// 冰凍度日長冰、融化度日（0 °C 以上）融冰，夾在 0～<see cref="MaxIceThickness"/>。
        /// </summary>
        public static float FirstYearThickness(float[] twelfthTemps, int currentTwelfth)
        {
            if (twelfthTemps == null || twelfthTemps.Length != 12)
            {
                return 0f;
            }
            int warmest = 0;
            for (int i = 1; i < 12; i++)
            {
                if (twelfthTemps[i] > twelfthTemps[warmest])
                {
                    warmest = i;
                }
            }
            float fdd = 0f;
            float mdd = 0f;
            int steps = ((currentTwelfth - warmest) % 12 + 12) % 12;
            for (int k = 1; k <= steps; k++)
            {
                float t = twelfthTemps[(warmest + k) % 12];
                fdd += Math.Max(0f, FreezingPoint - t) * DaysPerTwelfth * RealDaysPerGameDay;
                mdd += Math.Max(0f, t) * DaysPerTwelfth * RealDaysPerGameDay;
            }
            float h = LebedevThickness(fdd) - MeltPerDegreeDay * mdd;
            return Clamp(h, 0f, MaxIceThickness);
        }

        /// <summary>
        /// 一格的冰厚：一年冰厚度，多年冰另加 1～2 m（<paramref name="multiYearNoise"/> 0～1），再乘上格內雜訊 0.8～1.2（<paramref name="localNoise"/> 0～1）；
        /// 在冰間水道裡只剩 max(0.1, h × 0.25)。沒有冰或冰棚回傳 0（冰棚另外擋）。
        /// </summary>
        public static float Thickness(IceKind kind, float firstYear, float multiYearNoise, float localNoise, bool inLead)
        {
            if (kind != IceKind.FirstYear && kind != IceKind.MultiYear)
            {
                return 0f;
            }
            float h = firstYear;
            if (kind == IceKind.MultiYear)
            {
                h += 1f + Clamp(multiYearNoise, 0f, 1f);
            }
            h *= 0.8f + 0.4f * Clamp(localNoise, 0f, 1f);
            if (h <= 0f)
            {
                return 0f;
            }
            if (inLead)
            {
                h = Math.Max(0.1f, h * 0.25f);
            }
            return Clamp(h, 0f, MaxIceThickness);
        }

        /// <summary>這一季有沒有冰間水道：基本 15%，季節交替（每季的第一個十二分之一年）再加 25%。</summary>
        public static bool HasLead(float roll, bool seasonChange)
        {
            return roll < (seasonChange ? 0.40f : 0.15f);
        }

        // ── 強度與力 ─────────────────────────────────────────────────────

        /// <summary>抗彎強度（MPa）：越冷越硬，上限 0.75。</summary>
        public static float FlexuralStrength(float airTemperature)
        {
            return Math.Min(0.75f, 0.40f + 0.006f * Math.Max(0f, -airTemperature));
        }

        /// <summary>特徵長度（m）：彈性地基上薄板「彎多遠才斷」。</summary>
        public static float CharacteristicLength(float thickness)
        {
            if (thickness <= 0f)
            {
                return 0f;
            }
            double d = YoungsModulus * Math.Pow(thickness, 3) / (12.0 * (1.0 - PoissonRatio * PoissonRatio) * SeawaterDensity * Gravity);
            return (float)Math.Pow(d, 0.25);
        }

        public static float CharacteristicLengthCells(float thickness)
        {
            return CharacteristicLength(thickness) / CellMeters;
        }

        /// <summary>彎斷冰板需要的力（N）。</summary>
        public static float BreakForce(float flexuralStrengthMPa, float bendingThickness)
        {
            return BreakCoefficient * flexuralStrengthMPa * 1e6f * bendingThickness * bendingThickness;
        }

        /// <summary>把船體正上方的冰推出水面的力（N）。</summary>
        public static float LiftLoad(float thickness, int hullCells)
        {
            return LiftFraction * IceDensity * Gravity * thickness * hullCells * CellMeters * CellMeters;
        }

        /// <summary>緊急吹除主壓載的上浮力（N）。</summary>
        public static float Buoyancy(int hullCells)
        {
            return hullCells * ReserveBuoyancyTonnesPerCell * 1000f * Gravity;
        }

        /// <summary>破冰結構血量低於一半時，可破冰厚上限減半。</summary>
        public static float FrameLimit(float baseLimit, float healthFraction)
        {
            return healthFraction < 0.5f ? baseLimit * 0.5f : baseLimit;
        }

        /// <summary>
        /// 判定（§4.4）。<paramref name="cracked"/>：一天內被頂裂過，抗彎厚度打 7 折（抬起的冰一樣重）。
        /// <paramref name="frames"/> 座破冰結構平分傷害（R 不變）。
        /// </summary>
        public static IceBreachOutcome Evaluate(float thickness, float airTemperature, int hullCells, bool cracked, float frameLimit, int frames)
        {
            IceBreachOutcome o = new IceBreachOutcome();
            float sigma = FlexuralStrength(airTemperature);
            float bend = cracked ? thickness * CrackedFactor : thickness;
            o.breakForce = BreakForce(sigma, bend);
            o.liftLoad = LiftLoad(thickness, hullCells);
            o.buoyancy = Buoyancy(hullCells);
            float need = o.breakForce + o.liftLoad;
            o.ratio = need > 0f ? o.buoyancy / need : float.PositiveInfinity;
            if (thickness > frameLimit)
            {
                o.result = IceBreachResult.TooThick;
                return o;
            }
            float multiplier;
            if (o.ratio < StuckBelow)
            {
                o.result = IceBreachResult.Stuck;
                multiplier = 1.5f;
            }
            else if (o.ratio < 1f)
            {
                o.result = IceBreachResult.Partial;
                multiplier = 1.3f;
            }
            else
            {
                o.result = o.ratio >= ViolentFrom ? IceBreachResult.Violent : IceBreachResult.Surfaced;
                multiplier = 1f;
            }
            o.damagePerFrame = BaseDamage * (o.breakForce / DamageForcePerUnit) * multiplier / Math.Max(1, frames);
            return o;
        }

        // ── 重新結冰 ─────────────────────────────────────────────────────

        /// <summary>一段時間（遊戲天）在某個氣溫下累積的冰凍度日（°C·真實日）。</summary>
        public static float FreezingDegreeDays(float airTemperature, float gameDays)
        {
            return Math.Max(0f, FreezingPoint - airTemperature) * gameDays * RealDaysPerGameDay;
        }

        /// <summary>破口重新結冰：累積冰凍度日 → 冰厚。</summary>
        public static float RefrozenThickness(float freezingDegreeDays)
        {
            return Math.Min(MaxIceThickness, LebedevThickness(freezingDegreeDays));
        }

        /// <summary>結到 <paramref name="thickness"/> 要幾個遊戲天（給說明與測試；不結冰時回傳無限大）。</summary>
        public static float DaysToFreeze(float thickness, float airTemperature)
        {
            float perDay = FreezingDegreeDays(airTemperature, 1f);
            if (perDay <= 0f)
            {
                return float.PositiveInfinity;
            }
            double fdd = Math.Pow(thickness / LebedevCoefficient, 1.0 / LebedevExponent);
            return (float)(fdd / perDay);
        }

        private static float Clamp(float v, float min, float max)
        {
            return v < min ? min : (v > max ? max : v);
        }
    }
}
