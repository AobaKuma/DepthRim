using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified;
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
    /// 玩家：殖民者右鍵登艇（原版 <see cref="CompShuttle"/> 的登艇選單），按「出發」選一格海路到得了的海洋格，
    /// 確認出航前檢查（<see cref="BoardingReadiness"/>）後飛過去，一樣切入對方艙室。
    /// 艇上的人與貨放在原版 <see cref="CompTransporter"/> 的容器裡（<see cref="CompUnderwaterTransporter"/> 負責存檔），
    /// 登艇流程、載入/卸下 gizmo、檢視字串全部沿用原版穿梭機的實作。
    /// </summary>
    public class Building_BoardingCraft : Building, IThingHolder, IAirPayloadHolder, IInterceptResolver, IAirContactLabel
    {
        public const int EjectDelayTicks = 120;

        public const int ReturnTimeoutTicks = 5000;

        /// <summary>玩家艇的航程（海路格數）。</summary>
        public const int RangeTiles = 8;

        /// <summary>舊存檔（自製容器）的內容物；載入後搬進 <see cref="CompTransporter.innerContainer"/>。</summary>
        private ThingOwner<Thing> legacyAboard;

        /// <summary>這艘艇載來的人（含已經下艇的），撤退時等他們回來。</summary>
        public List<Pawn> raiders = new List<Pawn>();

        public int landedTick = -1;

        public bool ejected;

        public int returnRequestedTick = -1;

        /// <summary>玩家艇出發後的目的地（飛行中用）。</summary>
        public PlanetTile destinationTile = PlanetTile.Invalid;

        /// <summary>艇上的人與貨的容器（原版 CompTransporter）。</summary>
        public CompTransporter Transporter => GetComp<CompTransporter>();

        public CompUnderwaterShuttle Shuttle => GetComp<CompUnderwaterShuttle>();

        public ThingOwner HeldThings => Transporter?.innerContainer;

        public ThingOwner GetDirectlyHeldThings() => HeldThings;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public IEnumerable<Pawn> Aboard => (HeldThings?.OfType<Pawn>() ?? Enumerable.Empty<Pawn>());

        /// <summary>聲納分類（§11 M7 7.4）：來襲的突擊艇在抵達前這麼多 tick 內才分得出來，之前只是「未知聲納接觸」。</summary>
        public const int ClassifyTicksBeforeImpact = 1200;

        public string ContactLabel(AirDefenseTarget target, int ticksToImpact)
        {
            return ticksToImpact > ClassifyTicksBeforeImpact && !IsPlayerCraft ? "DepthRim.Sonar.UnknownContact".Translate().ToString() : def.label;
        }

        /// <summary>DMSE 的地圖層攔截用來判定陣營、擊落時處理艇上人員。</summary>
        public IEnumerable<Pawn> PawnsAboard => new List<Pawn>(Aboard);

        /// <summary>
        /// 被擊落的突擊艇在水下是「沉沒」（§3.4 第 3 點）：不用 DMSE 的殘骸空投，艇身碎片與遺體直接沉到落點附近的海床，
        /// 發一則訊息。艇上人員照 DMSE 的規則處理（玩家的人失蹤，其他人陣亡）。
        /// </summary>
        public bool TryResolveIntercept(AirDefenseTarget target, Map map, IntVec3 debrisCell)
        {
            if (map == null)
            {
                return false;
            }
            int crew = Aboard.Count();
            bool player = IsPlayerCraft;
            List<Pawn> dead = target.HandleInterceptedPawns();
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
            target.Discard();
            IntVec3 cell = BoardingUtility.SeabedNear(map, debrisCell.IsValid && debrisCell.InBounds(map) ? debrisCell : target.position);
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
            Messages.Message("DepthRim.Boarding.CraftSunk".Translate(crew), new LookTargets(cell, map),
                player ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.PositiveEvent);
            return true;
        }

        public int AboardCount => HeldThings?.Count(t => t is Pawn p && raiders.Contains(p)) ?? 0;

        public bool ReturnRequested => returnRequestedTick >= 0;

        public bool IsPlayerCraft => Faction == Faction.OfPlayer;

        /// <summary>出發前裝人（還沒生成的襲擊者）。</summary>
        public void Load(Pawn pawn)
        {
            if (HeldThings.TryAdd(pawn) && !raiders.Contains(pawn))
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
            ThingOwner held = HeldThings;
            return raiders.Where(p => p != null && !p.Dead && !p.Downed && p.Spawned && p.Map == Map
                && (held == null || !held.Contains(p))).ToList();
        }

        /// <summary>放出艇上的人（玩家艇也放出載的東西）；原版載入程序有留下的 lord／清單也一併收掉。</summary>
        public void Eject()
        {
            ejected = true;
            CompTransporter transporter = Transporter;
            if (transporter == null)
            {
                return;
            }
            if (transporter.LoadingInProgressOrReadyToLaunch)
            {
                // CleanUpLoadingVars 會把內容物倒在艇的位置，順便清掉 groupID 與 leftToLoad
                transporter.CancelLoad(Map);
            }
            if (transporter.innerContainer.Any)
            {
                transporter.innerContainer.TryDropAll(Position, Map, ThingPlaceMode.Near);
            }
        }

        public void RequestReturn()
        {
            if (returnRequestedTick < 0)
            {
                returnRequestedTick = Find.TickManager.TicksGame;
            }
        }

        /// <summary>
        /// 登艇（<see cref="JobDriver_BoardCraft"/>，敵方襲擊者用）：襲擊者離開 lord；扛著的東西（俘虜、戰利品）一起收進艇裡。
        /// 玩家的登艇走原版 <see cref="JobDriver_EnterTransporter"/>，直接放進同一個容器。
        /// </summary>
        public void Board(Pawn pawn)
        {
            ThingOwner held = HeldThings;
            if (held == null)
            {
                return;
            }
            Thing carried = pawn.carryTracker?.CarriedThing;
            if (carried != null)
            {
                pawn.carryTracker.innerContainer.TryTransferToContainer(carried, held);
            }
            pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.ExitedMap);
            pawn.DeSpawnOrDeselect();
            if (!held.TryAdd(pawn))
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
            else if (IsPlayerCraft)
            {
                PlanetTile from = LaunchTile(map);
                if (from.Valid)
                {
                    BurnFuelFor(OceanRouting.Distance(from, destination));
                }
            }
            landedTick = -1;
            ejected = false;
            returnRequestedTick = -1;
            // 離場前收掉原版載入程序留下的 groupID／lord：CompTransporter.PostDeSpawn 看到「還在載入」
            // 會把內容物倒在原地，這裡要保留艇上的人與貨。
            CompTransporter transporter = Transporter;
            if (transporter != null)
            {
                transporter.TryRemoveLord(map);
                transporter.groupID = -1;
                transporter.leftToLoad?.Clear();
                Shuttle?.CleanUpLoadingVars();
            }
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
            ThingOwner held = HeldThings;
            if (held == null)
            {
                return;
            }
            foreach (Thing t in held.ToList())
            {
                held.Remove(t);
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
            Command_Action launch = new Command_Action
            {
                defaultLabel = "DepthRim.Boarding.Launch".Translate(),
                defaultDesc = "DepthRim.Boarding.LaunchDesc".Translate(RangeTiles),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchShip"),
                action = StartChoosingDestination,
            };
            string blocked = LaunchBlockReason();
            if (blocked != null)
            {
                launch.Disable(blocked);
            }
            yield return launch;
            if (Aboard.Any() && !(Transporter?.LoadingInProgressOrReadyToLaunch ?? false))
            {
                // 載入程序中由原版 CompTransporter 提供「卸下／取消裝載」；這裡只補上著陸後的手動卸載。
                yield return new Command_Action
                {
                    defaultLabel = "DepthRim.Boarding.Unload".Translate(),
                    defaultDesc = "DepthRim.Boarding.UnloadDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel"),
                    action = Eject,
                };
            }
        }

        // ── 燃料與故障（玩家艇；敵方艇的 def 沒有這兩個 comp）──────────────────

        public CompRefuelable Fuel => GetComp<CompRefuelable>();

        public bool BrokenDown => GetComp<CompBreakdownable>()?.BrokenDown ?? false;

        /// <summary>現有燃料夠來回幾格海路（沒有燃料 comp 時不限）。</summary>
        public int FuelRangeTiles => Fuel == null ? int.MaxValue : BoardingCraftFuel.RoundTripTiles(Fuel.Fuel);

        /// <summary>「出發」按鈕不能用的原因；可以出發時回傳 null。</summary>
        public string LaunchBlockReason()
        {
            if (!Aboard.Any())
            {
                return "DepthRim.Boarding.NoCrew".Translate();
            }
            if (BrokenDown)
            {
                return "DepthRim.Boarding.BrokenDown".Translate();
            }
            if (!LaunchTile(Map).Valid)
            {
                return "DepthRim.Torpedo.SeaOnly".Translate();
            }
            if (FuelRangeTiles < 1)
            {
                return "DepthRim.Boarding.NoFuel".Translate(BoardingCraftFuel.RoundTrip(1).ToString("0"));
            }
            return null;
        }

        /// <summary>燃料不夠來回 <paramref name="distance"/> 格時的原因；夠的話回傳 null。</summary>
        public string FuelBlockReason(int distance)
        {
            CompRefuelable fuel = Fuel;
            if (fuel == null || BoardingCraftFuel.CanReach(fuel.Fuel, distance))
            {
                return null;
            }
            return "DepthRim.Boarding.NotEnoughFuel".Translate(BoardingCraftFuel.RoundTrip(distance).ToString("0"), fuel.Fuel.ToString("0"));
        }

        /// <summary>出發或回程時燒掉單程的燃料（不夠就燒到零，不會因此擱淺）。</summary>
        public void BurnFuelFor(int distance)
        {
            CompRefuelable fuel = Fuel;
            if (fuel != null && distance > 0)
            {
                fuel.ConsumeFuel(Mathf.Min(fuel.Fuel, BoardingCraftFuel.OneWay(distance)));
            }
        }

        /// <summary>
        /// 艇從這張地圖出航時在海洋層上的起點：海洋層的地圖就是它的格子；海面地圖（水面層、平台層）在地表層，
        /// 起點是正下方的海洋格。都不是（陸地殖民地）時無效。
        /// </summary>
        public static PlanetTile LaunchTile(Map map)
        {
            if (map == null)
            {
                return PlanetTile.Invalid;
            }
            if (OceanRouting.IsOcean(map.Tile))
            {
                return map.Tile;
            }
            if (SeaLevels.IsAnyLevel(map))
            {
                PlanetTile below = OceanWorldView.TileBelow(map.Tile);
                if (OceanRouting.IsOcean(below) && OceanRouting.Navigable(below))
                {
                    return below;
                }
            }
            return PlanetTile.Invalid;
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
            WorldObject other = Find.WorldObjects.AllWorldObjects.FirstOrDefault(o => o.Tile == to && !(o is MapParent) && !(o is WorldObject_OceanLandmark));
            return other != null ? "DepthRim.Boarding.Occupied" : null;
        }

        private void StartChoosingDestination()
        {
            PlanetTile from = LaunchTile(Map);
            if (!from.Valid)
            {
                return;
            }
            CameraJumper.TryJump(CameraJumper.GetWorldTarget(this));
            if (PlanetLayer.Selected != from.Layer)
            {
                PlanetLayer.Selected = from.Layer;
            }
            Find.WorldSelector.ClearSelection();
            Find.WorldTargeter.BeginTargeting(target =>
            {
                string reason = DestinationBlockReason(from, target.Tile);
                if (reason != null)
                {
                    Messages.Message(reason.Translate(RangeTiles), MessageTypeDefOf.RejectInput, historical: false);
                    return false;
                }
                string fuelReason = FuelBlockReason(OceanRouting.Distance(from, target.Tile));
                if (fuelReason != null)
                {
                    Messages.Message(fuelReason, MessageTypeDefOf.RejectInput, historical: false);
                    return false;
                }
                PlanetTile to = target.Tile;
                if (OceanLandmarkUtility.EncounterAt(to) != null)
                {
                    // 遭遇型地標不下艇，不用檢查潛水裝備
                    Launch(to);
                }
                else
                {
                    BoardingReadiness.Confirm(this, to, () => Launch(to));
                }
                return true;
            }, canTargetTiles: true, closeWorldTabWhenFinished: true,
            extraLabelGetter: target =>
            {
                string reason = DestinationBlockReason(from, target.Tile);
                if (reason != null)
                {
                    return reason.Translate(RangeTiles).ToString();
                }
                int tiles = OceanRouting.Distance(from, target.Tile);
                string fuelReason = FuelBlockReason(tiles);
                if (fuelReason != null)
                {
                    return fuelReason;
                }
                string distance = "DepthRim.Boarding.SeaDistance".Translate(tiles).ToString();
                if (Fuel != null)
                {
                    distance += "\n" + "DepthRim.Boarding.FuelNeeded".Translate(BoardingCraftFuel.RoundTrip(tiles).ToString("0"), Fuel.Fuel.ToString("0"));
                }
                WorldObject landmark = Find.WorldObjects.AllWorldObjects.FirstOrDefault(o => o.Tile == target.Tile && o is IOceanLandmark);
                return landmark != null ? landmark.Label + "\n" + distance : distance;
            });
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            int crew = Aboard.Count();
            string line = ReturnRequested
                ? "DepthRim.Boarding.InspectWaiting".Translate(AboardCount, RaidersStillOut().Count)
                : "DepthRim.Boarding.InspectAboard".Translate(crew);
            if (IsPlayerCraft && Fuel != null)
            {
                line += "\n" + "DepthRim.Boarding.InspectFuelRange".Translate(Mathf.Min(FuelRangeTiles, RangeTiles));
            }
            return text.NullOrEmpty() ? line : text + "\n" + line;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref raiders, "raiders", LookMode.Reference);
            Scribe_Values.Look(ref landedTick, "landedTick", -1);
            Scribe_Values.Look(ref ejected, "ejected", false);
            Scribe_Values.Look(ref returnRequestedTick, "returnRequestedTick", -1);
            Scribe_Values.Look(ref destinationTile, "destinationTile", PlanetTile.Invalid);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // 舊存檔的自製容器；PostLoadInit 再搬進 CompTransporter。
                Scribe_Deep.Look(ref legacyAboard, "aboard", this);
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                raiders ??= new List<Pawn>();
                raiders.RemoveAll(p => p == null);
                MigrateLegacyContainer();
            }
        }

        private void MigrateLegacyContainer()
        {
            if (legacyAboard == null)
            {
                return;
            }
            ThingOwner held = HeldThings;
            if (held != null)
            {
                foreach (Thing t in legacyAboard.ToList())
                {
                    legacyAboard.Remove(t);
                    if (!held.TryAddOrTransfer(t))
                    {
                        GenPlace.TryPlaceThing(t, Position, Map, ThingPlaceMode.Near);
                    }
                }
            }
            legacyAboard = null;
        }
    }

    /// <summary>離場的突擊艇：飛出畫面時，敵方艇把艇上的人送回世界；玩家艇變成世界地圖上的航跡。</summary>
    public class BoardingCraftLeaving : Skyfaller
    {
        protected override void LeaveMap()
        {
            Building_BoardingCraft craft = innerContainer.OfType<Building_BoardingCraft>().FirstOrDefault();
            PlanetTile origin = Building_BoardingCraft.LaunchTile(Map);
            if (craft != null && craft.destinationTile.Valid && origin.Valid)
            {
                innerContainer.Remove(craft);
                WorldObject_TravelingBoardingCraft traveling =
                    (WorldObject_TravelingBoardingCraft)WorldObjectMaker.MakeWorldObject(BoardingDefOf.DepthRim_TravelingBoardingCraft);
                traveling.Setup(craft, origin, craft.destinationTile, Map.Parent);
                Find.WorldObjects.Add(traveling);
            }
            else
            {
                craft?.SendAboardAway();
            }
            base.LeaveMap();
        }
    }

}
