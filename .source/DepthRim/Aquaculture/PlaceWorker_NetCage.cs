using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 箱網的放置（§3.1）：整個 7×7 都在開放海面或原版深／淺海水上（不在橋、船體平台、海床、陸地），
    /// 平台層不能放；水深夠掛這種網；外圍至少一格可站立（岸邊、船體平台、橋）。放置時顯示場地、水深、水溫與可養品種。
    /// </summary>
    public class PlaceWorker_NetCage : PlaceWorker
    {
        /// <summary>模組設定關掉箱網時隱藏藍圖（曝氣機也掛這個 PlaceWorker）。</summary>
        public override bool IsBuildDesignatorVisible(BuildableDef def)
        {
            return MapComponent_Aquaculture.Enabled;
        }

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            CompProperties_NetCage props = (checkingDef as ThingDef)?.GetCompProperties<CompProperties_NetCage>();
            if (props == null)
            {
                return true;
            }
            return Check(props, GenAdj.OccupiedRect(loc, rot, checkingDef.Size), map);
        }

        public static AcceptanceReport Check(CompProperties_NetCage props, CellRect rect, Map map)
        {
            if (SeaLevels.IsPlatformLevel(map))
            {
                return "DepthRim.NetCage.NotPlatformLevel".Translate();
            }
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map) || !MapComponent_Aquaculture.IsSurfaceWater(map.terrainGrid.TerrainAt(c)))
                {
                    return "DepthRim.NetCage.NeedWater".Translate();
                }
            }
            MapComponent_Aquaculture comp = MapComponent_Aquaculture.For(map);
            if (comp == null)
            {
                return false;
            }
            if (props.openSeaOnly && comp.Venue != CageVenue.OpenSea)
            {
                return "DepthRim.NetCage.OpenSeaOnly".Translate();
            }
            float depth = comp.WaterDepthAt(rect);
            if (depth < props.minWaterDepth)
            {
                return "DepthRim.NetCage.NoWater".Translate(depth.ToString("0"), props.minWaterDepth.ToString("0"));
            }
            if (!Building_NetCage.FindAccessCell(map, rect).IsValid)
            {
                return "DepthRim.NetCage.NoAccess".Translate();
            }
            return true;
        }

        public override void DrawPlaceMouseAttachments(float curX, ref float curY, BuildableDef def, IntVec3 center, Rot4 rot)
        {
            Map map = Find.CurrentMap;
            CompProperties_NetCage props = (def as ThingDef)?.GetCompProperties<CompProperties_NetCage>();
            MapComponent_Aquaculture comp = MapComponent_Aquaculture.For(map);
            if (props == null || comp == null)
            {
                return;
            }
            CellRect rect = GenAdj.OccupiedRect(center, rot, def.Size);
            float temp = comp.WaterTemperatureAt(props.netDepth) + ClimateMath.CurrentOffset(comp.Current, props.netDepth, comp.Venue);
            string venue = comp.Venue == CageVenue.Bay
                ? "DepthRim.NetCage.PreviewBay".Translate(comp.WaterDepthAt(rect).ToString("0"), temp.ToString("0.0"))
                : "DepthRim.NetCage.PreviewSea".Translate(comp.WaterDepthAt(rect).ToString("0"), props.netDepth.ToString("0"), temp.ToString("0.0"));
            DrawTextLine(curX, ref curY, venue);
            List<string> ok = DefDatabase<AquacultureSpeciesDef>.AllDefsListForReading
                .Where(s => s.CannotStockReason(comp.Venue, props.layer, temp) == null).Select(s => s.label).ToList();
            DrawTextLine(curX, ref curY, ok.Count > 0
                ? "DepthRim.NetCage.PreviewSpecies".Translate(string.Join(", ", ok)).ToString()
                : "DepthRim.NetCage.NoSpecies".Translate(temp.ToString("0.0")).ToString());
        }
    }

    /// <summary>曝氣機：登記到箱網地圖元件，半徑 4.9 格內的箱網溶氧 +0.15（§5.2）。</summary>
    public class Building_Aerator : Building
    {
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            MapComponent_Aquaculture.For(map)?.RegisterAerator(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            MapComponent_Aquaculture.For(Map)?.DeregisterAerator(this);
            base.DeSpawn(mode);
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            GenDraw.DrawRadiusRing(Position, MapComponent_Aquaculture.AeratorRadius);
        }
    }
}
