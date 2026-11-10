using System.Collections.Generic;
using System.Linq;
using DMSE;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 敵方魚雷艇（設計稿 §11 M5 5.7）：海洋層上沒有地圖的敵方世界物件，掛 DMSE 的導彈陣地元件
    /// （<see cref="WorldObjectComp_MissileBase"/>）定時對射程內的玩家海洋地圖發射魚雷。
    /// 魚雷只走海路、帶著艇的巡航深度；時間到了艇就離開（陣地轉移）。
    /// </summary>
    public class WorldObject_EnemyTorpedoBoat : WorldObject
    {
        /// <summary>巡航深度：不超過腳下海床。發射的魚雷帶著這個深度。</summary>
        public DepthTier depth = DepthTier.Shallow;

        public static WorldObject_EnemyTorpedoBoat At(PlanetTile tile)
        {
            List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is WorldObject_EnemyTorpedoBoat boat && !boat.Destroyed && boat.Tile == tile)
                {
                    return boat;
                }
            }
            return null;
        }

        public override string Label => SonarContacts.Classified(this) ? base.Label : "DepthRim.Sonar.UnknownContact".Translate().ToString();

        public override string GetInspectString()
        {
            if (!SonarContacts.Classified(this))
            {
                return "DepthRim.Sonar.UnknownInspect".Translate();
            }
            string text = base.GetInspectString();
            string line = "DepthRim.EnemyBoat.Depth".Translate(depth.Label());
            return text.NullOrEmpty() ? line : line + "\n" + text;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref depth, "depth", DepthTier.Shallow);
        }
    }

    /// <summary>掛在魚雷艇的 WorldObjectDef 上：每次發射隨機選一種導引頭（DMSE 的陣地只會用彈體的預設部件）。</summary>
    public class EnemyTorpedoLoadoutExtension : DefModExtension
    {
        public List<MissilePartDef> guidance = new List<MissilePartDef>();
    }

    [HarmonyPatch(typeof(WorldObjectComp_MissileBase), nameof(WorldObjectComp_MissileBase.BuildFireConfig))]
    public static class Patch_EnemyTorpedoLoadout
    {
        [HarmonyPostfix]
        public static void Postfix(WorldObjectComp_MissileBase __instance, MissileConfig __result)
        {
            EnemyTorpedoLoadoutExtension ext = __instance.parent?.def?.GetModExtension<EnemyTorpedoLoadoutExtension>();
            if (__result == null || ext == null || ext.guidance.NullOrEmpty())
            {
                return;
            }
            __result.SetPart(MissilePartCategory.Guidance, ext.guidance.RandomElement());
        }
    }

    /// <summary>
    /// 選址與生成：在玩家海洋地圖的海路 <see cref="EnemyTorpedoBoatIncidentExtension.seaDistance"/> 格內找一格沒人的海洋格。
    /// 獨立出來給事件與驗收測試共用。
    /// </summary>
    public static class EnemyTorpedoBoatUtility
    {
        /// <summary>玩家在海洋層上有地圖的據點（基地或潛艇）。</summary>
        public static IEnumerable<MapParent> PlayerOceanTargets()
        {
            foreach (Map map in Find.Maps)
            {
                MapParent parent = map.Parent;
                if (parent != null && !parent.Destroyed && parent.Faction == Faction.OfPlayer && OceanRouting.IsOcean(parent.Tile))
                {
                    yield return parent;
                }
            }
        }

        public static bool TryFindTile(PlanetTile target, IntRange seaDistance, out PlanetTile tile)
        {
            tile = PlanetTile.Invalid;
            if (!OceanRouting.IsOcean(target))
            {
                return false;
            }
            PlanetLayer layer = target.Layer;
            List<PlanetTile> candidates = new List<PlanetTile>();
            for (int i = 0; i < layer.TilesCount; i++)
            {
                PlanetTile t = new PlanetTile(i, layer);
                if (!OceanRouting.Navigable(t))
                {
                    continue;
                }
                int d = OceanRouting.Distance(target, t);
                if (d < seaDistance.min || d > seaDistance.max || Find.WorldObjects.AnyWorldObjectAt(t))
                {
                    continue;
                }
                candidates.Add(t);
            }
            return candidates.TryRandomElement(out tile);
        }

        public static bool TryFindFaction(out Faction faction)
        {
            return Find.FactionManager.AllFactionsListForReading
                .Where(f => !f.IsPlayer && !f.defeated && !f.temporary && !f.def.hidden && f.def.humanlikeFaction
                    && f.def.techLevel >= TechLevel.Industrial && f.HostileTo(Faction.OfPlayer))
                .TryRandomElement(out faction);
        }

        public static WorldObject_EnemyTorpedoBoat Spawn(WorldObjectDef def, PlanetTile tile, Faction faction)
        {
            WorldObject_EnemyTorpedoBoat boat = (WorldObject_EnemyTorpedoBoat)WorldObjectMaker.MakeWorldObject(def);
            boat.Tile = tile;
            boat.SetFaction(faction);
            boat.depth = (DepthTier)Rand.RangeInclusive(0, (int)DepthTierUtility.MaxTierAt(tile));
            Find.WorldObjects.Add(boat);
            return boat;
        }
    }

    public class EnemyTorpedoBoatIncidentExtension : DefModExtension
    {
        public WorldObjectDef worldObjectDef;

        /// <summary>離目標的海路距離（格）；上限要在魚雷射程內。</summary>
        public IntRange seaDistance = new IntRange(4, 8);

        /// <summary>世界上同時存在的魚雷艇上限。</summary>
        public int maxActive = 1;

        /// <summary>玩家完成這個研究後才會出現（手上已經有聲納與反制手段）。</summary>
        public ResearchProjectDef requiredResearch;
    }

    /// <summary>敵方魚雷艇出現在玩家海洋地圖附近，之後定時發射魚雷。</summary>
    public class IncidentWorker_EnemyTorpedoBoat : IncidentWorker
    {
        private EnemyTorpedoBoatIncidentExtension Ext => def.GetModExtension<EnemyTorpedoBoatIncidentExtension>();

        private static int ActiveCount()
            => Find.WorldObjects.AllWorldObjects.Count(o => o is WorldObject_EnemyTorpedoBoat && !o.Destroyed);

        protected override bool CanFireNowSub(IncidentParms parms)
        {
            EnemyTorpedoBoatIncidentExtension ext = Ext;
            if (ext?.worldObjectDef == null)
            {
                return false;
            }
            if (ext.requiredResearch != null && !ext.requiredResearch.IsFinished)
            {
                return false;
            }
            if (ActiveCount() >= ext.maxActive || !EnemyTorpedoBoatUtility.TryFindFaction(out _))
            {
                return false;
            }
            return EnemyTorpedoBoatUtility.PlayerOceanTargets().Any();
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            EnemyTorpedoBoatIncidentExtension ext = Ext;
            if (ext?.worldObjectDef == null || !EnemyTorpedoBoatUtility.TryFindFaction(out Faction faction))
            {
                return false;
            }
            foreach (MapParent target in EnemyTorpedoBoatUtility.PlayerOceanTargets().InRandomOrder())
            {
                if (!EnemyTorpedoBoatUtility.TryFindTile(target.Tile, ext.seaDistance, out PlanetTile tile))
                {
                    continue;
                }
                WorldObject_EnemyTorpedoBoat boat = EnemyTorpedoBoatUtility.Spawn(ext.worldObjectDef, tile, faction);
                int distance = OceanRouting.Distance(target.Tile, tile);
                SendStandardLetter(def.letterLabel,
                    def.letterText.Formatted(faction.NameColored, target.LabelCap, distance.ToString()),
                    def.letterDef ?? LetterDefOf.ThreatBig, parms, boat);
                return true;
            }
            return false;
        }
    }
}
