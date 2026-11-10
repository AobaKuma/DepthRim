using System.Collections.Generic;
using System.Linq;
using DMSE;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace DepthRim
{
    /// <summary>海盜潛艇的設定（掛在 WorldObjectDef 上）。</summary>
    public class PirateSubmarineExtension : DefModExtension
    {
        /// <summary>船殼耐久：沒有地圖時被魚雷打，歸零沉沒。</summary>
        public int hullPoints = 400;

        /// <summary>巡弋時離最近的玩家海洋地圖的海路距離（格）。</summary>
        public IntRange stalkDistance = new IntRange(3, 9);

        /// <summary>每移動一格的時間（tick）。</summary>
        public int ticksPerTile = 2500;

        /// <summary>船員點數（生成地圖時）；實際用 max(這個, 事件點數)。</summary>
        public float crewPoints = 500f;

        public IntVec3 mapSize = new IntVec3(75, 1, 75);
    }

    /// <summary>
    /// 海盜潛艇（設計稿 §11 M7）：海洋層上會移動的敵方 MapParent。
    /// 沒有地圖時：在玩家海洋地圖附近的海路上巡弋，用 DMSE 導彈陣地元件發射魚雷，用 DMSE 抽象防空攔截來襲的魚雷；
    /// 被魚雷打中扣船殼（<see cref="MissileBehavior_Torpedo"/> 的 PreArrive），歸零就沉沒。
    /// 玩家突擊艇跳幫時生成船體地圖（<see cref="GenStep_PirateSubHull"/>）；停在原地直到地圖移除。
    /// 船員清空後，玩家離開時潛艇被鑿沉。
    /// </summary>
    public class WorldObject_PirateSubmarine : MapParent, IDepthTierProvider
    {
        public int hullPoints = -1;

        public DepthTier depth = DepthTier.Shallow;

        public int nextMoveTick = -1;

        public float crewPoints = 500f;

        /// <summary>地圖生成的時間：生成後一段時間內不移除（突擊艇還在降落、船員還沒登記）。</summary>
        public int mapGeneratedTick = -1;

        public const int MapGraceTicks = 2500;

        public PirateSubmarineExtension Ext => def.GetModExtension<PirateSubmarineExtension>() ?? new PirateSubmarineExtension();

        public int MaxHull => Ext.hullPoints;

        public DepthTier CurrentDepthTier => depth;

        public override void PostAdd()
        {
            base.PostAdd();
            if (hullPoints < 0)
            {
                hullPoints = MaxHull;
            }
            if (nextMoveTick < 0)
            {
                nextMoveTick = Find.TickManager.TicksGame + Ext.ticksPerTile;
            }
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (HasMap || Destroyed)
            {
                return;
            }
            if (Find.TickManager.TicksGame >= nextMoveTick)
            {
                nextMoveTick = Find.TickManager.TicksGame + Ext.ticksPerTile;
                Patrol();
            }
        }

        /// <summary>巡弋一格：往可通行的鄰格走，盡量保持在玩家海洋地圖的海路距離範圍內，不離開海洋。</summary>
        public void Patrol()
        {
            if (HasMap)
            {
                return;
            }
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(Tile, neighbors);
            List<MapParent> targets = EnemyTorpedoBoatUtility.PlayerOceanTargets().ToList();
            IntRange band = Ext.stalkDistance;
            int Score(PlanetTile t)
            {
                if (targets.Count == 0)
                {
                    return 0;
                }
                int d = targets.Min(m => OceanRouting.Distance(m.Tile, t));
                return d < band.min ? band.min - d : d > band.max ? d - band.max : 0;
            }
            List<PlanetTile> options = neighbors.Where(t => OceanRouting.Navigable(t) && !Find.WorldObjects.AnyMapParentAt(t)).ToList();
            if (options.Count == 0)
            {
                return;
            }
            int best = options.Min(Score);
            Tile = options.Where(t => Score(t) == best).RandomElement();
            depth = DiveMath.Effective(depth, DepthTierUtility.MaxTierAt(Tile));
        }

        /// <summary>被魚雷打中（沒有地圖時）：扣船殼，歸零沉沒。回傳是否沉沒。</summary>
        public bool TakeTorpedoHit(int damage)
        {
            hullPoints -= Mathf.Max(1, damage);
            if (hullPoints > 0)
            {
                Messages.Message("DepthRim.Pirate.SubHit".Translate(LabelCap, hullPoints, MaxHull), this, MessageTypeDefOf.PositiveEvent);
                return false;
            }
            Find.LetterStack.ReceiveLetter("DepthRim.Pirate.SubSunkLabel".Translate(), "DepthRim.Pirate.SubSunk".Translate(Faction?.Name ?? ""),
                LetterDefOf.PositiveEvent, new GlobalTargetInfo(Tile));
            Destroy();
            return true;
        }

        public override MapGeneratorDef MapGeneratorDef => def.mapGenerator;

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = false;
            if (!HasMap || Map.mapPawns.AnyPawnBlockingMapRemoval
                || (mapGeneratedTick >= 0 && Find.TickManager.TicksGame < mapGeneratedTick + MapGraceTicks))
            {
                return false;
            }
            // 船員清空：玩家離開時鑿沉
            alsoRemoveWorldObject = !Map.mapPawns.AllPawnsSpawned.Any(p => p.Faction == Faction && !p.Dead && !p.Downed);
            return true;
        }

        public override void PostMapGenerate()
        {
            base.PostMapGenerate();
            mapGeneratedTick = Find.TickManager.TicksGame;
            Find.LetterStack.ReceiveLetter("DepthRim.Pirate.BoardedLabel".Translate(), "DepthRim.Pirate.Boarded".Translate(LabelCap),
                LetterDefOf.ThreatBig, new LookTargets(Map.Center, Map));
        }

        public override string Label => SonarContacts.Classified(this) ? base.Label : "DepthRim.Sonar.UnknownContact".Translate().ToString();

        public override string GetInspectString()
        {
            if (!SonarContacts.Classified(this))
            {
                return "DepthRim.Sonar.UnknownInspect".Translate();
            }
            string text = base.GetInspectString();
            string line = "DepthRim.Pirate.SubInspect".Translate(hullPoints, MaxHull, depth.Label());
            return text.NullOrEmpty() ? line : line + "\n" + text;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref hullPoints, "hullPoints", -1);
            Scribe_Values.Look(ref depth, "depth", DepthTier.Shallow);
            Scribe_Values.Look(ref nextMoveTick, "nextMoveTick", -1);
            Scribe_Values.Look(ref crewPoints, "crewPoints", 500f);
            Scribe_Values.Look(ref mapGeneratedTick, "mapGeneratedTick", -1);
        }
    }

    /// <summary>
    /// DMSE 的 NPC 世界節點要玩家研究了雷達網才建立（<c>WorldComponent_FactionAirDefenseAlert.Enabled</c>）。
    /// 海盜潛艇的抽象防空是海洋層的玩法核心，不跟著那個研究：每次重建節點後，自己把還沒在清單裡的海盜潛艇加進去。
    /// </summary>
    [HarmonyPatch(typeof(WorldComponent_BVRNetwork), "RebuildNodes")]
    public static class Patch_PirateSubmarineNodes
    {
        private static readonly AccessTools.FieldRef<WorldComponent_BVRNetwork, List<BVRNode>> Nodes =
            AccessTools.FieldRefAccess<WorldComponent_BVRNetwork, List<BVRNode>>("nodes");

        [HarmonyPostfix]
        public static void Postfix(WorldComponent_BVRNetwork __instance)
        {
            List<BVRNode> nodes = Nodes(__instance);
            foreach (WorldObject o in Find.WorldObjects.AllWorldObjects)
            {
                if (!(o is WorldObject_PirateSubmarine sub) || sub.Destroyed)
                {
                    continue;
                }
                WorldObjectComp_AirDefense comp = sub.GetComponent<WorldObjectComp_AirDefense>();
                if (comp == null || !comp.Available || nodes.Any(n => n.parent == sub))
                {
                    continue;
                }
                nodes.Add(new BVRNode { parent = sub, faction = sub.Faction, defense = new WorldAirDefenseAdapter(comp) });
            }
        }
    }

    /// <summary>
    /// 聲納目標分類（§11 M7 7.4）：玩家的聲納節點在偵測半徑一半以內聽到，才分得出是什麼；否則只是「未知聲納接觸」。
    /// </summary>
    public static class SonarContacts
    {
        public const float ClassifyFraction = 0.5f;

        /// <summary>開發／測試：強制全部已分類。</summary>
        public static bool debugAllClassified;

        public static bool Classified(WorldObject o)
        {
            if (debugAllClassified || o == null || !o.Tile.Valid)
            {
                return true;
            }
            WorldComponent_BVRNetwork network = WorldComponent_BVRNetwork.Current;
            if (network == null)
            {
                return false;
            }
            Vector3 at = MissileWorldUtility.TileCenter(o.Tile);
            foreach (BVRNode node in network.Nodes)
            {
                if (node.faction != Faction.OfPlayer || node.parent == null)
                {
                    continue;
                }
                float range = node.DetectionRange(BVRTargetProps.Default);
                if (range > 0f && MissileWorldUtility.SurfaceTilesBetween(node.Center, at) <= range * ClassifyFraction)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public class PirateSubmarineIncidentExtension : DefModExtension
    {
        public WorldObjectDef worldObjectDef;

        public IntRange seaDistance = new IntRange(5, 9);

        public int maxActive = 2;

        public ResearchProjectDef requiredResearch;
    }

    /// <summary>海盜潛艇出現在玩家海洋地圖附近（先是一個未知的聲納接觸）。</summary>
    public class IncidentWorker_PirateSubmarine : IncidentWorker
    {
        private PirateSubmarineIncidentExtension Ext => def.GetModExtension<PirateSubmarineIncidentExtension>();

        private static int ActiveCount() => Find.WorldObjects.AllWorldObjects.Count(o => o is WorldObject_PirateSubmarine && !o.Destroyed);

        protected override bool CanFireNowSub(IncidentParms parms)
        {
            PirateSubmarineIncidentExtension ext = Ext;
            if (ext?.worldObjectDef == null || (ext.requiredResearch != null && !ext.requiredResearch.IsFinished))
            {
                return false;
            }
            return ActiveCount() < ext.maxActive && EnemyTorpedoBoatUtility.TryFindFaction(out _) && EnemyTorpedoBoatUtility.PlayerOceanTargets().Any();
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            PirateSubmarineIncidentExtension ext = Ext;
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
                WorldObject_PirateSubmarine sub = PirateSubmarineUtility.Spawn(ext.worldObjectDef, tile, faction, parms.points);
                SendStandardLetter(def.letterLabel, def.letterText.Formatted(target.LabelCap), def.letterDef ?? LetterDefOf.ThreatBig, parms, sub);
                return true;
            }
            return false;
        }
    }

    public static class PirateSubmarineUtility
    {
        public static WorldObject_PirateSubmarine Spawn(WorldObjectDef def, PlanetTile tile, Faction faction, float points)
        {
            WorldObject_PirateSubmarine sub = (WorldObject_PirateSubmarine)WorldObjectMaker.MakeWorldObject(def);
            sub.Tile = tile;
            sub.SetFaction(faction);
            sub.depth = (DepthTier)Rand.RangeInclusive(0, (int)DepthTierUtility.MaxTierAt(tile));
            sub.crewPoints = Mathf.Max(sub.Ext.crewPoints, points);
            Find.WorldObjects.Add(sub);
            return sub;
        }
    }
}
