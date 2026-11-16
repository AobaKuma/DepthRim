using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using RimWorld;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>水中射程（2026-11-16）：泡在水裡開火射程 -75%，排乾的艙室不受影響；水下步槍不受限。</summary>
    public static class Tests_UnderwaterRange
    {
        private static void SetLevel(IEnumerable<IntVec3> cells, float level)
        {
            Map map = DepthRimFixture.Ocean;
            byte[] arr = HarmonyLib.Traverse.Create(DepthRimFixture.Flooding).Field("levels").GetValue<byte[]>();
            byte b = FloodingMath.ToByte(level);
            foreach (IntVec3 c in cells) { arr[map.cellIndices.CellToIndex(c)] = b; }
        }

        private static Pawn Armed(IntVec3 cell, ThingDef gun)
        {
            Pawn pawn = DepthRimFixture.SpawnColonist(DepthRimFixture.Ocean, cell);
            pawn.equipment.DestroyAllEquipment();
            pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gun, gun.MadeFromStuff ? GenStuff.DefaultStuffFor(gun) : null));
            return pawn;
        }

        private static float Range(Pawn pawn) => pawn.equipment.PrimaryEq.PrimaryVerb.EffectiveRange;

        public static IEnumerable<Wait> P19_UnderwaterRange(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            Map map = DepthRimFixture.Ocean;
            ThingDef assault = DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle");
            ThingDef underwater = DefDatabase<ThingDef>.GetNamedSilentFail("DepthRim_Gun_UnderwaterRifle");
            ctx.Require(underwater != null, "the underwater rifle def is loaded");
            ctx.Check(DefDatabase<RecipeDef>.AllDefs.Any(r => r.ProducedThingDef == underwater), "the underwater rifle can be crafted");

            CellRect ring = DepthRimFixture.ClearOceanArea(9);
            DepthRimFixture.BuildWallRing(ring);
            CellRect interior = ring.ContractedBy(1);
            SetLevel(interior.Cells, 0f);
            IntVec3 wet = ring.ExpandedBy(2).CenterCell + new IntVec3(0, 0, -6);
            SetLevel(new[] { wet, wet + IntVec3.East }, 1f);
            yield return new WaitTicks(2);

            Pawn dryAssault = Armed(interior.CenterCell, assault);
            Pawn dryUnderwater = Armed(interior.CenterCell + IntVec3.East, underwater);
            Pawn wetAssault = Armed(wet, assault);
            Pawn wetUnderwater = Armed(wet + IntVec3.East, underwater);
            ctx.Require(DepthRimFixture.Flooding.LevelAt(wetAssault.Position) >= FloodingMath.SubmergedLevelStanding, $"the wet shooter is submerged ({DepthRimFixture.Flooding.LevelAt(wetAssault.Position):P0})");
            ctx.Require(DepthRimFixture.Flooding.LevelAt(dryAssault.Position) < FloodingMath.SubmergedLevelStanding, "the compartment shooter is dry");

            float full = assault.Verbs[0].range;
            ctx.Check(Mathf.Approximately(Range(dryAssault), full), $"assault rifle in a drained compartment keeps full range ({Range(dryAssault)})");
            ctx.Check(Mathf.Approximately(Range(wetAssault), full * FloodingMath.UnderwaterRangeFactor), $"assault rifle underwater loses 75% ({Range(wetAssault)} of {full})");
            ctx.Check(Mathf.Approximately(Range(wetUnderwater), Range(dryUnderwater)), $"underwater rifle keeps its range underwater ({Range(wetUnderwater)})");
            ctx.Check(Range(dryUnderwater) < full && Range(wetUnderwater) > Range(wetAssault), $"underwater rifle: shorter than an assault rifle in air, longer underwater ({Range(dryUnderwater)})");
            ctx.Check(underwater.graphic != null && underwater.graphic.MatSingle.mainTexture != BaseContent.BadTex, "the underwater rifle texture loads");

            foreach (Pawn p in new[] { dryAssault, dryUnderwater, wetAssault, wetUnderwater }) { p.Destroy(); }
        }

        /// <summary>水下步槍貼圖（斜放、drawSize）和原版突擊步槍並排：徵召持槍、瞄準、掉在地上，截圖給人看。</summary>
        public static IEnumerable<Wait> P20_UnderwaterRifleLook(TestContext ctx)
        {
            Map map = DepthRimFixture.Home;
            ThingDef assault = DefDatabase<ThingDef>.GetNamed("Gun_AssaultRifle");
            ThingDef underwater = DefDatabase<ThingDef>.GetNamed("DepthRim_Gun_UnderwaterRifle");
            IntVec3 center = map.Center;
            CellRect area = CellRect.CenteredOn(center, 6);
            foreach (IntVec3 c in area.Cells)
            {
                foreach (Thing t in c.GetThingList(map).ToList()) { if (!(t is Pawn) && t.def.destroyable) { t.Destroy(); } }
                map.roofGrid.SetRoof(c, null);
            }
            map.weatherManager.TransitionTo(WeatherDefOf.Clear);
            List<Thing> spawned = new List<Thing>();
            Pawn Holder(IntVec3 cell, ThingDef gun, Rot4 rot)
            {
                Pawn pawn = DepthRimFixture.SpawnColonist(map, cell);
                pawn.equipment.DestroyAllEquipment();
                pawn.equipment.AddEquipment((ThingWithComps)ThingMaker.MakeThing(gun));
                pawn.drafter.Drafted = true;
                pawn.Rotation = rot;
                spawned.Add(pawn);
                return pawn;
            }
            // 上排：徵召持槍（四個方向）；左邊原版、右邊水下步槍
            Rot4[] rots = { Rot4.South, Rot4.East, Rot4.West, Rot4.North };
            for (int i = 0; i < 4; i++)
            {
                Holder(center + new IntVec3(-4 + i, 0, 2), assault, rots[i]);
                Holder(center + new IntVec3(1 + i, 0, 2), underwater, rots[i]);
            }
            // 中排：瞄準右上方
            Pawn target = DepthRimFixture.SpawnColonist(map, center + new IntVec3(6, 0, 5));
            spawned.Add(target);
            foreach (Pawn aimer in new[] { Holder(center + new IntVec3(-2, 0, 0), assault, Rot4.East), Holder(center + new IntVec3(2, 0, 0), underwater, Rot4.East) })
            {
                aimer.stances.SetStance(new Stance_Warmup(100000, target, aimer.equipment.PrimaryEq.PrimaryVerb));
            }
            // 下排：掉在地上
            spawned.Add(GenSpawn.Spawn(ThingMaker.MakeThing(assault), center + new IntVec3(-2, 0, -2), map));
            spawned.Add(GenSpawn.Spawn(ThingMaker.MakeThing(underwater), center + new IntVec3(2, 0, -2), map));
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            // 換地圖後相機會重設縮放：先跳過去等幾幀，再設縮放截圖
            CameraJumper.TryJump(center, map);
            yield return new WaitFrames(30);
            foreach (Wait w in ctx.ScreenshotOn(map, center, 6f, "rifle_look")) { yield return w; }
            yield return new WaitFrames(30);
            ctx.Check(underwater.graphic.MatSingle.mainTexture != BaseContent.BadTex, "the underwater rifle texture loads");
            foreach (Thing t in spawned) { if (!t.Destroyed) { t.Destroy(); } }
        }
    }
}
