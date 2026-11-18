using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>聲納地標（Docs/聲納地標與穿梭艇探索_機制設計.md）。</summary>
    public static class Tests_Landmark
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        private static Pawn MakeColonist(SkillDef skill, int level)
        {
            Pawn p = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true,
                mustBeCapableOfViolence: false));
            foreach (SkillRecord s in p.skills.skills)
            {
                s.Level = 0;
            }
            SkillRecord r = p.skills.GetSkill(skill);
            r.Level = level;
            return p;
        }

        /// <summary>
        /// LM1：
        /// 一、地標放在海路到得了的空海洋格；沒分類前顯示「未知聲納接觸」；有突擊艇開往時不會到期消失。
        /// 二、技能判定：挑技能最高的人擔任主角；大成功給物品、失敗讓主角受傷，都給經驗值。
        /// 三、突擊艇開到遭遇型地標：暫停並跳出對話（不下艇、不開地圖），選項處理完收穫進艇、地標消失、艇開回出發地降落。
        /// 四、突擊艇開到沉船：開出 100×100 的沉船地圖，海床上有鋼牆船殼。
        /// </summary>
        public static IEnumerable<Wait> LM1_Landmarks(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            OceanLandmarkDef pod = DefDatabase<OceanLandmarkDef>.GetNamed("DR_Landmark_SupplyPod");
            OceanLandmarkDef wreck = DefDatabase<OceanLandmarkDef>.GetNamed("DR_Landmark_Shipwreck");
            Dictionary<PlanetTile, BiomeDef> biomeBefore = Tests_TorpedoDefense.OceanRings(map.Tile, 3);
            OceanRouting.ClearCache();
            List<Pawn> made = new List<Pawn>();
            Building_BoardingCraft craft = null;
            WorldObject_LandmarkSite site = null;
            try
            {
                // 一、放置與分類
                ctx.Require(WorldComponent_OceanLandmarks.CandidateTiles(map.Tile, 4f).Count > 0, "there are free ocean tiles within 4 tiles");
                ctx.Require(WorldComponent_OceanLandmarks.TryPlace(map.Tile, 4f, pod, out WorldObject placed), "a supply pod landmark was placed");
                WorldObject_OceanLandmark landmark = (WorldObject_OceanLandmark)placed;
                ctx.Check(landmark.Tile != map.Tile && OceanRouting.Navigable(landmark.Tile), "the landmark sits on another navigable ocean tile");
                ctx.Check(landmark.expireTick > Find.TickManager.TicksGame, "the landmark will fade from sonar eventually");
                SonarContacts.debugAllClassified = false;
                ctx.Check(landmark.Label == "DepthRim.Sonar.UnknownContact".Translate().ToString(), $"without sonar close by it is an unknown contact ({landmark.Label})");
                SonarContacts.debugAllClassified = true;
                ctx.Check(landmark.Label == pod.LabelCap.ToString(), $"once classified it shows what it is ({landmark.Label})");
                ctx.Check(Building_BoardingCraft.DestinationBlockReason(map.Tile, landmark.Tile) == null, "a boarding craft may head for the landmark");

                // 二、技能判定
                Pawn builder = MakeColonist(SkillDefOf.Construction, 18);
                Pawn thinker = MakeColonist(SkillDefOf.Intellectual, 3);
                made.Add(builder);
                made.Add(thinker);
                List<Pawn> crew = new List<Pawn> { thinker, builder };
                LandmarkOption pry = pod.options[0];
                ctx.Check(LandmarkEncounter.ChooseActor(crew, pry.skill) == builder, "the best builder leads the pry option");
                SkillRecord construction = builder.skills.GetSkill(SkillDefOf.Construction);
                float xpBefore = construction.XpTotalEarned + construction.xpSinceLastLevel;
                LandmarkResolution great = LandmarkEncounter.Resolve(pod, pry, crew, map, 0f);
                ctx.Check(great.result == LandmarkResult.Critical && great.actor == builder, $"roll 0 is a great success by the builder ({great.result})");
                ctx.Check(great.cargo.Any(t => t.def == ThingDefOf.Steel), "a great success recovers steel");
                ctx.Check(construction.XpTotalEarned + construction.xpSinceLastLevel > xpBefore, "the leader gains construction xp");
                int injuries = builder.health.hediffSet.hediffs.OfType<Hediff_Injury>().Count();
                LandmarkResolution bad = LandmarkEncounter.Resolve(pod, pry, crew, map, 0.999f);
                ctx.Check(bad.result == LandmarkResult.Failure, "roll 0.999 fails");
                ctx.Check(builder.health.hediffSet.hediffs.OfType<Hediff_Injury>().Count() > injuries, "failing to pry the hatch injures the leader");
                ctx.Check(!bad.text.NullOrEmpty(), "the result has text for the dialog");
                ctx.Note(great.text.Replace('\n', ' '));

                // 三、突擊艇處理遭遇型地標
                IntVec3 open = DepthRimFixture.OpenWaterCell();
                ctx.Require(open.IsValid, "found an open-water cell for the craft");
                craft = (Building_BoardingCraft)ThingMaker.MakeThing(BoardingDefOf.DepthRim_PlayerBoardingCraft, GenStuff.DefaultStuffFor(BoardingDefOf.DepthRim_PlayerBoardingCraft));
                craft.SetFaction(Faction.OfPlayer);
                GenSpawn.Spawn(craft, open, map, WipeMode.Vanish);
                yield return new WaitTicks(Building_BoardingCraft.EjectDelayTicks + 40);
                Pawn sailor = MakeColonist(SkillDefOf.Construction, 20);
                made.Add(sailor);
                craft.Load(sailor);
                PlanetTile landmarkTile = landmark.Tile;
                int legTiles = OceanRouting.Distance(map.Tile, landmarkTile);
                craft.Fuel.Refuel(craft.Fuel.Props.fuelCapacity);
                float fullTank = craft.Fuel.Fuel;
                ctx.Check(craft.FuelBlockReason(legTiles) == null && craft.FuelBlockReason(Building_BoardingCraft.RangeTiles + 1) != null,
                    "a full tank reaches the landmark and back but not beyond the craft's range");
                craft.Launch(landmarkTile);
                ctx.Check(UnityEngine.Mathf.Abs(craft.Fuel.Fuel - (fullTank - BoardingCraftFuel.OneWay(legTiles))) < 0.01f,
                    $"launching burns the outbound leg ({fullTank} -> {craft.Fuel.Fuel}, {legTiles} tiles)");
                WorldObject_TravelingBoardingCraft traveling = null;
                yield return new WaitUntil(() =>
                {
                    traveling = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_TravelingBoardingCraft>().FirstOrDefault();
                    return traveling != null;
                }, 1200);
                ctx.Require(traveling != null, "the craft left for the landmark");
                ctx.Check(traveling.home == map.Parent, "the craft remembers where it came from");
                LandmarkEncounter.lastShown = null;
                int arriveIn = traveling.arriveTick - Find.TickManager.TicksGame;
                yield return new WaitUntil(() => traveling.encounterPending || traveling.Destroyed, arriveIn + 600);
                ctx.Require(traveling.encounterPending && !traveling.Destroyed, "on arrival the craft stops for the encounter");
                ctx.Check(Find.WorldObjects.MapParentAt(landmarkTile) == null, "no map was generated for an encounter landmark");
                DiaNode root = LandmarkEncounter.lastShown;
                ctx.Require(root != null && root.options.Count == pod.options.Count + 1, $"the dialog offers every option plus leave ({root?.options.Count})");
                ctx.Check(root.options[0].action != null, "the first option can be chosen");
                root.options[0].action();
                ctx.Check(landmark.Destroyed, "dealing with the landmark removes it");
                ctx.Check(craft.Aboard.Any() && ((IThingHolder)craft).GetDirectlyHeldThings().Any(t => t.def == ThingDefOf.Steel), "the haul is loaded into the craft");
                DiaNode result = LandmarkEncounter.lastShown;
                ctx.Require(result != null && result != root && result.options.Count == 1, "a result page follows");
                result.options[0].action();
                foreach (Dialog_NodeTree w in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) { w.Close(false); }
                ctx.Check(!traveling.encounterPending && traveling.returning, "after the dialog the craft heads home");
                ctx.Check(traveling.to == map.Tile, $"the return course points at the home map ({traveling.to})");
                arriveIn = traveling.arriveTick - Find.TickManager.TicksGame;
                yield return new WaitUntil(() => traveling.Destroyed, arriveIn + 600);
                ctx.Require(traveling.Destroyed, "the craft made it back");
                yield return new WaitUntil(() => craft.Spawned && craft.Map == map && craft.ejected, 1500);
                ctx.Check(craft.Spawned && craft.Map == map, "the craft landed back on the home map");
                ctx.Check(sailor.Spawned && sailor.Map == map, "the crew came back");
                ctx.Check(UnityEngine.Mathf.Abs(craft.Fuel.Fuel - (fullTank - BoardingCraftFuel.RoundTrip(legTiles))) < 0.01f,
                    $"the trip home burnt the return leg ({craft.Fuel.Fuel} left)");
                ctx.Check(map.listerThings.ThingsOfDef(ThingDefOf.Steel).Any(t => t.Position.DistanceTo(craft.Position) < 10f), "the haul was unloaded at home");

                // 四、沉船地點
                yield return new WaitTicks(60);
                ctx.Require(WorldComponent_OceanLandmarks.TryPlace(map.Tile, 4f, wreck, out WorldObject wreckObject), "a shipwreck site was placed");
                site = (WorldObject_LandmarkSite)wreckObject;
                ctx.Check(!site.HasMap, "the wreck has no map before anyone visits");
                if (sailor.Spawned) { sailor.DeSpawn(); }
                craft.Load(sailor);
                craft.Launch(site.Tile);
                traveling = null;
                yield return new WaitUntil(() =>
                {
                    traveling = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_TravelingBoardingCraft>().FirstOrDefault();
                    return traveling != null;
                }, 1200);
                ctx.Require(traveling != null, "the craft left for the wreck");
                arriveIn = traveling.arriveTick - Find.TickManager.TicksGame;
                yield return new WaitUntil(() => traveling.Destroyed, arriveIn + 600);
                ctx.Require(site.HasMap, "arriving at the wreck generates its map");
                Map wreckMap = site.Map;
                ctx.Check(wreckMap.Size.x == 100 && wreckMap.Size.z == 100, $"the wreck map is 100x100 ({wreckMap.Size})");
                ctx.Check(site.visited, "the site is marked visited");
                CellRect hull = GenStep_Shipwreck.lastHull;
                int walls = hull.EdgeCells.Count(c => c.GetEdifice(wreckMap)?.def == ThingDefOf.Wall);
                ctx.Check(walls >= hull.EdgeCells.Count() / 2, $"the wreck hull is mostly standing steel walls ({walls}/{hull.EdgeCells.Count()} at {hull})");
                ctx.Check(hull.EdgeCells.Any(c => c.GetEdifice(wreckMap) == null), "the hull has a way in");
                yield return new WaitUntil(() => craft.Spawned && craft.Map == wreckMap && craft.ejected, 1500);
                ctx.Check(craft.Spawned && craft.Map == wreckMap, "the craft landed at the wreck");
                Current.Game.CurrentMap = wreckMap;
                CameraJumper.TryJump(new GlobalTargetInfo(hull.CenterCell, wreckMap));
                yield return new WaitTicks(10);
                ctx.Screenshot("LM1_shipwreck");
                yield return new WaitTicks(30);
            }
            finally
            {
                SonarContacts.debugAllClassified = false;
                LandmarkEncounter.lastShown = null;
                foreach (Dialog_NodeTree w in Find.WindowStack.Windows.OfType<Dialog_NodeTree>().ToList()) { w.Close(false); }
                foreach (WorldObject_TravelingBoardingCraft t in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_TravelingBoardingCraft>().ToList()) { t.Destroy(); }
                Current.Game.CurrentMap = map;
                if (site != null && site.HasMap) { Current.Game.DeinitAndRemoveMap(site.Map, false); }
                foreach (WorldObject o in OceanLandmarkUtility.AllLandmarks.ToList()) { o.Destroy(); }
                if (craft != null && craft.Spawned) { craft.Destroy(); }
                foreach (Pawn p in made)
                {
                    if (p.Spawned) { p.DeSpawn(); }
                    if (!p.Destroyed) { p.Destroy(); }
                }
                foreach (KeyValuePair<PlanetTile, BiomeDef> kv in biomeBefore) { kv.Key.Tile.PrimaryBiome = kv.Value; }
                OceanRouting.ClearCache();
            }
            yield return Updates(1);
        }
    }
}
