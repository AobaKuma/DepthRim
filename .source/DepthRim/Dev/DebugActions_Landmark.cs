using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    public static class DebugActions_Landmark
    {
        /// <summary>在目前地圖附近（海路 2～4 格）放一個指定種類的聲納地標。</summary>
        [DebugAction("DepthRim", "Spawn sonar landmark nearby", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void SpawnLandmark()
        {
            Map map = Find.CurrentMap;
            PlanetTile center = WorldComponent_OceanLandmarks.OceanCenter(map?.Parent);
            if (!center.Valid)
            {
                Messages.Message("Not on or above the ocean.", MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            List<DebugMenuOption> options = new List<DebugMenuOption>();
            foreach (OceanLandmarkDef def in DefDatabase<OceanLandmarkDef>.AllDefsListForReading)
            {
                OceanLandmarkDef captured = def;
                options.Add(new DebugMenuOption(def.defName, DebugMenuOptionMode.Action, () =>
                {
                    if (!WorldComponent_OceanLandmarks.TryPlace(center, 4f, captured, out WorldObject o))
                    {
                        Messages.Message("No free ocean tile of a matching depth nearby.", MessageTypeDefOf.RejectInput, historical: false);
                    }
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }
    }
}
