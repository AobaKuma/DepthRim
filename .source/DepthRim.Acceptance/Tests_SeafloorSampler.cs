using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>海底採樣器（2026-11-17）：2×1，緊貼海底火山或熱液泉，通電時把地標的研究點數加進目前的研究專案，每個地標有總量上限。</summary>
    public static class Tests_SeafloorSampler
    {
        /// <summary>
        /// D1：生成一張中洋脊地圖（有海底火山），另外放一個熱液泉。放置限制：火山旁可以、空地與地表不行、壓在地標上不行；
        /// 沒電不提取、通電後研究專案進度增加、火山的剩餘量同步減少；採完後停止並換到旁邊另一個地標；沒有研究專案時待機。
        /// </summary>
        public static IEnumerable<Wait> D1_SeafloorSampler(TestContext ctx)
        {
            DepthRimFixture.EnsureOcean();
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("DR_SeafloorSampler");
            ctx.Require(def != null && def.size == new IntVec2(2, 1), "the seafloor sampler is loaded and 2x1");
            CompProperties_SeafloorSampler props = def.GetCompProperties<CompProperties_SeafloorSampler>();
            ctx.Require(props != null, "it has the sampler comp");
            ThingDef volcanoDef = DefDatabase<ThingDef>.GetNamed("DR_UnderwaterVolcano");
            float volcanoTotal = props.SourceFor(volcanoDef).totalPoints;
            float ventTotal = props.SourceFor(ThingDefOf.SteamGeyser).totalPoints;

            BiomeDef biome = DefDatabase<BiomeDef>.GetNamed("DepthRim_MidOceanRidge");
            PlanetTile tile = Tests_Ocean.FreeTileOf(DepthRimFixture.OceanLayer, biome, out BiomeDef original);
            ctx.Require(tile.Valid, "found a mid-ocean ridge tile");
            MapParent site = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
            site.Tile = tile;
            site.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(site);
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(tile, new IntVec3(100, 1, 100), DepthRimDefOf.DepthRim_OceanSite);
            ctx.Require(map != null, "map generated");
            // 海洋地點在沒有玩家建築時會移除地圖：先放一段電線留住
            DepthRimFixture.Spawn(ThingDefOf.PowerConduit, map.Center, map, faction: Faction.OfPlayer);
            yield return new WaitFrames(2);

            Thing volcano = map.AllCells.SelectMany(c => map.thingGrid.ThingsListAtFast(c)).FirstOrDefault(t => t.def == volcanoDef);
            ctx.Require(volcano != null, "the ridge has an underwater volcano");
            CellRect vr = volcano.OccupiedRect();
            // 火山東側緊貼的 2×1（朝北：佔 x、x+1）；周圍清成可蓋的碎石地，熱液泉稍後放在北邊
            IntVec3 east = new IntVec3(vr.maxX + 1, 0, vr.CenterCell.z);
            foreach (IntVec3 c in GenAdj.OccupiedRect(east, Rot4.North, def.size).ExpandedBy(3))
            {
                if (!c.InBounds(map) || vr.Contains(c)) { continue; }
                map.terrainGrid.SetTerrain(c, TerrainDefOf.Gravel);
                foreach (Thing t in c.GetThingList(map).ToList())
                {
                    if (t != volcano && (t.def.category == ThingCategory.Building || t.def.category == ThingCategory.Plant)) { t.Destroy(); }
                }
            }
            ctx.Check(GenConstruct.CanPlaceBlueprintAt(def, east, Rot4.North, map, godMode: true).Accepted, "a sampler can be placed right next to the volcano");
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(def, vr.CenterCell, Rot4.North, map, godMode: true).Accepted, "not on top of the volcano");
            IntVec3 far = map.AllCells.FirstOrDefault(c => c.Standable(map) && c.DistanceTo(volcano.Position) > 12f
                && GenAdj.OccupiedRect(c, Rot4.North, def.size).ExpandedBy(2).InBounds(map)
                && GenAdj.OccupiedRect(c, Rot4.North, def.size).ExpandedBy(1).Cells.All(x => !x.GetThingList(map).Any(t => props.SourceFor(t.def) != null)));
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(def, far, Rot4.North, map, godMode: true).Accepted, $"not out on the open sea floor away from any site ({far})");
            Map home = DepthRimFixture.Home;
            ctx.Check(!GenConstruct.CanPlaceBlueprintAt(def, home.Center, Rot4.North, home, godMode: true).Accepted, "not on a surface map");

            // 海底研究（FFF 知識類別）：完成初始科技後，耐壓工程是分頁裡第一個能推進的專案
            KnowledgeCategoryDef seabed = DefDatabase<KnowledgeCategoryDef>.GetNamed("DepthRim_Seabed");
            ctx.Check(props.knowledgeCategory == seabed, "the sampler files its points as seabed research");
            ResearchProjectDef survival = DefDatabase<ResearchProjectDef>.GetNamed("DepthRim_UnderwaterSurvival");
            ResearchProjectDef proj = DefDatabase<ResearchProjectDef>.GetNamed("DepthRim_PressureEngineering");
            ctx.Check(proj.knowledgeCategory == seabed && proj.baseCost <= 0f && proj.knowledgeCost > 0f, $"pressure engineering costs seabed research ({proj.knowledgeCost})");
            ctx.Check(survival.knowledgeCategory == null && survival.baseCost > 0f, "the starting tech is ordinary research");
            ctx.Check(def.researchPrerequisites.Contains(survival), "the sampler is unlocked by the starting tech");
            if (!survival.IsFinished) { Find.ResearchManager.FinishProject(survival, doCompletionLetter: false); }
            ctx.Check(!proj.IsFinished && proj.CanStartNow, "pressure engineering can be started");
            if (ModsConfig.AnomalyActive) { Find.ResearchManager.SetCurrentProject(proj); }
            ctx.Note($"Anomaly active: {ModsConfig.AnomalyActive}");
            float before = Find.ResearchManager.GetProgress(proj);

            Thing sampler = DepthRimFixture.Spawn(def, east, map, faction: Faction.OfPlayer);
            CompSeafloorSampler comp = sampler.TryGetComp<CompSeafloorSampler>();
            CompPowerTrader power = sampler.TryGetComp<CompPowerTrader>();
            ctx.Require(comp != null && power != null, "the sampler spawned with its comps");
            ctx.Check(comp.Source == volcano, "it found the volcano as its source");
            ctx.Check(comp.SourceRemaining == volcanoTotal, $"the volcano starts full ({comp.SourceRemaining}/{volcanoTotal})");

            power.PowerOn = false;
            yield return new WaitTicks(CompSeafloorSampler.Interval * 2 + 5);
            ctx.Check(Find.ResearchManager.GetProgress(proj) == before, "nothing is extracted without power");

            power.PowerOn = true;
            yield return new WaitTicks(CompSeafloorSampler.Interval * 4 + 5);
            float gained = Find.ResearchManager.GetProgress(proj) - before;
            float speed = Find.Storyteller.difficulty.researchSpeedFactor;
            float perRare = props.pointsPerDay * CompSeafloorSampler.Interval / GenDate.TicksPerDay;
            ctx.Check(gained > perRare * speed * 2f, $"powered, seabed research goes into {proj.defName} ({gained:0.00}, about {perRare * speed:0.00} per rare tick)");
            ctx.Check(System.Math.Abs((volcanoTotal - comp.SourceRemaining) * speed - gained) < 0.05f, $"the volcano lost what the project gained ({volcanoTotal - comp.SourceRemaining:0.00} drawn)");
            ctx.Check(power.PowerOutput == -power.Props.PowerConsumption, $"full power draw while sampling ({power.PowerOutput} W)");
            ctx.Note(sampler.GetInspectString());

            // 快採完：只給剩下的，之後停止
            MapComponent_SeafloorResearch ledger = map.GetComponent<MapComponent_SeafloorResearch>();
            ledger.Draw(volcano, comp.SourceRemaining - 0.25f);
            float p0 = Find.ResearchManager.GetProgress(proj);
            comp.Extract(CompSeafloorSampler.Interval);
            ctx.Check(System.Math.Abs(Find.ResearchManager.GetProgress(proj) - p0 - 0.25f * speed) < 0.01f, "a nearly exhausted volcano gives only what is left");
            ctx.Check(comp.SourceRemaining == 0f, "the volcano is exhausted");
            float p1 = Find.ResearchManager.GetProgress(proj);
            comp.Extract(CompSeafloorSampler.Interval);
            ctx.Check(Find.ResearchManager.GetProgress(proj) == p1, "an exhausted volcano gives nothing more");

            // 旁邊放一個熱液泉（貼著採樣器北側）：下一次 rare tick 換過去
            IntVec3 ventCell = east + new IntVec3(0, 0, 1);
            Thing vent = GenSpawn.Spawn(ThingMaker.MakeThing(ThingDefOf.SteamGeyser), ventCell, map, WipeMode.Vanish);
            yield return new WaitTicks(CompSeafloorSampler.Interval + 5);
            ctx.Check(comp.Source == vent, $"with the volcano used up, it moves on to the adjacent vent ({comp.Source?.def.defName})");
            ctx.Check(comp.SourceRemaining < ventTotal && comp.SourceRemaining > ventTotal - perRare * 2f, $"and samples the vent ({comp.SourceRemaining:0.00}/{ventTotal})");

            ctx.Check(Find.ResearchManager.GetProgress(proj) > p1, "the vent's samples also go into seabed research");

            // 沒有可推進的海底研究（有 Anomaly 時取消選取；沒有 Anomaly 時 FFF 自動灌，無法取消）：待機電力、不扣地標
            if (ModsConfig.AnomalyActive)
            {
                Find.ResearchManager.StopProject(proj);
                ctx.Check(!comp.HasResearchTarget, "with no seabed project selected there is nothing to research");
                float ventLeft = comp.SourceRemaining;
                yield return new WaitTicks(CompSeafloorSampler.Interval + 5);
                ctx.Check(comp.SourceRemaining == ventLeft, "with no project the vent is not drawn on");
                ctx.Check(power.PowerOutput == -power.Props.idlePowerDraw, $"and the sampler idles ({power.PowerOutput} W)");
            }
            else
            {
                ctx.Note("no Anomaly: FFF injects into any open seabed project, so the no-project idle check is skipped");
            }

            // 開局卡關檢查：升降井到得了的極地冰蓋也有冷泉，旁邊蓋得了採樣器
            BiomeDef polar = DefDatabase<BiomeDef>.GetNamed("DepthRim_PolarIceCap");
            PlanetTile polarTile = Tests_Ocean.FreeTileOf(DepthRimFixture.OceanLayer, polar, out BiomeDef polarOriginal);
            MapParent polarSite = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
            polarSite.Tile = polarTile;
            polarSite.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(polarSite);
            Map polarMap = GetOrGenerateMapUtility.GetOrGenerateMap(polarTile, new IntVec3(100, 1, 100), DepthRimDefOf.DepthRim_OceanSite);
            DepthRimFixture.Spawn(ThingDefOf.PowerConduit, polarMap.Center, polarMap, faction: Faction.OfPlayer);
            yield return new WaitFrames(2);
            List<Thing> seeps = polarMap.AllCells.SelectMany(c => polarMap.thingGrid.ThingsListAtFast(c)).Distinct().Where(t => t.def.defName == "DR_ColdSeep").ToList();
            ctx.Check(seeps.Count >= 1, $"the polar ice cap (reachable by ice shaft) has cold seeps ({seeps.Count})");
            Thing seep = seeps.FirstOrDefault();
            if (seep != null)
            {
                CellRect sr = seep.OccupiedRect();
                bool placeable = false;
                foreach (IntVec3 c in sr.ExpandedBy(2).EdgeCells)
                {
                    foreach (Rot4 r in new[] { Rot4.North, Rot4.East })
                    {
                        if (GenConstruct.CanPlaceBlueprintAt(def, c, r, polarMap, godMode: true).Accepted) { placeable = true; break; }
                    }
                    if (placeable) { break; }
                }
                ctx.Check(placeable, "a sampler can be placed next to a cold seep");
                ctx.Check(CompSeafloorSampler.FindSource(def, new IntVec3(sr.maxX + 1, 0, sr.minZ), Rot4.North, polarMap, props) == seep, "a sampler beside the seep finds it as its source");
            }
            Current.Game.CurrentMap = home;
            Current.Game.DeinitAndRemoveMap(polarMap, false);
            if (!polarSite.Destroyed) { polarSite.Destroy(); }
            if (polarOriginal != null) { polarTile.Tile.PrimaryBiome = polarOriginal; }

            Current.Game.CurrentMap = home;
            Current.Game.DeinitAndRemoveMap(map, false);
            if (!site.Destroyed) { site.Destroy(); }
            if (original != null) { tile.Tile.PrimaryBiome = original; }
            yield return new WaitFrames(5);
        }
    }
}
