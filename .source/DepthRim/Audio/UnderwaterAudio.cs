using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 水中聲音（二期 P2-11，設計見二期計畫 §2.8）。比照 Odyssey 真空音效：
    /// <list type="bullet">
    /// <item>原版 <c>SampleOneShot.TryMakeAndPlay</c> 在真空地圖會依發聲格的真空程度壓低音量並加低通濾波；
    /// 這裡在水下地圖做同樣的事，但看的是「浸水程度」，而且聲源與聽者（鏡頭）任一方在水中就算悶（<see cref="FloodingMath.AudioMuffle"/>）。</item>
    /// <item>原版持續音效完全沒有真空處理；這裡每 30 幀更新一次，所以艙室抽乾時機械聲會逐漸變清楚。</item>
    /// <item>音量倍率不直接改 <c>AudioSource.volume</c>（原版 <c>Sample.Update</c> 每幀都會重設），
    /// 而是記在樣本上，由 <see cref="Patch_Sample_Volume"/> 乘進原版的 <c>Volume</c>。</item>
    /// </list>
    /// 迷霧格的聲源一律當作完全在水中，不讀那一格的房間或水位（不洩漏未探索的地形）。
    /// </summary>
    public static class UnderwaterAudio
    {
        public const int SustainerUpdateFrames = 30;

        private static readonly ConditionalWeakTable<Sample, StrongBox<float>> volumeFactors = new ConditionalWeakTable<Sample, StrongBox<float>>();

        private static readonly SoundFilterLowPass sharedFilter = new SoundFilterLowPass { lowpassResonaceQ = 0f };

        /// <summary>某一格的浸水程度：迷霧或開放水域 = 1，密閉艙室 = 艙室水位，界外 = 1。</summary>
        public static float WetnessAt(Map map, IntVec3 c, MapComponent_Flooding flooding)
        {
            if (!c.InBounds(map) || c.Fogged(map) || flooding.IsOpenAt(c))
            {
                return 1f;
            }
            return flooding.LevelAt(c);
        }

        /// <summary>聽者的浸水程度：鏡頭中心所在格；鏡頭不在這張地圖時視為在水中（不會有人聽到乾艙室的聲音）。</summary>
        public static float ListenerWetness(Map map, MapComponent_Flooding flooding)
        {
            if (Find.CurrentMap != map || Find.CameraDriver == null)
            {
                return 1f;
            }
            return WetnessAt(map, Find.CameraDriver.MapPosition, flooding);
        }

        /// <summary>目前這個樣本的悶音音量倍率（沒有記錄 = 1）。</summary>
        public static float VolumeFactorFor(Sample sample)
        {
            return sample != null && volumeFactors.TryGetValue(sample, out StrongBox<float> box) ? box.Value : 1f;
        }

        /// <summary>對一個樣本套用悶音：低通濾波（音源上的元件）與音量倍率（記在樣本上）。回傳悶音強度。</summary>
        public static float Apply(Sample sample, Map map, IntVec3 cell, MapComponent_Flooding flooding)
        {
            if (sample?.source == null)
            {
                return 0f;
            }
            float muffle = FloodingMath.AudioMuffle(WetnessAt(map, cell, flooding), ListenerWetness(map, flooding))
                           * Mathf.Clamp01(DepthRimModSettings.Current.muffleStrength);
            sharedFilter.cutoffFrequency = FloodingMath.MuffleCutoffHz(muffle, flooding.DepthTier);
            sharedFilter.SetupOn(sample.source);
            float factor = FloodingMath.MuffleVolume(muffle);
            volumeFactors.Remove(sample);
            volumeFactors.Add(sample, new StrongBox<float>(factor));
            return muffle;
        }

        /// <summary>每 30 幀重新套用一次目前地圖上有位置的持續音效（排水泵、機械聲）。</summary>
        public static void UpdateSustainers(Map map, MapComponent_Flooding flooding)
        {
            if (Find.SoundRoot?.sustainerManager == null || Find.CurrentMap != map)
            {
                return;
            }
            List<Sustainer> all = Find.SoundRoot.sustainerManager.AllSustainers;
            for (int i = 0; i < all.Count; i++)
            {
                Sustainer sustainer = all[i];
                TargetInfo maker = sustainer.info.Maker;
                if (sustainer.Ended || !maker.HasThing && !maker.Cell.IsValid || maker.Map != map)
                {
                    continue;
                }
                List<SubSustainer> subs = Traverse.Create(sustainer).Field("subSustainers").GetValue<List<SubSustainer>>();
                if (subs == null)
                {
                    continue;
                }
                for (int s = 0; s < subs.Count; s++)
                {
                    if (subs[s].subDef.onCamera || !subs[s].subDef.canVacuumDampen)
                    {
                        continue;
                    }
                    List<SampleSustainer> samples = Traverse.Create(subs[s]).Field("samples").GetValue<List<SampleSustainer>>();
                    if (samples == null)
                    {
                        continue;
                    }
                    for (int k = 0; k < samples.Count; k++)
                    {
                        Apply(samples[k], map, maker.Cell, flooding);
                    }
                }
            }
        }
    }

    /// <summary>單次音效：比照原版真空的位置（同一個方法、同一個 canVacuumDampen 旗標），改成水下的悶音。</summary>
    [HarmonyPatch(typeof(SampleOneShot), nameof(SampleOneShot.TryMakeAndPlay))]
    public static class Patch_SampleOneShot_TryMakeAndPlay
    {
        [HarmonyPostfix]
        public static void Postfix(SampleOneShot __result, SubSoundDef def, SoundInfo info)
        {
            if (__result == null || def == null || def.onCamera || !def.canVacuumDampen)
            {
                return;
            }
            Map map = info.Maker.Map;
            MapComponent_Flooding flooding = map?.Flooding();
            if (flooding == null || !flooding.Active)
            {
                return;
            }
            UnderwaterAudio.Apply(__result, map, info.Maker.Cell, flooding);
            __result.Update();
        }
    }

    /// <summary>
    /// 悶音的音量倍率乘進原版的 <c>Sample.Volume</c>。原版 <c>Sample.Update</c> 每幀都用 Volume 重設
    /// <c>AudioSource.volume</c>，所以只改 AudioSource 會在下一幀失效；持續音效的 Volume 也是先呼叫這個基底屬性。
    /// </summary>
    [HarmonyPatch(typeof(Sample), "get_Volume")]
    public static class Patch_Sample_Volume
    {
        [HarmonyPostfix]
        public static void Postfix(Sample __instance, ref float __result)
        {
            __result *= UnderwaterAudio.VolumeFactorFor(__instance);
        }
    }
}
