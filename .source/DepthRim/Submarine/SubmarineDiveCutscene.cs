using System;
using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 從海面或冰面下潛的離場演出（出水演出 <see cref="SubmarineSurfacingCutscene"/> 的反向）。
    /// 船體不像離港那樣滑走，而是在原地沉下去：
    /// <list type="number">
    /// <item>0～1.6 s：壓載艙排氣，沿船脊噴出白色水霧，壓載艙進水聲。</item>
    /// <item>1～5 s：船體開始下沉，船邊海水翻滾，海浪從兩舷往甲板上漫（往船內打的水花）。</item>
    /// <item>船體照水深變暗偏藍、稍微縮小，2 s 後淡出，6.5 s 時完全沒入。</item>
    /// <item>4 s 起：水面合攏，從船的範圍往外擴散漣漪，留下一片泡沫。</item>
    /// </list>
    /// 鏡頭固定。顯示地圖上的效果用船體在海面地圖上的格子（下潛前的位置）。
    /// </summary>
    public class SubmarineDiveCutscene : TransferTakeoffCutscene
    {
        /// <summary>船體在海面地圖（顯示地圖）上的格子。</summary>
        private readonly HashSet<IntVec3> hull;

        private readonly CellRect hullRect;

        private readonly List<(IntVec3 cell, Vector3 normal)> edges = new List<(IntVec3, Vector3)>();

        private readonly List<IntVec3> spine = new List<IntVec3>();

        private FleckSystem flecks;

        private readonly EventQueue fleckTicker = new EventQueue(1f / 60f);

        private readonly DrawBatch drawBatch = new DrawBatch();

        private FleckDef foam;
        private FleckDef mist;
        private FleckDef churn;
        private FleckDef ripple;
        private FleckDef splash;

        private Sustainer flood;

        /// <summary>船體上方的排氣白霧：自己畫在船體影像之上（地圖的 fleck 會被 DMSE 畫的船體影像蓋住）。</summary>
        private struct Mist
        {
            public Vector3 pos;
            public Vector3 vel;
            public float size;
            public float grow;
            public float age;
            public float life;
            public float alpha;
        }

        private readonly List<Mist> mists = new List<Mist>();
        private Texture2D mistTex;
        private Material mistMat;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        private float lastElapsed;
        private float ventCarry;
        private float churnCarry;
        private float washCarry;
        private float rippleCarry;
        private bool closed;

        private readonly System.Random rand = new System.Random();

        private static readonly Color DeepWater = new Color(0.05f, 0.12f, 0.2f);

        private const float VentEnd = 1.6f;
        private const float SinkStart = 1f;
        private const float FadeStart = 2f;
        private const float SubmergedAt = 6.5f;
        private const float CloseAt = 4f;

        /// <summary>測試用：各種效果生成了幾個。</summary>
        public int Vents { get; private set; }
        public int Churns { get; private set; }
        public int Washes { get; private set; }
        public int Ripples { get; private set; }

        public float Seconds => Elapsed;

        /// <param name="captureCells">船體在潛艇地圖（擷取地圖）上的格子。</param>
        /// <param name="displayCells">船體在海面地圖（顯示地圖）上原本的格子。</param>
        public SubmarineDiveCutscene(Map displayMap, Vector3 center, Action onComplete, Map captureMap, HashSet<IntVec3> captureCells, HashSet<IntVec3> displayCells)
            : base(displayMap, center, onComplete, captureMap, captureCells)
        {
            hull = displayCells ?? new HashSet<IntVec3>();
            hullRect = hull.Count > 0 ? CellRect.FromCellList(hull) : CellRect.SingleCell(center.ToIntVec3());
            BuildEdges();
        }

        protected override float Duration => 8f;


        /// <summary>船邊（四鄰有不是船體的格子）與每一格往船外的方向；船脊是離船邊最遠的那一排。</summary>
        private void BuildEdges()
        {
            foreach (IntVec3 c in hull)
            {
                Vector3 n = Vector3.zero;
                foreach (IntVec3 d in GenAdj.CardinalDirections)
                {
                    if (!hull.Contains(c + d))
                    {
                        n += d.ToVector3();
                    }
                }
                if (n != Vector3.zero)
                {
                    edges.Add((c, n.normalized));
                }
            }
            bool alongX = hullRect.Width >= hullRect.Height;
            float mid = alongX ? hullRect.CenterVector3.z : hullRect.CenterVector3.x;
            spine.AddRange(hull.Where(c => Mathf.Abs((alongX ? c.z + 0.5f : c.x + 0.5f) - mid) <= 1f));
            if (spine.Count == 0)
            {
                spine.AddRange(hull);
            }
        }

        // ── 聲音、鏡頭、船體 ───────────────────────────────────────────

        protected override void StartEffects()
        {
            foam = DefDatabase<FleckDef>.GetNamedSilentFail("FoamSpray") ?? DefDatabase<FleckDef>.GetNamedSilentFail("DustPuffThick");
            mist = DefDatabase<FleckDef>.GetNamedSilentFail("DustPuffThick") ?? foam;
            churn = DefDatabase<FleckDef>.GetNamedSilentFail("WaterChurnNoise");
            ripple = DefDatabase<FleckDef>.GetNamedSilentFail("WaterRipple");
            splash = DefDatabase<FleckDef>.GetNamedSilentFail("DepthRim_SurfacingSplash");
            mistTex = SubmarineSurfacingCutscene.MakeSoftDisc();
            mistMat = new Material(ShaderDatabase.Transparent) { mainTexture = mistTex, renderQueue = 3260 };
            flood = DepthRimDefOf.DepthRim_FloodLoop?.TrySpawnSustainer(SoundInfo.InMap(new TargetInfo(hullRect.CenterCell, DisplayMap), MaintenanceType.PerFrame));
        }

        protected override void UpdateEffects(float elapsed, float progress)
        {
            if (flood != null && !flood.Ended)
            {
                // 進水聲：排氣時最大，船沒入後漸弱
                flood.info.volumeFactor = 1f - Mathf.InverseLerp(SubmergedAt - 1f, Duration, elapsed) * 0.9f;
                flood.Maintain();
            }
        }

        protected override void StopEffects()
        {
            if (flood != null && !flood.Ended)
            {
                flood.End();
            }
            flood = null;
            if (mistMat != null)
            {
                UnityEngine.Object.Destroy(mistMat);
                mistMat = null;
            }
            if (mistTex != null)
            {
                UnityEngine.Object.Destroy(mistTex);
                mistTex = null;
            }
        }

        protected override float ZoomAt(float progress) => InitialZoom;

        protected override Vector3 ShipOffset(float progress)
        {
            // 排氣時船身往下一頓，之後穩穩沉下去（俯視看不出深度，只有一點點晃）
            float t = Elapsed;
            float z = -0.06f * Mathf.Sin(Mathf.Clamp01(t / VentEnd) * Mathf.PI) * Mathf.Exp(-t * 0.6f);
            return new Vector3(0f, 0f, z);
        }

        protected override float ShipScale(float progress)
        {
            return Mathf.Lerp(1f, 0.95f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(SinkStart, SubmergedAt, Elapsed)));
        }

        protected override float FadeAt(float progress)
        {
            return Mathf.InverseLerp(FadeStart, SubmergedAt, Elapsed);
        }

        protected override Color ShipColor(float progress)
        {
            float t = Elapsed;
            Color water = WaterVisualTierDef.For(DepthTier.Shallow)?.deepColor ?? DeepWater;
            Color c = Color.Lerp(Color.white, water, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(SinkStart, SubmergedAt - 0.5f, t)) * 0.85f);
            c.a = 1f - Mathf.SmoothStep(0f, 1f, FadeAt(progress));
            return c;
        }

        protected override float FlameAt(float progress) => 0f;

        // ── 每一幀 ─────────────────────────────────────────────────────

        protected override void DrawExtras(Vector3 offset, float progress)
        {
            float t = Elapsed;
            float dt = Mathf.Clamp(t - lastElapsed, 0f, 0.1f);
            lastElapsed = t;
            if (flecks == null)
            {
                flecks = new FleckSystemThrown(DisplayMap.flecks);
            }

            if (t < VentEnd)
            {
                // 壓載艙排氣：沿船脊噴出白霧，開頭最猛
                ventCarry += Mathf.Lerp(70f, 12f, t / VentEnd) * dt;
                while (ventCarry >= 1f)
                {
                    ventCarry -= 1f;
                    Vent();
                }
            }
            if (t >= SinkStart * 0.5f && t < SubmergedAt)
            {
                // 船邊海水翻滾，沉到一半最劇烈
                float peak = 1f - Mathf.Abs(Mathf.InverseLerp(SinkStart * 0.5f, SubmergedAt, t) * 2f - 1f);
                churnCarry += (10f + 50f * peak) * dt;
                while (churnCarry >= 1f)
                {
                    churnCarry -= 1f;
                    Churn();
                }
            }
            if (t >= SinkStart + 0.5f && t < CloseAt + 1f)
            {
                // 海浪從兩舷漫上甲板
                washCarry += 22f * dt;
                while (washCarry >= 1f)
                {
                    washCarry -= 1f;
                    Wash();
                }
            }
            if (!closed && t >= CloseAt)
            {
                closed = true;
                Close();
            }
            if (t >= CloseAt)
            {
                rippleCarry += Mathf.Lerp(12f, 2f, Mathf.InverseLerp(CloseAt, Duration, t)) * dt;
                while (rippleCarry >= 1f)
                {
                    rippleCarry -= 1f;
                    Ripple(RandomHullPoint(), 1.5f + Rand01() * 2f);
                }
            }

            UpdateMists(dt);
            fleckTicker.Push(dt);
            while (fleckTicker.Pop())
            {
                flecks.Tick();
            }
            flecks.Update(dt);
            flecks.ForceDraw(drawBatch);
            drawBatch.Flush(true);
            DrawMists();
        }

        private void UpdateMists(float dt)
        {
            for (int i = mists.Count - 1; i >= 0; i--)
            {
                Mist m = mists[i];
                m.age += dt;
                if (m.age >= m.life)
                {
                    mists.RemoveAt(i);
                    continue;
                }
                m.pos += m.vel * dt;
                m.vel *= 1f - 1.2f * dt;
                m.size += m.grow * dt;
                mists[i] = m;
            }
        }

        private void DrawMists()
        {
            if (mistMat == null)
            {
                return;
            }
            float alt = AltitudeLayer.Skyfaller.AltitudeFor() + 0.2f;
            foreach (Mist m in mists)
            {
                float u = m.age / m.life;
                float a = m.alpha * Mathf.Min(1f, u * 8f) * (1f - u * u);
                block.SetColor(ShaderPropertyIDs.Color, new Color(0.95f, 0.98f, 1f, a));
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(m.pos.WithY(alt), Quaternion.identity, new Vector3(m.size, 1f, m.size)), mistMat, 0, null, 0, block);
            }
        }

        /// <summary>壓載艙排氣：船脊上冒出一團白霧（看得見的大小），偶爾加一道水沫。</summary>
        private void Vent()
        {
            if (spine.Count == 0)
            {
                return;
            }
            Vector3 at = spine[rand.Next(spine.Count)].ToVector3Shifted() + new Vector3(Rand01() - 0.5f, 0f, Rand01() - 0.5f);
            float ang = Rand01() * Mathf.PI * 2f;
            float speed = 0.4f + Rand01() * 1.2f;
            mists.Add(new Mist
            {
                pos = at,
                vel = new Vector3(Mathf.Cos(ang) * speed, 0f, Mathf.Sin(ang) * speed),
                size = 1.2f + Rand01() * 1.2f,
                grow = 1.2f + Rand01() * 1.2f,
                life = 1.2f + Rand01() * 1.2f,
                alpha = 0.55f + Rand01() * 0.3f,
            });
            Vents++;
            if (foam != null && Rand01() < 0.5f)
            {
                FleckCreationData d = FleckMaker.GetDataStatic(at, DisplayMap, foam, 1.5f + Rand01() * 1.5f);
                d.velocityAngle = Rand01() * 360f;
                d.velocitySpeed = 0.8f + Rand01() * 1.5f;
                d.instanceColor = new Color(0.95f, 0.97f, 1f, 0.9f);
                flecks.CreateFleck(d);
            }
        }

        private void Churn()
        {
            if (churn == null || edges.Count == 0)
            {
                return;
            }
            (IntVec3 cell, Vector3 normal) e = edges[rand.Next(edges.Count)];
            Vector3 at = e.cell.ToVector3Shifted() + e.normal * (0.4f + Rand01() * 0.8f);
            FleckCreationData d = FleckMaker.GetDataStatic(at, DisplayMap, churn, 3f + Rand01() * 5f);
            d.velocityAngle = e.normal.AngleFlat() + Rand01() * 80f - 40f;
            d.velocitySpeed = 0.3f + Rand01() * 1.2f;
            d.rotationRate = 30f + Rand01() * 20f;
            flecks.CreateFleck(d);
            Churns++;
        }

        /// <summary>往船內打的水花：從船邊外側往甲板方向。</summary>
        private void Wash()
        {
            if (splash == null || edges.Count == 0)
            {
                return;
            }
            (IntVec3 cell, Vector3 normal) e = edges[rand.Next(edges.Count)];
            Vector3 inward = Quaternion.AngleAxis(Rand01() * 40f - 20f, Vector3.up) * -e.normal;
            float size = 1.4f + Rand01() * 1.8f;
            FleckCreationData d = FleckMaker.GetDataStatic(e.cell.ToVector3Shifted() + e.normal * 0.6f + inward * size * 0.3f, DisplayMap, splash, size);
            d.rotation = inward.AngleFlat();
            d.velocityAngle = d.rotation;
            d.velocitySpeed = (0.3f + Rand01() * 0.6f) * Mathf.Sqrt(size);
            flecks.CreateFleck(d);
            Washes++;
        }

        /// <summary>水面在船上方合攏：一圈大漣漪加一片泡沫。</summary>
        private void Close()
        {
            Ripple(hullRect.CenterVector3, Mathf.Max(hullRect.Width, hullRect.Height) * 0.9f);
            foreach (IntVec3 c in spine.Where((_, i) => i % 3 == 0))
            {
                Ripple(c.ToVector3Shifted(), 2.5f + Rand01() * 2f);
                if (mist != null)
                {
                    FleckCreationData d = FleckMaker.GetDataStatic(c.ToVector3Shifted(), DisplayMap, mist, 2.5f + Rand01() * 1.5f);
                    d.velocityAngle = Rand01() * 360f;
                    d.velocitySpeed = 0.2f + Rand01() * 0.4f;
                    d.instanceColor = new Color(0.9f, 0.95f, 1f, 0.6f);
                    flecks.CreateFleck(d);
                }
            }
        }

        private void Ripple(Vector3 at, float size)
        {
            if (ripple == null)
            {
                return;
            }
            FleckCreationData d = FleckMaker.GetDataStatic(at, DisplayMap, ripple, size);
            flecks.CreateFleck(d);
            Ripples++;
        }

        private Vector3 RandomHullPoint()
        {
            if (hull.Count == 0)
            {
                return Center;
            }
            return hull.ElementAt(rand.Next(hull.Count)).ToVector3Shifted();
        }

        private float Rand01() => (float)rand.NextDouble();
    }
}
