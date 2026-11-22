using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// <list type="bullet">
    /// <item>生成：野生生成只來自海洋生態區（水下地圖），原版不看生態區的生成（獸群遷徙、獸群暴走、蹣跚者、任務、商人、遊蕩農場動物）在種族上關掉。</item>
    /// <item>存活看水，不看地圖：所在格水位低於 <see cref="MinWaterLevel"/> 就離水，倒地並很快窒息死亡，死後照常變成漁獲；回到水裡馬上恢復。
    /// 有水位的地圖在水位結算時判斷（<see cref="ApplyWater"/>），包括沒排乾就上浮、把艙室積水帶到地面層的地圖（<see cref="MapComponent_Flooding.HoldsWater"/>）；
    /// 完全沒有水的地圖（地表、乾的海面）每次結算都算離水（<see cref="ApplyNoWater"/>），所以跑到那裡的魚會死成漁獲，不會憑空消失。</item>
    /// </list>
    /// </summary>
    public static class FishHabitat
    {
        /// <summary>魚需要的最低水位：比這淺就算離水（同躺下的人被淹沒的水位）。</summary>
        public const float MinWaterLevel = FloodingPawnEffects.SubmergedLevelLying;

        /// <summary>離水時每次水位結算（250 ticks）加的嚴重度：一離水就倒地，四次結算（約 1000 ticks）後死亡。</summary>
        public const float SuffocationPerUpdate = 0.25f;

        public static bool OutOfWater(float level)
        {
            return level < MinWaterLevel;
        }

        /// <summary>離水幾次水位結算後死亡。</summary>
        public static int UpdatesToDeath => UnityEngine.Mathf.CeilToInt(1f / SuffocationPerUpdate);

        /// <summary>水下地圖的水位結算時呼叫（<see cref="FloodingPawnEffects"/>）：離水就累加窒息，回到水裡移除。</summary>
        public static void ApplyWater(Pawn fish, float level)
        {
            if (OutOfWater(level))
            {
                HealthUtility.AdjustSeverity(fish, DepthRimDefOf.DepthRim_FishOutOfWater, SuffocationPerUpdate);
                return;
            }
            Hediff hediff = fish.health.hediffSet.GetFirstHediffOfDef(DepthRimDefOf.DepthRim_FishOutOfWater);
            if (hediff != null)
            {
                fish.health.RemoveHediff(hediff);
            }
        }

        private static readonly List<Pawn> tmpFish = new List<Pawn>();

        /// <summary>
        /// 沒有水位的地圖上，海面的水（開放海面、原版深／淺海水）也算水（箱網養殖 §6.6、§10 #13）：
        /// 逃逸的養殖魚、外海的睡鯊在海面上不會窒息。有水位的水下地圖照舊看水位。
        /// </summary>
        public static bool IsSurfaceWater(TerrainDef t)
        {
            return t != null && (t == DepthRimDefOf.DepthRim_OpenSea || t == TerrainDefOf.WaterOceanShallow || t == TerrainDefOf.WaterOceanDeep);
        }

        public static bool OnSurfaceWater(Pawn fish)
        {
            Map map = fish.MapHeld;
            return map != null && fish.Spawned && IsSurfaceWater(map.terrainGrid.TerrainAt(fish.Position));
        }

        /// <summary>沒有水位的地圖：地圖上每條魚都離水（每次水位結算的間隔呼叫一次）。</summary>
        public static void ApplyNoWater(Map map)
        {
            tmpFish.Clear();
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (!pawns[i].Dead && CompFishCatch.IsAquatic(pawns[i]))
                {
                    tmpFish.Add(pawns[i]);
                }
            }
            for (int i = 0; i < tmpFish.Count; i++)
            {
                if (tmpFish[i].Spawned && !tmpFish[i].Dead)
                {
                    ApplyWater(tmpFish[i], OnSurfaceWater(tmpFish[i]) ? 1f : 0f);
                }
            }
            tmpFish.Clear();
        }
    }
}
