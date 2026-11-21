using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 箱網（§3）：7×7 浮框，浮在灣內或外海的水面上。同一個表層網 Def 兩種場地通用，用顏色區分（灣內偏綠、外海偏藍）。
    /// 作業從相鄰的可站立格（岸邊、船體平台、橋）進行。被摧毀時魚全部逃走。
    /// </summary>
    public class Building_NetCage : Building
    {
        private CompNetCage cage;

        public CompNetCage Cage => cage ?? (cage = GetComp<CompNetCage>());

        public CompProperties_NetCage Props => Cage.Props;

        public static readonly Color BayTint = new Color(0.62f, 0.86f, 0.62f);

        public static readonly Color SeaTint = new Color(0.60f, 0.76f, 0.95f);

        public override Color DrawColor
        {
            get
            {
                Color venue = Spawned && SeaLevels.IsSeaLevel(Map) ? SeaTint : BayTint;
                Color t = Props.tint;
                return new Color(venue.r * t.r, venue.g * t.g, venue.b * t.b, 1f);
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            MapComponent_Aquaculture.For(map)?.Register(this);
            Notify_ColorChanged();
            map.GetComponent<MapComponent_SeaSurface>()?.MarkDirty(); // 海面遮罩：浮框四周的白沫
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            MapComponent_Aquaculture.For(map)?.Deregister(this);
            base.DeSpawn(mode);
            map?.GetComponent<MapComponent_SeaSurface>()?.MarkDirty();
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            if (Spawned && (mode == DestroyMode.KillFinalize || mode == DestroyMode.Deconstruct))
            {
                Cage.EscapeAll();
            }
            base.Destroy(mode);
        }

        /// <summary>第一個相鄰的可站立格（南→東→西→北），作業與產物都在這裡。</summary>
        public IntVec3 AccessCell()
        {
            IntVec3 found = FindAccessCell(Map, this.OccupiedRect());
            return found.IsValid ? found : Position;
        }

        public static IntVec3 FindAccessCell(Map map, CellRect rect)
        {
            if (map == null) { return IntVec3.Invalid; }
            CellRect outer = rect.ExpandedBy(1);
            // 南、東、西、北邊各自掃
            foreach (IntVec3 c in SideCells(outer))
            {
                if (c.InBounds(map) && c.Standable(map) && !rect.Contains(c))
                {
                    return c;
                }
            }
            return IntVec3.Invalid;
        }

        private static IEnumerable<IntVec3> SideCells(CellRect outer)
        {
            for (int x = outer.minX; x <= outer.maxX; x++) { yield return new IntVec3(x, 0, outer.minZ); }
            for (int z = outer.minZ; z <= outer.maxZ; z++) { yield return new IntVec3(outer.maxX, 0, z); }
            for (int z = outer.minZ; z <= outer.maxZ; z++) { yield return new IntVec3(outer.minX, 0, z); }
            for (int x = outer.minX; x <= outer.maxX; x++) { yield return new IntVec3(x, 0, outer.maxZ); }
        }

        // ── 浮動（2026-11-22 使用者要求）：浮框跟著海面輕輕漂移、上下起伏，幅度隨天氣（海面的擺動倍率）。
        // 只動畫面：位置、選取框、作業格都不變。各座箱網的相位不同，不會整排一起晃。

        /// <summary>平靜時水平漂移的幅度（格）。</summary>
        public const float DriftAmplitude = 0.05f;

        /// <summary>平靜時上下起伏的幅度（格）。</summary>
        public const float BobAmplitude = 0.04f;

        public float SwayFactor
        {
            get
            {
                if (!Spawned) { return 1f; }
                MapComponent_SeaSurface sea = SeaLevels.IsSeaLevel(Map) ? Map.GetComponent<MapComponent_SeaSurface>() : null;
                return sea != null ? Mathf.Max(1f, sea.SwayFactor) : SeaSway.WeatherFactor(Map.windManager?.WindSpeed ?? 0f);
            }
        }

        /// <summary>某個時間（真實秒數）的畫面位移（格）。</summary>
        public static Vector3 FloatOffset(float t, float phase, float swayFactor)
        {
            Vector2 drift = SeaSway.Offset(t * 0.8f + phase, DriftAmplitude * swayFactor);
            float bob = BobAmplitude * swayFactor * (Mathf.Sin(t * 1.25f + phase * 1.7f) + 0.35f * Mathf.Sin(t * 2.9f + phase));
            return new Vector3(drift.x, 0f, drift.y + bob);
        }

        public float Phase => (thingIDNumber * 0.6180339f) % 1f * 40f;

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc + FloatOffset(Time.time, Phase, SwayFactor), flip);
        }
    }
}
