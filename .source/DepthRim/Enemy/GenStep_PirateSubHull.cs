using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace DepthRim
{
    /// <summary>
    /// 海盜潛艇的船體地圖（§11 M7 7.3）：在深淵上鋪一塊船體平台，耐壓牆圍成三個艙室，艙壁之間用水密門相通，
    /// 全部蓋屋頂，所以三個艙室都是密閉的；水位初始化時設成乾的（<see cref="MapComponent_Flooding.MarkDry"/>）。
    /// 船員穿潛水服守在船上（<see cref="LordJob_DefendPoint"/>），最後一個艙室放戰利品。
    /// </summary>
    public class GenStep_PirateSubHull : GenStep
    {
        public IntVec2 hullSize = new IntVec2(23, 11);

        public int compartments = 3;

        public override int SeedPart => 730114233;

        public static CellRect HullRect(Map map, IntVec2 size)
        {
            return CellRect.CenteredOn(map.Center, size.x, size.z);
        }

        public override void Generate(Map map, GenStepParams parms)
        {
            Faction faction = map.ParentFaction ?? Find.FactionManager.RandomEnemyFaction();
            CellRect hull = HullRect(map, hullSize);
            TerrainDef platform = DefDatabase<TerrainDef>.GetNamed("DepthRim_HullPlatform");
            ThingDef wallDef = DefDatabase<ThingDef>.GetNamed("DepthRim_PressureHullWall");
            ThingDef doorDef = DefDatabase<ThingDef>.GetNamed("DepthRim_WatertightDoor");
            ThingDef wallStuff = wallDef.MadeFromStuff ? GenStuff.DefaultStuffFor(wallDef) : null;
            ThingDef doorStuff = doorDef.MadeFromStuff ? GenStuff.DefaultStuffFor(doorDef) : null;

            foreach (IntVec3 c in hull)
            {
                map.terrainGrid.SetTerrain(c, platform);
            }

            // 艙壁的 x 位置：等分內部
            CellRect inner = hull.ContractedBy(1);
            List<int> bulkheads = new List<int>();
            for (int i = 1; i < compartments; i++)
            {
                bulkheads.Add(inner.minX + Mathf.RoundToInt(inner.Width * i / (float)compartments));
            }
            int doorZ = inner.CenterCell.z;

            void Place(ThingDef def, ThingDef stuff, IntVec3 c)
            {
                Thing t = ThingMaker.MakeThing(def, stuff);
                t.SetFaction(faction);
                GenSpawn.Spawn(t, c, map, WipeMode.Vanish);
            }

            foreach (IntVec3 c in hull.EdgeCells)
            {
                Place(wallDef, wallStuff, c);
            }
            foreach (int x in bulkheads)
            {
                for (int z = inner.minZ; z <= inner.maxZ; z++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (z == doorZ)
                    {
                        Place(doorDef, doorStuff, c);
                    }
                    else
                    {
                        Place(wallDef, wallStuff, c);
                    }
                }
            }
            foreach (IntVec3 c in hull)
            {
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            }

            List<IntVec3> interior = inner.Cells.Where(c => !bulkheads.Contains(c.x)).ToList();
            map.GetComponent<MapComponent_Flooding>()?.MarkDry(hull.Cells);

            // 戰利品：最後一個艙室
            int lootMinX = bulkheads.Count > 0 ? bulkheads.Last() + 1 : inner.minX;
            List<IntVec3> lootCells = interior.Where(c => c.x >= lootMinX).ToList();
            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = Rand.RangeInclusive(250, 600);
            GenPlace.TryPlaceThing(silver, lootCells.RandomElement(), map, ThingPlaceMode.Near);
            Thing parts = ThingMaker.MakeThing(ThingDefOf.ComponentIndustrial);
            parts.stackCount = Rand.RangeInclusive(3, 8);
            GenPlace.TryPlaceThing(parts, lootCells.RandomElement(), map, ThingPlaceMode.Near);

            // 船員
            float points = (map.Parent as WorldObject_PirateSubmarine)?.crewPoints ?? 500f;
            if (faction == null)
            {
                return;
            }
            points = Mathf.Max(points, faction.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat) * 1.05f);
            List<Pawn> crew = PawnGroupMakerUtility.GeneratePawns(new PawnGroupMakerParms
            {
                groupKind = PawnGroupKindDefOf.Combat,
                faction = faction,
                points = points,
                tile = map.Tile,
            }).ToList();
            foreach (Pawn pawn in crew)
            {
                MarineXenotypes.TryAssignCrew(pawn);
                BoardingUtility.GiveDivingGear(pawn);
                IntVec3 cell = interior.Where(c => c.Standable(map) && c.GetFirstPawn(map) == null).RandomElementWithFallback(inner.CenterCell);
                GenSpawn.Spawn(pawn, cell, map);
            }
            if (crew.Count > 0)
            {
                LordMaker.MakeNewLord(faction, new LordJob_DefendPoint(inner.CenterCell), map, crew);
            }
        }
    }
}
