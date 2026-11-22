using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    public static class OceanTemperature
    {
        /// <summary>海水冰點（含鹽 3.5%）。</summary>
        public const float FreezingPoint = -1.8f;

        /// <summary>地下冰洋的深層水溫（極地深層水）。</summary>
        public const float SubglacialDeepWater = -0.5f;

        /// <summary>海面水溫上限（暖池最熱也大約如此）。</summary>
        public const float MaxSurfaceWater = 29.5f;

        /// <summary>混合層厚度（m）：這麼淺的水溫和海面一樣。</summary>
        public const float MixedLayer = 50f;

        /// <summary>斜溫層的衰減深度（m）：混合層以下，水溫每這麼深一段往深層水靠近到 1/e。</summary>
        public const float ThermoclineScale = 350f;

        /// <summary>海面的季節振幅相對於氣溫的比例。</summary>
        public const float SurfaceSeasonalFactor = 0.4f;

        /// <summary>季節振幅隨深度衰減的尺度（m）。</summary>
        public const float SeasonalDepthScale = 80f;

        /// <summary>海面的日夜／每日隨機振幅相對於氣溫的比例。</summary>
        public const float SurfaceDailyFactor = 0.15f;

        /// <summary>日夜振幅隨深度衰減的尺度（m）。</summary>
        public const float DailyDepthScale = 15f;

        /// <summary>熱液噴口區比周圍深層水暖幾度（瀰散的熱液）。</summary>
        public const float VentWarming = 6f;

        /// <summary>熱液噴口區的每日起伏，相對於原版每日隨機變化（±7 °C）的比例：約 ±3.5 °C。</summary>
        public const float VentPulseFactor = 0.5f;

        public enum Kind
        {
            OpenWater,
            Subglacial,
            Hydrothermal
        }

        /// <summary>海面年均水溫：跟著年均氣溫（海洋調節，冷處偏暖），夾在冰點與 29.5 °C 之間。</summary>
        public static float SurfaceWater(float airAnnual)
        {
            return Mathf.Clamp(0.8f * airAnnual + 4f, FreezingPoint, MaxSurfaceWater);
        }

        /// <summary>深層水溫：高緯度沉降的冷水，熱帶海域下方略暖（約 1–3 °C）。</summary>
        public static float DeepWater(float surfaceWater)
        {
            return Mathf.Clamp(1f + 0.06f * surfaceWater, 0.5f, 3f);
        }

        /// <summary>某深度的年均水溫（m，正值）。</summary>
        public static float AnnualMean(float depth, float airAnnual, Kind kind)
        {
            if (kind == Kind.Subglacial)
            {
                // 冰下：冰面下的水在冰點，往深處略暖（極地深層水約 −0.5 °C）
                float under = Mathf.Max(0f, depth - MixedLayer);
                return SubglacialDeepWater + (FreezingPoint - SubglacialDeepWater) * Mathf.Exp(-under / ThermoclineScale);
            }
            float surface = SurfaceWater(airAnnual);
            float deep = DeepWater(surface);
            float below = Mathf.Max(0f, depth - MixedLayer);
            float t = deep + (surface - deep) * Mathf.Exp(-below / ThermoclineScale);
            if (kind == Kind.Hydrothermal)
            {
                t += VentWarming;
            }
            return t;
        }

        /// <summary>季節振幅倍率（乘在原版依緯度算的季節偏移上）。</summary>
        public static float SeasonalFactor(float depth, Kind kind)
        {
            if (kind == Kind.Subglacial)
            {
                return 0f;
            }
            return SurfaceSeasonalFactor * Mathf.Exp(-Mathf.Max(0f, depth) / SeasonalDepthScale);
        }

        /// <summary>日夜與每日隨機變化的倍率。熱液噴口區的每日隨機變化改成噴發起伏（<see cref="DailyRandomFactor"/>）。</summary>
        public static float DailyFactor(float depth, Kind kind)
        {
            if (kind == Kind.Subglacial)
            {
                return 0f;
            }
            return SurfaceDailyFactor * Mathf.Exp(-Mathf.Max(0f, depth) / DailyDepthScale);
        }

        /// <summary>每日隨機變化的倍率：一般同 <see cref="DailyFactor"/>；熱液噴口區是噴發強弱的起伏。</summary>
        public static float DailyRandomFactor(float depth, Kind kind)
        {
            return kind == Kind.Hydrothermal ? VentPulseFactor : DailyFactor(depth, kind);
        }

        /// <summary>季節偏移不能讓水溫低於冰點（海水在冰點結冰，不會再往下）。</summary>
        public static float ClampSeasonalOffset(float annualMean, float offset)
        {
            return Mathf.Max(offset, FreezingPoint - annualMean);
        }

        private static readonly Dictionary<BiomeDef, Kind?> kinds = new Dictionary<BiomeDef, Kind?>();

        /// <summary>生態區的水溫類型；不是海洋生態區（例如陸塊）時回傳 null，照原版的陸地溫度。</summary>
        public static Kind? KindOf(BiomeDef biome)
        {
            if (biome == null)
            {
                return null;
            }
            if (!kinds.TryGetValue(biome, out Kind? kind))
            {
                if (!biome.HasModExtension<WaterMapExtension>())
                {
                    kind = null;
                }
                else if (OceanBiomeSelector.IsSubglacial(biome.defName))
                {
                    kind = Kind.Subglacial;
                }
                else if (biome.defName == OceanBiomeSelector.DefName(OceanBiome.HydrothermalField))
                {
                    kind = Kind.Hydrothermal;
                }
                else
                {
                    kind = Kind.OpenWater;
                }
                kinds[biome] = kind;
            }
            return kind;
        }

        /// <summary>海洋層上的海洋格：回傳水溫類型與深度（m）。</summary>
        public static bool TryGetWater(PlanetTile tile, out Kind kind, out float depth, out Tile data)
        {
            kind = Kind.OpenWater;
            depth = 0f;
            data = null;
            if (!tile.Valid || tile.LayerDef != DepthRimDefOf.DepthRim_Ocean)
            {
                return false;
            }
            data = tile.Tile;
            Kind? k = KindOf(data?.PrimaryBiome);
            if (k == null)
            {
                return false;
            }
            kind = k.Value;
            depth = Mathf.Max(0f, -data.elevation);
            return true;
        }
    }

    /// <summary>
    /// 舊存檔相容：2026-11-11 之前海洋格的年均溫是生態區的固定值（例如淺珊瑚礁一律 24 °C）。
    /// 讀檔時重算一次（依深度與正上方的年均氣溫），之後存檔記住已經算過。新世界生成時就是新算法，重算結果相同。
    /// </summary>
    public class WorldComponent_OceanTemperature : WorldComponent
    {
        private int version;

        public const int CurrentVersion = 1;

        public WorldComponent_OceanTemperature(World world) : base(world)
        {
        }

        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);
            PlanetLayer pending = Patch_PlanetLayer_ExposeOceanTiles.PendingRestore;
            Patch_PlanetLayer_ExposeOceanTiles.PendingRestore = null;
            if (fromLoad && pending != null && pending == Find.WorldGrid?.FirstLayerOfDef(DepthRimDefOf.DepthRim_Ocean))
            {
                // 舊存檔沒存海洋格的深度、溫度、地勢（讀檔後深度都是 0 m）：依世界種子重算
                int restored = WorldGenStep_OceanTiles.RestoreTileData(world.info.seedString, pending);
                Find.World.tileTemperatures?.ClearCaches();
                Log.Message($"[DepthRim] Restored seafloor depth and water temperature for {restored} ocean tiles (save without ocean tile data).");
            }
            if (version >= CurrentVersion)
            {
                return;
            }
            version = CurrentVersion;
            int updated = Recalculate();
            if (fromLoad && updated > 0)
            {
                Log.Message($"[DepthRim] Recalculated water temperature for {updated} ocean tiles (old save).");
            }
        }

        /// <summary>重算海洋層所有海洋格的年均水溫，回傳更新了幾格。</summary>
        public static int Recalculate()
        {
            PlanetLayer ocean = Find.WorldGrid?.FirstLayerOfDef(DepthRimDefOf.DepthRim_Ocean);
            PlanetLayer surface = Find.WorldGrid?.Surface;
            if (ocean == null || surface == null)
            {
                return 0;
            }
            bool sameGrid = surface.TilesCount == ocean.TilesCount;
            SurfaceTileLookup lookup = sameGrid ? null : SurfaceTileLookup.Build(surface);
            int updated = 0;
            for (int i = 0; i < ocean.TilesCount; i++)
            {
                Tile tile = ocean[i];
                OceanTemperature.Kind? kind = OceanTemperature.KindOf(tile?.PrimaryBiome);
                if (kind == null)
                {
                    continue;
                }
                int above = sameGrid ? i : lookup?.ClosestTo(ocean.GetTileCenter(new PlanetTile(i, ocean)).normalized) ?? -1;
                float air = above >= 0 ? surface[above].temperature : 10f;
                tile.temperature = OceanTemperature.AnnualMean(Mathf.Max(0f, -tile.elevation), air, kind.Value);
                updated++;
            }
            Find.World.tileTemperatures?.ClearCaches();
            return updated;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref version, "version", 0);
        }
    }

    /// <summary>海洋格的季節偏移照深度衰減（深海沒有季節），也不會讓水溫低於冰點。</summary>
    [HarmonyPatch(typeof(GenTemperature), nameof(GenTemperature.OffsetFromSeasonCycle))]
    public static class Patch_GenTemperature_OffsetFromSeasonCycle
    {
        [HarmonyPostfix]
        public static void Postfix(PlanetTile tile, ref float __result)
        {
            if (OceanTemperature.TryGetWater(tile, out OceanTemperature.Kind kind, out float depth, out Tile data))
            {
                __result = OceanTemperature.ClampSeasonalOffset(data.temperature, __result * OceanTemperature.SeasonalFactor(depth, kind));
            }
        }
    }

    /// <summary>海洋格的日夜變化：只有很淺的水有一點。</summary>
    [HarmonyPatch(typeof(GenTemperature), nameof(GenTemperature.OffsetFromSunCycle))]
    public static class Patch_GenTemperature_OffsetFromSunCycle
    {
        [HarmonyPostfix]
        public static void Postfix(PlanetTile tile, ref float __result)
        {
            if (OceanTemperature.TryGetWater(tile, out OceanTemperature.Kind kind, out float depth, out _))
            {
                __result *= OceanTemperature.DailyFactor(depth, kind);
            }
        }
    }

    /// <summary>海洋格的每日隨機變化：淺水一點點、深水沒有；熱液噴口區是噴發強弱的起伏。</summary>
    [HarmonyPatch]
    public static class Patch_TileTemperatures_DailyRandomVariation
    {
        private static readonly AccessTools.FieldRef<object, PlanetTile> TileField =
            AccessTools.FieldRefAccess<PlanetTile>(AccessTools.Inner(typeof(TileTemperaturesComp), "CachedTileTemperatureData"), "tile");

        [HarmonyTargetMethod]
        public static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.Inner(typeof(TileTemperaturesComp), "CachedTileTemperatureData"), "OffsetFromDailyRandomVariation");
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, ref float __result)
        {
            if (OceanTemperature.TryGetWater(TileField(__instance), out OceanTemperature.Kind kind, out float depth, out _))
            {
                __result *= OceanTemperature.DailyRandomFactor(depth, kind);
            }
        }
    }
}
