using System.Collections.Generic;
using DMSE;
using Fortified;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇停泊到海床上的觸底揚塵：DMSE 把船體放到海床地圖（<see cref="ShipSeparationUtility.HullPlaced"/>）後，
    /// 船體壓到海床的那幾格（<see cref="MapComponent_SeabedMemory"/> 有記錄）周圍揚起一圈沉積物：
    /// <list type="number">
    /// <item>0 s：觸底那一下，緊貼船體外緣的一圈濃塵往外推開。</item>
    /// <item>0～3 s：外圍慢慢翻起、擴散的大團懸浮沉積物，越來越稀。</item>
    /// </list>
    /// 塵的顏色取海床地形的顏色，再往水色（暗藍）混一些。DMSE 的降落沒有過場動畫可掛，所以這是降落後在地圖上播的效果。
    /// </summary>
    public class MapComponent_TouchdownDust : MapComponent
    {
        /// <summary>揚塵從船體外緣往外幾格。</summary>
        public const int RingRadius = 2;

        /// <summary>翻起的塵持續幾 tick（3 秒）。</summary>
        public const int DurationTicks = 180;

        private const int MaxBurst = 160;

        private static readonly Color WaterTint = new Color(0.28f, 0.38f, 0.48f);

        private readonly List<(IntVec3 cell, Vector3 outward, Color color)> ring = new List<(IntVec3, Vector3, Color)>();

        private int startTick = -1;

        /// <summary>給測試：最近一次觸底的外圈格數、目前為止丟出的塵團數。</summary>
        public int lastRingCells;

        public int emitted;

        public MapComponent_TouchdownDust(Map map) : base(map)
        {
        }

        public bool Active => startTick >= 0;

        /// <summary>
        /// 揚塵的外圈：船體外 1～<paramref name="radius"/> 格、而且是地面（<paramref name="ground"/>）的格子，
        /// 只取靠近觸底格（<paramref name="contact"/>）的。純函式，可單元測試。
        /// </summary>
        public static List<IntVec3> RingCells(HashSet<IntVec3> hull, HashSet<IntVec3> contact, System.Predicate<IntVec3> ground, int radius)
        {
            List<IntVec3> result = new List<IntVec3>();
            if (contact.Count == 0)
            {
                return result;
            }
            HashSet<IntVec3> seen = new HashSet<IntVec3>();
            foreach (IntVec3 c in contact)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        IntVec3 n = new IntVec3(c.x + dx, 0, c.z + dz);
                        if (hull.Contains(n) || !seen.Add(n) || !ground(n))
                        {
                            continue;
                        }
                        result.Add(n);
                    }
                }
            }
            return result;
        }

        /// <summary>船體剛放到這張地圖：壓到海床就開始揚塵（回傳是否開始）。</summary>
        public bool Notify_HullSettled(IEnumerable<IntVec3> hullCells)
        {
            MapComponent_SeabedMemory memory = map.GetComponent<MapComponent_SeabedMemory>();
            if (memory == null)
            {
                return false;
            }
            HashSet<IntVec3> hull = new HashSet<IntVec3>(hullCells);
            HashSet<IntVec3> contact = new HashSet<IntVec3>();
            foreach (IntVec3 c in hull)
            {
                if (memory.TryGetUnder(c, out _))
                {
                    contact.Add(c);
                }
            }
            List<IntVec3> cells = RingCells(hull, contact, IsGround, RingRadius);
            ring.Clear();
            lastRingCells = cells.Count;
            if (cells.Count == 0)
            {
                return false;
            }
            Vector3 center = Vector3.zero;
            foreach (IntVec3 c in contact)
            {
                center += c.ToVector3Shifted();
            }
            center /= contact.Count;
            foreach (IntVec3 c in cells)
            {
                Vector3 outward = c.ToVector3Shifted() - center;
                outward.y = 0f;
                ring.Add((c, outward.sqrMagnitude > 0.01f ? outward.normalized : Vector3.forward, DustColor(map.terrainGrid.TerrainAt(c))));
            }
            startTick = Find.TickManager.TicksGame;
            Burst();
            return true;
        }

        private bool IsGround(IntVec3 c)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            TerrainDef t = map.terrainGrid.TerrainAt(c);
            return t != null && !t.dontRender && !MapComponent_SeabedMemory.IsPlatform(t);
        }

        /// <summary>海床的顏色（地形的 DrawColor）往水色混一半，半透明。</summary>
        public static Color DustColor(TerrainDef terrain)
        {
            Color baseColor = terrain != null ? terrain.DrawColor : new Color(0.55f, 0.55f, 0.5f);
            Color sediment = new Color(0.62f * baseColor.r + 0.1f, 0.6f * baseColor.g + 0.1f, 0.55f * baseColor.b + 0.1f);
            Color c = Color.Lerp(sediment, WaterTint, 0.45f);
            c.a = 0.85f;
            return c;
        }

        /// <summary>觸底那一下：緊貼船體外緣的濃塵往外推。</summary>
        private void Burst()
        {
            int step = Mathf.Max(1, ring.Count / MaxBurst);
            for (int i = 0; i < ring.Count; i += step)
            {
                (IntVec3 cell, Vector3 outward, Color color) = ring[i];
                Throw(cell.ToVector3Shifted() - outward * 0.5f + Gen.RandomHorizontalVector(0.3f), outward, color, Rand.Range(1.6f, 2.6f), Rand.Range(0.8f, 1.6f));
            }
        }

        public override void MapComponentTick()
        {
            if (startTick < 0)
            {
                return;
            }
            int age = Find.TickManager.TicksGame - startTick;
            if (age >= DurationTicks || ring.Count == 0)
            {
                startTick = -1;
                ring.Clear();
                return;
            }
            if (age % 5 != 0)
            {
                return;
            }
            // 越來越稀：一開始每 5 tick 約十團，結束時一兩團
            float fade = 1f - (float)age / DurationTicks;
            int count = Mathf.Max(1, Mathf.RoundToInt(Mathf.Min(ring.Count, 40) * 0.25f * fade));
            for (int i = 0; i < count; i++)
            {
                (IntVec3 cell, Vector3 outward, Color color) = ring.RandomElement();
                float drift = RingRadius * (1f - fade) * 0.6f;
                Throw(cell.ToVector3Shifted() + outward * drift + Gen.RandomHorizontalVector(0.6f), outward, color.ToTransparent(color.a * (0.5f + 0.5f * fade)),
                    Rand.Range(2.4f, 4.2f), Rand.Range(0.15f, 0.5f));
            }
        }

        private void Throw(Vector3 loc, Vector3 outward, Color color, float scale, float speed)
        {
            if (!loc.ShouldSpawnMotesAt(map))
            {
                return;
            }
            FleckCreationData d = FleckMaker.GetDataStatic(loc, map, FleckDefOf.DustPuffThick, scale);
            d.rotationRate = Rand.Range(-30f, 30f);
            d.velocityAngle = outward.AngleFlat() + Rand.Range(-25f, 25f);
            d.velocitySpeed = speed;
            d.instanceColor = color;
            map.flecks.CreateFleck(d);
            emitted++;
        }
    }

    /// <summary>DMSE 把船體放到別張地圖時，通知那張地圖的 <see cref="MapComponent_TouchdownDust"/>。</summary>
    [StaticConstructorOnStartup]
    public static class TouchdownDustHook
    {
        static TouchdownDustHook()
        {
            ShipSeparationUtility.HullPlaced -= OnHullPlaced;
            ShipSeparationUtility.HullPlaced += OnHullPlaced;
        }

        private static void OnHullPlaced(ShipSeparationUtility.HullMove move)
        {
            if (move.to == null || move.cells == null || move.to.Parent is WorldObject_Submarine)
            {
                return;
            }
            MapComponent_TouchdownDust dust = move.to.GetComponent<MapComponent_TouchdownDust>();
            if (dust == null)
            {
                return;
            }
            List<IntVec3> cells = new List<IntVec3>(move.cells.Count);
            foreach (IntVec3 c in move.cells)
            {
                cells.Add(c + move.offset);
            }
            dust.Notify_HullSettled(cells);
        }
    }
}
