using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>出航前檢查的結果（可以同時有好幾項）。</summary>
    [Flags]
    public enum BoardingReadinessIssue
    {
        None = 0,

        /// <summary>沒穿有氣瓶的潛水服：泡在水裡會溺水。</summary>
        NoSuit = 1,

        /// <summary>氣瓶不到 <see cref="BoardingReadinessMath.LowAirPercent"/>。</summary>
        LowAir = 2,

        /// <summary>水壓抗性加適應值撐不住目的地的水壓：泡在水裡會受水壓傷。</summary>
        PressureShortfall = 4,

        /// <summary>適應值和目的地的壓力差超過容許範圍：抵達後會壓力不適。</summary>
        Maladapted = 8,
    }

    /// <summary>
    /// 玩家突擊艇出航前檢查的純計算（不碰遊戲物件，單元測試直接驗）。
    /// 目的地的壓力以那一格的水深層級為準：突擊艇降在海床上時是開放水域，切入艙室時艙壓通常也接近外面的水壓。
    /// </summary>
    public static class BoardingReadinessMath
    {
        /// <summary>氣瓶低於這個比例就提醒。</summary>
        public const float LowAirPercent = 0.5f;

        public static BoardingReadinessIssue Assess(bool flesh, bool suit, float airPercent, float resistance, float adapted, DepthTier destination)
        {
            if (!flesh)
            {
                return BoardingReadinessIssue.None;
            }
            BoardingReadinessIssue issues = BoardingReadinessIssue.None;
            if (!suit)
            {
                issues |= BoardingReadinessIssue.NoSuit;
            }
            else if (airPercent < LowAirPercent)
            {
                issues |= BoardingReadinessIssue.LowAir;
            }
            if (FloodingMath.PressureShortfall(destination, resistance, adapted) > 0f)
            {
                issues |= BoardingReadinessIssue.PressureShortfall;
            }
            if (PressureAdaptationMath.Mismatch(adapted, (int)destination) > 0f)
            {
                issues |= BoardingReadinessIssue.Maladapted;
            }
            return issues;
        }
    }

    /// <summary>
    /// 出航前的確認視窗：列出每位乘員的潛水服、氣瓶、水壓抗性與壓力適應是否撐得住目的地的深度。
    /// 有問題時「出航」按鈕標成危險，但不擋：玩家可能打算只在乾的艙室裡打。
    /// </summary>
    public static class BoardingReadiness
    {
        public static BoardingReadinessIssue Assess(Pawn pawn, DepthTier destination)
        {
            CompDivingAir air = CompDivingAir.WornBy(pawn);
            return BoardingReadinessMath.Assess(
                PressureAdaptationUtility.Applies(pawn),
                air != null,
                air?.AirPercent ?? 0f,
                pawn.GetStatValue(DepthRimDefOf.DepthRim_PressureResistance),
                PressureAdaptationUtility.AdaptedLevel(pawn),
                destination);
        }

        /// <summary>整份報告；<paramref name="anyIssue"/> 表示至少一位乘員有問題。</summary>
        public static string Report(IEnumerable<Pawn> crew, DepthTier destination, out bool anyIssue)
        {
            anyIssue = false;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("DepthRim.Readiness.Intro".Translate(destination.Label()));
            foreach (Pawn pawn in crew)
            {
                sb.AppendLine();
                BoardingReadinessIssue issues = Assess(pawn, destination);
                anyIssue |= issues != BoardingReadinessIssue.None;
                sb.AppendLine(PawnLine(pawn, issues, destination));
            }
            sb.AppendLine();
            sb.Append((anyIssue ? "DepthRim.Readiness.OutroWarn" : "DepthRim.Readiness.OutroOk").Translate());
            return sb.ToString();
        }

        private static string PawnLine(Pawn pawn, BoardingReadinessIssue issues, DepthTier destination)
        {
            string name = pawn.LabelShortCap.Colorize(ColoredText.NameColor);
            if (!PressureAdaptationUtility.Applies(pawn))
            {
                return name + ": " + "DepthRim.Readiness.NotFlesh".Translate();
            }
            CompDivingAir air = CompDivingAir.WornBy(pawn);
            float resistance = pawn.GetStatValue(DepthRimDefOf.DepthRim_PressureResistance);
            float adapted = PressureAdaptationUtility.AdaptedLevel(pawn);
            List<string> lines = new List<string>();

            // 潛水服與氣瓶
            if (air == null)
            {
                lines.Add(Bad("DepthRim.Readiness.NoSuit".Translate()));
            }
            else
            {
                string text = "DepthRim.Readiness.Suit".Translate(air.parent.LabelNoParenthesisCap, air.AirPercent.ToStringPercent());
                lines.Add(issues.HasFlag(BoardingReadinessIssue.LowAir) ? Warn(text + " " + "DepthRim.Readiness.LowAir".Translate()) : Good(text));
            }

            // 水壓抗性（裝備 + 適應值 對 水深層級）
            float shortfall = FloodingMath.PressureShortfall(destination, resistance, adapted);
            string pressure = "DepthRim.Readiness.Pressure".Translate(resistance.ToString("0.#"), adapted.ToString("0.0"), FloodingMath.RequiredPressureResistance(destination));
            lines.Add(shortfall > 0f
                ? Bad(pressure + "DepthRim.Readiness.PressureShort".Translate(shortfall.ToString("0.#")))
                : Good(pressure));

            // 壓力適應
            float mismatch = PressureAdaptationMath.Mismatch(adapted, (int)destination);
            string adaptation = "DepthRim.Readiness.Adaptation".Translate(adapted.ToString("0.0"), (int)destination);
            if (mismatch > 0f)
            {
                float filtration = pawn.health?.capacities?.GetLevel(PawnCapacityDefOf.BloodFiltration) ?? 1f;
                int ticks = PressureAdaptationMath.TicksUntilComfortable(adapted, (int)destination, filtration);
                lines.Add(Warn(adaptation + "DepthRim.Readiness.Maladapted".Translate(mismatch.ToString("0.0"), ticks.ToStringTicksToPeriod())));
            }
            else
            {
                lines.Add(Good(adaptation));
            }
            return name + "\n" + string.Join("\n", lines.Select(l => "    " + l));
        }

        private static string Good(string text) => ("✓ " + text).Colorize(ColorLibrary.Green);

        private static string Warn(string text) => ("! " + text).Colorize(ColorLibrary.Yellow);

        private static string Bad(string text) => ("✗ " + text).Colorize(ColorLibrary.RedReadable);

        /// <summary>打開確認視窗；按「出航」才呼叫 <paramref name="launch"/>。</summary>
        public static void Confirm(Building_BoardingCraft craft, PlanetTile destination, Action launch)
        {
            DepthTier tier = SubmarineLandingWorker.SiteSeafloor(destination);
            List<Pawn> crew = craft.Aboard.ToList();
            string text = Report(crew, tier, out bool anyIssue);
            Find.WindowStack.Add(new Dialog_MessageBox(
                text,
                "DepthRim.Readiness.Go".Translate(),
                () =>
                {
                    if (craft.Spawned && craft.Aboard.Any())
                    {
                        launch();
                    }
                },
                "CancelButton".Translate(),
                null,
                "DepthRim.Readiness.Title".Translate(),
                buttonADestructive: anyIssue));
        }
    }
}
