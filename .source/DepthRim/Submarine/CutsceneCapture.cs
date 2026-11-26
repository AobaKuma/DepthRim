using DMSE;
using Fortified;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 出水過場的船體影像處理：把 DMSE 擷取到的船體影像（<see cref="TransferTakeoffCutscene.CapturedTexture"/>）
    /// 縮小、模糊一份，給水下的船影用（輪廓不會像剪紙一樣銳利）。
    /// 取景與擷取時機（長船超出主鏡頭的那一截擷取成黑的）由 DMSE 處理（2026-11-15 移到 DMSE）。
    /// </summary>
    public static class CutsceneCapture
    {
        /// <summary>
        /// 縮小 <paramref name="downsample"/> 倍再做兩次可分離的方框模糊（近似高斯），半徑 <paramref name="radius"/>（縮小後的像素）。
        /// 顏色以 alpha 預乘後平均，透明的邊緣不會帶進黑邊。
        /// </summary>
        public static Texture2D SoftCopy(Texture2D src, int downsample, int radius)
        {
            int w = Mathf.Max(1, src.width / downsample);
            int h = Mathf.Max(1, src.height / downsample);
            Color32[] pixels = src.GetPixels32();
            Vector4[] buf = new Vector4[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Vector4 sum = Vector4.zero;
                    int n = 0;
                    for (int dy = 0; dy < downsample; dy++)
                    {
                        int sy = y * downsample + dy;
                        if (sy >= src.height) break;
                        for (int dx = 0; dx < downsample; dx++)
                        {
                            int sx = x * downsample + dx;
                            if (sx >= src.width) break;
                            Color32 p = pixels[sy * src.width + sx];
                            float a = p.a / 255f;
                            sum += new Vector4(p.r / 255f * a, p.g / 255f * a, p.b / 255f * a, a);
                            n++;
                        }
                    }
                    buf[y * w + x] = n > 0 ? sum / n : Vector4.zero;
                }
            }
            for (int pass = 0; pass < 2; pass++)
            {
                buf = BoxBlur(buf, w, h, radius, horizontal: true);
                buf = BoxBlur(buf, w, h, radius, horizontal: false);
            }
            Color32[] outPx = new Color32[w * h];
            for (int i = 0; i < buf.Length; i++)
            {
                Vector4 v = buf[i];
                float a = v.w;
                outPx[i] = a > 0.001f
                    ? new Color32((byte)(Mathf.Clamp01(v.x / a) * 255), (byte)(Mathf.Clamp01(v.y / a) * 255), (byte)(Mathf.Clamp01(v.z / a) * 255), (byte)(Mathf.Clamp01(a) * 255))
                    : new Color32(0, 0, 0, 0);
            }
            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            tex.SetPixels32(outPx);
            tex.Apply();
            return tex;
        }

        private static Vector4[] BoxBlur(Vector4[] src, int w, int h, int r, bool horizontal)
        {
            if (r <= 0)
            {
                return src;
            }
            Vector4[] dst = new Vector4[src.Length];
            int len = horizontal ? w : h;
            int lines = horizontal ? h : w;
            float inv = 1f / (2 * r + 1);
            for (int line = 0; line < lines; line++)
            {
                Vector4 sum = Vector4.zero;
                for (int k = -r; k <= r; k++)
                {
                    sum += At(src, w, h, horizontal, line, k);
                }
                for (int i = 0; i < len; i++)
                {
                    int idx = horizontal ? line * w + i : i * w + line;
                    dst[idx] = sum * inv;
                    sum += At(src, w, h, horizontal, line, i + r + 1) - At(src, w, h, horizontal, line, i - r);
                }
            }
            return dst;
        }

        private static Vector4 At(Vector4[] src, int w, int h, bool horizontal, int line, int i)
        {
            int len = horizontal ? w : h;
            if (i < 0 || i >= len)
            {
                return Vector4.zero;
            }
            return horizontal ? src[line * w + i] : src[i * w + line];
        }
    }
}
