using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 淹水系統的事件音效（二期 P2-11，§2.8）：進水聲、破艙聲、船殼承壓聲、排水泵聲、潛水呼吸。
    /// 每張地圖一份，由 <see cref="MapComponent_Flooding"/> 持有；不存檔（持續音效讀檔後由下一次更新重建）。
    /// 悶音不在這裡處理：單次音效由 <see cref="Patch_SampleOneShot_TryMakeAndPlay"/> 套用，
    /// 持續音效由 <see cref="UnderwaterAudio.UpdateSustainers"/> 每 30 幀套用。
    /// </summary>
    public class FloodingAudio
    {
        /// <summary>同時最多幾個進水聲（取離鏡頭最近的）。</summary>
        public const int MaxFloodLoops = 4;

        /// <summary>每次水位更新最多播幾個破艙聲。</summary>
        public const int MaxBreachSounds = 2;

        /// <summary>低於這個氣量比例時，呼吸聲換成急促的低氣量聲。</summary>
        public const float LowAirFraction = 0.2f;

        /// <summary>潛水呼吸只在離鏡頭這個距離內播放。</summary>
        public const float BreathAudibleRange = 30f;

        private readonly Map map;

        private readonly MapComponent_Flooding flooding;

        private readonly Dictionary<IntVec3, Sustainer> floodLoops = new Dictionary<IntVec3, Sustainer>();

        /// <summary>每個進水聲目前的強度階段；階段改變（牆被打得更破）時重新開始那一個進水聲。</summary>
        private readonly Dictionary<IntVec3, int> floodLoopStages = new Dictionary<IntVec3, int>();

        private readonly Dictionary<CompFloodPump, Sustainer> pumpLoops = new Dictionary<CompFloodPump, Sustainer>();

        private static readonly List<IntVec3> tmpCells = new List<IntVec3>();

        private static readonly List<CompFloodPump> tmpPumps = new List<CompFloodPump>();

        /// <summary>最近一次更新播出的破艙聲數量（給驗收測試）。</summary>
        public int LastBreachSounds { get; private set; }

        /// <summary>這張地圖累計播過幾次破艙聲（給驗收測試；不受「兩次更新落在同一段等待裡」影響）。</summary>
        public int TotalBreachSounds { get; private set; }

        /// <summary>最近一次更新是否播了船殼承壓聲（給驗收測試）。</summary>
        public bool LastCreakPlayed { get; private set; }

        public FloodingAudio(Map map, MapComponent_Flooding flooding)
        {
            this.map = map;
            this.flooding = flooding;
        }

        public int FloodLoopCount => floodLoops.Count;

        public int PumpLoopCount => pumpLoops.Count;

        public IEnumerable<IntVec3> FloodLoopCells => floodLoops.Keys;

        /// <summary>某個進水聲的階段（沒有時 -1，給驗收測試）。</summary>
        public int FloodLoopStageAt(IntVec3 c) => floodLoopStages.TryGetValue(c, out int s) ? s : -1;

        /// <summary>
        /// 進水聲的音量與音高倍率：滲水小聲、偏高（細細的水流），湧入大聲、偏低（整片水牆）。
        /// 使用者要求「漏水效果的速度與強度要跟牆體耐久階段相關」，聲音跟著同一個階段。
        /// </summary>
        public static void FloodLoopParams(int stage, out float volume, out float pitch)
        {
            stage = Mathf.Clamp(stage, 0, 3);
            volume = 0.35f + 0.22f * stage;
            pitch = 1.25f - 0.15f * stage;
        }

        public Sustainer PumpLoopFor(CompFloodPump pump) => pumpLoops.TryGetValue(pump, out Sustainer s) ? s : null;

        /// <summary>每次水位更新後呼叫：進水聲換到新的進水格、播破艙聲、擲船殼承壓聲。</summary>
        public void AfterLevelUpdate(List<IntVec3> inflowCells, List<int> inflowStages, List<IntVec3> breachCells, int damagedWalls, IntVec3 creakCell)
        {
            LastBreachSounds = 0;
            LastCreakPlayed = false;
            if (Find.CurrentMap != map)
            {
                EndAll();
                return;
            }
            UpdateFloodLoops(inflowCells, inflowStages);

            for (int i = 0; i < breachCells.Count && LastBreachSounds < MaxBreachSounds; i++)
            {
                DepthRimDefOf.DepthRim_Breach.PlayOneShot(new TargetInfo(breachCells[i], map));
                LastBreachSounds++;
                TotalBreachSounds++;
            }

            float creak = FloodingMath.HullCreakChance(flooding.DepthTier, damagedWalls)
                          * Mathf.Clamp(DepthRimModSettings.Current.hullCreakFrequency, 0f, 2f);
            if (creakCell.IsValid && creak > 0f && Rand.Chance(creak))
            {
                PlayCreak(creakCell);
            }
        }

        /// <summary>直接播一次船殼承壓聲（除錯動作與驗收測試也用）。</summary>
        public void PlayCreak(IntVec3 cell)
        {
            DepthRimDefOf.DepthRim_HullCreak.PlayOneShot(new TargetInfo(cell, map));
            LastCreakPlayed = true;
        }

        private void UpdateFloodLoops(List<IntVec3> inflowCells, List<int> inflowStages)
        {
            IntVec3 camera = Find.CameraDriver != null ? Find.CameraDriver.MapPosition : map.Center;
            // 同一格可能有多個來源（牆與門）：取最強的階段
            Dictionary<IntVec3, int> stageOf = new Dictionary<IntVec3, int>();
            for (int i = 0; i < inflowCells.Count; i++)
            {
                int stage = inflowStages != null && i < inflowStages.Count ? inflowStages[i] : 1;
                stageOf[inflowCells[i]] = stageOf.TryGetValue(inflowCells[i], out int old) ? Mathf.Max(old, stage) : stage;
            }
            tmpCells.Clear();
            tmpCells.AddRange(stageOf.Keys.OrderBy(c => c.DistanceToSquared(camera)).Take(MaxFloodLoops));
            foreach (IntVec3 c in floodLoops.Keys.ToList())
            {
                if (!tmpCells.Contains(c) || floodLoopStages[c] != stageOf[c])
                {
                    floodLoops[c].End();
                    floodLoops.Remove(c);
                    floodLoopStages.Remove(c);
                }
            }
            for (int i = 0; i < tmpCells.Count; i++)
            {
                IntVec3 c = tmpCells[i];
                if (!floodLoops.ContainsKey(c))
                {
                    FloodLoopParams(stageOf[c], out float volume, out float pitch);
                    SoundInfo info = SoundInfo.InMap(new TargetInfo(c, map), MaintenanceType.None);
                    info.volumeFactor = volume;
                    info.pitchFactor = pitch;
                    Sustainer s = DepthRimDefOf.DepthRim_FloodLoop.TrySpawnSustainer(info);
                    if (s != null)
                    {
                        floodLoops[c] = s;
                        floodLoopStages[c] = stageOf[c];
                    }
                }
            }
            tmpCells.Clear();
        }

        /// <summary>每 30 幀：排水泵聲跟著泵的運轉狀態開關，並重新套用所有持續音效的悶音。</summary>
        public void FrameUpdate(List<CompFloodPump> pumps)
        {
            if (Find.CurrentMap != map)
            {
                EndAll();
                return;
            }
            tmpPumps.Clear();
            tmpPumps.AddRange(pumpLoops.Keys);
            for (int i = 0; i < tmpPumps.Count; i++)
            {
                CompFloodPump pump = tmpPumps[i];
                if (!pumps.Contains(pump) || !pump.Working || !pump.parent.Spawned)
                {
                    pumpLoops[pump].End();
                    pumpLoops.Remove(pump);
                }
            }
            tmpPumps.Clear();
            for (int i = 0; i < pumps.Count; i++)
            {
                CompFloodPump pump = pumps[i];
                if (pump.Working && pump.parent.Spawned && !pumpLoops.ContainsKey(pump))
                {
                    Sustainer s = DepthRimDefOf.DepthRim_PumpLoop.TrySpawnSustainer(SoundInfo.InMap(pump.parent, MaintenanceType.None));
                    if (s != null)
                    {
                        pumpLoops[pump] = s;
                    }
                }
            }
            UnderwaterAudio.UpdateSustainers(map, flooding);
        }

        /// <summary>潛在水裡、穿著潛水氣瓶的角色：偶爾吐一串氣泡；氣量低時改成急促的低氣量聲。</summary>
        public static void PlayDiverBreath(Pawn pawn, CompDivingAir air)
        {
            if (air == null || !DepthRimModSettings.Current.diverBreathing || Find.CurrentMap != pawn.Map || Find.CameraDriver == null)
            {
                return;
            }
            if (pawn.Position.DistanceTo(Find.CameraDriver.MapPosition) > BreathAudibleRange)
            {
                return;
            }
            SoundDef def = air.AirPercent < LowAirFraction ? DepthRimDefOf.DepthRim_DiverLowAir : DepthRimDefOf.DepthRim_DiverBubbles;
            def.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
        }

        /// <summary>結束所有持續音效（離開地圖、地圖卸除）。</summary>
        public void EndAll()
        {
            foreach (Sustainer s in floodLoops.Values)
            {
                if (!s.Ended)
                {
                    s.End();
                }
            }
            floodLoops.Clear();
            floodLoopStages.Clear();
            foreach (Sustainer s in pumpLoops.Values)
            {
                if (!s.Ended)
                {
                    s.End();
                }
            }
            pumpLoops.Clear();
        }
    }
}
