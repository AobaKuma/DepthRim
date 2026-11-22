using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 高出海面的平台在下方造成的明暗（2026-11-15 重做）。分成兩種暗：
    /// <list type="bullet">
    /// <item><b>天光遮蔽</b>（sky）：甲板擋住一部分天空。只在甲板正下方、很柔（大半徑模糊），日夜都在。</item>
    /// <item><b>陽光投影</b>（sun）：甲板與支柱擋住太陽。方向、長度、強度照原版建築影子（<see cref="GenCelestial.GetLightSourceInfo"/>）：
    /// 早晚拉長、正午縮短、夜裡淡、陰天淡；影子越長邊緣越柔。支柱（直立的柱身）在海面拖出細長的影子。</item>
    /// </list>
    /// 畫面（海面、暗化層、柱身三個著色器）與遊戲邏輯的光照（<see cref="Patch_PlatformShadowGlow"/>）用同一份資料：
    /// <see cref="PlatformShadowField"/> 在 CPU 上先模糊好的遮罩，著色器每次只取樣幾次，C# 用一樣的雙線性取樣算同一個值。
    /// </summary>
    public static class PlatformShadow
    {
        /// <summary>原版影子向量（正午約 1.5、黃昏約 16.6）換成「高 <see cref="MapComponent_SeaSurface.SeaDropCells"/> 格的甲板」影子位移（格）的倍率。</summary>
        public const float CastScale = 0.1f;

        /// <summary>影子位移最長幾格（太陽很低時不會拖到半張地圖）。</summary>
        public const float MaxCast = 14f;

        /// <summary>陽光投影最暗的程度（影子濃度）。</summary>
        public const float CastDarkness = 0.55f;

        /// <summary>天光遮蔽最暗的程度：甲板正下方即使沒有直射陽光也不是全黑。</summary>
        public const float SkyDarkness = 0.3f;

        /// <summary>夜裡月光的影子相對白天的強度。</summary>
        public const float MoonShare = 0.3f;

        /// <summary>原版晴天的影子顏色亮度（Clear 的 skyColorsDay.shadow）：亮度越接近 1（陰天 0.92），影子越淡。</summary>
        public const float ClearShadowLuma = 0.74f;

        /// <summary>平台層往下看：海面遠在下方，影子淡一點（濃度倍率）、柔一點（多混大半徑模糊）。</summary>
        public const float LowerFade = 0.65f;

        public const float LowerExtraSoftness = 0.35f;

        /// <summary>水面層看得到頭上甲板的邊緣：甲板（往北偏甲板高度）外緣一圈淡淡的暗線的濃度。</summary>
        public const float DeckOutline = 0.12f;

        /// <summary>支柱影子沿投影方向取樣幾次（著色器的 PILLAR_TAPS 要一樣）。</summary>
        public const int PillarTaps = 8;

        /// <summary>
        /// 支柱影子的厚度：遮罩 g 只有入水處（柱子前緣兩排），柱子本身是寬 <see cref="PlatformPillarExtension.waterlineWidth"/> 的圓柱，
        /// 所以從入水處往北（往後）再延伸這麼多格當作斷面，影子才和柱子一樣粗（著色器的 PILLAR_DEPTH 要一樣）。
        /// </summary>
        public const float PillarDepth = 7f;

        /// <summary>斷面往北取樣幾次（著色器的 PILLAR_DEPTH_TAPS 要一樣）。</summary>
        public const int PillarDepthTaps = 5;

        /// <summary>遮罩模糊（高斯 σ，格）：r = 甲板（投影的邊緣）、a = 甲板（天光遮蔽）、g = 支柱入水處（白沫與柱影）。</summary>
        public const float DeckSigma = 0.9f;

        public const float SkySigma = 3f;

        public const float PillarSigma = 0.8f;

        /// <summary>g 從這個值開始投柱影（著色器 platformShade 的 smoothstep 起點要一樣）。箱網白沫壓在這以下（<see cref="PlatformShadowField.CageFoamMax"/>）。</summary>
        public const float PillarShadowStart = 0.35f;

        /// <summary>人造光到這個亮度（原版燈具的地面亮度上限是 0.5）時影子完全被照亮。</summary>
        public const float FullyLitGlow = 0.5f;

        /// <summary>人造光照亮影子的程度（0 = 沒有燈，1 = 影子完全被照亮）。</summary>
        public static float LitFraction(float artificialGlow)
        {
            return Mathf.Clamp01(artificialGlow / FullyLitGlow);
        }

        /// <summary>原版的影子向量換成甲板影子的位移（格），限制長度。</summary>
        public static Vector2 CastFromVanilla(Vector2 vanillaShadowVector)
        {
            return Vector2.ClampMagnitude(vanillaShadowVector * (MapComponent_SeaSurface.SeaDropCells * CastScale), MaxCast);
        }

        /// <summary>天氣讓影子變淡多少：晴天 1，陰天、下雨約 0.3。</summary>
        public static float WeatherFactor(Color skyShadowColor)
        {
            float luma = (skyShadowColor.r + skyShadowColor.g + skyShadowColor.b) / 3f;
            return Mathf.InverseLerp(1f, ClearShadowLuma, luma);
        }

        /// <summary>影子裡的地面亮度：天光乘上沒被遮住的部分，再和燈光取大（和原版 GroundGlowAt 的組合方式一樣）。</summary>
        public static float ShadedGlow(float skyGlow, float shadow, float artificialGlow)
        {
            return Mathf.Max(skyGlow * (1f - Mathf.Clamp01(shadow)), artificialGlow);
        }

        /// <summary>水面層在海面座標點 (<paramref name="x"/>, <paramref name="z"/>) 的人造光：取這一點四周的格子平均（和原版光照層的頂點一樣）。</summary>
        public static float ArtificialGlowAt(Map sea, float x, float z)
        {
            int x0 = Mathf.FloorToInt(x - 0.5f);
            int z0 = Mathf.FloorToInt(z - 0.5f);
            float sum = 0f;
            int n = 0;
            for (int dx = 0; dx <= 1; dx++)
            {
                for (int dz = 0; dz <= 1; dz++)
                {
                    IntVec3 c = new IntVec3(x0 + dx, 0, z0 + dz);
                    if (c.InBounds(sea))
                    {
                        sum += sea.glowGrid.GroundGlowAt(c, ignoreSky: true);
                        n++;
                    }
                }
            }
            return n == 0 ? 0f : sum / n;
        }

        /// <summary>頂點色：a = 1 − 燈光照亮影子的程度（著色器把影子濃度乘上它）。</summary>
        public static Color32 LampColor(Map sea, float x, float z)
        {
            return new Color32(255, 255, 255, (byte)Mathf.RoundToInt((1f - LitFraction(ArtificialGlowAt(sea, x, z))) * 255f));
        }
    }

    /// <summary>某一刻的太陽：影子位移（格，高 <see cref="MapComponent_SeaSurface.SeaDropCells"/> 格的甲板）與影子的強度（0..1）。</summary>
    public struct PlatformSun
    {
        public Vector2 cast;

        public float strength;

        public PlatformSun(Vector2 cast, float strength)
        {
            this.cast = cast;
            this.strength = strength;
        }

        /// <summary>照原版建築影子：方向與長度用 <see cref="GenCelestial.LightType.Shadow"/>，強度乘上天氣，夜裡（月光）再淡一些。</summary>
        public static PlatformSun For(Map map)
        {
            GenCelestial.LightInfo info = GenCelestial.GetLightSourceInfo(map, GenCelestial.LightType.Shadow);
            float strength = info.intensity * PlatformShadow.WeatherFactor(map.skyManager.CurSky.colors.shadow);
            if (!GenCelestial.IsDaytime(GenCelestial.CurCelestialSunGlow(map)))
            {
                strength *= PlatformShadow.MoonShare;
            }
            return new PlatformSun(PlatformShadow.CastFromVanilla(info.vector), Mathf.Clamp01(strength));
        }
    }

    /// <summary>
    /// 模糊好的遮罩（每格一個值，和上傳的貼圖同一份）：r = 甲板（σ <see cref="PlatformShadow.DeckSigma"/>），
    /// a = 甲板（σ <see cref="PlatformShadow.SkySigma"/>），g = 支柱入水處（σ <see cref="PlatformShadow.PillarSigma"/>），b = 浮船（不模糊）。
    /// <see cref="Shade"/> 和著色器的 platformShade 是同一個算法。
    /// </summary>
    public class PlatformShadowField
    {
        public readonly int width;
        public readonly int height;
        public readonly float[] deck;
        public readonly float[] sky;
        public readonly float[] pillar;
        public readonly float[] hull;

        /// <summary>
        /// 箱網浮框（Docs/箱網養殖_內容設計.md）：貼著水面，水線白沫要正好在浮框上、沒有甲板的偏移與短影子，所以不放 b，
        /// 而是加進 g（支柱入水處的白沫）。值壓在 <see cref="CageFoamMax"/> 以下：支柱柱影從 g ≥ <see cref="PlatformShadow.PillarShadowStart"/> 才開始，箱網不會投出柱影。
        /// </summary>
        public const float CageFoamSigma = 0.5f;

        public const float CageFoamScale = 0.47f;

        public const float CageFoamMax = 0.32f;

        public PlatformShadowField(int width, int height, bool[] raised, bool[] pillarCells, bool[] hullCells, bool[] cageCells = null)
        {
            this.width = width;
            this.height = height;
            float[] raw = ToFloats(raised);
            deck = Blur(raw, width, height, PlatformShadow.DeckSigma);
            sky = Blur(raw, width, height, PlatformShadow.SkySigma);
            pillar = Blur(ToFloats(pillarCells), width, height, PlatformShadow.PillarSigma);
            hull = ToFloats(hullCells);
            if (cageCells != null)
            {
                float[] cage = Blur(ToFloats(cageCells), width, height, CageFoamSigma);
                for (int i = 0; i < pillar.Length; i++)
                {
                    pillar[i] = Mathf.Max(pillar[i], Mathf.Min(CageFoamMax, cage[i] * CageFoamScale));
                }
            }
        }

        private static float[] ToFloats(bool[] cells)
        {
            float[] f = new float[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                f[i] = cells[i] ? 1f : 0f;
            }
            return f;
        }

        /// <summary>可分離的高斯模糊（地圖外當成 0）。</summary>
        public static float[] Blur(float[] src, int w, int h, float sigma)
        {
            int r = Mathf.CeilToInt(sigma * 3f);
            float[] kernel = new float[r * 2 + 1];
            float sum = 0f;
            for (int i = -r; i <= r; i++)
            {
                kernel[i + r] = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                sum += kernel[i + r];
            }
            for (int i = 0; i < kernel.Length; i++)
            {
                kernel[i] /= sum;
            }
            float[] tmp = new float[src.Length];
            float[] dst = new float[src.Length];
            for (int z = 0; z < h; z++)
            {
                int row = z * w;
                for (int x = 0; x < w; x++)
                {
                    float v = 0f;
                    int k0 = Mathf.Max(-r, -x);
                    int k1 = Mathf.Min(r, w - 1 - x);
                    for (int k = k0; k <= k1; k++)
                    {
                        v += src[row + x + k] * kernel[k + r];
                    }
                    tmp[row + x] = v;
                }
            }
            for (int z = 0; z < h; z++)
            {
                int k0 = Mathf.Max(-r, -z);
                int k1 = Mathf.Min(r, h - 1 - z);
                for (int x = 0; x < w; x++)
                {
                    float v = 0f;
                    for (int k = k0; k <= k1; k++)
                    {
                        v += tmp[(z + k) * w + x] * kernel[k + r];
                    }
                    dst[z * w + x] = v;
                }
            }
            return dst;
        }

        /// <summary>和 GPU 一樣的雙線性取樣（像素中心在 i + 0.5、邊緣 Clamp，地圖外為 0）。</summary>
        public float Sample(float[] channel, Vector2 p)
        {
            if (p.x < 0f || p.y < 0f || p.x > width || p.y > height)
            {
                return 0f;
            }
            float fx = p.x - 0.5f;
            float fz = p.y - 0.5f;
            int x0 = Mathf.FloorToInt(fx);
            int z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0;
            float tz = fz - z0;
            int xa = Mathf.Clamp(x0, 0, width - 1);
            int xb = Mathf.Clamp(x0 + 1, 0, width - 1);
            int za = Mathf.Clamp(z0, 0, height - 1) * width;
            int zb = Mathf.Clamp(z0 + 1, 0, height - 1) * width;
            return Mathf.Lerp(Mathf.Lerp(channel[za + xa], channel[za + xb], tx), Mathf.Lerp(channel[zb + xa], channel[zb + xb], tx), tz);
        }

        private static float SmoothStep(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        /// <summary>
        /// 海面座標點 <paramref name="sea"/>（高出海面 <paramref name="heightAbove"/> 格）被遮住的程度：x = 陽光（0..1，未乘強度），y = 天光。
        /// 和著色器的 platformShade 相同：甲板在 sea − 位移 處就擋住陽光；影子越長越柔；海面上的點也會被支柱的影子擋住。
        /// </summary>
        public Vector2 Shade(Vector2 sea, Vector2 cast, float heightAbove, float extraSoftness)
        {
            float deckHeight = MapComponent_SeaSurface.SeaDropCells;
            if (heightAbove >= deckHeight)
            {
                return Vector2.zero;
            }
            float reach = 1f - heightAbove / deckHeight;
            Vector2 offset = cast * reach;
            float soft = Mathf.Clamp01(offset.magnitude / 16f * 0.7f + extraSoftness);
            Vector2 p = sea - offset;
            float sun = SmoothStep(0.05f, 0.85f, Mathf.Lerp(Sample(deck, p), Sample(sky, p), soft));
            if (heightAbove <= 0f)
            {
                float column = 0f;
                for (int i = 1; i <= PlatformShadow.PillarTaps; i++)
                {
                    Vector2 along = sea - cast * (i / (float)PlatformShadow.PillarTaps);
                    for (int j = 0; j < PlatformShadow.PillarDepthTaps; j++)
                    {
                        float back = PlatformShadow.PillarDepth * j / (PlatformShadow.PillarDepthTaps - 1);
                        column = Mathf.Max(column, Sample(pillar, along - new Vector2(0f, back)));
                    }
                }
                sun = Mathf.Max(sun, SmoothStep(0.15f, 0.6f, column));
            }
            return new Vector2(sun, SmoothStep(0.1f, 0.9f, Sample(sky, sea)));
        }

        public float GameplayShadow(Vector2 sea, PlatformSun sun)
        {
            Vector2 s = Shade(sea, sun.cast, 0f, 0f);
            return Mathf.Max(s.y, s.x * sun.strength);
        }

        public Color32[] ToPixels()
        {
            Color32[] pixels = new Color32[width * height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color32(ToByte(deck[i]), ToByte(pillar[i]), ToByte(hull[i]), ToByte(sky[i]));
            }
            return pixels;
        }

        private static byte ToByte(float v)
        {
            return (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f);
        }
    }

    [HarmonyPatch(typeof(GlowGrid), nameof(GlowGrid.GroundGlowAt))]
    public static class Patch_PlatformShadowGlow
    {
        private static readonly AccessTools.FieldRef<GlowGrid, Map> MapField = AccessTools.FieldRefAccess<GlowGrid, Map>("map");

        [HarmonyPostfix]
        public static void Postfix(GlowGrid __instance, IntVec3 c, bool ignoreCavePlants, bool ignoreSky, ref float __result)
        {
            if (ignoreSky || __result <= 0f)
            {
                return;
            }
            Map map = MapField(__instance);
            if (!(map?.Parent is WorldObject_SeaSurfaceSite) || map.roofGrid.Roofed(c))
            {
                return;
            }
            float shadow = map.GetComponent<MapComponent_SeaSurface>()?.ShadowAt(c) ?? 0f;
            if (shadow <= 0f)
            {
                return;
            }
            float artificial = __instance.GroundGlowAt(c, ignoreCavePlants, ignoreSky: true);
            __result = PlatformShadow.ShadedGlow(map.skyManager.CurSkyGlow, shadow, artificial);
        }
    }
}
