using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>箱網警報（§8.4）的共用：列出所有地圖上符合條件的玩家箱網。</summary>
    public abstract class Alert_NetCage : Alert
    {
        private readonly List<Thing> culprits = new List<Thing>();

        protected abstract bool Matches(CompNetCage cage);

        protected List<Thing> Culprits
        {
            get
            {
                culprits.Clear();
                if (!MapComponent_Aquaculture.Enabled)
                {
                    return culprits;
                }
                List<Map> maps = Find.Maps;
                for (int m = 0; m < maps.Count; m++)
                {
                    MapComponent_Aquaculture comp = MapComponent_Aquaculture.For(maps[m]);
                    if (comp == null) { continue; }
                    foreach (Building_NetCage cage in comp.Cages)
                    {
                        if (cage.Spawned && cage.Faction == Faction.OfPlayer && Matches(cage.Cage))
                        {
                            culprits.Add(cage);
                        }
                    }
                }
                return culprits;
            }
        }

        public override AlertReport GetReport()
        {
            return AlertReport.CulpritsAre(Culprits);
        }
    }

    /// <summary>溶氧偏低：&lt; 0.5 黃色；&lt; 0.35 持續 1 小時改成紅色。</summary>
    public class Alert_CageHypoxia : Alert_NetCage
    {
        public Alert_CageHypoxia()
        {
            defaultLabel = "DepthRim.Alert.CageHypoxia".Translate();
            defaultExplanation = "DepthRim.Alert.CageHypoxiaDesc".Translate();
        }

        protected override bool Matches(CompNetCage c) => c.HasFish && c.Oxygen < NetCageMath.OxygenWarning;

        public override AlertReport GetReport()
        {
            List<Thing> list = Culprits;
            bool critical = false;
            foreach (Thing t in list)
            {
                if (((Building_NetCage)t).Cage.LowOxygenTicks >= 2500) { critical = true; }
            }
            defaultPriority = critical ? AlertPriority.Critical : AlertPriority.Medium;
            return AlertReport.CulpritsAre(list);
        }
    }

    public class Alert_CageNetBreach : Alert_NetCage
    {
        public Alert_CageNetBreach()
        {
            defaultLabel = "DepthRim.Alert.CageNetBreach".Translate();
            defaultExplanation = "DepthRim.Alert.CageNetBreachDesc".Translate();
            defaultPriority = AlertPriority.High;
        }

        protected override bool Matches(CompNetCage c) => c.Integrity < NetCageMath.BreachIntegrity;
    }

    public class Alert_CageNoFeed : Alert_NetCage
    {
        public Alert_CageNoFeed()
        {
            defaultLabel = "DepthRim.Alert.CageNoFeed".Translate();
            defaultExplanation = "DepthRim.Alert.CageNoFeedDesc".Translate();
        }

        protected override bool Matches(CompNetCage c) => c.HasFish && c.FeedStore <= 0f;
    }

    public class Alert_CageDisease : Alert_NetCage
    {
        public Alert_CageDisease()
        {
            defaultLabel = "DepthRim.Alert.CageDisease".Translate();
            defaultExplanation = "DepthRim.Alert.CageDiseaseDesc".Translate();
        }

        protected override bool Matches(CompNetCage c) => c.HasFish && c.DiseaseSeverity > 0.2f;
    }

    /// <summary>可收成超過 2 天還沒收（藍色、低優先）。</summary>
    public class Alert_CageReady : Alert_NetCage
    {
        public Alert_CageReady()
        {
            defaultLabel = "DepthRim.Alert.CageReady".Translate();
            defaultExplanation = "DepthRim.Alert.CageReadyDesc".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        protected override bool Matches(CompNetCage c) => c.Ready && !c.HarvestOrdered && c.DaysAtHarvestWeight >= 2f;
    }
}
