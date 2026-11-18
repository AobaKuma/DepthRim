using System.Collections.Generic;
using System.Linq;
using Fortified;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>處理一個遭遇型地標時的狀況：誰在場、誰擔任主角、收穫放哪裡、出事時找哪張地圖。</summary>
    public class LandmarkContext
    {
        public OceanLandmarkDef def;

        public List<Pawn> crew = new List<Pawn>();

        public Pawn actor;

        /// <summary>乘員的家（突擊艇的出發地或潛艇本身的地圖）：襲擊之類的後果落在這裡。</summary>
        public Map homeMap;

        /// <summary>收穫：物品與加入的人。由呼叫端放進艇裡或潛艇地圖上。</summary>
        public List<Thing> cargo = new List<Thing>();
    }

    /// <summary>地標選項的一項結果。<see cref="Apply"/> 回傳一行給玩家看的說明（沒有就回傳 null）。</summary>
    public abstract class LandmarkOutcome
    {
        public abstract string Apply(LandmarkContext ctx);
    }

    /// <summary>得到物品（放進 <see cref="LandmarkContext.cargo"/>）。</summary>
    public class LandmarkOutcome_Things : LandmarkOutcome
    {
        public List<ThingDefCountRangeClass> things = new List<ThingDefCountRangeClass>();

        public override string Apply(LandmarkContext ctx)
        {
            List<string> got = new List<string>();
            foreach (ThingDefCountRangeClass entry in things)
            {
                int count = entry.countRange.RandomInRange;
                if (entry.thingDef == null || count <= 0)
                {
                    continue;
                }
                int left = count;
                while (left > 0)
                {
                    Thing t = ThingMaker.MakeThing(entry.thingDef, GenStuff.DefaultStuffFor(entry.thingDef));
                    t.stackCount = Mathf.Min(left, entry.thingDef.stackLimit);
                    left -= t.stackCount;
                    ctx.cargo.Add(t);
                }
                got.Add(entry.thingDef.label + " x" + count);
            }
            return got.Count == 0 ? null : "DepthRim.Landmark.GotThings".Translate(string.Join(", ", got)).ToString();
        }
    }

    /// <summary>海底研究點數（FFF 知識類別）。</summary>
    public class LandmarkOutcome_Knowledge : LandmarkOutcome
    {
        public KnowledgeCategoryDef category;

        public FloatRange amount = new FloatRange(200f, 400f);

        public override string Apply(LandmarkContext ctx)
        {
            if (category == null)
            {
                return null;
            }
            float points = Mathf.Round(amount.RandomInRange * Find.Storyteller.difficulty.researchSpeedFactor);
            KnowledgeUtility.AddKnowledge(category, points);
            return "DepthRim.Landmark.GotKnowledge".Translate(points.ToString("0"), category.LabelCap).ToString();
        }
    }

    /// <summary>主角受傷（直接加傷口，乘員不在地圖上也能用）。</summary>
    public class LandmarkOutcome_Injury : LandmarkOutcome
    {
        public DamageDef damage;

        public FloatRange amount = new FloatRange(4f, 10f);

        public override string Apply(LandmarkContext ctx)
        {
            Pawn pawn = ctx.actor;
            DamageDef dmg = damage ?? DamageDefOf.Cut;
            if (pawn == null || pawn.Dead || pawn.health == null)
            {
                return null;
            }
            float severity = amount.RandomInRange;
            List<BodyPartRecord> parts = pawn.health.hediffSet.GetNotMissingParts(BodyPartHeight.Undefined, BodyPartDepth.Outside)
                .Where(p => p.def.GetMaxHealth(pawn) > severity + 1f && !p.def.tags.Contains(BodyPartTagDefOf.ConsciousnessSource)).ToList();
            if (!parts.TryRandomElement(out BodyPartRecord part))
            {
                return null;
            }
            HediffDef hediffDef = HealthUtility.GetHediffDefFromDamage(dmg, pawn, part);
            if (hediffDef == null)
            {
                return null;
            }
            Hediff_Injury injury = (Hediff_Injury)HediffMaker.MakeHediff(hediffDef, pawn, part);
            injury.Severity = severity;
            pawn.health.AddHediff(injury, part);
            return "DepthRim.Landmark.Injured".Translate(pawn.LabelShortCap, injury.LabelCap, part.LabelCap).ToString();
        }
    }

    /// <summary>救起一個人，加入殖民地。</summary>
    public class LandmarkOutcome_Survivor : LandmarkOutcome
    {
        public PawnKindDef kind;

        public override string Apply(LandmarkContext ctx)
        {
            PawnGenerationRequest request = new PawnGenerationRequest(kind ?? PawnKindDefOf.Colonist, Faction.OfPlayer, forceGenerateNewPawn: true);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            ctx.cargo.Add(pawn);
            return "DepthRim.Landmark.Survivor".Translate(pawn.LabelShortCap).ToString();
        }
    }

    /// <summary>
    /// 引來襲擊：乘員的家（海洋地圖）在 <see cref="delayHours"/> 小時後遭到 <see cref="incident"/>。家不在海洋上時什麼都不會發生。
    /// </summary>
    public class LandmarkOutcome_Incident : LandmarkOutcome
    {
        public IncidentDef incident;

        public FloatRange delayHours = new FloatRange(2f, 6f);

        public float pointsFactor = 1f;

        public override string Apply(LandmarkContext ctx)
        {
            Map map = ctx.homeMap;
            if (incident == null || map == null || map.Parent == null || !OceanRouting.IsOcean(map.Tile))
            {
                return null;
            }
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(incident.category, map);
            parms.forced = true;
            parms.points *= pointsFactor;
            if (!incident.Worker.CanFireNow(parms))
            {
                return null;
            }
            int delay = Mathf.RoundToInt(delayHours.RandomInRange * GenDate.TicksPerHour);
            Find.Storyteller.incidentQueue.Add(new QueuedIncident(new FiringIncident(incident, null, parms), Find.TickManager.TicksGame + delay));
            return "DepthRim.Landmark.Attracted".Translate(map.Parent.LabelCap).ToString();
        }
    }
}
