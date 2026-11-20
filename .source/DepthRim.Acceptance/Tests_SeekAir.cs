using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DepthRim.Acceptance
{
    /// <summary>水下 AI（SeekAir.cs）：缺氧時自己游到乾處、潛水裝備氣量低時回去補氣、睡覺處淹水時醒來離開。</summary>
    public static class Tests_SeekAir
    {
        private const int Update = MapComponent_Flooding.UpdateInterval;

        private static WaitTicks Updates(int n) => new WaitTicks(Update * n + 5);

        private static float Severity(Pawn pawn, HediffDef def) => pawn.health.hediffSet.GetFirstHediffOfDef(def)?.Severity ?? 0f;

        private static float Level(IntVec3 c) => DepthRimFixture.Flooding.LevelAt(c);

        private static bool Seeking(Pawn pawn) => pawn.CurJobDef == DepthRimDefOf.DepthRim_SeekAir;

        /// <summary>直接設定格子水位（測試用，跳過抽水）。</summary>
        private static void SetLevel(IEnumerable<IntVec3> cells, float level)
        {
            Map map = DepthRimFixture.Ocean;
            byte[] arr = HarmonyLib.Traverse.Create(DepthRimFixture.Flooding).Field("levels").GetValue<byte[]>();
            byte b = FloodingMath.ToByte(level);
            foreach (IntVec3 c in cells) { arr[map.cellIndices.CellToIndex(c)] = b; }
        }

        /// <summary>
        /// 一間 9×9 的乾燥艙室（內部 7×7），南牆中間一扇門；回傳門外一格（灌滿水的地方）。
        /// <paramref name="interior"/> 是艙室內部。
        /// </summary>
        private static IntVec3 BuildDryRoomWithDoor(out CellRect interior, ThingDef doorDef = null)
        {
            Map map = DepthRimFixture.Ocean;
            CellRect ring = DepthRimFixture.ClearOceanArea(9);
            List<Thing> walls = DepthRimFixture.BuildWallRing(ring);
            IntVec3 doorCell = new IntVec3(ring.CenterCell.x, 0, ring.minZ);
            walls.First(w => w.Position == doorCell).Destroy(DestroyMode.Vanish);
            doorDef ??= ThingDefOf.Door;
            DepthRimFixture.Spawn(doorDef, doorCell, map, doorDef.MadeFromStuff ? ThingDefOf.Steel : null);
            interior = ring.ContractedBy(1);
            SetLevel(interior.Cells, 0f);
            IntVec3 outside = doorCell + IntVec3.South;
            // 門外：不論原本是開放水域還是圍起來的岩洞，都灌滿
            Room room = outside.GetRoom(map);
            if (room != null && !DepthRimFixture.Flooding.IsOpenWater(room))
            {
                SetLevel(room.Cells, 1f);
            }
            return outside;
        }

        private static Pawn SpawnUndrafted(IntVec3 cell)
        {
            Pawn pawn = DepthRimFixture.SpawnColonist(DepthRimFixture.Ocean, cell);
            pawn.drafter.Drafted = false;
            return pawn;
        }

        public static IEnumerable<Wait> P7_SeekAirWithoutGear(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            // 水密門：泡在水裡的人進得來，乾了之後不會為了工作（清潔、搬運）自己再開門出去。
            // 一般的門擋不住：殖民者乾了就會出去工作，門一開整間灌水，「嗆水不再加重」就看當時外面有沒有工作，結果隨機。
            ThingDef watertight = ThingDef.Named("DepthRim_WatertightDoor");
            IntVec3 outside = BuildDryRoomWithDoor(out CellRect interior, watertight);
            yield return Updates(1);
            ctx.Require(Level(outside) >= FloodingMath.SubmergedLevelStanding, $"water outside the door ({Level(outside):P0})");
            ctx.Require(Level(interior.CenterCell) < FloodingMath.SleepSafeLevel, $"room is dry ({Level(interior.CenterCell):P0})");

            // 徵召中的人由玩家控制：不自己跑
            Pawn drafted = DepthRimFixture.SpawnColonist(DepthRimFixture.Ocean, outside + IntVec3.West);
            Pawn pawn = SpawnUndrafted(outside);
            ctx.Require(CompDivingAir.WornBy(pawn) == null, "colonist has no diving gear");
            WaitUntil started = new WaitUntil(() => Seeking(pawn), 300);
            yield return started;
            ctx.Check(!started.TimedOut, "an undrafted colonist underwater starts seeking air");
            ctx.Check(!Seeking(drafted), "a drafted colonist is left to the player");
            if (started.TimedOut) { yield break; }
            IntVec3 target = pawn.CurJob.targetA.Cell;
            ctx.Check(interior.Contains(target), $"target {target} is inside the dry room");
            ctx.Check(Level(target) < FloodingMath.SleepSafeLevel, $"target is dry ({Level(target):P0})");

            WaitUntil safe = new WaitUntil(() => !FloodingPawnEffects.IsSubmerged(pawn, Level(pawn.Position)), 1500);
            yield return safe;
            ctx.Check(!safe.TimedOut, $"colonist got out of the water (at {pawn.Position}, level {Level(pawn.Position):P0})");
            float s1 = Severity(pawn, DepthRimDefOf.DepthRim_Drowning);
            yield return Updates(2);
            float s2 = Severity(pawn, DepthRimDefOf.DepthRim_Drowning);
            if (s2 > s1)
            {
                Map m = DepthRimFixture.Ocean;
                ctx.Note($"diagnose: pawn at {pawn.Position} level {Level(pawn.Position):P0} posture {pawn.GetPosture()} downed {pawn.Downed} job {pawn.CurJobDef?.defName}; room level {Level(interior.CenterCell):P0}; "
                    + $"pawns in room: {string.Join(", ", interior.ExpandedBy(1).Cells.Select(c => c.GetFirstPawn(m)).Where(p => p != null).Select(p => p.LabelShort + "@" + p.Position))}; "
                    + $"open doors: {string.Join(", ", interior.ExpandedBy(1).EdgeCells.Select(c => c.GetDoor(m)).Where(d => d != null).Select(d => d.Position + (d.Open ? " open" : " closed")))}");
            }
            ctx.Check(s2 <= s1, $"drowning stops getting worse ({s1:F2} -> {s2:F2})");
            ctx.Check(!pawn.Dead, "colonist survived");
        }

        public static IEnumerable<Wait> P8_SeekAirLowTank(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            IntVec3 outside = BuildDryRoomWithDoor(out CellRect interior);
            yield return Updates(1);
            ctx.Require(Level(outside) >= FloodingMath.SubmergedLevelStanding, $"water outside the door ({Level(outside):P0})");
            Pawn pawn = SpawnUndrafted(outside);
            Apparel suit = (Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit));
            pawn.apparel.Wear(suit, dropReplacedApparel: false);
            CompDivingAir air = CompDivingAir.WornBy(pawn);
            ctx.Require(air != null, "diving suit worn");

            // 滿瓶：在水裡照常做事，不去找空氣
            yield return new WaitTicks(120);
            ctx.Check(!Seeking(pawn), "a full tank does not send the diver back");

            // 剩 30%：游回乾處補氣，補滿才離開。滿瓶那段時間沒徵召的人可能自己走開了，先放回水裡
            ctx.Note($"after the full-tank wait: at {pawn.Position}, level {Level(pawn.Position):P0}, job {pawn.CurJobDef?.defName}");
            if (!FloodingPawnEffects.IsSubmerged(pawn, Level(pawn.Position)))
            {
                pawn.Position = outside;
                pawn.Notify_Teleported();
            }
            air.TryConsume((int)(air.Props.capacityTicks * 0.7f));
            ctx.Require(air.AirPercent < FloodingMath.SeekAirBelowPercent, $"tank is low ({air.AirPercent:P0})");
            WaitUntil started = new WaitUntil(() => Seeking(pawn), 300);
            yield return started;
            ctx.Check(!started.TimedOut, "a diver with a low tank seeks air");
            if (started.TimedOut)
            {
                ctx.Note($"diagnostics: at {pawn.Position}, level {Level(pawn.Position):P0}, submerged {FloodingPawnEffects.IsSubmerged(pawn, Level(pawn.Position))}, " +
                    $"air {air.AirPercent:P0}, job {pawn.CurJobDef?.defName}, awake {pawn.Awake()}, drafted {pawn.Drafted}, inDanger {SeekAirUtility.InDanger(pawn, DepthRimFixture.Flooding)}");
                yield break;
            }
            ctx.Check(interior.Contains(pawn.CurJob.targetA.Cell), "target is inside the dry room");
            float low = air.AirPercent;
            WaitUntil done = new WaitUntil(() => !Seeking(pawn), JobDriver_SeekAir.MaxRefillTicks + 1500);
            yield return done;
            ctx.Check(!done.TimedOut, "the seek-air job ends");
            ctx.Check(air.AirPercent >= FloodingMath.SeekAirRefilledPercent, $"tank refilled before leaving ({low:P0} -> {air.AirPercent:P0})");
            ctx.Check(Severity(pawn, DepthRimDefOf.DepthRim_Drowning) == 0f, "never drowned");
        }

        public static IEnumerable<Wait> P9_SleeperEvacuates(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            Map map = DepthRimFixture.Ocean;
            // 11×11 艙室，中間一道牆隔成兩間，牆上一扇門：西邊睡覺，東邊乾燥
            CellRect ring = DepthRimFixture.ClearOceanArea(11);
            DepthRimFixture.BuildWallRing(ring);
            int divX = ring.minX + 5;
            IntVec3 doorCell = new IntVec3(divX, 0, ring.CenterCell.z);
            for (int z = ring.minZ + 1; z < ring.maxZ; z++)
            {
                IntVec3 c = new IntVec3(divX, 0, z);
                if (c == doorCell) { DepthRimFixture.Spawn(ThingDefOf.Door, c, map, ThingDefOf.Steel); }
                else { DepthRimFixture.Spawn(ThingDefOf.Wall, c, map, ThingDefOf.Steel); }
            }
            CellRect west = CellRect.FromLimits(ring.minX + 1, ring.minZ + 1, divX - 1, ring.maxZ - 1);
            CellRect east = CellRect.FromLimits(divX + 1, ring.minZ + 1, ring.maxX - 1, ring.maxZ - 1);
            SetLevel(west.Cells, 0f);
            SetLevel(east.Cells, 0f);
            IntVec3 bedCell = new IntVec3(west.minX + 1, 0, west.CenterCell.z);
            Building_Bed bed = (Building_Bed)DepthRimFixture.Spawn(ThingDefOf.Bed, bedCell, map, ThingDefOf.Steel);
            Pawn pawn = SpawnUndrafted(bed.Position);
            ctx.Check(RestUtility.IsValidBedFor(bed, pawn, pawn, checkSocialProperness: false), "a dry bed is a valid bed");
            pawn.needs.rest.CurLevel = 0.05f;
            pawn.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, bed), JobCondition.InterruptForced);
            WaitUntil asleep = new WaitUntil(() => !pawn.Awake(), 600);
            yield return asleep;
            ctx.Require(!asleep.TimedOut, "colonist fell asleep in bed");

            // 西艙淹到 25%：還沒蓋過躺著的人（30%），但已經算淹水，應該叫醒
            SetLevel(west.Cells, 0.25f);
            ctx.Check(!RestUtility.IsValidBedFor(bed, pawn, pawn, checkSocialProperness: false), "a flooded bed is no longer a valid bed");
            WaitUntil woke = new WaitUntil(() => Seeking(pawn), Update * 2 + 10);
            yield return woke;
            ctx.Check(!woke.TimedOut, "the sleeper is woken and seeks a safe place");
            ctx.Check(pawn.Awake(), "the sleeper is awake");
            if (woke.TimedOut) { yield break; }
            IntVec3 target = pawn.CurJob.targetA.Cell;
            ctx.Check(east.Contains(target), $"target {target} is in the dry room");
            WaitUntil arrived = new WaitUntil(() => !Seeking(pawn), 1500);
            yield return arrived;
            ctx.Check(!arrived.TimedOut, "the job ends after arriving");
            ctx.Check(!west.Contains(pawn.Position) || pawn.Position == doorCell || Level(pawn.Position) < FloodingMath.SleepSafeLevel, $"left the flooded room (at {pawn.Position})");
            ctx.Check(Severity(pawn, DepthRimDefOf.DepthRim_Drowning) == 0f, "never drowned");

            // 一個全身潛水裝備、氣量充足的人可以睡在淹水的床上
            Apparel suit = (Apparel)ThingMaker.MakeThing(DepthRimFixture.DivingSuit, GenStuff.DefaultStuffFor(DepthRimFixture.DivingSuit));
            pawn.apparel.Wear(suit, dropReplacedApparel: false);
            ctx.Check(RestUtility.IsValidBedFor(bed, pawn, pawn, checkSocialProperness: false), "a diver with air may use the flooded bed");
        }
    }
}
