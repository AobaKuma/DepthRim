using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛水鐘收放的離場動畫（2026-11-22）：收放一開始鐘就離開地圖（收進吊架），地圖上只留一段約 1.5 秒的影像——
    /// 在水面往下沉（往下移、淡出，冒一圈水花）；在海床被往上拉（往上移、淡出，揚起一點泥沙）。只有位移與淡出，不縮放（2026-11-22 使用者決定）。
    /// 用真實時間播（暫停時也會播完）；只是畫面，不影響任何狀態。
    /// </summary>
    public static class BellFx
    {
        /// <summary>動畫長度（真實秒）。</summary>
        public const float Duration = 1.5f;

        /// <summary>給測試：目前在播的動畫數。</summary>
        public static int Playing(Map map) => map?.GetComponent<MapComponent_BellFx>()?.Count ?? 0;

        /// <summary>
        /// 位置、大小、透明度、亮度隨進度（0..1）變化（純函式，單元測試）。<paramref name="sink"/>：往下沉；否則往上拉。
        /// 回傳的 offsetZ 是畫面上往北的位移（往上拉＝往北、往鏡頭靠近）。
        /// </summary>
        public static void Pose(bool sink, float t, out float offsetZ, out float scale, out float alpha, out float brightness)
        {
            t = Mathf.Clamp01(t);
            float ease = t * t * (3f - 2f * t);
            if (sink)
            {
                offsetZ = -0.35f * ease;
                scale = 1f;
                alpha = 1f - ease;
                brightness = 1f;
            }
            else
            {
                offsetZ = 3.5f * t * t;
                scale = 1f;
                alpha = 1f - Mathf.Clamp01((t - 0.25f) / 0.75f);
                brightness = 1f;
            }
        }

        /// <summary>鐘離開地圖之前呼叫：記下影像並在原地播動畫，冒水花或揚起泥沙。</summary>
        public static void Play(Building_DivingBell bell, bool sink)
        {
            if (bell == null || !bell.Spawned)
            {
                return;
            }
            Map map = bell.Map;
            Material mat = bell.Graphic?.MatAt(bell.Rotation, bell);
            Vector3 center = bell.TrueCenter();
            map.GetComponent<MapComponent_BellFx>()?.Add(new MapComponent_BellFx.Entry
            {
                center = center,
                size = bell.Graphic?.drawSize ?? new Vector2(2f, 2f),
                texture = mat?.mainTexture,
                sink = sink,
                start = Time.realtimeSinceStartup,
            });
            if (!center.ShouldSpawnMotesAt(map))
            {
                return;
            }
            for (int i = 0; i < 6; i++)
            {
                Vector3 at = center + Gen.RandomHorizontalVector(1.2f);
                if (sink)
                {
                    FleckMaker.WaterSplash(at, map, Rand.Range(1.2f, 2f), Rand.Range(1.5f, 3f));
                }
                else
                {
                    FleckCreationData d = FleckMaker.GetDataStatic(at, map, FleckDefOf.DustPuffThick, Rand.Range(1.4f, 2.4f));
                    d.velocityAngle = Rand.Range(0f, 360f);
                    d.velocitySpeed = Rand.Range(0.2f, 0.6f);
                    d.instanceColor = new Color(0.3f, 0.36f, 0.42f, 0.7f);
                    map.flecks.CreateFleck(d);
                }
            }
        }
    }

    public class MapComponent_BellFx : MapComponent
    {
        public struct Entry
        {
            public Vector3 center;
            public Vector2 size;
            public Texture texture;
            public bool sink;
            public float start;
        }

        private readonly List<Entry> entries = new List<Entry>();

        private static readonly Dictionary<Texture, Material> materials = new Dictionary<Texture, Material>();

        private MaterialPropertyBlock block;

        public MapComponent_BellFx(Map map) : base(map)
        {
        }

        public int Count => entries.Count;

        public void Add(Entry e)
        {
            entries.Add(e);
        }

        public override void MapComponentUpdate()
        {
            if (entries.Count == 0)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            entries.RemoveAll(e => now - e.start >= BellFx.Duration);
            if (Find.CurrentMap != map)
            {
                return;
            }
            block ??= new MaterialPropertyBlock();
            foreach (Entry e in entries)
            {
                if (e.texture == null)
                {
                    continue;
                }
                if (!materials.TryGetValue(e.texture, out Material mat) || mat == null)
                {
                    mat = new Material(ShaderDatabase.Transparent) { mainTexture = e.texture };
                    materials[e.texture] = mat;
                }
                BellFx.Pose(e.sink, (now - e.start) / BellFx.Duration, out float dz, out float scale, out float alpha, out float bright);
                block.SetColor(ShaderPropertyIDs.Color, new Color(bright, bright, bright, alpha));
                Vector3 pos = e.center + new Vector3(0f, 0f, dz);
                pos.y = AltitudeLayer.MoteOverhead.AltitudeFor();
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(e.size.x * scale, 1f, e.size.y * scale)), mat, 0, null, 0, block);
            }
        }
    }
}
