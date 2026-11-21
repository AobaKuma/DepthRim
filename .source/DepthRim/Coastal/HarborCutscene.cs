using System;
using System.Collections.Generic;
using DMSE;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 進出港演出（M4，設計稿 §6）。DMSE 的 <see cref="TransferTakeoffCutscene"/> 負責擷取船體影像、暫停遊戲與流程；
    /// 它畫船體時不會旋轉，所以這裡把它的船體設成全透明，自己在 <see cref="DrawExtras"/> 畫：位置與旋轉照 <see cref="HarborShowMath"/>。
    /// <list type="bullet">
    /// <item>靠岸：從進場口沿路線開進來、在迴轉點轉到最後的朝向、平移進泊位（舷靠時碰墊擠一下、浪花打在靠岸那一側；錨泊時船艏拋錨的水花），停好起伏。
    /// 擷取的是已經轉好朝向的船體（在船自己的地圖上），最後一幀才真的搬進殖民地，影像同幀消失。</item>
    /// <item>離港：同一條時間軸倒著播。船體先搬走（殖民地上不會同時有真的船），影像從泊位退出、轉向、沿路線開出地圖邊淡出。</item>
    /// </list>
    /// 遊戲暫停（奧德賽式，§6.5）；鏡頭從船慢慢移到泊位（離港反過來），船尾撒浪花，推進器的運轉聲。開船速度固定（演出長度跟著路線長度走）；短版（同一個泊位再次靠岸）只開路線最後一段、其他段也較短。
    /// </summary>
    public class HarborCutscene : TransferTakeoffCutscene
    {
        /// <summary>給驗收測試：最近一次開始的演出。</summary>
        public static HarborCutscene Last;

        private readonly List<Vector2> path;
        private readonly Vector2 berth;
        private readonly float finalHeading;
        private readonly bool sideBerth;
        private readonly bool shortVersion;
        private readonly bool departing;
        private readonly Vector2 fenderSide;
        private Material hullMaterial;
        private Sustainer propeller;
        private float nextSplash;
        private bool landed;

        public HarborCutscene(Map displayMap, Vector3 berthCenter, Action onComplete, Map captureMap, HashSet<IntVec3> cells,
            List<Vector2> path, float finalHeading, bool sideBerth, bool shortVersion, bool departing, Vector2 fenderSide)
            : base(displayMap, berthCenter, onComplete, captureMap, cells)
        {
            this.path = path;
            berth = new Vector2(berthCenter.x, berthCenter.z);
            this.finalHeading = finalHeading;
            this.sideBerth = sideBerth;
            this.shortVersion = shortVersion;
            this.departing = departing;
            this.fenderSide = fenderSide;
            timeline = HarborShowMath.TimelineFor(HarborShowMath.PathLength(path), shortVersion);
            Last = this;
        }

        private readonly HarborShowMath.Timeline timeline;

        public bool Departing => departing;

        public bool SideBerth => sideBerth;

        public bool ShortVersion => shortVersion;

        /// <summary>目前的時間軸位置（靠岸 0→1；離港也是 0→1，但姿態倒著取）。</summary>
        public float Progress => Mathf.Clamp01(Elapsed / Duration);

        /// <summary>目前畫出來的姿態（驗收測試看位置與角度）。</summary>
        public HarborShowMath.Pose CurrentPose => PoseAt(Progress);

        /// <summary>長度跟著路線長度走（開船的速度固定，見 <see cref="HarborShowMath.TimelineFor"/>）。</summary>
        protected override float Duration => timeline.Total;

        /// <summary>演出經過比例 → 姿態時間軸位置。離港是靠岸倒著播：同一個實際秒數倒過來取，兩邊的開船速度一樣。</summary>
        private float ShowTime(float progress)
        {
            float seconds = Mathf.Clamp01(progress) * timeline.Total;
            return HarborShowMath.ProgressAt(departing ? timeline.Total - seconds : seconds, timeline);
        }

        private HarborShowMath.Pose PoseAt(float progress)
        {
            return HarborShowMath.At(ShowTime(progress), path, berth, finalHeading, sideBerth, shortVersion);
        }

        protected override void StartEffects()
        {
            SoundDef loop = DefDatabase<SoundDef>.GetNamedSilentFail("DepthRim_PropellerLoop");
            propeller = loop?.TrySpawnSustainer(SoundInfo.OnCamera(MaintenanceType.PerFrame));
        }

        protected override void UpdateEffects(float elapsed, float progress)
        {
            float t = ShowTime(progress);
            if (propeller != null && !propeller.Ended)
            {
                // 靠岸：停好前推進器慢下來；離港：開出去越來越遠
                propeller.info.volumeFactor = departing ? Mathf.Lerp(0.4f, 1f, t) : 1f - Mathf.InverseLerp(HarborShowMath.SlideEnd - 0.05f, 1f, t);
                propeller.Maintain();
            }
            HarborShowMath.Pose pose = PoseAt(progress);
            // 鏡頭從船慢慢移到泊位（離港反過來：從泊位跟到船）
            Vector2 cam = Vector2.Lerp(pose.pos, berth, departing ? 1f - Mathf.SmoothStep(0f, 1f, progress) : Mathf.SmoothStep(0f, 1f, progress));
            Find.CameraDriver.JumpToCurrentMapLoc(new Vector3(cam.x, 0f, cam.y));
            // 船尾浪花：前進中每 0.12 秒一團
            if (t < HarborShowMath.SlideEnd && elapsed >= nextSplash)
            {
                nextSplash = elapsed + 0.12f;
                float heading = HarborShowMath.Compass(Vector2.up) + finalHeading + pose.angle;
                Vector2 back = -new Vector2(Mathf.Sin(heading * Mathf.Deg2Rad), Mathf.Cos(heading * Mathf.Deg2Rad));
                Vector2 stern = pose.pos + back * (Bounds.Height > Bounds.Width ? Bounds.Height : Bounds.Width) * 0.4f;
                if (InMap(stern))
                {
                    FleckMaker.WaterSplash(new Vector3(stern.x, 0f, stern.y), DisplayMap, Rand.Range(1.2f, 2.2f), Rand.Range(1.5f, 3f));
                }
            }
            // 靠上泊位的那一刻：舷靠浪花打在靠岸那一側並輕輕一震，錨泊是船艏拋錨的水花
            if (!departing && !landed && t >= HarborShowMath.TurnEnd + (HarborShowMath.SlideEnd - HarborShowMath.TurnEnd) * 0.8f)
            {
                landed = true;
                Vector2 hit = sideBerth ? berth + fenderSide * (Mathf.Abs(fenderSide.x) > 0f ? Bounds.Width : Bounds.Height) * 0.35f
                    : berth + new Vector2(Mathf.Sin(finalHeading * Mathf.Deg2Rad), Mathf.Cos(finalHeading * Mathf.Deg2Rad)) * (Bounds.Height > Bounds.Width ? Bounds.Height : Bounds.Width) * 0.4f;
                for (int i = 0; i < 4; i++)
                {
                    Vector2 p = hit + Rand.InsideUnitCircle * 1.5f;
                    if (InMap(p))
                    {
                        FleckMaker.WaterSplash(new Vector3(p.x, 0f, p.y), DisplayMap, Rand.Range(2f, 3.5f), Rand.Range(2f, 4f));
                    }
                }
                if (sideBerth)
                {
                    Find.CameraDriver.shaker.DoShake(0.4f);
                }
            }
        }

        private bool InMap(Vector2 p) => new IntVec3((int)p.x, 0, (int)p.y).InBounds(DisplayMap);

        protected override void StopEffects()
        {
            if (propeller != null && !propeller.Ended)
            {
                propeller.End();
            }
            propeller = null;
            if (hullMaterial != null)
            {
                UnityEngine.Object.Destroy(hullMaterial);
                hullMaterial = null;
            }
        }

        protected override float ZoomAt(float progress)
        {
            // 進場時拉遠一點看得到路線，停好時回到原本的大小
            // 不能超過鏡頭允許的最大縮放：鏡頭高度隨縮放上升，超過會超出遠裁切面，海面與地形被裁掉只剩船體（長船時 InitialZoom 本身就到頂了）
            float t = ShowTime(progress);
            return Mathf.Min(Mathf.Lerp(InitialZoom * 1.35f, InitialZoom, Mathf.SmoothStep(0f, 1f, t / HarborShowMath.SlideEnd)), MaxZoom);
        }

        // DMSE 畫的船體不會旋轉：設成全透明、不位移，由 DrawExtras 畫
        protected override Vector3 ShipOffset(float progress) => Vector3.zero;

        protected override Color ShipColor(float progress) => new Color(1f, 1f, 1f, 0f);

        protected override float FadeAt(float progress) => 0f;

        protected override float FlameAt(float progress) => 0f;

        protected override void DrawExtras(Vector3 offset, float progress)
        {
            Texture2D tex = CapturedTexture;
            if (tex == null)
            {
                return;
            }
            if (hullMaterial == null)
            {
                hullMaterial = new Material(ShaderDatabase.Transparent) { mainTexture = tex, renderQueue = 3200 };
            }
            float t = ShowTime(progress);
            HarborShowMath.Pose pose = HarborShowMath.At(t, path, berth, finalHeading, sideBerth, shortVersion);
            float bob = HarborShowMath.Bob(t, Elapsed);
            // 靠岸開頭淡入（離港倒著播＝結尾淡出）
            float alpha = Mathf.SmoothStep(0f, 1f, t / 0.08f);
            hullMaterial.color = new Color(1f, 1f, 1f, alpha);
            Vector3 pos = new Vector3(pose.pos.x, AltitudeLayer.Skyfaller.AltitudeFor(), pose.pos.y + bob);
            GenDraw.DrawQuad(hullMaterial, pos, Quaternion.AngleAxis(pose.angle, Vector3.up), new Vector3(Bounds.Width, 1f, Bounds.Height));
        }
    }
}
