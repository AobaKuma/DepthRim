using System.Reflection;
using HarmonyLib;
using Verse;

namespace DepthRim
{
    [StaticConstructorOnStartup]
    public static class DepthRimMod
    {
        public const string HarmonyId = "DepthRim.Core";

        static DepthRimMod()
        {
            new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
        }
    }
}
