using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 玩家設定（二期 P2-5）。**所有預設值都是中性的**：不改任何 Def 的原值，
    /// 所以設定檔不存在時遊戲畫面與一期定稿完全一致。
    ///
    /// <para>每一項都對應一個實際的著色器/Def 參數，改完會重建材質並標記整張地圖重建，
    /// 不需要重開遊戲。</para>
    /// </summary>
    public class DepthRimModSettings : ModSettings
    {
        /// <summary>海洋雪、生物光、沉積霧氣是否繪製。關掉時著色器整段跳過（參數歸零）。</summary>
        public bool particles = true;

        /// <summary>是否顯示「暗處的己方提示」。</summary>
        public bool hints = true;

        /// <summary>提示的最大不透明度倍率（0–1）。</summary>
        public float hintOpacity = 1f;

        /// <summary>
        /// 混濁度倍率（0.2–1）：乘在每個層級的 <c>murkAlpha</c> 上，
        /// 覺得暗處太黑看不到東西時往下調。1 = 完全照 Def。
        /// </summary>
        public float murkScale = 1f;

        /// <summary>日光穿透倍率（0–1.5）：乘在每個層級的 <c>ambientReach</c> 上。</summary>
        public float ambientScale = 1f;

        /// <summary>頭燈半徑覆寫（0 = 用 Def 的 4.5）。</summary>
        public float lampRadiusOverride = 0f;

        /// <summary>水中悶音強度（0–1，二期 P2-11）：0 = 完全不悶，1 = 照設計曲線。</summary>
        public float muffleStrength = 1f;

        /// <summary>是否播放潛水呼吸與氣泡聲。</summary>
        public bool diverBreathing = true;

        /// <summary>船殼承壓聲的頻率倍率（0–2）：0 = 關閉。</summary>
        public float hullCreakFrequency = 1f;

        private static DepthRimModSettings current;

        /// <summary>目前載入的設定；沒有載入（單元測試、極早期）時回傳一份預設值。</summary>
        public static DepthRimModSettings Current
        {
            get
            {
                if (current == null)
                {
                    current = new DepthRimModSettings();
                }
                return current;
            }
        }

        public static void SetCurrent(DepthRimModSettings settings)
        {
            current = settings ?? new DepthRimModSettings();
        }

        /// <summary>頭燈半徑：設定 &gt; 0 時用設定，否則用 Def。</summary>
        public static float LampRadiusOf(float defRadius)
        {
            float value = Current.lampRadiusOverride;
            return value > 0f ? value : defRadius;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref particles, "particles", true);
            Scribe_Values.Look(ref hints, "hints", true);
            Scribe_Values.Look(ref hintOpacity, "hintOpacity", 1f);
            Scribe_Values.Look(ref murkScale, "murkScale", 1f);
            Scribe_Values.Look(ref ambientScale, "ambientScale", 1f);
            Scribe_Values.Look(ref lampRadiusOverride, "lampRadiusOverride", 0f);
            Scribe_Values.Look(ref muffleStrength, "muffleStrength", 1f);
            Scribe_Values.Look(ref diverBreathing, "diverBreathing", true);
            Scribe_Values.Look(ref hullCreakFrequency, "hullCreakFrequency", 1f);
        }

        public void DoWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);
            list.CheckboxLabeled("DepthRim.Settings.Particles".Translate(), ref particles,
                "DepthRim.Settings.ParticlesTip".Translate());
            list.Gap(6f);
            list.CheckboxLabeled("DepthRim.Settings.Hints".Translate(), ref hints,
                "DepthRim.Settings.HintsTip".Translate());
            if (hints)
            {
                list.Label("DepthRim.Settings.HintOpacity".Translate(hintOpacity.ToStringPercent()));
                hintOpacity = list.Slider(hintOpacity, 0.1f, 1f);
            }
            list.Gap(6f);
            list.Label("DepthRim.Settings.Murk".Translate(murkScale.ToStringPercent()));
            murkScale = list.Slider(murkScale, 0.2f, 1f);
            list.Gap(6f);
            list.Label("DepthRim.Settings.Ambient".Translate(ambientScale.ToStringPercent()));
            ambientScale = list.Slider(ambientScale, 0f, 1.5f);
            list.Gap(6f);
            list.Label("DepthRim.Settings.LampRadius".Translate(
                lampRadiusOverride <= 0f
                    ? "DepthRim.Settings.LampRadiusDef".Translate().ToString()
                    : lampRadiusOverride.ToString("0.0")));
            lampRadiusOverride = list.Slider(lampRadiusOverride, 0f, 8f);
            if (lampRadiusOverride > 0f && lampRadiusOverride < 1f)
            {
                lampRadiusOverride = 1f; // 太小的半徑沒有意義，也讓滑桿好對齊
            }
            list.GapLine();
            list.Label("DepthRim.Settings.AudioHeader".Translate());
            list.Label("DepthRim.Settings.Muffle".Translate(muffleStrength.ToStringPercent()));
            muffleStrength = list.Slider(muffleStrength, 0f, 1f);
            list.CheckboxLabeled("DepthRim.Settings.Breathing".Translate(), ref diverBreathing);
            list.Label("DepthRim.Settings.HullCreak".Translate(hullCreakFrequency.ToStringPercent()));
            hullCreakFrequency = list.Slider(hullCreakFrequency, 0f, 2f);
            list.End();
        }
    }

    /// <summary>把 <see cref="DepthRimModSettings"/> 掛進「選項 → 模組設定」。</summary>
    public class DepthRimSettingsMod : Mod
    {
        public DepthRimSettingsMod(ModContentPack content) : base(content)
        {
            DepthRimModSettings.SetCurrent(GetSettings<DepthRimModSettings>());
        }

        public override string SettingsCategory()
        {
            return "DepthRim.Settings.Category".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            GetSettings<DepthRimModSettings>().DoWindowContents(inRect);
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            // 材質是照「Def × 設定」建出來的，設定一改就要重建並讓整張地圖重畫網格。
            WaterTierMaterials.Reset();
            if (Find.Maps != null)
            {
                for (int i = 0; i < Find.Maps.Count; i++)
                {
                    Find.Maps[i].mapDrawer?.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
                }
            }
        }
    }
}
