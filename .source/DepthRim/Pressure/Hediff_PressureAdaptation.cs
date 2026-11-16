using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 壓力適應（<c>DepthRim_PressureAdaptation</c>）：記住這個角色的適應值，並把「適應值和環境差超過 1 級」的部分當成嚴重度。
    ///
    /// <para>嚴重度 = <see cref="PressureAdaptationMath.Mismatch"/>（沒有不適時維持在 <see cref="RestingSeverity"/>，
    /// 讓第一個沒有效果的階段顯示目前的適應值）。適應值存在這個 hediff 上，跟著角色跨地圖、進商隊、存檔。
    /// 適應值回到 0 而且環境也是常壓時自己移除。</para>
    ///
    /// <para>自己每 <see cref="UpdateInterval"/> ticks 結算一次（不靠水位系統），所以回到地表後也會繼續往常壓適應。
    /// 環境壓力由 <see cref="PressureEnvironment"/> 決定：開放水域是水深層級，艙室是艙壓與水壓取大的。</para>
    /// </summary>
    public class Hediff_PressureAdaptation : Hediff
    {
        public const int UpdateInterval = 250;

        /// <summary>沒有不適時的嚴重度（不能是 0：原版把嚴重度 0 的 hediff 當成該移除）。</summary>
        public const float RestingSeverity = 0.001f;

        /// <summary>適應值低於這個數字、環境又是常壓時移除。</summary>
        public const float ForgetBelow = 0.01f;

        private float adapted;

        private float ambient;

        private int pendingTicks;

        private int reachabilityKey = int.MinValue;

        /// <summary>目前的適應值（0–3）。</summary>
        public float Adapted => adapted;

        /// <summary>最近一次結算時所在環境的壓力等級。</summary>
        public float Ambient => ambient;

        public float Mismatch => PressureAdaptationMath.Mismatch(adapted, ambient);

        /// <summary>血液過濾能力：決定適應速度。</summary>
        public float BloodFiltration => pawn?.health?.capacities?.GetLevel(PawnCapacityDefOf.BloodFiltration) ?? 1f;

        public override bool ShouldRemove => adapted < ForgetBelow && ambient <= 0f;

        public override string LabelInBrackets
        {
            get
            {
                string stage = base.LabelInBrackets;
                string mine = "DepthRim.Adaptation.Bracket".Translate(adapted.ToString("0.0"), ambient.ToString("0.#"));
                return stage.NullOrEmpty() ? mine : stage + ", " + mine;
            }
        }

        public override string TipStringExtra
        {
            get
            {
                string s = base.TipStringExtra;
                string line = "DepthRim.Adaptation.Rate".Translate(PressureAdaptationMath.DaysPerLevel(BloodFiltration).ToString("0.#"));
                int left = PressureAdaptationMath.TicksUntilComfortable(adapted, ambient, BloodFiltration);
                if (left > 0)
                {
                    line += "\n" + "DepthRim.Adaptation.TimeLeft".Translate(left.ToStringTicksToPeriod());
                }
                return s.NullOrEmpty() ? line : s + "\n" + line;
            }
        }

        /// <summary>直接設定適應值與環境（剛加上時的初始值、測試、除錯）。</summary>
        public void SetState(float newAdapted, float newAmbient)
        {
            adapted = Mathf.Clamp(newAdapted, 0f, PressureAdaptationMath.MaxLevel);
            ambient = Mathf.Clamp(newAmbient, 0f, PressureAdaptationMath.MaxLevel);
            RefreshSeverity();
        }

        public override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            pendingTicks += delta;
            if (pendingTicks >= UpdateInterval)
            {
                int ticks = pendingTicks;
                pendingTicks = 0;
                Advance(ticks);
            }
        }

        /// <summary>結算 <paramref name="ticks"/>：讀目前的環境壓力，適應值往它移動，更新嚴重度。</summary>
        public void Advance(int ticks)
        {
            float? now = PressureEnvironment.For(pawn);
            if (now == null)
            {
                return;
            }
            ambient = now.Value;
            adapted = PressureAdaptationMath.Step(adapted, ambient, BloodFiltration, ticks);
            RefreshSeverity();
            // 水密門依適應值決定放不放行；適應值每變 0.1 級，清一次這張地圖的可達性快取（有加壓機才需要）
            int key = Mathf.RoundToInt(adapted * 10f);
            if (key != reachabilityKey)
            {
                reachabilityKey = key;
                Map map = pawn.MapHeld;
                if (PressureEnvironment.HasPressureDifferences(map))
                {
                    map.reachability.ClearCache();
                }
            }
        }

        private void RefreshSeverity()
        {
            float target = Mathf.Max(RestingSeverity, Mismatch);
            if (!Mathf.Approximately(Severity, target))
            {
                Severity = target;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref adapted, "adapted", 0f);
            Scribe_Values.Look(ref ambient, "ambient", 0f);
            Scribe_Values.Look(ref pendingTicks, "pendingTicks", 0);
        }
    }

    /// <summary>
    /// 某個位置的環境壓力。兩種單位：等級（0–3，適應值用）與絕對壓力（常壓 0 … 海床 8，牆體水壓用，見 <see cref="PressureAdaptationMath.GaugeOfLevel"/>）。
    /// <list type="bullet">
    /// <item>開放水域：地圖的水深層級。</item>
    /// <item>地表的外面：0。</item>
    /// <item>密閉艙室：艙壓（<see cref="MapComponent_PressureChambers"/>）和水壓（水位 × 層級水壓）取大的。乾艙就是它記住的艙壓。</item>
    /// <item>門口：兩側取大的。</item>
    /// </list>
    /// </summary>
    public static class PressureEnvironment
    {
        /// <summary>這張地圖上可能有壓差（水下地圖，或有艙壓的艙室）：水密門才需要看壓差。</summary>
        public static bool HasPressureDifferences(Map map)
        {
            if (map == null)
            {
                return false;
            }
            MapComponent_Flooding flooding = map.Flooding();
            if (flooding != null && flooding.Active && flooding.DepthTier != DepthTier.Shallow)
            {
                return true;
            }
            return map.GetComponent<MapComponent_PressureChambers>()?.AnyPressure ?? false;
        }

        /// <summary>地圖「外面」的壓力等級：水下地圖是開放水域的水深層級，其他地圖是 0。</summary>
        public static float AmbientOf(Map map)
        {
            MapComponent_Flooding flooding = map?.Flooding();
            return flooding != null && flooding.Active ? (int)flooding.DepthTier : 0f;
        }

        /// <summary>這一格的壓力等級。</summary>
        public static float At(Map map, IntVec3 cell)
        {
            if (map == null || !cell.InBounds(map))
            {
                return 0f;
            }
            Room room = cell.GetRoom(map);
            if (room != null && room.IsDoorway)
            {
                float max = 0f;
                bool any = false;
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 n = cell + GenAdj.CardinalDirections[i];
                    Room r = n.InBounds(map) ? n.GetRoom(map) : null;
                    if (r != null && !r.IsDoorway)
                    {
                        max = Mathf.Max(max, LevelOf(map, r));
                        any = true;
                    }
                }
                return any ? max : AmbientOf(map);
            }
            return LevelOf(map, room);
        }

        /// <summary>一間房間的壓力等級（null = 牆裡、深淵：算外面）。</summary>
        public static float LevelOf(Map map, Room room)
        {
            return PressureAdaptationMath.LevelOfGauge(GaugeOf(map, room));
        }

        /// <summary>一間房間的絕對壓力（牆體水壓用）。</summary>
        public static float GaugeOf(Map map, Room room)
        {
            MapComponent_Flooding flooding = map?.Flooding();
            bool water = flooding != null && flooding.Active;
            float sea = water ? FloodingMath.PressureFactor(flooding.DepthTier) : 0f;
            if (room == null)
            {
                return sea;
            }
            if (water ? flooding.IsOpenWater(room) : MapComponent_PressureChambers.IsOutside(room))
            {
                return sea;
            }
            float air = map.GetComponent<MapComponent_PressureChambers>()?.AirOf(room) ?? 0f;
            float gauge = PressureAdaptationMath.GaugeOfLevel(air);
            return water ? Mathf.Max(gauge, flooding.LevelOf(room) * sea) : gauge;
        }

        /// <summary>角色所在環境的壓力；商隊算常壓；不在任何地圖上也不在商隊裡（運輸途中等）時回傳 null，適應值暫停。</summary>
        public static float? For(Pawn pawn)
        {
            if (pawn == null)
            {
                return null;
            }
            Map map = pawn.MapHeld;
            if (map != null)
            {
                return At(map, pawn.PositionHeld);
            }
            return pawn.IsCaravanMember() ? 0f : (float?)null;
        }
    }

    public static class PressureAdaptationUtility
    {
        /// <summary>壓力適應只影響血肉角色（機械體不用適應）。</summary>
        public static bool Applies(Pawn pawn)
        {
            return pawn != null && !pawn.Dead && pawn.RaceProps.IsFlesh;
        }

        public static Hediff_PressureAdaptation Get(Pawn pawn)
        {
            return pawn?.health?.hediffSet?.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_PressureAdaptation) as Hediff_PressureAdaptation;
        }

        /// <summary>角色目前的適應值；沒有 hediff 就是 0（常壓）。</summary>
        public static float AdaptedLevel(Pawn pawn)
        {
            return Get(pawn)?.Adapted ?? 0f;
        }

        /// <summary>
        /// 角色進到非常壓的環境時呼叫：還沒有適應 hediff 就加上。玩家的角色從 0 開始；
        /// 其他陣營與野生動物視為本來就住在這個深度，直接從環境壓力開始（襲擊者不會一上船就不適）。
        /// </summary>
        public static Hediff_PressureAdaptation Ensure(Pawn pawn, float ambient)
        {
            if (!Applies(pawn))
            {
                return null;
            }
            Hediff_PressureAdaptation existing = Get(pawn);
            if (existing != null)
            {
                return existing;
            }
            Hediff_PressureAdaptation hediff = (Hediff_PressureAdaptation)HediffMaker.MakeHediff(DepthRimDefOf.DepthRim_PressureAdaptation, pawn);
            bool native = pawn.Faction == null || !pawn.Faction.IsPlayer;
            hediff.SetState(native ? ambient : 0f, ambient);
            pawn.health.AddHediff(hediff);
            return hediff;
        }

        /// <summary>壓力等級的名稱：0 是「常壓」，1–3 用水深層級的名稱。</summary>
        public static string LevelLabel(int level)
        {
            if (level <= 0)
            {
                return "DepthRim.Pressure.Level0".Translate();
            }
            return ((DepthTier)Mathf.Min(level, PressureAdaptationMath.MaxLevel)).Label();
        }
    }
}
