using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>
    /// 水中內爆（Docs/水中內爆_實作計畫.md §8.2 I1–I5，驗收編號 IM1）：水中的爆炸改成內爆（原版視覺與音效關掉、傷害照算）；
    /// 乾艙室、半淹艙室與排除類型照原版；火焰類改成燙傷、不起火；附帶的毒氣照放；打壞艙壁照常。
    /// </summary>
    public static class Tests_Implosion
    {
        private static void SetLevel(IEnumerable<IntVec3> cells, float level)
        {
            Map map = DepthRimFixture.Ocean;
            byte[] arr = HarmonyLib.Traverse.Create(DepthRimFixture.Flooding).Field("levels").GetValue<byte[]>();
            byte b = FloodingMath.ToByte(level);
            foreach (IntVec3 c in cells) { arr[map.cellIndices.CellToIndex(c)] = b; }
        }

        private static MapComponent_Implosions Comp => DepthRimFixture.Ocean.GetComponent<MapComponent_Implosions>();

        /// <summary>引爆並回傳這一發有沒有被接手；<paramref name="explosion"/> 是剛生成的 Explosion（判斷原版旗標用）。</summary>
        private static bool Boom(IntVec3 cell, DamageDef damage, out Explosion explosion, float chanceToStartFire = 0f, GasType? gas = null)
        {
            Map map = DepthRimFixture.Ocean;
            int before = MapComponent_Implosions.TotalStarted;
            GenExplosion.DoExplosion(cell, map, 1.9f, damage, null, damAmount: 6, chanceToStartFire: chanceToStartFire, postExplosionGasType: gas);
            explosion = cell.GetThingList(map).OfType<Explosion>().LastOrDefault();
            return MapComponent_Implosions.TotalStarted == before + 1;
        }

        /// <summary>
        /// 主氣泡與震波環（§4.2）：貼圖載入，在開放水域引爆，於震波環擴張、氣泡最大、崩塌前三個時間點暫停截圖給人看。
        /// </summary>
        public static IEnumerable<Wait> IM2_ImplosionBubbleLook(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            Map map = DepthRimFixture.Ocean;
            ctx.Check(ImplosionAssets.BubbleMat.mainTexture != null && ImplosionAssets.BubbleMat.mainTexture != BaseContent.BadTex, "the bubble texture loads");
            ctx.Check(ImplosionAssets.RingMat.mainTexture != null && ImplosionAssets.RingMat.mainTexture != BaseContent.BadTex, "the shock ring texture loads");
            ctx.Check(ImplosionAssets.FoamMat.mainTexture != null && ImplosionAssets.FoamMat.mainTexture != BaseContent.BadTex, "the foam texture loads");

            CellRect area = DepthRimFixture.ClearOceanArea(9);
            foreach (IntVec3 c in area) { map.roofGrid.SetRoof(c, null); }
            IntVec3 center = area.CenterCell;
            SetLevel(area.ExpandedBy(2).ClipInsideMap(map).Cells, 1f);
            CameraJumper.TryJump(center, map);
            Find.CameraDriver.SetRootSize(9f);
            yield return new WaitFrames(30);
            ctx.Require(DepthRimFixture.Flooding.IsOpenAt(center), "the test spot is open water");

            // 一般速度、允許暫停：快轉時一幀跑好幾 tick，截圖時間點會跳過
            ctx.allowPause = true;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            int before = MapComponent_Implosions.TotalStarted;
            GenExplosion.DoExplosion(center, map, 3.9f, DamageDefOf.Bomb, null, damAmount: 30);
            ctx.Require(MapComponent_Implosions.TotalStarted == before + 1, "the blast became an implosion");
            ImplosionInstance inst = Comp.Active.Last();
            float period = inst.Period;
            ctx.Note($"tier {inst.tier}, period {period:F2}s, max bubble radius {ImplosionMath.MaxBubbleRadius(inst.radius, inst.tier):F2}");

            int particlesBefore = MapComponent_Implosions.TotalParticles;
            float[] seconds = { ImplosionMath.RingSeconds(period) * 0.45f, period * ImplosionMath.ExpandFraction, period * 0.95f, period + 0.25f, period + 1.0f };
            string[] names = { "flash", "peak", "collapsing", "foam", "column" };
            foreach (Wait w in ShotsAt(ctx, inst, seconds, names, "implosion_")) { yield return w; }
            ctx.Check(inst.inwardThrown > 0, $"inward bubbles were thrown from the blast cells ({inst.inwardThrown})");
            ctx.Check(MapComponent_Implosions.TotalParticles - particlesBefore > inst.inwardThrown, $"murk and the bubble column were thrown after the collapse ({MapComponent_Implosions.TotalParticles - particlesBefore - inst.inwardThrown})");

            // 燙傷型：橘紅核心、蒸氣色水團、更密更久的氣泡柱
            GenExplosion.DoExplosion(center, map, 3.9f, DamageDefOf.Flame, null, damAmount: 30);
            ImplosionInstance scald = Comp.Active.Last();
            ctx.Check(scald.scald, "the flame blast plays the scald variant");
            foreach (Wait w in ShotsAt(ctx, scald, new[] { scald.Period * ImplosionMath.ExpandFraction, scald.Period + 1.0f }, new[] { "peak", "column" }, "scald_")) { yield return w; }

            // 深海（用視覺層級覆寫）：氣泡更小、更暗（跟著水中光照），不比周圍的水亮
            yield return new WaitTicks(240);
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            flooding.debugTierOverride = DepthTier.Deep;
            map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
            yield return new WaitFrames(30);
            GenExplosion.DoExplosion(center, map, 3.9f, DamageDefOf.Bomb, null, damAmount: 30);
            ImplosionInstance deep = Comp.Active.Last();
            ctx.Check(deep.tier == DepthTier.Deep, "the implosion follows the visual tier override");
            ctx.Check(ImplosionMath.MaxBubbleRadius(deep.radius, deep.tier) < ImplosionMath.MaxBubbleRadius(inst.radius, inst.tier), "the deep bubble is smaller");
            foreach (Wait w in ShotsAt(ctx, deep, new[] { deep.Period * ImplosionMath.ExpandFraction, deep.Period + 0.6f }, new[] { "peak", "column" }, "deep_")) { yield return w; }
            flooding.debugTierOverride = null;
            map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);

            // 淹滿的艙室（艙室水體層，renderQueue 2950）：內爆一樣要被水的混濁蓋住
            yield return new WaitTicks(240);
            foreach (IntVec3 c in area) { map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed); }
            DepthRimFixture.BuildWallRing(area);
            SetLevel(area.ContractedBy(1).Cells, 1f);
            WaitUntil sealedWait = new WaitUntil(() => !flooding.IsOpenAt(center), 600);
            yield return sealedWait;
            ctx.Check(!sealedWait.TimedOut, "the test compartment is sealed");
            SetLevel(area.ContractedBy(1).Cells, 1f);
            map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
            yield return new WaitFrames(10);
            GenExplosion.DoExplosion(center, map, 2.5f, DamageDefOf.Bomb, null, damAmount: 5);
            ImplosionInstance room = Comp.Active.Last();
            foreach (Wait w in ShotsAt(ctx, room, new[] { room.Period * ImplosionMath.ExpandFraction, room.Period + 0.3f }, new[] { "peak", "foam" }, "compartment_")) { yield return w; }

            ctx.allowPause = false;
            yield return new WaitTicks(240);
            ctx.Check(Comp.ActiveCount == 0, "the implosions finished");
        }

        /// <summary>在實例開始後的指定秒數暫停截圖，截完恢復一般速度。</summary>
        private static IEnumerable<Wait> ShotsAt(TestContext ctx, ImplosionInstance inst, float[] seconds, string[] names, string prefix)
        {
            for (int i = 0; i < seconds.Length; i++)
            {
                int target = inst.startTick + Mathf.Max(1, Mathf.RoundToInt(seconds[i] * GenTicks.TicksPerRealSecond));
                yield return new WaitUntil(() => Find.TickManager.TicksGame >= target, 600);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                yield return new WaitFrames(5);
                ctx.Screenshot(prefix + names[i]);
                yield return new WaitFrames(30);
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            }
        }

        public static IEnumerable<Wait> IM1_UnderwaterImplosion(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            Map map = DepthRimFixture.Ocean;
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            ctx.Require(Comp != null, "the implosion map component exists on the ocean map");

            CellRect ring = DepthRimFixture.ClearOceanArea(9);
            List<Thing> walls = DepthRimFixture.BuildWallRing(ring);
            CellRect interior = ring.ContractedBy(1);
            IntVec3 inside = interior.CenterCell;
            // 牆圈外 2 格：r1.9 的爆炸碰不到牆，艙室保持密閉（炸傷艙壁放到最後測：受損的牆會破，艙室變成開放水域）
            IntVec3 wet = ring.CenterCell + new IntVec3(0, 0, -6);
            IntVec3 byWall = ring.CenterCell + new IntVec3(0, 0, -5);
            SetLevel(new[] { wet, wet + IntVec3.East, wet + IntVec3.West, byWall }, 1f);
            yield return new WaitTicks(2);
            ctx.Require(flooding.IsOpenAt(wet), "the test cell outside the compartment is open water");
            // 當第一個測試跑時（地圖剛建好），新蓋的牆圈要等一次水位更新才會被認成密閉艙室
            WaitUntil sealedWait = new WaitUntil(() => !flooding.IsOpenAt(inside), 600);
            yield return sealedWait;
            ctx.Require(!sealedWait.TimedOut, "the new wall ring is recognised as a sealed compartment");

            // 1. 開放水域：接手、原版旗標關掉、傷害照算
            Pawn swimmer = DepthRimFixture.SpawnColonist(map, wet + IntVec3.East);
            ctx.Check(Boom(wet, DamageDefOf.Bomb, out Explosion openBlast), "a bomb in open water becomes an implosion");
            ctx.Check(openBlast != null && !openBlast.doVisualEffects && !openBlast.doSoundEffects, "the vanilla explosion visuals and sound are switched off");
            ctx.Check(Comp.ActiveCount > 0 && !Comp.Active.Last().scald, "the implosion is playing (not scald)");
            yield return new WaitTicks(20);
            ctx.Check(swimmer.health.hediffSet.hediffs.OfType<Hediff_Injury>().Any(), "the bomb still injures a pawn in the water");
            ctx.Check(Comp.ActiveCount == 0 || Find.TickManager.TicksGame - Comp.Active.First().startTick < 120, "finished implosions are removed");

            // 2. 乾艙室與半淹艙室：照原版
            SetLevel(interior.Cells, 0f);
            ctx.Check(!Boom(inside, DamageDefOf.Bomb, out Explosion dryBlast), "a bomb in a drained compartment explodes normally");
            ctx.Check(dryBlast != null && dryBlast.doVisualEffects, "the dry explosion keeps the vanilla visuals");
            SetLevel(interior.Cells, 0.5f);
            ctx.Check(!Boom(inside, DamageDefOf.Bomb, out _), "a bomb in a half-flooded compartment explodes normally");

            // 3. 淹滿的艙室：接手
            SetLevel(interior.Cells, 1f);
            ctx.Check(!flooding.IsOpenAt(inside) && Boom(inside, DamageDefOf.Bomb, out _), "a bomb in a flooded compartment becomes an implosion");

            // 4. 排除類型：EMP、煙霧、眩暈、消防泡沫
            foreach (string name in new[] { "EMP", "Smoke", "Stun", "Extinguish" })
            {
                DamageDef def = DefDatabase<DamageDef>.GetNamedSilentFail(name);
                if (!ctx.Check(def != null, $"{name} is loaded")) { continue; }
                ctx.Check(def.GetModExtension<UnderwaterImplosionExtension>()?.exempt == true, $"{name} carries the exempt extension");
                SetLevel(interior.Cells, 1f);
                ctx.Check(!Boom(inside, def, out _), $"{name} underwater explodes normally");
            }
            yield return new WaitTicks(20);

            // 5. 火焰類：改成燙傷，不起火
            SetLevel(interior.Cells, 1f);
            Pawn diver = DepthRimFixture.SpawnColonist(map, inside + IntVec3.North);
            Thing logs = DepthRimFixture.Spawn(ThingDefOf.WoodLog, inside + IntVec3.South, map);
            ctx.Check(Boom(inside, DamageDefOf.Flame, out Explosion flameBlast, chanceToStartFire: 1f), "a flame explosion underwater becomes an implosion");
            ctx.Check(flameBlast != null && flameBlast.damType == DepthRimDefOf.DepthRim_Scald, $"its damage becomes scald ({flameBlast?.damType?.defName})");
            ctx.Check(flameBlast != null && flameBlast.chanceToStartFire == 0f, "its fire chance is cleared");
            ctx.Check(Comp.Active.Last().scald, "the implosion plays the scald variant");
            yield return new WaitTicks(20);
            ctx.Check(diver.health.hediffSet.hediffs.Any(h => h.def.defName == "Burn"), "the scald leaves burns");
            ctx.Check(!map.listerThings.ThingsOfDef(ThingDefOf.Fire).Any(f => ring.ExpandedBy(3).Contains(f.Position)), "no fire starts underwater");
            ctx.Check(!logs.Destroyed, "the wood next to the blast is not burned away");

            // 6. 附帶毒氣照放
            if (ModsConfig.BiotechActive)
            {
                SetLevel(interior.Cells, 1f);
                ctx.Check(Boom(inside, DamageDefOf.Bomb, out _, gas: GasType.ToxGas), "a tox shell underwater becomes an implosion");
                yield return new WaitTicks(10);
                ctx.Check(map.gasGrid.DensityAt(inside, GasType.ToxGas) > 0, "the tox gas is still released");
            }

            // 7. 緊貼艙壁的內爆照樣炸傷牆（之後交給既有的進水系統，F5／T1 涵蓋）
            ctx.Check(Boom(byWall, DamageDefOf.Bomb, out _), "a bomb right outside the hull becomes an implosion");
            yield return new WaitTicks(20);
            Thing nearWall = walls.OrderBy(w => w.Position.DistanceToSquared(byWall)).First();
            ctx.Check(nearWall.Destroyed || nearWall.HitPoints < nearWall.MaxHitPoints, "the implosion still damages the hull wall next to it");
            yield return new WaitTicks(60);
        }
    }
}
