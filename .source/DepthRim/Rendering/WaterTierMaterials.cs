using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水體材質（實作指南 A.1.3、C.2、E.1）：外海層與艙室層各一份「每個層級一個」的材質陣列，
    /// 所有區塊共用，不會每個區塊都建材質。數值來自 <see cref="WaterVisualTierDef"/>。
    ///
    /// <para><b>執行緒：</b>材質只能在主執行緒建立；第一次用到時建立（<c>SectionLayer.Regenerate</c> 在主執行緒）。
    /// 建立後直接設定所有參數，不依賴著色器的 <c>@default</c>，所以驗收測試可以從材質讀回參數。</para>
    ///
    /// <para><b>Def 沒有定義的參數</b>（B.4 的全域值）在這裡以常數提供：<c>_FogAlpha</c>、<c>_LightMin</c>、
    /// <c>_LightMax</c>、<c>_LightTint</c>、<c>_RippleContrast</c>、<c>_RippleSpeed</c>、光紋顏色。</para>
    /// </summary>
    public static class WaterTierMaterials
    {
        public const int TierCount = 4;

        // B.4：所有層級共用的參數
        public const float FogAlpha = 0.93f;

        public const float LightMin = 0.05f;

        public const float LightMax = 0.60f;

        public const float LightTint = 0.25f;

        public const float RippleContrast = 0.60f;

        public const float RippleSpeed = 1f;

        /// <summary>艙室層的光紋與光紋強度（E 階段：只讀水色與混濁度，其餘保持固定）。</summary>
        public const float CompartmentCausticStrength = 0.35f;

        public const float CompartmentShoreDarken = 0.45f;

        /// <summary>艙室積水有光時的最低覆蓋程度（乘上水位曲線 0.5–1.5 倍，半淹 0.6）。</summary>
        public const float CompartmentMinAlpha = 0.6f;

        /// <summary>半淹時靠牆水線泡沫的強度。</summary>
        public const float CompartmentFoamStrength = 0.8f;

        /// <summary>相機預設的 rootSize；著色器用它判斷鏡頭拉遠時要降低顆粒亮度。</summary>
        public const float DefaultCameraZoom = 14f;

        private static readonly Color CausticColor = new Color(0.50f, 0.80f, 1f);

        private static readonly Color CompartmentCausticColor = new Color(0.55f, 0.85f, 1f);

        private static readonly Material[] outer = new Material[TierCount];

        private static readonly Material[] volume = new Material[TierCount];

        public static Material Outer(DepthTier tier)
        {
            int index = Index(tier);
            return outer[index] ?? (outer[index] = CreateOuter(tier));
        }

        public static Material Volume(DepthTier tier)
        {
            int index = Index(tier);
            return volume[index] ?? (volume[index] = CreateVolume(tier));
        }

        /// <summary>每幀更新相機距離（海洋雪的亮度會跟著鏡頭拉遠變淡）。</summary>
        /// <summary>
        /// 每幀設定日光強度：外海層頂點只存「看得到天空的比例」，日光在這裡乘上（避免烘進網格造成區塊色差）。
        /// 每個層級的材質用自己的 ambientReach。
        /// </summary>
        public static void UpdateSkyLight(Map map)
        {
            for (int i = 0; i < outer.Length; i++)
            {
                outer[i]?.SetFloat("_SkyLight", WaterLight.SkyLevel(map, WaterVisualTierDef.For((DepthTier)i).ambientReach));
            }
        }

        public static void UpdateCameraZoom(float rootSize)
        {
            for (int i = 0; i < outer.Length; i++)
            {
                outer[i]?.SetFloat("_CameraZoom", rootSize);
            }
        }

        /// <summary>
        /// 丟掉已建立的材質（Def 重新載入或玩家改設定時呼叫）：下次重建時重新讀 Def 與設定。
        /// 先 <see cref="Object.Destroy"/> 再放掉參考，否則反覆重建會累積材質（二期 P2-3）。
        /// </summary>
        public static void Reset()
        {
            for (int i = 0; i < TierCount; i++)
            {
                if (outer[i] != null)
                {
                    Object.Destroy(outer[i]);
                }
                if (volume[i] != null)
                {
                    Object.Destroy(volume[i]);
                }
                outer[i] = null;
                volume[i] = null;
            }
        }

        private static int Index(DepthTier tier)
        {
            int index = (int)tier;
            return index < 0 || index >= TierCount ? 0 : index;
        }

        private static Material CreateOuter(DepthTier tier)
        {
            WaterVisualTierDef def = WaterVisualTierDef.For(tier);
            // 每層級一個獨立材質：直接 new Material(shader)，不經過 MaterialPool
            // （MaterialPool 以 MaterialRequest 為 key 共用材質，MaterialAllocator 則是 internal 無法從模組呼叫）
            Material mat = new Material(DepthRimDefOf.DepthRim_WaterOuter.Shader);
            mat.name = "DepthRim_WaterOuter_" + tier;
            mat.mainTexture = BaseContent.WhiteTex;
            mat.color = def?.waterColor ?? new Color(0.06f, 0.24f, 0.40f);
            mat.SetColor("_DeepColor", def?.deepColor ?? new Color(0.02f, 0.07f, 0.15f));
            mat.SetColor("_FogColor", def?.fogColor ?? new Color(0.015f, 0.05f, 0.11f));
            mat.SetColor("_CausticColor", CausticColor);
            mat.SetColor("_BiolumColor", def?.biolumColor ?? new Color(0.35f, 0.90f, 0.85f));
            mat.SetFloat("_FogAlpha", FogAlpha);
            mat.SetFloat("_ClearAlpha", def?.clearAlpha ?? 0.40f);
            mat.SetFloat("_MurkAlpha", MurkOf(def));
            mat.SetFloat("_LightMin", LightMin);
            mat.SetFloat("_LightMax", LightMax);
            mat.SetFloat("_LightTint", LightTint);
            mat.SetFloat("_RippleScale", def?.rippleScale ?? 0.03f);
            mat.SetFloat("_RippleSpeed", RippleSpeed);
            mat.SetFloat("_RippleContrast", RippleContrast);
            mat.SetFloat("_RippleStrength", def?.rippleStrength ?? 0.8f);
            mat.SetFloat("_CausticStrength", def?.causticStrength ?? 0.22f);
            mat.SetFloat("_LightShaftStrength", def?.lightShaftStrength ?? 0f);
            mat.SetFloat("_SnowDensity", Particle(def?.snowDensity));
            mat.SetFloat("_SnowSpeed", def?.snowSpeed ?? 1f);
            mat.SetFloat("_BiolumDensity", Particle(def?.biolumDensity));
            mat.SetFloat("_HazeStrength", Particle(def?.hazeStrength));
            mat.SetFloat("_WobbleStrength", def?.wobbleStrength ?? 0f);
            mat.SetFloat("_CameraZoom", DefaultCameraZoom);
            // 著色器的 @default 不會自動套到材質上（實測 GetFloat 回 0），每個參數都要在這裡設；日光每幀由 UpdateSkyLight 覆寫
            mat.SetFloat("_SkyLight", 0f);
            return mat;
        }

        /// <summary>混濁度套上玩家設定（P2-5）；1 = 照 Def。</summary>
        private static float MurkOf(WaterVisualTierDef def)
        {
            float murk = def?.murkAlpha ?? 0.85f;
            return Mathf.Clamp01(murk * Mathf.Clamp(DepthRimModSettings.Current.murkScale, 0f, 1f));
        }

        /// <summary>
        /// 公開入口（二期 P2-5）：讓單元測試能驗「玩家設定真的會改變材質參數」。
        /// 原本 P2-5 只有「編譯得過」的證據，因為材質要真的建立才有東西可看；
        /// 這個入口把純計算的部分拉出來，才能在沒有 GPU、沒有 DefDatabase 的測試裡驗證。
        /// </summary>
        public static float MurkFor(WaterVisualTierDef def)
        {
            return MurkOf(def);
        }

        /// <summary>同上，粒子密度（P2-5：玩家關掉粒子時為 0，著色器會整段跳過）。</summary>
        public static float ParticleFor(float? defValue)
        {
            return Particle(defValue);
        }

        /// <summary>粒子類效果；玩家關掉時歸零，著色器就會整段跳過（P2-5）。</summary>
        private static float Particle(float? defValue)
        {
            if (!DepthRimModSettings.Current.particles)
            {
                return 0f;
            }
            return defValue ?? 0f;
        }

        private static Material CreateVolume(DepthTier tier)
        {
            WaterVisualTierDef def = WaterVisualTierDef.For(tier);
            Material mat = new Material(DepthRimDefOf.DepthRim_WaterVolume.Shader);
            mat.name = "DepthRim_WaterVolume_" + tier;
            mat.mainTexture = BaseContent.WhiteTex;
            mat.color = def?.waterColor ?? new Color(0.10f, 0.36f, 0.55f);
            mat.SetColor("_DeepColor", def?.deepColor ?? new Color(0.02f, 0.10f, 0.22f));
            mat.SetColor("_CausticColor", CompartmentCausticColor);
            mat.SetFloat("_ClearAlpha", def?.clearAlpha ?? 0.40f);
            mat.SetFloat("_MurkAlpha", MurkOf(def));
            mat.SetFloat("_LightMin", LightMin);
            mat.SetFloat("_LightMax", LightMax);
            mat.SetFloat("_LightTint", LightTint);
            mat.SetFloat("_RippleScale", def?.rippleScale ?? 0.06f);
            mat.SetFloat("_RippleSpeed", RippleSpeed);
            mat.SetFloat("_RippleContrast", RippleContrast);
            mat.SetFloat("_RippleStrength", 1f);
            mat.SetFloat("_CausticStrength", CompartmentCausticStrength);
            mat.SetFloat("_ShoreDarken", CompartmentShoreDarken);
            // 半淹可見度（使用者回報「半淹不明顯」）：最低覆蓋與水線泡沫。@default 不會自動套用，一定要設
            mat.SetFloat("_MinAlpha", CompartmentMinAlpha);
            mat.SetFloat("_FoamStrength", CompartmentFoamStrength);
            // 艙室層海洋雪（二期 P2-6）：只有 Def 開啟 compartmentEffects 的層級才有，
            // 而且刻意比外海層淡（係數 0.6），免得小房間裡變成一片雜訊。
            mat.SetFloat("_SnowDensity", CompartmentSnow(def));
            mat.SetFloat("_SnowSpeed", def?.snowSpeed ?? 1f);
            return mat;
        }

        /// <summary>艙室層的海洋雪密度：Def 沒開啟 <c>compartmentEffects</c> 時為 0。</summary>
        private static float CompartmentSnow(WaterVisualTierDef def)
        {
            if (def == null || !def.compartmentEffects)
            {
                return 0f;
            }
            return Particle(def.snowDensity) * 0.6f;
        }
    }
}
