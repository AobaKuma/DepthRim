using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 洋流發電機只能蓋在水下地圖、而且整台（3×3）都浸在水裡：抽乾的艙室、地面殖民地都不行。
    /// 放置時框出前後的水流通道（<see cref="OceanCurrentMath.FlowCells"/>），和原版風力發電機的風道預覽同一個概念。
    /// </summary>
    public class PlaceWorker_OceanCurrentTurbine : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            CompProperties_OceanCurrentTurbine props = (checkingDef as ThingDef)?.GetCompProperties<CompProperties_OceanCurrentTurbine>();
            MapComponent_Flooding flooding = map?.Flooding();
            if (!FloodingUtility.IsWaterMap(map) || flooding == null || !flooding.Active)
            {
                return "DepthRim.CurrentTurbine.MustBeUnderwater".Translate();
            }
            float minWaterLevel = props?.minWaterLevel ?? 0.5f;
            CellRect rect = GenAdj.OccupiedRect(loc, rot, checkingDef.Size);
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map) || flooding.LevelAt(c) < minWaterLevel)
                {
                    return "DepthRim.CurrentTurbine.MustBeSubmerged".Translate();
                }
            }
            return true;
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map map = Find.CurrentMap;
            CompProperties_OceanCurrentTurbine props = def?.GetCompProperties<CompProperties_OceanCurrentTurbine>();
            if (map == null || props == null || !FloodingUtility.IsWaterMap(map))
            {
                return;
            }
            List<IntVec3> cells = OceanCurrentMath.FlowCells(center, rot, def.size, props.flowReach).Where(c => c.InBounds(map)).ToList();
            GenDraw.DrawFieldEdges(cells);
            int blocked = CompOceanCurrentTurbine.CountBlocked(map, cells, props.minWaterLevel);
            if (blocked > 0)
            {
                Widgets.MouseAttachedLabel("DepthRim.CurrentTurbine.GhostBlocked".Translate(blocked, Mathf.RoundToInt((1f - OceanCurrentMath.FlowFactor(blocked)) * 100f)));
            }
        }
    }

    /// <summary>
    /// 原版風力發電機不能蓋在水下地圖（透過程式補丁加到 <c>WindTurbine</c> 的 placeWorkers）。
    /// 水下沒有風，與其讓玩家白花材料蓋一台永遠不發電的機器，不如在建之前就擋下來。
    /// </summary>
    public class PlaceWorker_NoWindUnderwater : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            if (FloodingUtility.IsWaterMap(map))
            {
                return "DepthRim.WindTurbine.NoWindUnderwater".Translate();
            }
            return true;
        }
    }
}
