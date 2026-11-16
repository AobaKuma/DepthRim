using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水下地圖邊界外畫成深水的顏色（2026-11-11）：原版地圖外是一片黑；太空船的地圖外有太空背景，
    /// 潛艇與海洋地圖的邊界外卻是黑的，船塢靠近地圖邊緣時半個畫面都是黑色。這裡在地圖底下鋪一大片深水色，
    /// 地形畫在它上面，所以只有地圖外看得到。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class OffMapWater
    {
        private static Material material;

        private static Color ColorOf(DepthTier tier)
        {
            WaterVisualTierDef def = WaterVisualTierDef.For(tier);
            return def != null ? Color.Lerp(def.deepColor, def.waterColor, 0.6f) : new Color(0.048f, 0.19f, 0.33f);
        }

        /// <summary>每幀畫一次（水下地圖、正在看這張地圖時）。</summary>
        public static void Draw(Map map, DepthTier tier)
        {
            if (material == null)
            {
                material = new Material(ShaderDatabase.SolidColor) { renderQueue = 1000, name = "DepthRim_OffMapWater" };
            }
            // 和地圖裡的外海水色接近（實測淺海外海約 0.075, 0.20, 0.31）：深處水色往水色拉六成；換層淡入淡出中時兩層內插
            MapComponent_Flooding flooding = map.Flooding();
            material.color = flooding != null && flooding.FadingTier
                ? Color.Lerp(ColorOf(flooding.FadeFrom), ColorOf(flooding.FadeTo), flooding.TierFadeProgress)
                : ColorOf(tier);
            if (VoyageView.IsVoyageMap(map))
            {
                // 航行中：艙外已經淡出成界外色（SectionLayer_VoyageDarkness），地圖外也要同色，不然邊緣會透出一圈水色
                material.color = VoyageView.OutOfBoundsColor;
            }
            Vector3 center = map.Center.ToVector3Shifted().WithY(AltitudeLayer.Terrain.AltitudeFor() - 0.01f);
            float size = Mathf.Max(map.Size.x, map.Size.z) * 4f;
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(center, Quaternion.identity, new Vector3(size, 1f, size)), material, 0);
        }
    }
}
