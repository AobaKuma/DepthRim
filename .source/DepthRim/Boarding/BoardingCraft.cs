using System.Collections.Generic;
using System.Linq;
using DMSE;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DepthRim
{
    [DefOf]
    public static class BoardingDefOf
    {
        public static ThingDef DepthRim_BoardingCraft;
        public static ThingDef DepthRim_PlayerBoardingCraft;
        public static ThingDef DepthRim_MoonPool;
        public static ThingDef DepthRim_BoardingCraftIncoming;
        public static ThingDef DepthRim_BoardingCraftLeaving;
        public static JobDef DepthRim_BoardCraft;
        public static JobDef DepthRim_CarryToCraft;
        public static DutyDef DepthRim_ReturnToCraft;
        public static RaidStrategyDef DepthRim_Boarding;
        public static PawnsArrivalModeDef DepthRim_BoardingArrival;
        public static WorldObjectDef DepthRim_TravelingBoardingCraft;

        static BoardingDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(BoardingDefOf));
        }
    }

    /// <summary>
    /// 跳幫突擊艇（設計稿 §6.2、§11 M6）：敵方襲擊者搭它來、搭它走；玩家也能在月池造一艘自己的（6.7）。
    /// 降落時不破壞屋頂（skyfaller 的 hitRoof 關掉），所以不會打破艙室的密閉；落地後放出艇上的人。
    /// 敵方：襲擊撤退時（<see cref="LordToil_ReturnToCraft"/>）存活的人回到艇上（可能扛著俘虜或戰利品），
    /// 全員上艇或等太久就離開。摧毀停著的艇，裡面的人和東西會掉出來。
    /// 玩家：殖民者右鍵登艇，按「出發」選一格海路到得了的海洋格，飛過去後一樣切入對方艙室。
    /// 艇上的人放在自己的容器（不用 CompTransporter：它在離場時會倒出內容物，也不能在 skyfaller 裡存非世界 pawn）。
    /// </summary>
    public class Building_BoardingCraft : Building, IThingHolder
    {
        public const int EjectDelayTicks = 120;

        public const int ReturnTimeoutTicks = 5000;

        /// <summary>玩家艇的航程（海路格數）。</summary>
        public const int RangeTiles = 8;

        private ThingOwner<Thing> aboard;

        /// <summary>這艘艇載來的人（含已經下艇的），撤退時等他們回來。</summary>
        public List<Pawn> raiders = new List<Pawn>();

        public int landedTick = -1;

        public bool ejected;

        public int returnRequestedTick = -1;

        /// <summary>玩家艇出發後的目的地（飛行中用）。</summary>
        public PlanetTile destinationTile = PlanetTile.Invalid;

        public Building_BoardingCraft()
        {
            aboard = new ThingOwner<Thing>(this, oneStackOnly: false);
        }

        public ThingOwner GetDirectlyHeldThings() => aboard;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public IEnumerable<Pawn> Aboard => aboard.OfType<Pawn>();

        public int AboardCount => aboard.Count(t => t is Pawn p && raiders.Contains(p));

        public bool ReturnRequested => returnRequestedTick >= 0;

        public bool IsPlayerCraft => Faction == Faction.OfPlayer;

        /// <summary>出發前裝人（還沒生成的襲擊者）。</summary>
        public void Load(Pawn pawn)
        {
            if (aboard.TryAdd(pawn) && !raiders.Contains(pawn))
            {
                raiders.Add(pawn);
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (!respawningAfterLoad && landedTick < 0)
            {
                landedTick = Find.TickManager.TicksGame;
            }
        }

        protected override void Tick()
        {
            base.Tick();
            if (!Spawned || !this.IsHashIntervalTick(30))
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (!ejected && landedTick >= 0 && now >= landedTick + EjectDelayTicks)
            {
                Eject();
            }
            if (!IsPlayerCraft && ReturnRequested && ejected && AboardCount > 0)
            {
                bool nobodyLeft = RaidersStillOut().Count == 0;
                if (nobodyLeft || now >= returnRequestedTick + ReturnTimeoutTicks)
                {
                    Launch(PlanetTile.Invalid);
                }
            }
        }

        /// <summary>還在外面、還能自己走回來的襲擊者。</summary>
        public List<Pawn> RaidersStillOut()
        {
            return raiders.Where(p => p != null && !p.Dead && !p.Downed && p.Spawned && p.Map == Map && !aboard.Contains(p)).ToList();
        }

        /// <summary>放出艇上的人（玩家艇也放出載的東西）。</summary>
        public void Eject()
        {
            ejected = true;
            aboard.TryDropAll(Position, Map, ThingPlaceMode.Near);
        }

        public void RequestReturn()
        {
            if (returnRequestedTick < 0)
            {
                returnRequestedTick = Find.TickManager.TicksGame;
            }
        }

        /// <summary>
        /// 登艇（<see cref="JobDriver_BoardCraft"/>）：襲擊者離開 lord；扛著的東西（俘虜、戰利品）一起收進艇裡。
        /// </summary>
        public void Board(Pawn pawn)
        {
            Thing carried = pawn.carryTracker?.CarriedThing;
            if (carried != null)
            {
                pawn.carryTracker.innerContainer.TryTransferToContainer(carried, aboard);
            }
            pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.ExitedMap);
            pawn.DeSpawnOrDeselect();
            if (!aboard.TryAdd(pawn))
            {
                GenSpawn.Spawn(pawn, Position, Map);
                return;
            }
            if (!raiders.Contains(pawn))
            {
                raiders.Add(pawn);
            }
        }

        /// <summary>
        /// 離開：換成向上飛走的 skyfaller。<paramref name="destination"/> 無效時（敵方撤退）飛出畫面就把艇上的人送回世界；
        /// 有效時（玩家艇）飛出畫面後在世界地圖上走海路過去。
        /// </summary>
        public void Launch(PlanetTile destination)
        {
            Map map = Map;
            IntVec3 pos = Position;
            destinationTile = destination;
            if (!destination.Valid)
            {
                Messages.Message("DepthRim.Boarding.CraftLeft".Translate(AboardCount), new TargetInfo(pos, map), MessageTypeDefOf.NeutralEvent);
            }
            landedTick = -1;
            ejected = false;
            returnRequestedTick = -1;
            DeSpawn(DestroyMode.WillReplace);
            Skyfaller leaving = SkyfallerMaker.MakeSkyfaller(BoardingDefOf.DepthRim_BoardingCraftLeaving, this);
            GenSpawn.Spawn(leaving, pos, map);
        }

        /// <summary>
        /// 敵方艇飛出地圖：襲擊者成為世界 pawn；扛走的殖民者成為該陣營的俘虜（原版的綁架追蹤），搶走的東西就沒了。
        /// </summary>
        public void SendAboardAway()
        {
            Pawn kidnapper = Aboard.FirstOrDefault(p => !p.Dead && p.Faction == Faction);
            int captives = 0;
            float loot = 0f;
            foreach (Thing t in aboard.ToList())
            {
                aboard.Remove(t);
                if (t is Pawn pawn)
                {
                    if (pawn.Dead || pawn.Discarded)
                    {
                        continue;
                    }
                    if (pawn.Faction == Faction.OfPlayer && Faction != null && Faction != Faction.OfPlayer && kidnapper != null)
                    {
                        Faction.kidnapped.Kidnap(pawn, kidnapper);
                        captives++;
                    }
                    else
                    {
                        Find.WorldPawns.PassToWorld(pawn);
                    }
                }
                else
                {
                    loot += t.MarketValue * t.stackCount;
                    t.Destroy();
                }
            }
            if (captives > 0 || loot > 0f)
            {
                Messages.Message("DepthRim.Boarding.TookAway".Translate(captives, loot.ToStringMoney()), MessageTypeDefOf.NegativeEvent);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            if (!IsPlayerCraft)
            {
                yield break;
            }
            int crew = aboard.Count(t => t is Pawn);
            Command_Action launch = new Command_Action
            {
                defaultLabel = "DepthRim.Boarding.Launch".Translate(),
                defaultDesc = "DepthRim.Boarding.LaunchDesc".Translate(RangeTiles),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchShip"),
                action = StartChoosingDestination,
            };
            if (crew == 0)
            {
                launch.Disable("DepthRim.Boarding.NoCrew".Translate());
            }
            else if (!OceanRouting.IsOcean(Map.Tile))
            {
                launch.Disable("DepthRim.Torpedo.SeaOnly".Translate());
            }
            yield return launch;
            if (aboard.Any)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DepthRim.Boarding.Unload".Translate(),
                    defaultDesc = "DepthRim.Boarding.UnloadDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel"),
                    action = Eject,
                };
            }
        }

        /// <summary>目的地檢查：海路到得了、在航程內的海洋格，而且那裡沒有不是據點的世界物件。回傳 null 表示可以。</summary>
        public static string DestinationBlockReason(PlanetTile from, PlanetTile to)
        {
            if (to == from)
            {
                return "DepthRim.Boarding.SameTile";
            }
            string key = TorpedoUtility.BlockReason(from, to, RangeTiles, out _);
            if (key != null)
            {
                return key;
            }
            WorldObject other = Find.WorldObjects.AllWorldObjects.FirstOrDefault(o => o.Tile == to && !(o is MapParent));
            return other != null ? "DepthRim.Boarding.Occupied" : null;
        }

        private void StartChoosingDestination()
        {
            PlanetTile from = Map.Tile;
            CameraJumper.TryJump(CameraJumper.GetWorldTarget(this));
            Find.WorldSelector.ClearSelection();
            Find.WorldTargeter.BeginTargeting(target =>
            {
                string reason = DestinationBlockReason(from, target.Tile);
                if (reason != null)
                {
                    Messages.Message(reason.Translate(RangeTiles), MessageTypeDefOf.RejectInput, historical: false);
                    return false;
                }
                Launch(target.Tile);
                return true;
            }, canTargetTiles: true, closeWorldTabWhenFinished: true,
            extraLabelGetter: target =>
            {
                string reason = DestinationBlockReason(from, target.Tile);
                return reason != null ? reason.Translate(RangeTiles).ToString()
                    : "DepthRim.Boarding.SeaDistance".Translate(OceanRouting.Distance(from, target.Tile)).ToString();
            });
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            IntVec3 pos = Position;
            if (mode != DestroyMode.Vanish && mode != DestroyMode.WillReplace && map != null)
            {
                aboard.TryDropAll(pos, map, ThingPlaceMode.Near);
            }
            base.Destroy(mode);
            aboard.ClearAndDestroyContents();
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            int crew = aboard.Count(t => t is Pawn);
            string line = ReturnRequested
                ? "DepthRim.Boarding.InspectWaiting".Translate(AboardCount, RaidersStillOut().Count)
                : "DepthRim.Boarding.InspectAboard".Translate(crew);
            return text.NullOrEmpty() ? line : text + "\n" + line;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref aboard, "aboard", this);
            Scribe_Collections.Look(ref raiders, "raiders", LookMode.Reference);
            Scribe_Values.Look(ref landedTick, "landedTick", -1);
            Scribe_Values.Look(ref ejected, "ejected", false);
            Scribe_Values.Look(ref returnRequestedTick, "returnRequestedTick", -1);
            Scribe_Values.Look(ref destinationTile, "destinationTile", PlanetTile.Invalid);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                raiders ??= new List<Pawn>();
                raiders.RemoveAll(p => p == null);
                aboard ??= new ThingOwner<Thing>(this, oneStackOnly: false);
            }
        }
    }

    /// <summary>離場的突擊艇：飛出畫面時，敵方艇把艇上的人送回世界；玩家艇變成世界地圖上的航跡。</summary>
    public class BoardingCraftLeaving : Skyfaller
    {
        protected override void LeaveMap()
        {
            Building_BoardingCraft craft = innerContainer.OfType<Building_BoardingCraft>().FirstOrDefault();
            if (craft != null && craft.destinationTile.Valid && Map != null)
            {
                innerContainer.Remove(craft);
                WorldObject_TravelingBoardingCraft traveling =
                    (WorldObject_TravelingBoardingCraft)WorldObjectMaker.MakeWorldObject(BoardingDefOf.DepthRim_TravelingBoardingCraft);
                traveling.Setup(craft, Map.Tile, craft.destinationTile);
                Find.WorldObjects.Add(traveling);
            }
            else
            {
                craft?.SendAboardAway();
            }
            base.LeaveMap();
        }
    }

    /// <summary>
    /// 玩家突擊艇在世界地圖上的航跡：沿海路前進，抵達後在目的地的地圖降落（沒有地圖就開一個海洋地點）。
    /// 降落點與敵方的突擊艇一樣：優先切入對方的密閉艙室，否則降在海床上。
    /// </summary>
    public class WorldObject_TravelingBoardingCraft : WorldObject, IThingHolder
    {
        public const int TicksPerTile = 250;

        private ThingOwner<Building_BoardingCraft> craftHolder;

        public PlanetTile from = PlanetTile.Invalid;

        public PlanetTile to = PlanetTile.Invalid;

        public int departTick;

        public int arriveTick;

        /// <summary>出發時目的地上的據點（例如會移動的海盜潛艇）：抵達時它走了就追過去。</summary>
        public MapParent targetParent;

        public WorldObject_TravelingBoardingCraft()
        {
            craftHolder = new ThingOwner<Building_BoardingCraft>(this);
        }

        public Building_BoardingCraft Craft => craftHolder.InnerListForReading.FirstOrDefault();

        public ThingOwner GetDirectlyHeldThings() => craftHolder;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public void Setup(Building_BoardingCraft craft, PlanetTile origin, PlanetTile destination)
        {
            craftHolder.TryAdd(craft);
            from = origin;
            to = destination;
            Tile = origin;
            SetFaction(craft.Faction);
            targetParent = Find.WorldObjects.MapParentAt(destination);
            SetCourse(origin, destination);
        }

        private void SetCourse(PlanetTile origin, PlanetTile destination)
        {
            from = origin;
            to = destination;
            departTick = Find.TickManager.TicksGame;
            int distance = Mathf.Max(1, OceanRouting.Distance(origin, destination));
            arriveTick = departTick + distance * TicksPerTile;
        }

        public float Progress => arriveTick <= departTick ? 1f
            : Mathf.Clamp01((float)(Find.TickManager.TicksGame - departTick) / (arriveTick - departTick));

        public override Vector3 DrawPos => Vector3.Slerp(Find.WorldGrid.GetTileCenter(from), Find.WorldGrid.GetTileCenter(to), Progress);

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (Find.TickManager.TicksGame < arriveTick)
            {
                return;
            }
            if (targetParent != null && !targetParent.Destroyed && targetParent.Tile != to && OceanRouting.Navigable(targetParent.Tile))
            {
                Tile = to;
                SetCourse(to, targetParent.Tile);
                return;
            }
            Arrive();
        }

        public void Arrive()
        {
            Building_BoardingCraft craft = Craft;
            if (craft == null)
            {
                Destroy();
                return;
            }
            craftHolder.Remove(craft);
            craft.destinationTile = PlanetTile.Invalid;
            MapParent parent = Find.WorldObjects.MapParentAt(to);
            Map map = parent?.Map;
            if (map == null)
            {
                if (parent == null)
                {
                    parent = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
                    parent.Tile = to;
                    parent.SetFaction(Faction.OfPlayer);
                    Find.WorldObjects.Add(parent);
                }
                IntVec3 size = parent is WorldObject_PirateSubmarine pirate ? pirate.Ext.mapSize : Find.World.info.initialMapSize;
                map = GetOrGenerateMapUtility.GetOrGenerateMap(to, size, parent.def);
            }
            Faction target = parent.Faction != null && parent.Faction != craft.Faction ? parent.Faction : null;
            if (!BoardingUtility.TryFindLandingCell(map, target ?? craft.Faction, out IntVec3 cell, out _))
            {
                cell = CellFinder.RandomCell(map);
            }
            SkyfallerMaker.SpawnSkyfaller(BoardingDefOf.DepthRim_BoardingCraftIncoming, craft, cell, map);
            Messages.Message("DepthRim.Boarding.Arrived".Translate(parent.LabelCap), new LookTargets(cell, map), MessageTypeDefOf.PositiveEvent);
            Destroy();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref craftHolder, "craft", this);
            Scribe_Values.Look(ref from, "from", PlanetTile.Invalid);
            Scribe_Values.Look(ref to, "to", PlanetTile.Invalid);
            Scribe_Values.Look(ref departTick, "departTick", 0);
            Scribe_Values.Look(ref arriveTick, "arriveTick", 0);
            Scribe_References.Look(ref targetParent, "targetParent");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                craftHolder ??= new ThingOwner<Building_BoardingCraft>(this);
            }
        }
    }

    /// <summary>讓 DMSE 的地圖層攔截認得突擊艇上的人（判定陣營、被擊落時一起處理）。</summary>
    [HarmonyPatch(typeof(BVRDetection), nameof(BVRDetection.PawnsIn))]
    public static class Patch_BoardingCraftPawns
    {
        [HarmonyPostfix]
        public static void Postfix(Thing innerThing, ref IEnumerable<Pawn> __result)
        {
            if (innerThing is Building_BoardingCraft craft)
            {
                __result = __result.Concat(new List<Pawn>(craft.Aboard));
            }
        }
    }

    /// <summary>
    /// 被擊落的突擊艇在水下是「沉沒」（§3.4 第 3 點）：不用 DMSE 的殘骸空投，艇身碎片與遺體直接沉到落點附近的海床，
    /// 發一則訊息。艇上人員照 DMSE 的規則處理（玩家的人失蹤，其他人陣亡）。
    /// </summary>
    [HarmonyPatch(typeof(BVRTarget), nameof(BVRTarget.SpawnDebris), new[] { typeof(Map), typeof(IntVec3) })]
    public static class Patch_BoardingCraftSinks
    {
        [HarmonyPrefix]
        public static bool Prefix(BVRTarget __instance, Map map, IntVec3 debrisCell)
        {
            if (!(__instance.Skyfaller?.innerContainer.FirstOrDefault() is Building_BoardingCraft craft) || map == null)
            {
                return true;
            }
            int crew = craft.Aboard.Count();
            List<Pawn> dead = __instance.HandleInterceptedPawns();
            List<Thing> sinking = new List<Thing>();
            foreach (Pawn pawn in dead)
            {
                Corpse corpse = pawn.Corpse ?? pawn.MakeCorpse(null, null);
                if (corpse == null)
                {
                    continue;
                }
                corpse.holdingOwner?.Remove(corpse);
                sinking.Add(corpse);
            }
            __instance.Discard();
            IntVec3 cell = BoardingUtility.SeabedNear(map, debrisCell.IsValid && debrisCell.InBounds(map) ? debrisCell : __instance.position);
            for (int i = 0; i < 2; i++)
            {
                sinking.Add(ThingMaker.MakeThing(ThingDefOf.ChunkSlagSteel));
            }
            foreach (Thing t in sinking)
            {
                GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near);
            }
            for (int i = 0; i < 6; i++)
            {
                FleckMaker.ThrowDustPuffThick(cell.ToVector3Shifted() + Gen.RandomHorizontalVector(1.5f), map, 2f, new Color(0.6f, 0.7f, 0.8f, 0.8f));
            }
            Messages.Message("DepthRim.Boarding.CraftSunk".Translate(crew), new LookTargets(cell, map), craft.IsPlayerCraft ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.PositiveEvent);
            return false;
        }
    }
}
