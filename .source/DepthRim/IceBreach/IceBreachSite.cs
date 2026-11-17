using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 地表格上的冰況（設計稿 §3）：正上方是原版海冰生態區 → 多年冰；年均溫低於 −10 °C 的海 → 一年冰（夏天可能化光）；
    /// 冰原 → 冰棚（頂不開）；其他陸地 → 沒有海；其他海 → 開放海面。冰厚照冰凍度日、格子雜訊與冰間水道算，同一季內穩定。
    /// </summary>
    public static class IceConditions
    {
        public struct Info
        {
            public PlanetTile surfaceTile;
            public IceKind kind;
            public bool land;
            public float thickness;
            public float firstYear;
            public bool lead;
            public float airTemperature;

            /// <summary>上面有冰（要破冰）。</summary>
            public bool Frozen => (kind == IceKind.FirstYear || kind == IceKind.MultiYear) && thickness > 0f;

            /// <summary>可以常規上浮：上面是海、沒有冰。</summary>
            public bool OpenSea => !land && kind == IceKind.None;
        }

        public const float SeasonalIceBelow = -10f;

        /// <summary>測試與除錯用：有冰的格子一律當這麼厚（null = 照計算）。</summary>
        public static float? debugThickness;

        public static Info At(PlanetTile surfaceTile)
        {
            return At(surfaceTile, Find.TickManager.TicksAbs);
        }

        public static Info At(PlanetTile surfaceTile, int ticksAbs)
        {
            Info info = new Info { surfaceTile = surfaceTile, kind = IceKind.None };
            Tile tile = surfaceTile.Valid ? surfaceTile.Tile : null;
            if (tile == null)
            {
                info.land = true;
                return info;
            }
            info.airTemperature = GenTemperature.GetTemperatureFromSeasonAtTile(ticksAbs, surfaceTile);
            string biome = tile.PrimaryBiome?.defName;
            if (biome == "SeaIce")
            {
                info.kind = IceKind.MultiYear;
            }
            else if (biome == "IceSheet")
            {
                info.kind = IceKind.Shelf;
                info.land = true;
                return info;
            }
            else if (!tile.WaterCovered)
            {
                info.land = true;
                return info;
            }
            else if (tile.temperature < SeasonalIceBelow)
            {
                info.kind = IceKind.FirstYear;
            }
            else
            {
                return info;
            }

            float[] temps = new float[12];
            for (int i = 0; i < 12; i++)
            {
                temps[i] = GenTemperature.AverageTemperatureAtTileForTwelfth(surfaceTile, (Twelfth)i);
            }
            int twelfth = (int)(ticksAbs / GenDate.TicksPerTwelfth % 12);
            int year = ticksAbs / GenDate.TicksPerYear;
            int season = twelfth / 3;
            int id = surfaceTile.tileId;
            info.firstYear = IceBreachMath.FirstYearThickness(temps, twelfth);
            info.lead = IceBreachMath.HasLead(IceCrackPattern.Noise(id, year * 4 + season, 0x1EAD), twelfth % 3 == 0);
            info.thickness = IceBreachMath.Thickness(info.kind, info.firstYear, IceCrackPattern.Noise(id, 0, 0x3417), IceCrackPattern.Noise(id, year, 0x10CA), info.lead);
            if (debugThickness.HasValue)
            {
                info.thickness = debugThickness.Value;
                info.lead = false;
            }
            if (info.thickness <= 0f)
            {
                // 一年冰化光了：這一季是開放海面
                info.kind = IceKind.None;
                info.lead = false;
            }
            return info;
        }
    }

    /// <summary>破冰點：地表格上的冰面地點。有玩家建築、擋住移除的 Pawn 或玩家正在看時保留地圖（和海面地點一樣）。</summary>
    public class WorldObject_IceBreachSite : MapParent
    {
        /// <summary>破冰時量到的冰厚（冰面地圖的完整冰都當這麼厚）。</summary>
        public float iceThickness = 1f;

        public IceKind iceKind = IceKind.MultiYear;

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = true;
            if (!HasMap)
            {
                return false;
            }
            return Map.listerBuildings.allBuildingsColonist.Count == 0 && !Map.mapPawns.AnyPawnBlockingMapRemoval && Find.CurrentMap != Map;
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string ice = "DepthRim.IceBreach.SiteInspect".Translate(("DepthRim.IceKind." + iceKind).Translate(), iceThickness.ToString("0.0"));
            return s.NullOrEmpty() ? ice : s + "\n" + ice;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref iceThickness, "iceThickness", 1f);
            Scribe_Values.Look(ref iceKind, "iceKind", IceKind.MultiYear);
        }
    }

    /// <summary>
    /// 冰面地圖。冰厚記在 <see cref="MapComponent_IceSheet"/>（由地點的冰厚決定）。
    /// 多年冰（地表格是原版 <c>SeaIce</c>）：照原版生態區生成地形（冰、淺水坑、靠海那側的海水），
    /// 再把冰換成海冰板、水換成正在重新結冰的開放水（當成冰間水道）。一年冰（地表格是海洋生態區）：整張鋪海冰板。
    /// </summary>
    public class GenStep_IceFloe : GenStep
    {
        public override int SeedPart => 0x1CEF10E;

        public const string VanillaSeaIce = "SeaIce";

        public override void Generate(Map map, GenStepParams parms)
        {
            WorldObject_IceBreachSite site = map.Parent as WorldObject_IceBreachSite;
            MapComponent_IceSheet sheet = map.GetComponent<MapComponent_IceSheet>();
            if (sheet != null && site != null)
            {
                sheet.baseThickness = site.iceThickness;
            }
            TerrainDef ice = DepthRimDefOf.DepthRim_SeaIceSheet;
            if (!UsesVanillaTerrain(map, site))
            {
                foreach (IntVec3 c in map.AllCells)
                {
                    map.terrainGrid.SetTerrain(c, ice);
                }
                return;
            }
            new GenStep_ElevationFertility().Generate(map, parms);
            new GenStep_Terrain().Generate(map, parms);
            TerrainDef water = DepthRimDefOf.DepthRim_BreachWater;
            foreach (IntVec3 c in map.AllCells)
            {
                bool isWater = c.GetTerrain(map)?.IsWater ?? false;
                map.terrainGrid.SetTerrain(c, isWater ? water : ice);
                if (isWater)
                {
                    sheet?.TrackOpenWater(c);
                }
            }
        }

        /// <summary>多年冰、而且地圖生態區就是原版海冰時，照原版生態區生成地形。</summary>
        public static bool UsesVanillaTerrain(Map map, WorldObject_IceBreachSite site)
        {
            return site != null && site.iceKind == IceKind.MultiYear && map.Biome?.defName == VanillaSeaIce;
        }
    }

    /// <summary>
    /// 冰面地圖的冰（設計稿 §7.2、§7.3）：破口（開放水）與新冰每小時照氣溫累積冰凍度日、重新結冰
    /// （開放水 → 0.1 m 新冰 → 0.3 m 完整冰），裂紋累積 50 °C·日後癒合。船體四周的冰比 0.4 m 厚時潛艇被凍住、不能下潛。
    /// 只在破冰點地圖上作用。
    /// </summary>
    public class MapComponent_IceSheet : MapComponent
    {
        public const int UpdateInterval = GenDate.TicksPerHour;

        /// <summary>完整冰的厚度（地點量到的值）。</summary>
        public float baseThickness = 1f;

        /// <summary>正在重新結冰的格子：累積的冰凍度日。</summary>
        private Dictionary<IntVec3, float> refreezing = new Dictionary<IntVec3, float>();

        /// <summary>裂紋：累積的冰凍度日（到 <see cref="IceBreachMath.CrackHealDegreeDays"/> 癒合）。</summary>
        private Dictionary<IntVec3, float> cracks = new Dictionary<IntVec3, float>();

        private List<IntVec3> tmpKeys;
        private List<float> tmpValues;
        private List<IntVec3> tmpKeys2;
        private List<float> tmpValues2;

        public MapComponent_IceSheet(Map map) : base(map)
        {
        }

        public bool Active => map.Parent is WorldObject_IceBreachSite;

        public int RefreezingCount => refreezing.Count;

        public int CrackCount => cracks.Count;

        public static MapComponent_IceSheet For(Map map) => map?.GetComponent<MapComponent_IceSheet>();

        /// <summary>這一格變成開放水，從頭開始結冰。</summary>
        public void TrackOpenWater(IntVec3 c)
        {
            refreezing[c] = 0f;
            cracks.Remove(c);
        }

        public void TrackCrack(IntVec3 c)
        {
            cracks[c] = 0f;
        }

        /// <summary>這一格的冰厚（m）。開放水 0。</summary>
        public float ThicknessAt(IntVec3 c)
        {
            if (refreezing.TryGetValue(c, out float fdd))
            {
                return IceBreachMath.RefrozenThickness(fdd);
            }
            TerrainDef t = c.GetTerrain(map);
            if (t == DepthRimDefOf.DepthRim_BreachWater)
            {
                return 0f;
            }
            if (t == DepthRimDefOf.DepthRim_NewIce)
            {
                return IceBreachMath.NewIceFrom;
            }
            if (t == DepthRimDefOf.DepthRim_SeaIceSheet || t == DepthRimDefOf.DepthRim_IceCracked || t == DepthRimDefOf.DepthRim_IceRubble)
            {
                return baseThickness;
            }
            return 0f;
        }

        /// <summary>船體旁邊（八鄰接、不在船體上）的格子。</summary>
        public static List<IntVec3> RingAround(Map map, ICollection<IntVec3> hull)
        {
            HashSet<IntVec3> set = hull as HashSet<IntVec3> ?? new HashSet<IntVec3>(hull);
            HashSet<IntVec3> ring = new HashSet<IntVec3>();
            foreach (IntVec3 c in set)
            {
                for (int i = 0; i < 8; i++)
                {
                    IntVec3 n = c + GenAdj.AdjacentCells[i];
                    if (n.InBounds(map) && !set.Contains(n))
                    {
                        ring.Add(n);
                    }
                }
            }
            return ring.ToList();
        }

        /// <summary>船體四周最厚的冰（m）。</summary>
        public float ThickestAround(ICollection<IntVec3> hull)
        {
            float max = 0f;
            foreach (IntVec3 c in RingAround(map, hull))
            {
                max = Mathf.Max(max, ThicknessAt(c));
            }
            return max;
        }

        public bool FrozenIn(ICollection<IntVec3> hull)
        {
            return ThickestAround(hull) > IceBreachMath.FrozenInThickness;
        }

        /// <summary>把船體四周鑿成開放水（破冰結構撞開凍住的冰）。</summary>
        public void OpenRing(ICollection<IntVec3> hull)
        {
            foreach (IntVec3 c in RingAround(map, hull))
            {
                if (c.GetEdifice(map) != null)
                {
                    continue;
                }
                map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_BreachWater);
                TrackOpenWater(c);
            }
        }

        public override void MapComponentTick()
        {
            if (!Active || (refreezing.Count == 0 && cracks.Count == 0) || Find.TickManager.TicksGame % UpdateInterval != 0)
            {
                return;
            }
            Advance(IceBreachMath.FreezingDegreeDays(map.mapTemperature.OutdoorTemp, UpdateInterval / (float)GenDate.TicksPerDay));
        }

        /// <summary>累積 <paramref name="degreeDays"/> 的冰凍度日，更新地形（測試可以直接呼叫）。</summary>
        public void Advance(float degreeDays)
        {
            if (degreeDays <= 0f)
            {
                return;
            }
            foreach (IntVec3 c in refreezing.Keys.ToList())
            {
                TerrainDef now = c.GetTerrain(map);
                if (now != DepthRimDefOf.DepthRim_BreachWater && now != DepthRimDefOf.DepthRim_NewIce && now != DepthRimDefOf.DepthRim_SeaIceSheet)
                {
                    // 被別的東西蓋掉了（例如船體又降落在這裡）
                    refreezing.Remove(c);
                    continue;
                }
                float fdd = refreezing[c] + degreeDays;
                float h = IceBreachMath.RefrozenThickness(fdd);
                TerrainDef want = h < IceBreachMath.NewIceFrom ? DepthRimDefOf.DepthRim_BreachWater
                    : h < IceBreachMath.SolidIceFrom ? DepthRimDefOf.DepthRim_NewIce : DepthRimDefOf.DepthRim_SeaIceSheet;
                if (want != now)
                {
                    map.terrainGrid.SetTerrain(c, want);
                }
                if (want == DepthRimDefOf.DepthRim_SeaIceSheet && h >= baseThickness)
                {
                    refreezing.Remove(c);
                }
                else
                {
                    refreezing[c] = fdd;
                }
            }
            foreach (IntVec3 c in cracks.Keys.ToList())
            {
                float fdd = cracks[c] + degreeDays;
                if (fdd >= IceBreachMath.CrackHealDegreeDays)
                {
                    cracks.Remove(c);
                    if (c.GetTerrain(map) == DepthRimDefOf.DepthRim_IceCracked)
                    {
                        map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_SeaIceSheet);
                    }
                }
                else
                {
                    cracks[c] = fdd;
                }
            }
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref baseThickness, "baseThickness", 1f);
            Scribe_Collections.Look(ref refreezing, "refreezing", LookMode.Value, LookMode.Value, ref tmpKeys, ref tmpValues);
            Scribe_Collections.Look(ref cracks, "cracks", LookMode.Value, LookMode.Value, ref tmpKeys2, ref tmpValues2);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                refreezing ??= new Dictionary<IntVec3, float>();
                cracks ??= new Dictionary<IntVec3, float>();
            }
        }
    }

    /// <summary>
    /// 破冰點地圖上變成太空的格（DMSE 船體離開後留下的「虛空」：地表圖層沒有 voidTerrain，DMSE 會鋪太空）改成破口水，並開始重新結冰。
    /// 海面地圖由 <see cref="Patch_SeaSurfaceVoid"/> 處理。
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    public static class Patch_IceBreachVoid
    {
        [HarmonyPostfix]
        public static void Postfix(IntVec3 c, TerrainDef newTerr, Map ___map)
        {
            if (newTerr != TerrainDefOf.Space || !(___map?.Parent is WorldObject_IceBreachSite))
            {
                return;
            }
            ___map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_BreachWater);
            MapComponent_IceSheet.For(___map)?.TrackOpenWater(c);
        }
    }
}
