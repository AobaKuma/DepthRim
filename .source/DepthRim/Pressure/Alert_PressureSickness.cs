using System.Collections.Generic;
using System.Text;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 警示：有殖民者正在壓力不適（適應值和所在環境差超過 1 級）。
    /// 說明裡列出每個人的適應值、環境壓力，以及環境不變的話還要多久才會好。
    /// </summary>
    public class Alert_PressureSickness : Alert
    {
        /// <summary>低於這個嚴重度不提醒（剛好在容許範圍邊緣的浮點誤差）。</summary>
        public const float MinMismatch = 0.02f;

        private readonly List<Pawn> sick = new List<Pawn>();

        public Alert_PressureSickness()
        {
            defaultLabel = "DepthRim.Alert.PressureSickness".Translate();
            defaultPriority = AlertPriority.High;
        }

        /// <summary>目前壓力不適的殖民者（所有地圖）。</summary>
        public List<Pawn> SickColonists
        {
            get
            {
                sick.Clear();
                List<Map> maps = Find.Maps;
                for (int m = 0; m < maps.Count; m++)
                {
                    List<Pawn> colonists = maps[m].mapPawns.FreeColonistsSpawned;
                    for (int i = 0; i < colonists.Count; i++)
                    {
                        Hediff_PressureAdaptation hediff = PressureAdaptationUtility.Get(colonists[i]);
                        if (hediff != null && hediff.Mismatch >= MinMismatch)
                        {
                            sick.Add(colonists[i]);
                        }
                    }
                }
                return sick;
            }
        }

        public override TaggedString GetExplanation()
        {
            StringBuilder list = new StringBuilder();
            List<Pawn> pawns = SickColonists;
            for (int i = 0; i < pawns.Count; i++)
            {
                Hediff_PressureAdaptation hediff = PressureAdaptationUtility.Get(pawns[i]);
                int left = hediff.TicksUntilComfortable;
                string time = left == PressureAdaptationMath.Never ? "DepthRim.Adaptation.NeverShort".Translate().ToString() : left.ToStringTicksToPeriod();
                list.AppendLine("  - " + "DepthRim.Alert.PressureSickness.Line".Translate(
                    pawns[i].LabelShortCap, hediff.Adapted.ToString("0.0"), hediff.Ambient.ToString("0.#"), time));
            }
            return "DepthRim.Alert.PressureSickness.Desc".Translate(list.ToString().TrimEndNewlines());
        }

        public override AlertReport GetReport()
        {
            return AlertReport.CulpritsAre(SickColonists);
        }
    }
}
