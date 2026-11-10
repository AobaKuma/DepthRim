using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 魚群（§11 M7 7.4）：無害的聲納接觸。遠處聽起來和潛艇一樣是「未知聲納接觸」，靠近到聲納分得出來才知道是魚群。
    /// 每小時漂一格，幾天後消失。
    /// </summary>
    public class WorldObject_FishSchool : WorldObject
    {
        public const int TicksPerTile = 2500;

        public int nextMoveTick = -1;

        public int expireTick = -1;

        public override void PostAdd()
        {
            base.PostAdd();
            int now = Find.TickManager.TicksGame;
            if (nextMoveTick < 0)
            {
                nextMoveTick = now + TicksPerTile;
            }
            if (expireTick < 0)
            {
                expireTick = now + Rand.RangeInclusive(2, 5) * GenDate.TicksPerDay;
            }
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            int now = Find.TickManager.TicksGame;
            if (now >= expireTick)
            {
                Destroy();
                return;
            }
            if (now < nextMoveTick)
            {
                return;
            }
            nextMoveTick = now + TicksPerTile;
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(Tile, neighbors);
            if (neighbors.Where(OceanRouting.Navigable).TryRandomElement(out PlanetTile next))
            {
                Tile = next;
            }
        }

        public override string Label => SonarContacts.Classified(this) ? base.Label : "DepthRim.Sonar.UnknownContact".Translate().ToString();

        public override string GetInspectString()
        {
            return SonarContacts.Classified(this) ? "DepthRim.Sonar.FishInspect".Translate().ToString() : "DepthRim.Sonar.UnknownInspect".Translate().ToString();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref nextMoveTick, "nextMoveTick", -1);
            Scribe_Values.Look(ref expireTick, "expireTick", -1);
        }
    }

    /// <summary>魚群出現在玩家海洋地圖附近（沒有信件：它就是一個聲納上的接觸）。</summary>
    public class IncidentWorker_FishSchool : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            return def.GetModExtension<PirateSubmarineIncidentExtension>()?.worldObjectDef != null && EnemyTorpedoBoatUtility.PlayerOceanTargets().Any();
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            PirateSubmarineIncidentExtension ext = def.GetModExtension<PirateSubmarineIncidentExtension>();
            foreach (MapParent target in EnemyTorpedoBoatUtility.PlayerOceanTargets().InRandomOrder())
            {
                if (EnemyTorpedoBoatUtility.TryFindTile(target.Tile, ext.seaDistance, out PlanetTile tile))
                {
                    WorldObject school = WorldObjectMaker.MakeWorldObject(ext.worldObjectDef);
                    school.Tile = tile;
                    Find.WorldObjects.Add(school);
                    return true;
                }
            }
            return false;
        }
    }
}
