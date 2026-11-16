using System.Collections.Generic;
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
                if (!CanPlace(rect.ExpandedBy(1), map))
                {
                    continue;
                }
                GenSpawn.Spawn(ThingMaker.MakeThing(def), c, map, WipeMode.Vanish);
                placed++;
            }
            Log.Message($"[DepthRim] placed {placed}/{want} hydrothermal vent(s) on map {map.uniqueID}");
        }

        /// <summary>整個範圍都在地圖內、可站立、沒有建築，而且不是深淵或不能通行的地形。</summary>
        internal static bool CanPlace(CellRect rect, Map map)
        {
            foreach (IntVec3 c in rect)
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
    /// 海床上的天然物件（海底火山生態的 DR_UnderwaterVolcano、DR_Rock 等）：清單與數量來自生態區的
    /// <see cref="WaterMapExtension.seafloorFeatures"/>，依序放置（大型物件寫在前面，才不會被小岩塊佔掉空地）。
    /// 排在岩體與深淵之後，只放在空曠、可站立的海床上。
    /// </summary>
    public class GenStep_OceanFeatures : GenStep
    {
        public override int SeedPart => 730214597;

        public override void Generate(Map map, GenStepParams parms)
        {
            List<SeafloorFeature> features = map.Biome?.GetModExtension<WaterMapExtension>()?.seafloorFeatures;
            if (features.NullOrEmpty())
            {
                return;
            }
            foreach (SeafloorFeature f in features)
            {
                if (f?.thing == null || f.count.max <= 0)
                {
                    continue;
                }
                int want = f.count.RandomInRange;
                int placed = 0;
                for (int attempt = 0; attempt < want * 40 && placed < want; attempt++)
                {
                    IntVec3 c = CellFinder.RandomNotEdgeCell(f.edgeMargin, map);
                    CellRect rect = GenAdj.OccupiedRect(c, Rot4.North, f.thing.size);
                    if (!GenStep_OceanVents.CanPlace(rect.ExpandedBy(f.clearance), map))
                    {
                        continue;
                    }
                    GenSpawn.Spawn(ThingMaker.MakeThing(f.thing), c, map, WipeMode.Vanish);
                    placed++;
                }
                if (placed < want)
                {
                    Log.Message($"[DepthRim] placed {placed}/{want} {f.thing.defName} on map {map.uniqueID}");
                }
            }
        }
    }

    /// <summary>
    /// 升降井只能蓋在「下方海洋是淺海或變溫層」的地表格上（設計上的鑽探深度上限；潛水服雖然撐得到深海，
    /// 深海與海床的生態區仍留給潛艇）。
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
            // 陸地正下方沒有海洋（陸塊）：升降井只能開在冰原、海冰等冰層之上
            if (OceanWorldView.IsLandmass(below))
            {
                return "DepthRim.IceShaft.NoOcean".Translate();
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
            PlanetTile tile = OceanWorldView.TileBelow(map.Tile, ocean);
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
