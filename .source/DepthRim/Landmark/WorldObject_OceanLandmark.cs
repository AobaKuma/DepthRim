using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>聲納發現的地標共用的部分：是哪一種、何時消失、沒分類前只是「未知聲納接觸」。</summary>
    public interface IOceanLandmark
    {
        OceanLandmarkDef LandmarkDef { get; }
    }

    public static class OceanLandmarkUtility
    {
        public static IEnumerable<WorldObject> AllLandmarks =>
            Find.WorldObjects.AllWorldObjects.Where(o => o is IOceanLandmark && !o.Destroyed);

        public static WorldObject_OceanLandmark EncounterAt(PlanetTile tile) =>
            Find.WorldObjects.AllWorldObjects.OfType<WorldObject_OceanLandmark>().FirstOrDefault(o => !o.Destroyed && o.Tile == tile);

        /// <summary>有玩家的突擊艇正開往這裡：地標先不消失。</summary>
        public static bool Targeted(WorldObject landmark) =>
            Find.WorldObjects.AllWorldObjects.OfType<WorldObject_TravelingBoardingCraft>().Any(t => !t.Destroyed && t.to == landmark.Tile);

        public static string Label(WorldObject o, OceanLandmarkDef def) =>
            SonarContacts.Classified(o) || def == null ? (def?.LabelCap.ToString() ?? o.def.label) : "DepthRim.Sonar.UnknownContact".Translate().ToString();

        public static string Inspect(WorldObject o, OceanLandmarkDef def, int expireTick)
        {
            List<string> lines = new List<string>();
            if (def != null && SonarContacts.Classified(o))
            {
                lines.Add((def.kind == OceanLandmarkKind.Site ? "DepthRim.Landmark.KindSite" : "DepthRim.Landmark.KindEncounter").Translate());
                if (def.kind == OceanLandmarkKind.Encounter && def.options.Any())
                {
                    lines.Add("DepthRim.Landmark.Skills".Translate(string.Join(", ", def.options.Select(op => op.skill?.LabelCap.ToString()).Where(s => s != null).Distinct())));
                }
            }
            else
            {
                lines.Add("DepthRim.Landmark.Unclassified".Translate());
            }
            if (expireTick > 0)
            {
                lines.Add("DepthRim.Landmark.FadesIn".Translate((expireTick - Find.TickManager.TicksGame).ToStringTicksToPeriod()));
            }
            return string.Join("\n", lines);
        }
    }

    /// <summary>遭遇型地標：不是地圖，抵達時用對話處理（<see cref="LandmarkEncounter"/>）。</summary>
    public class WorldObject_OceanLandmark : WorldObject, IOceanLandmark
    {
        public OceanLandmarkDef landmarkDef;

        public int expireTick = -1;

        public OceanLandmarkDef LandmarkDef => landmarkDef;

        public override string Label => OceanLandmarkUtility.Label(this, landmarkDef);

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            string extra = OceanLandmarkUtility.Inspect(this, landmarkDef, expireTick);
            return text.NullOrEmpty() ? extra : text + "\n" + extra;
        }

        public override string GetDescription() => SonarContacts.Classified(this) && landmarkDef != null ? landmarkDef.description : base.GetDescription();

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (expireTick > 0 && Find.TickManager.TicksGame >= expireTick && !OceanLandmarkUtility.Targeted(this))
            {
                Destroy();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref landmarkDef, "landmarkDef");
            Scribe_Values.Look(ref expireTick, "expireTick", -1);
        }
    }

    /// <summary>
    /// 地點型地標：突擊艇抵達時生成水下地圖（地圖生成器寫在 WorldObjectDef 上）。
    /// 沒生成地圖前會到期消失；地圖照 <see cref="WorldObject_OceanSite"/> 的規則保留，玩家離開、地圖移除時地標也一併移除。
    /// </summary>
    public class WorldObject_LandmarkSite : WorldObject_OceanSite, IOceanLandmark
    {
        public OceanLandmarkDef landmarkDef;

        public int expireTick = -1;

        public bool visited;

        public OceanLandmarkDef LandmarkDef => landmarkDef;

        public IntVec3 MapSize => landmarkDef?.siteMapSize ?? Find.World.info.initialMapSize;

        public override string Label => OceanLandmarkUtility.Label(this, landmarkDef);

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            string extra = OceanLandmarkUtility.Inspect(this, landmarkDef, HasMap ? -1 : expireTick);
            return text.NullOrEmpty() ? extra : text + "\n" + extra;
        }

        public override string GetDescription() => SonarContacts.Classified(this) && landmarkDef != null ? landmarkDef.description : base.GetDescription();

        public override void PostMapGenerate()
        {
            base.PostMapGenerate();
            visited = true;
        }

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            bool remove = base.ShouldRemoveMapNow(out alsoRemoveWorldObject);
            if (remove)
            {
                alsoRemoveWorldObject = true;
            }
            return remove;
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (!HasMap && !visited && expireTick > 0 && Find.TickManager.TicksGame >= expireTick && !OceanLandmarkUtility.Targeted(this))
            {
                Destroy();
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref landmarkDef, "landmarkDef");
            Scribe_Values.Look(ref expireTick, "expireTick", -1);
            Scribe_Values.Look(ref visited, "visited", false);
        }
    }
}
