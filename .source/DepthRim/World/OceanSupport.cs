using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 熱液噴口區的熱液泉：數量來自生態區的 <see cref="WaterMapExtension.hydrothermalVents"/>。
    /// 原型先用原版蒸汽間歇泉（可以蓋地熱發電機），之後換成 P2-7 的水下熱液泉。
    /// 原版 <c>GenStep_ScatterGeysers</c> 看的條件（水域生態區不放、要找沒有岩體的大空地）不適合海底，所以自己散佈。
    /// </summary>
    public class GenStep_OceanVents : GenStep
    {
        public ThingDef thingDef;

        public override int SeedPart => 418806213;

        public override void Generate(Map map, GenStepParams parms)
        {
            WaterMapExtension ext = map.Biome?.GetModExtension<WaterMapExtension>();
            ThingDef def = thingDef ?? ThingDefOf.SteamGeyser;
            if (ext == null || ext.hydrothermalVents.max <= 0 || def == null)
            {
                return;
            }
            int want = ext.hydrothermalVents.RandomInRange;
            int placed = 0;
            for (int attempt = 0; attempt < want * 60 && placed < want; attempt++)
            {
                IntVec3 c = CellFinder.RandomNotEdgeCell(12, map);
                CellRect rect = GenAdj.OccupiedRect(c, Rot4.North, def.size);
                if (!CanPlace(rect, map))
                {
                    continue;
                }
                GenSpawn.Spawn(ThingMaker.MakeThing(def), c, map, WipeMode.Vanish);
                placed++;
            }
            Log.Message($"[DepthRim] placed {placed}/{want} hydrothermal vent(s) on map {map.uniqueID}");
        }

        private static bool CanPlace(CellRect rect, Map map)
        {
            foreach (IntVec3 c in rect.ExpandedBy(1))
            {
                if (!c.InBounds(map) || !c.Standable(map) || c.GetEdifice(map) != null)
                {
                    return false;
                }
                TerrainDef t = c.GetTerrain(map);
                if (t == null || t.dontRender || t.passability == Traversability.Impassable)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// 升降井只能蓋在「下方海洋是淺海或變溫層」的地表格上：目前的潛水服只撐得住水壓 1，
    /// 通到深海或海床的地圖殖民者一下去就會受水壓傷害。深的生態區留給之後的潛艇。
    /// </summary>
    public class PlaceWorker_IceShaft : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            BiomeDef below = OceanBelow(map);
            if (below == null)
            {
                return true;
            }
            WaterMapExtension ext = below.GetModExtension<WaterMapExtension>();
            if (ext != null && !ext.ShaftAccessible)
            {
                return "DepthRim.IceShaft.TooDeep".Translate(below.LabelCap, ext.depthTier.Label());
            }
            return true;
        }

        public override void DrawMouseAttachments(BuildableDef def)
        {
            BiomeDef below = OceanBelow(Find.CurrentMap);
            if (below != null)
            {
                Widgets.MouseAttachedLabel("DepthRim.IceShaft.Below".Translate(below.LabelCap));
            }
        }

        /// <summary>這張地表地圖正下方的海洋生態區（沒有海洋層或不在地表時回傳 null）。</summary>
        public static BiomeDef OceanBelow(Map map)
        {
            if (map == null || !map.Tile.Valid)
            {
                return null;
            }
            PlanetLayer ocean = Find.WorldGrid.FirstLayerOfDef(DepthRimDefOf.DepthRim_Ocean);
            if (ocean == null || map.Tile.Layer == ocean)
            {
                return null;
            }
            PlanetTile tile = ocean.GetClosestTile_NewTemp(map.Tile);
            return tile.Valid ? tile.Tile.PrimaryBiome : null;
        }
    }

    /// <summary>
    /// 掛在牆或門上：受損時滲水量的倍率（一般牆與門是 1）。水密門與抗壓水密牆用這個降低滲水，
    /// <see cref="MapComponent_Flooding"/> 在計算滲水時讀取。
    /// </summary>
    public class LeakResistanceExtension : DefModExtension
    {
        public float leakFactor = 1f;

        /// <summary>這種牆能承受的水深層級（說明用；之後潛艇的船殼等級會讀它）。</summary>
        public DepthTier pressureRating = DepthTier.Shallow;

        public static float FactorFor(ThingDef def)
        {
            return def?.GetModExtension<LeakResistanceExtension>()?.leakFactor ?? 1f;
        }
    }
}
