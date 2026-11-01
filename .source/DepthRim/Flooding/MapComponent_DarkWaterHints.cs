using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 暗處的己方 Pawn 提示（實作指南 B2）：己方 Pawn 站混濁的水裡時，用原版剪影著色器畫一層淡青色輪廓，
    /// 讓玩家在幾乎全黑的水下還看得到自己的人。敵人與野生生物沒有提示。
    ///
    /// <para><b>判斷與畫面一致：</b>混濁度用和著色器同一條 <see cref="FloodingMath.WaterAlpha"/> 公式，
    /// 光照來源也和外海層一樣是 <see cref="WaterLight.At"/>（含日光，見 <see cref="FloodingMath.CombineLight"/>），所以「看起來很暗」與
    /// 「有沒有提示」不會互相矛盾。</para>
    ///
    /// <para><b>為什麼不用原版 <see cref="Verse.SilhouetteUtility"/>：</b>它受玩家的「醒目提示樣式」設定控制，
    /// 而且只處理被滑鼠指到或選取的目標；這個提示必須永遠有效，所以自己畫，但沿用它的網格、縮放與繪製順序
    /// （<see cref="AltitudeLayer.Silhouettes"/>，在原版光照暗化之後）。</para>
    /// </summary>
    public class MapComponent_DarkWaterHints : MapComponent
    {
        /// <summary>混濁度門檻：低於這個值（亮處）完全不畫。</summary>
        public const float Threshold = 0.5f;

        /// <summary>最大不透明度。</summary>
        public const float MaxAlpha = 0.6f;

        /// <summary>B2.2：淡青色 (120, 220, 255)。</summary>
        public static readonly Color HintColor = new Color(120f / 255f, 220f / 255f, 1f);

        private const int MaxCachedMaterials = 512;

        private readonly Dictionary<Graphic, (Material east, Material west)> materialCache = new Dictionary<Graphic, (Material, Material)>();

        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        public MapComponent_DarkWaterHints(Map map) : base(map)
        {
        }

        /// <summary>
        /// 這個 Pawn 的提示不透明度；0 = 不畫。給驗收測試 V5 直接呼叫，和實際繪製走同一條路徑。
        /// </summary>
        public static float HintAlphaFor(Pawn pawn, MapComponent_Flooding flooding)
        {
            if (pawn == null || flooding == null || !flooding.Active)
            {
                return 0f;
            }
            if (!DepthRimModSettings.Current.hints)
            {
                return 0f;
            }
            if (!pawn.Spawned || pawn.Dead || pawn.Destroyed || pawn.Faction != Faction.OfPlayer)
            {
                return 0f;
            }
            Map map = pawn.Map;
            IntVec3 cell = pawn.Position;
            if (!cell.InBounds(map) || cell.Fogged(map))
            {
                return 0f;
            }
            // 只在開放水域提示；密閉艙室不由外海層蓋住，Pawn 本來就看得到
            if (!flooding.IsOpenAt(cell))
            {
                return 0f;
            }
            WaterVisualTierDef def = WaterVisualTierDef.For(flooding.RenderTier);
            // 和外海層頂點用同一個光照定義（含日光），所以「看起來很暗」和「出現提示」永遠一致
            float light = WaterLight.At(map, cell, def?.ambientReach ?? 1f);
            float turbidity = FloodingMath.WaterAlpha(light, def?.clearAlpha ?? 0.15f, def?.murkAlpha ?? 0.85f,
                WaterTierMaterials.LightMin, WaterTierMaterials.LightMax);
            return FloodingMath.HintAlpha(turbidity, Threshold, MaxAlpha) * Mathf.Clamp01(DepthRimModSettings.Current.hintOpacity);
        }

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            // 跟著右下角的水體顯示開關；關掉水體時也不畫提示
            if (!FloodingOverlay.Show || Find.CurrentMap != map || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            MapComponent_Flooding flooding = map.Flooding();
            if (flooding == null || !flooding.Active)
            {
                return;
            }
            CellRect view = Find.CameraDriver.CurrentViewRect;
            // 提示要在原版光照暗化之後才畫得出來，所以延後到本幀的材質佇列（drawNow: false）即可
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                if (pawn.Faction != Faction.OfPlayer || !view.Contains(pawn.Position))
                {
                    continue;
                }
                float alpha = HintAlphaFor(pawn, flooding);
                if (alpha <= 0f)
                {
                    continue;
                }
                DrawHint(pawn, alpha);
            }
        }

        private void DrawHint(Pawn pawn, float alpha)
        {
            Graphic graphic = pawn.Drawer?.renderer?.SilhouetteGraphic;
            if (graphic == null)
            {
                return;
            }
            if (!materialCache.TryGetValue(graphic, out (Material east, Material west) mats))
            {
                if (materialCache.Count > MaxCachedMaterials)
                {
                    materialCache.Clear();
                }
                Graphic colored = graphic.GetColoredVersion(ShaderDatabase.Silhouette, Color.white, Color.white);
                if (colored == null)
                {
                    return;
                }
                mats = (colored.MatEast, colored.MatWest);
                materialCache[graphic] = mats;
            }
            bool west = pawn.Rotation == Rot4.West;
            Material material = west ? mats.west : mats.east;
            if (material == null)
            {
                return;
            }
            Mesh mesh = west ? MeshPool.GridPlaneFlip(Vector2.one) : MeshPool.GridPlane(Vector2.one);
            // 和原版 SilhouetteUtility.DrawSilhouette 相同的縮放：小於 2.5 格時補回鏡頭拉遠造成的縮小
            Vector3 scale = Find.CameraDriver.InverseFovScale;
            float width = graphic.drawSize.x;
            float height = graphic.drawSize.y;
            scale.x *= width < 2.5f ? width + SilhouetteUtility.AdjustScale(width) : width;
            scale.z *= height < 2.5f ? height + SilhouetteUtility.AdjustScale(height) : height;
            Vector3 pos = pawn.DrawPos.SetToAltitude(AltitudeLayer.Silhouettes);
            Matrix4x4 matrix = Matrix4x4.TRS(pos, Quaternion.AngleAxis(0f, Vector3.up), scale);
            Color color = HintColor;
            color.a = alpha;
            block.SetColor(ShaderPropertyIDs.Color, color);
            GenDraw.DrawMeshNowOrLater(mesh, matrix, material, drawNow: false, block);
        }
    }
}
