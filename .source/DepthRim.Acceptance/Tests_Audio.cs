using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim.Acceptance
{
    /// <summary>水中聲音（二期 P2-11，§2.8）。只驗證機制（濾波、音量倍率、事件觸發），實際聽感要玩家自己聽。</summary>
    public static class Tests_Audio
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        private static void SetRoomLevel(Map map, Room room, float level)
        {
            byte[] arr = Traverse.Create(DepthRimFixture.Flooding).Field("levels").GetValue<byte[]>();
            byte b = FloodingMath.ToByte(level);
            foreach (IntVec3 c in room.Cells) { arr[map.cellIndices.CellToIndex(c)] = b; }
        }

        /// <summary>在 cell 播一次 def，回傳剛建立的單次樣本（找不到時 null）。</summary>
        private static SampleOneShot PlayAt(SoundDef def, Map map, IntVec3 cell)
        {
            HashSet<SampleOneShot> before = new HashSet<SampleOneShot>(Find.SoundRoot.oneShotManager.PlayingOneShots);
            def.PlayOneShot(new TargetInfo(cell, map));
            return Find.SoundRoot.oneShotManager.PlayingOneShots.FirstOrDefault(s => !before.Contains(s) && s.subDef.parentDef == def);
        }

        /// <summary>原版同一個 SoundDef 在 0.05 秒內最多 maxSimultaneous 個；兩次播放之間隔一點真實時間。</summary>
        private static Wait Gap() => new WaitRealtime(() => false, 0.12f);

        private static float Cutoff(Sample sample)
        {
            AudioLowPassFilter filter = sample?.source != null ? sample.source.GetComponent<AudioLowPassFilter>() : null;
            return filter != null && filter.enabled ? filter.cutoffFrequency : 22000f;
        }

        private static void Jump(Map map, IntVec3 cell)
        {
            Current.Game.CurrentMap = map;
            Find.CameraDriver.JumpToCurrentMapLoc(cell);
        }

        public static IEnumerable<Wait> V13_UnderwaterAudio(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            CellRect ring = DepthRimFixture.ClearOceanArea(7);
            List<Thing> walls = DepthRimFixture.BuildWallRing(ring);
            CellRect inner = ring.ContractedBy(1);
            IntVec3 center = inner.CenterCell;
            Room room = center.GetRoom(map);
            ctx.Require(room != null && !flooding.IsOpenWater(room), "sealed compartment for the audio test");
            IntVec3 open = DepthRimFixture.OpenWaterCell();
            ctx.Require(open.IsValid, "found an open-water cell");
            DepthTier tier = flooding.DepthTier;
            yield return Updates(1);

            SetRoomLevel(map, room, 0f);
            Jump(map, center);
            yield return new WaitFrames(5);
            ctx.Check(UnderwaterAudio.ListenerWetness(map, flooding) == 0f, "camera in a dry compartment: the listener is dry");

            // a) 乾艙室裡的聲音：不悶
            yield return Gap();
            SampleOneShot dry = PlayAt(DepthRimDefOf.DepthRim_Breach, map, center + IntVec3.East);
            ctx.Require(dry != null, "one-shot sample created in the dry compartment");
            ctx.Check(Cutoff(dry) >= 21999f, $"dry source, dry listener: unfiltered ({Cutoff(dry):F0} Hz)");
            ctx.Check(UnderwaterAudio.VolumeFactorFor(dry) == 1f, "dry source, dry listener: full volume");

            // b) 外面的海：完全悶（鏡頭在乾艙室也一樣）
            yield return Gap();
            SampleOneShot sea = PlayAt(DepthRimDefOf.DepthRim_Breach, map, open);
            ctx.Require(sea != null, "one-shot sample created in open water");
            float fullHz = FloodingMath.MuffleCutoffHz(1f, tier);
            ctx.Check(Mathf.Abs(Cutoff(sea) - fullHz) < 1f, $"open-water source: cutoff {Cutoff(sea):F0} Hz = {fullHz:F0} Hz");
            ctx.Check(Mathf.Abs(UnderwaterAudio.VolumeFactorFor(sea) - FloodingMath.MuffleVolume(1f)) < 0.001f, $"open-water source: volume factor {UnderwaterAudio.VolumeFactorFor(sea):F2}");
            ctx.Check(Mathf.Abs(sea.source.volume - Traverse.Create(sea).Property("Volume").GetValue<float>()) < 0.001f, "the volume factor reaches the AudioSource through Sample.Volume");

            // c) 半淹的艙室：悶音 = 水位
            SetRoomLevel(map, room, 0.5f);
            yield return Gap();
            SampleOneShot half = PlayAt(DepthRimDefOf.DepthRim_Breach, map, center + IntVec3.West);
            ctx.Require(half != null, "one-shot sample created in the half-flooded compartment");
            float halfHz = FloodingMath.MuffleCutoffHz(FloodingMath.ToByte(0.5f) / 255f, tier);
            ctx.Check(Mathf.Abs(Cutoff(half) - halfHz) < 5f, $"half-flooded compartment: cutoff {Cutoff(half):F0} Hz ≈ {halfHz:F0} Hz");
            ctx.Check(Cutoff(half) > Cutoff(sea) && Cutoff(half) < Cutoff(dry), "half-flooded sits between dry and open water");

            // d) 聽者規則：鏡頭移到開放水域，乾艙室裡的聲音也悶
            SetRoomLevel(map, room, 0f);
            Jump(map, open);
            yield return new WaitFrames(5);
            ctx.Check(UnderwaterAudio.ListenerWetness(map, flooding) == 1f, "camera over open water: the listener is wet");
            yield return Gap();
            SampleOneShot heardFromSea = PlayAt(DepthRimDefOf.DepthRim_Breach, map, center);
            ctx.Check(heardFromSea != null && Mathf.Abs(Cutoff(heardFromSea) - fullHz) < 1f, "listener in water hears a dry compartment fully muffled");
            Jump(map, center);
            yield return new WaitFrames(5);

            // e) 迷霧格：一律當作完全在水中（不洩漏未探索的地形）
            IntVec3 fogged = map.AllCells.FirstOrDefault(c => c.Fogged(map));
            if (fogged.IsValid && fogged != IntVec3.Zero)
            {
                ctx.Check(UnderwaterAudio.WetnessAt(map, fogged, flooding) == 1f, "fogged cell counts as fully wet");
                yield return Gap();
                SampleOneShot fog = PlayAt(DepthRimDefOf.DepthRim_HullCreak, map, fogged);
                ctx.Check(fog != null && Mathf.Abs(Cutoff(fog) - fullHz) < 1f, "a sound from a fogged cell uses the full-muffle values");
            }
            else
            {
                ctx.Note("no fogged cell on the test map; fog rule checked only by WetnessAt");
            }

            // f) 設定：悶音強度 0 → 完全不悶
            DepthRimModSettings settings = DepthRimModSettings.Current;
            float oldMuffle = settings.muffleStrength;
            settings.muffleStrength = 0f;
            yield return Gap();
            SampleOneShot noMuffle = PlayAt(DepthRimDefOf.DepthRim_Breach, map, open);
            ctx.Check(noMuffle != null && Cutoff(noMuffle) >= 21999f && UnderwaterAudio.VolumeFactorFor(noMuffle) == 1f, "muffle strength 0 turns muffling off");
            settings.muffleStrength = oldMuffle;

            // g) 排水泵持續音效：艙室抽乾時截止頻率上升
            DepthRimFixture.Spawn(ThingDefOf.PowerConduit, center + IntVec3.North, map);
            CompFloodPump pump = DepthRimFixture.Spawn(DepthRimFixture.BilgePump, center, map).TryGetComp<CompFloodPump>();
            SetRoomLevel(map, room, 0.9f);
            WaitUntil powered = new WaitUntil(() => pump.Working, 1200);
            yield return powered;
            ctx.Require(!powered.TimedOut, "pump powered");
            SetRoomLevel(map, room, 0.9f);
            flooding.Audio.FrameUpdate(Traverse.Create(flooding).Field("pumps").GetValue<List<CompFloodPump>>());
            Sustainer pumpLoop = flooding.Audio.PumpLoopFor(pump);
            ctx.Require(pumpLoop != null, "working pump has a pump-loop sustainer");
            yield return new WaitFrames(10);
            SampleSustainer pumpSample = PumpSamples(pumpLoop).FirstOrDefault();
            ctx.Require(pumpSample != null, "pump-loop sustainer is playing a sample");
            SetRoomLevel(map, room, 0.9f);
            UnderwaterAudio.UpdateSustainers(map, flooding);
            float wetHz = Cutoff(pumpSample);
            SetRoomLevel(map, room, 0f);
            UnderwaterAudio.UpdateSustainers(map, flooding);
            float dryHz = Cutoff(pumpSample);
            ctx.Check(dryHz > wetHz, $"pump sound clears up as the room drains ({wetHz:F0} Hz -> {dryHz:F0} Hz)");
            ctx.Check(UnderwaterAudio.VolumeFactorFor(pumpSample) == 1f, "drained room: pump at full volume");
            pump.parent.TryGetComp<CompFlickable>().DoFlick();
            yield return Updates(1);
            yield return new WaitFrames(UnderwaterAudio.SustainerUpdateFrames + 5);
            ctx.Check(flooding.Audio.PumpLoopFor(pump) == null, "switched-off pump stops its loop");

            // h) 層級環境音：除錯切換層級，只有對應層級的參數是 1
            SoundParamSource_DepthTier deepParam = new SoundParamSource_DepthTier { tier = DepthTier.Deep };
            SoundParamSource_DepthTier shallowParam = new SoundParamSource_DepthTier { tier = DepthTier.Shallow };
            flooding.debugTierOverride = DepthTier.Deep;
            ctx.Check(deepParam.ValueFor(null) == 1f && shallowParam.ValueFor(null) == 0f, "debug tier Deep: only the deep ambience plays");
            flooding.debugTierOverride = null;
            ctx.Check(new SoundParamSource_DepthTier { tier = tier }.ValueFor(null) == 1f, $"no override: the map's own tier ({tier}) ambience plays");
            ctx.Check(new SoundParamSource_CameraInCompartment().ValueFor(null) == 1f, "camera in a dry compartment: ambience is damped");
            bool ambientRunning = Find.SoundRoot.sustainerManager.AllSustainers.Any(s => s.def.defName.StartsWith("DepthRim_Ambient_"));
            ctx.Note($"tier ambience sustainers running: {ambientRunning}");

            // i) 進水聲：受損牆在漏水 → 進水格 + 進水聲
            SetRoomLevel(map, room, 0f);
            Thing wall = walls.First(w => w.Position.z == inner.minZ - 1 && w.Position.x == center.x);
            wall.HitPoints = Mathf.Max(1, wall.MaxHitPoints / 10);
            yield return Updates(1);
            ctx.Check(flooding.InflowCells.Contains(wall.Position), "damaged wall is an inflow cell");
            ctx.Check(flooding.DamagedWallCount >= 1, $"damaged wall counted ({flooding.DamagedWallCount})");
            ctx.Check(flooding.Audio.FloodLoopCells.Contains(wall.Position), $"flood loop plays at the leaking wall ({flooding.Audio.FloodLoopCount} loop(s))");
            ctx.Check(flooding.Audio.FloodLoopCount <= FloodingAudio.MaxFloodLoops, "flood loops are capped");

            // j) 船殼承壓聲
            flooding.Audio.PlayCreak(wall.Position);
            ctx.Check(flooding.Audio.LastCreakPlayed, "hull creak plays");

            // k) 破艙：牆被打掉，未滿水的艙室變成開放水域 → 破艙聲
            // 等待 255 ticks 可能包含兩次 250-tick 更新，所以看累計次數而不是「最近一次更新」的清單
            SetRoomLevel(map, room, 0.2f);
            int breachesBefore = flooding.Audio.TotalBreachSounds;
            wall.Destroy(DestroyMode.Vanish);
            yield return Updates(1);
            int breaches = flooding.Audio.TotalBreachSounds - breachesBefore;
            ctx.Check(breaches >= 1, $"breach detected and its sound plays ({breaches})");
            yield return Updates(2);
            ctx.Check(flooding.Audio.TotalBreachSounds - breachesBefore == breaches, "the breach is reported once, not every update");
            ctx.Check(flooding.BreachCells.Count == 0, "no breach in later updates");
            // 打通後是慢慢灌滿（破口前緣會有進水聲）；等灌滿之後才不該再有進水聲
            WaitUntil filled = new WaitUntil(() => flooding.LevelAt(center) >= 0.995f, MapComponent_Flooding.UpdateInterval * 12);
            yield return filled;
            yield return Updates(1);
            ctx.Check(!flooding.Audio.FloodLoopCells.Contains(wall.Position), "flood loop stops once the room is full");
        }

        private static IEnumerable<SampleSustainer> PumpSamples(Sustainer sustainer)
        {
            List<SubSustainer> subs = Traverse.Create(sustainer).Field("subSustainers").GetValue<List<SubSustainer>>();
            if (subs == null) { yield break; }
            foreach (SubSustainer sub in subs)
            {
                List<SampleSustainer> samples = Traverse.Create(sub).Field("samples").GetValue<List<SampleSustainer>>();
                if (samples == null) { continue; }
                foreach (SampleSustainer s in samples) { yield return s; }
            }
        }
    }
}
