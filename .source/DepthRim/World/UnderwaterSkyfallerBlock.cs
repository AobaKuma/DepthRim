using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水下地圖（海洋層上的地圖）沒有天空：原版所有會從天上掉東西（skyfaller、空投艙）的事件都不能在這裡發生。
    /// <para>海洋層雖然設了 <c>onlyAllowWhitelistedIncidents</c>，但原版的隕石、空投物資、飛船殘骸與敵襲都帶
    /// <c>canOccurOnAllPlanetLayers</c>，白名單擋不住；強制觸發（任務、開發者選單、其他模組）也會跳過白名單。
    /// 所以這裡在 <see cref="IncidentWorker.CanFireNow"/> 最後一關直接否決，連 forced 也擋。</para>
    /// <para>只擋原版的事件與到達方式。DepthRim 自己的跳幫突擊艇雖然也是 skyfaller，但它是「切穿船殼」的演出，不受影響。</para>
    /// </summary>
    public static class UnderwaterSkyfallerBlock
    {
        /// <summary>會生成 skyfaller／空投艙的原版事件工作類別（含子類別）。</summary>
        private static readonly Type[] SkyfallerWorkers =
        {
            typeof(IncidentWorker_ShipChunkDrop),
            typeof(IncidentWorker_MeteoriteImpact),
            typeof(IncidentWorker_ResourcePodCrash),
            typeof(IncidentWorker_CrashedShipPart),
            typeof(IncidentWorker_MechCluster),
            typeof(IncidentWorker_OrbitalDebris),
            typeof(IncidentWorker_Obelisk),
        };

        /// <summary>
        /// 經由任務（<c>IncidentWorker_GiveQuest</c>）用空投艙送東西或人下來的原版事件，
        /// 以及用遊戲狀態持續落下熔岩碎塊的火山碎屑雨。
        /// </summary>
        private static readonly HashSet<string> SkyfallerIncidentNames = new HashSet<string>
        {
            "RefugeePodCrash",
            "RefugeePodCrash_Baby",
            "RefugeePodCrash_Ghoul",
            "MysteriousCargoCube",
            "MysteriousCargoRevenantSpine",
            "MysteriousCargoUnnaturalCorpse",
            "VolcanicDebris",
        };

        /// <summary>原版用空投艙（或機械集群空投）送人進場的到達方式。</summary>
        private static readonly Type[] DropArrivalWorkers =
        {
            typeof(PawnsArrivalModeWorker_EdgeDrop),
            typeof(PawnsArrivalModeWorker_EdgeDropGroups),
            typeof(PawnsArrivalModeWorker_CenterDrop),
            typeof(PawnsArrivalModeWorker_RandomDrop),
            typeof(PawnsArrivalModeWorker_ClusterDrop),
            typeof(PawnsArrivalModeWorker_SpecificLocationDrop),
        };

        /// <summary>海洋層上的地圖就是水下地圖（海面地圖在地表層，不算）。</summary>
        public static bool IsUnderwater(IIncidentTarget target)
        {
            return target is Map map && OceanRouting.IsOcean(map.Tile);
        }

        public static bool IsSkyfallerIncident(IncidentDef def)
        {
            if (def == null)
            {
                return false;
            }
            if (SkyfallerIncidentNames.Contains(def.defName))
            {
                return true;
            }
            if (def.gameCondition?.conditionClass != null && typeof(GameCondition_VolcanicDebrisShower).IsAssignableFrom(def.gameCondition.conditionClass))
            {
                return true;
            }
            return def.workerClass != null && IsAny(def.workerClass, SkyfallerWorkers);
        }

        public static bool IsDropArrival(PawnsArrivalModeDef def)
        {
            return def?.workerClass != null && IsAny(def.workerClass, DropArrivalWorkers);
        }

        private static bool IsAny(Type type, Type[] bases)
        {
            foreach (Type b in bases)
            {
                if (b.IsAssignableFrom(type))
                {
                    return true;
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.CanFireNow))]
    public static class Patch_IncidentWorker_CanFireNow_Underwater
    {
        public static void Postfix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
        {
            if (__result && UnderwaterSkyfallerBlock.IsUnderwater(parms?.target) && UnderwaterSkyfallerBlock.IsSkyfallerIncident(__instance.def))
            {
                __result = false;
            }
        }
    }

    /// <summary>直接呼叫 <c>TryExecute</c> 而沒先問 <c>CanFireNow</c> 的路徑（部分任務、其他模組）也擋下。</summary>
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    public static class Patch_IncidentWorker_TryExecute_Underwater
    {
        public static bool Prefix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
        {
            if (UnderwaterSkyfallerBlock.IsUnderwater(parms?.target) && UnderwaterSkyfallerBlock.IsSkyfallerIncident(__instance.def))
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>敵襲、友軍支援在水下不能用空投艙進場。</summary>
    [HarmonyPatch(typeof(PawnsArrivalModeWorker), nameof(PawnsArrivalModeWorker.CanUseWith))]
    public static class Patch_PawnsArrivalModeWorker_CanUseWith_Underwater
    {
        public static void Postfix(PawnsArrivalModeWorker __instance, IncidentParms parms, ref bool __result)
        {
            if (__result && UnderwaterSkyfallerBlock.IsUnderwater(parms?.target) && UnderwaterSkyfallerBlock.IsDropArrival(__instance.def))
            {
                __result = false;
            }
        }
    }

    /// <summary>
    /// 事先指定了空投到達方式的襲擊（任務、開發者選單）先清掉指定，讓原版重新挑；
    /// 原版在「快速軍事支援」找不到可用方式時會硬塞 EdgeDrop／CenterDrop，這時直接讓襲擊失敗。
    /// </summary>
    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryGenerateRaidInfo))]
    public static class Patch_IncidentWorker_Raid_TryGenerateRaidInfo_Underwater
    {
        public static void Prefix(IncidentParms parms)
        {
            if (UnderwaterSkyfallerBlock.IsUnderwater(parms?.target) && UnderwaterSkyfallerBlock.IsDropArrival(parms.raidArrivalMode))
            {
                parms.raidArrivalMode = null;
            }
        }
    }

    [HarmonyPatch(typeof(IncidentWorker_Raid), nameof(IncidentWorker_Raid.TryResolveRaidArriveMode))]
    public static class Patch_IncidentWorker_Raid_TryResolveRaidArriveMode_Underwater
    {
        public static void Postfix(IncidentParms parms, ref bool __result)
        {
            if (UnderwaterSkyfallerBlock.IsUnderwater(parms?.target) && UnderwaterSkyfallerBlock.IsDropArrival(parms.raidArrivalMode))
            {
                parms.raidArrivalMode = null;
                __result = false;
            }
        }
    }

    /// <summary>圍攻會用空投艙補給物資（機械族圍攻用機械集群空投），水下不能選。</summary>
    [HarmonyPatch(typeof(RaidStrategyWorker_Siege), nameof(RaidStrategyWorker.CanUseWith))]
    public static class Patch_RaidStrategyWorker_Siege_Underwater
    {
        public static void Postfix(IncidentParms parms, ref bool __result)
        {
            if (__result && UnderwaterSkyfallerBlock.IsUnderwater(parms?.target))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(RaidStrategyWorker_SiegeMechanoid), nameof(RaidStrategyWorker.CanUseWith))]
    public static class Patch_RaidStrategyWorker_SiegeMechanoid_Underwater
    {
        public static void Postfix(IncidentParms parms, ref bool __result)
        {
            Patch_RaidStrategyWorker_Siege_Underwater.Postfix(parms, ref __result);
        }
    }
}
