using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{

    [HarmonyPatch(typeof(WeatherEvent_LightningStrike), nameof(WeatherEvent_LightningStrike.DoStrike))]
    public static class Patch_LightningAtSea
    {
        public static bool Prefix(ref IntVec3 strikeLoc, Map map, ref Mesh boltMesh)
        {
            if (strikeLoc.IsValid || map == null)
            {
                return true;
            }
            IntVec3 loc = CellFinderLoose.RandomCellWith(sq => sq.Standable(map) && !map.roofGrid.Roofed(sq), map);
            if (!loc.IsValid)
            {
                loc = CellFinderLoose.RandomCellWith(sq => !map.roofGrid.Roofed(sq), map);
            }
            if (!loc.IsValid)
            {
                return false;
            }
            strikeLoc = loc;
            return true;
        }
    }
}
