using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 從深淵底下經過的生物（只是畫面，不是 Pawn）：貼圖朝 +x，畫在地形下方、只在深淵格露出來。
    /// 背景生物（<see cref="ambientWeight"/> &gt; 0）由 <see cref="MapComponent_Midwater"/> 持續補上；
    /// 巨物（<see cref="giant"/>）只由事件 <c>DepthRim_GiantPasses</c> 叫出來。
    /// </summary>
    public class MidwaterCreatureDef : Def
    {
        public string texPath;

        /// <summary>貼圖在地圖上的大小（格）：x 沿著前進方向，y 是寬。</summary>
        public Vector2 drawSize = new Vector2(10f, 4f);

        /// <summary>乘在貼圖顏色上的色調；alpha 是整體不透明度。</summary>
        public Color color = Color.white;

        /// <summary>發光部位（貼圖裡亮度 &gt; 0.85 的地方，例如眼睛、發光管）的顏色；alpha 是強度，0 = 不發光。</summary>
        public Color glowColor = new Color(0f, 0f, 0f, 0f);

        /// <summary>每秒游幾格（1 倍速）。</summary>
        public FloatRange speed = new FloatRange(1f, 2f);

        /// <summary>在地板下多深（0..0.9）：位置與大小往鏡頭正下方縮的比例，越大看起來越深、越小。</summary>
        public float parallax = 0.2f;

        /// <summary>出現在哪些層級。</summary>
        public List<DepthTier> tiers = new List<DepthTier>();

        /// <summary>只由事件叫出的巨物。</summary>
        public bool giant;

        /// <summary>背景生物的抽選權重（0 = 不會當背景生物出現）。</summary>
        public float ambientWeight;

        /// <summary>游動的身體擺動（著色器）：幅度（佔貼圖寬的比例）、波數、速度。</summary>
        public float swimAmplitude = 0.03f;

        public float swimFrequency = 6f;

        public float swimSpeed = 2f;

        /// <summary>巨物出現時的訊息（可翻譯；空白就不發訊息）。</summary>
        [MustTranslate]
        public string passMessage;

        [Unsaved(false)]
        private Material material;

        [Unsaved(false)]
        private bool materialFailed;

        public bool AllowedOn(DepthTier tier)
        {
            return tiers.Contains(tier);
        }

        /// <summary>每種生物一份材質（主執行緒、第一次畫的時候才建立）。</summary>
        public Material Material
        {
            get
            {
                if (material == null && !materialFailed)
                {
                    Shader shader = DepthRimDefOf.DepthRim_Midwater?.Shader;
                    Texture2D tex = ContentFinder<Texture2D>.Get(texPath, false);
                    if (shader == null || tex == null)
                    {
                        materialFailed = true;
                        Log.ErrorOnce($"[DepthRim] midwater creature {defName} has no shader or texture ({texPath}); it will not be drawn.", defName.GetHashCode() ^ 0x5A17);
                        return null;
                    }
                    material = new Material(shader) { name = "DepthRim_Midwater_" + defName };
                    material.mainTexture = tex;
                    material.SetColor("_Color", color);
                    material.SetColor("_GlowColor", glowColor);
                    material.SetFloat("_SwimAmplitude", swimAmplitude);
                    material.SetFloat("_SwimFrequency", swimFrequency);
                    material.SetFloat("_SwimSpeed", swimSpeed);
                }
                return material;
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (texPath.NullOrEmpty())
            {
                yield return "texPath is empty";
            }
            if (tiers.NullOrEmpty())
            {
                yield return "no tiers: this creature can never appear";
            }
            foreach (DepthTier t in tiers)
            {
                if (!MidwaterMath.ShowsMidwater(t))
                {
                    yield return $"tier {t} never shows the midwater layer";
                }
            }
            if (giant && ambientWeight > 0f)
            {
                yield return "a giant cannot also be an ambient creature";
            }
            if (speed.min <= 0f)
            {
                yield return "speed must be positive";
            }
        }
    }
}
