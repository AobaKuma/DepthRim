using System;
using System.Collections.Generic;
using System.Linq;
using DMSE;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DepthRim
{
    /// <summary>潛水鐘的數值（純函式，可單元測試）。設計稿 §7.4、§7.5。</summary>
    public static class BellMath
    {
        /// <summary>收放時間（遊戲秒，一倍速＝真實秒）：淺海 20 秒，每深一層多 40/3 秒，最深 60 秒。</summary>
        public static int TransitTicks(DepthTier tier)
        {
            float seconds = Mathf.Lerp(20f, 60f, Mathf.Clamp01((int)tier / 3f));
            return Mathf.RoundToInt(seconds * 60f);
        }

        /// <summary>鐘裡的殘氣（人·tick）：沒電時兩個人撐一個遊戲小時、一個人兩個小時。</summary>
        public const float ReserveOccupantTicks = 2f * 2500f;

        /// <summary>每次更新（<see cref="Building_DivingBell.AirInterval"/> ticks）沒有氧氣時增加的溺水嚴重度（同水中沒有氣瓶）。</summary>
        public const float SuffocationPerUpdate = 0.15f;

        /// <summary>有氧氣時每次更新消退的溺水嚴重度（同離開水面）。</summary>
        public const float RecoveryPerUpdate = 12f * 250f / 60000f;

        /// <summary>
        /// 殘氣更新：有供氧時補滿；沒有時扣掉 <paramref name="ticks"/> × 人數。回傳殘氣是否已經用完（這次更新開始要靠自己的氣瓶或窒息）。
        /// </summary>
        public static bool StepReserve(ref float reserve, int ticks, int occupants, bool supplied)
        {
            if (supplied)
            {
                reserve = ReserveOccupantTicks;
                return false;
            }
            if (occupants <= 0)
            {
                return reserve <= 0f;
            }
            if (reserve <= 0f)
            {
                return true;
            }
            reserve = Mathf.Max(0f, reserve - ticks * occupants);
            return false;
        }
    }

    public enum BellState
    {
        Stowed,
        Deploying,
        Deployed,
        Retracting,
    }

    public class CompProperties_BellShuttle : CompProperties_UnderwaterShuttle
    {
        public CompProperties_BellShuttle()
        {
            compClass = typeof(CompBellShuttle);
        }
    }

    /// <summary>
    /// 登鐘：沿用突擊艇的 <see cref="CompUnderwaterShuttle"/>（原版 CompShuttle 的載入／卸下 gizmo，並避開原版穿梭機卸載排程的例外與自動裝載鈕），
    /// 只覆寫誰可以進來（玩家的人，最多 <see cref="Building_DivingBell.MaxPeople"/> 人）與右鍵文字。
    /// </summary>
    public class CompBellShuttle : CompUnderwaterShuttle
    {
        public Building_DivingBell Bell => parent as Building_DivingBell;

        public override bool IsAllowed(Thing t)
        {
            if (t is Pawn pawn)
            {
                if (pawn.Dead || pawn.Destroyed || pawn.Faction != Faction.OfPlayer)
                {
                    return false;
                }
                ThingOwner held = Bell?.HeldThings;
                return held == null || held.Contains(pawn) || Bell.PeopleAboard < Building_DivingBell.MaxPeople;
            }
            return base.IsAllowed(t);
        }

        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn selPawn)
        {
            if (Bell == null || parent.Faction != Faction.OfPlayer || !selPawn.CanReach(parent, PathEndMode.Touch, Danger.Deadly))
            {
                yield break;
            }
            string label = "DepthRim.Bell.BoardOption".Translate(parent.LabelShort);
            if (!IsAllowedNow(selPawn))
            {
                yield return new FloatMenuOption(label + " (" + "DepthRim.Bell.Full".Translate(Building_DivingBell.MaxPeople) + ")", null);
                yield break;
            }
            yield return new FloatMenuOption(label, delegate { OrderEnterBell(selPawn); }, MenuOptionPriority.High);
        }

        public override IEnumerable<FloatMenuOption> CompMultiSelectFloatMenuOptions(IEnumerable<Pawn> selPawns)
        {
            if (Bell == null || parent.Faction != Faction.OfPlayer)
            {
                yield break;
            }
            List<Pawn> able = selPawns.Where(p => p.CanReach(parent, PathEndMode.Touch, Danger.Deadly) && IsAllowedNow(p))
                .Take(Mathf.Max(0, Building_DivingBell.MaxPeople - Bell.PeopleAboard)).ToList();
            string label = "DepthRim.Bell.BoardOption".Translate(parent.LabelShort);
            if (able.Count == 0)
            {
                yield return new FloatMenuOption(label + " (" + "DepthRim.Bell.Full".Translate(Building_DivingBell.MaxPeople) + ")", null);
                yield break;
            }
            yield return new FloatMenuOption(label, delegate
            {
                foreach (Pawn pawn in able)
                {
                    OrderEnterBell(pawn);
                }
            }, MenuOptionPriority.High);
        }

        /// <summary>同突擊艇：先讓載入程序有個 groupID，再派原版的 EnterTransporter 工作。</summary>
        public void OrderEnterBell(Pawn pawn)
        {
            if (!Transporter.LoadingInProgressOrReadyToLaunch)
            {
                TransporterUtility.InitiateLoading(Gen.YieldSingle(Transporter));
            }
            pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterTransporter, parent), JobTag.Misc);
        }
    }

    /// <summary>
    /// 潛水鐘（設計稿 §7.1–7.2、§7.5）：2×2 載具建築，人與貨在原版 <see cref="CompTransporter"/> 的容器裡。
    /// 每 <see cref="AirInterval"/> ticks 照「有沒有供氧」處理容器裡的人（容器裡的人不走一般的溺水流程）。
    /// 被毀時原版 CompTransporter 會把內容物倒在旁邊。
    /// </summary>
    public class Building_DivingBell : Building
    {
        public const int MaxPeople = 2;

        public const int AirInterval = 250;

        /// <summary>吊著這台鐘的吊架（放在甲板上、還沒連上時 null）。</summary>
        public Building_BellDavit davit;

        /// <summary>鐘裡剩下的殘氣（人·tick）。</summary>
        public float reserve = BellMath.ReserveOccupantTicks;

        public CompTransporter Transporter => GetComp<CompTransporter>();

        public ThingOwner HeldThings => Transporter?.innerContainer;

        public IEnumerable<Pawn> PeopleInside => HeldThings == null ? Enumerable.Empty<Pawn>() : HeldThings.OfType<Pawn>();

        public int PeopleAboard => PeopleInside.Count();

        /// <summary>現在有沒有供氧：連著吊架、吊架沒被毀、有電。</summary>
        public bool Supplied => davit != null && !davit.Destroyed && davit.Bell == this && davit.PowerOn;

        /// <summary>放出鐘裡的人與貨（原版載入程序留下的 lord／清單一併收掉）。</summary>
        public void Eject()
        {
            CompTransporter transporter = Transporter;
            if (transporter == null || !Spawned)
            {
                return;
            }
            if (transporter.LoadingInProgressOrReadyToLaunch)
            {
                transporter.CancelLoad(Map);
            }
            if (transporter.innerContainer.Any)
            {
                transporter.innerContainer.TryDropAll(Position, Map, ThingPlaceMode.Near);
            }
        }

        /// <summary>搬到另一張地圖前收掉原版載入程序（不倒出內容物）。</summary>
        public void PrepareToMove()
        {
            CompTransporter transporter = Transporter;
            if (transporter == null || !Spawned)
            {
                return;
            }
            transporter.TryRemoveLord(Map);
            transporter.groupID = -1;
            transporter.leftToLoad?.Clear();
            GetComp<CompShuttle>()?.CleanUpLoadingVars();
        }

        /// <summary>收放途中：鐘不在地圖上，收在吊架的容器裡（<see cref="Building_BellDavit.InTransit"/>）。</summary>
        public bool InTransit => !Spawned && ParentHolder is Building_BellDavit;

        protected override void Tick()
        {
            base.Tick();
            if ((!Spawned && !InTransit) || !this.IsHashIntervalTick(AirInterval))
            {
                return;
            }
            UpdateAir(AirInterval);
        }

        /// <summary>容器裡的人：有供氧時潛水服回充、溺水消退；沒有時先用殘氣，再用自己的潛水服，最後窒息。</summary>
        public void UpdateAir(int ticks)
        {
            List<Pawn> people = PeopleInside.Where(p => !p.Dead && p.RaceProps.IsFlesh && !DepthRimGenes.Of(p).BreathesWater).ToList();
            bool supplied = Supplied;
            bool exhausted = BellMath.StepReserve(ref reserve, ticks, people.Count, supplied);
            foreach (Pawn p in people)
            {
                CompDivingAir air = CompDivingAir.WornBy(p);
                if (supplied || !exhausted)
                {
                    air?.Refill(supplied ? ticks : 0);
                    if (p.health.hediffSet.HasHediff(DepthRimDefOf.DepthRim_Drowning))
                    {
                        HealthUtility.AdjustSeverity(p, DepthRimDefOf.DepthRim_Drowning, -BellMath.RecoveryPerUpdate);
                    }
                    continue;
                }
                if (air != null && air.TryConsume(ticks))
                {
                    continue;
                }
                HealthUtility.AdjustSeverity(p, DepthRimDefOf.DepthRim_Drowning, BellMath.SuffocationPerUpdate);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            if (Faction != Faction.OfPlayer)
            {
                yield break;
            }
            if (HeldThings != null && HeldThings.Any)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DepthRim.Bell.Unload".Translate(),
                    defaultDesc = "DepthRim.Bell.UnloadDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/PodEject", reportFailure: false) ?? BaseContent.BadTex,
                    action = Eject,
                };
            }
            // 在海床上時也能直接下令收鐘
            if (davit != null && !davit.Destroyed && davit.Bell == this && davit.State == BellState.Deployed && Map != davit.Map)
            {
                yield return davit.RetractGizmo();
            }
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            List<string> lines = new List<string>();
            if (!s.NullOrEmpty())
            {
                lines.Add(s);
            }
            lines.Add("DepthRim.Bell.Aboard".Translate(PeopleAboard, MaxPeople));
            if (PeopleAboard > 0)
            {
                lines.Add(Supplied ? "DepthRim.Bell.AirSupplied".Translate().ToString()
                    : "DepthRim.Bell.AirReserve".Translate(Mathf.RoundToInt(reserve / Mathf.Max(1, PeopleAboard)).ToStringTicksToPeriod()).ToString());
            }
            if (davit == null || davit.Destroyed || davit.Bell != this)
            {
                if (Map != null && SeaLevels.IsVanillaSurface(Map) == false && !(Map.Parent is WorldObject_SeaSurfaceSite) && !(Map.Parent is WorldObject_Submarine))
                {
                    lines.Add("DepthRim.Bell.Lost".Translate());
                }
            }
            return string.Join("\n", lines);
        }

        /// <summary>海床上連著吊架時畫往上的臍帶（純渲染；海面那一段由吊架畫）。</summary>
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            if (davit != null && !davit.Destroyed && davit.Bell == this && davit.Map != Map)
            {
                Vector3 top = drawLoc + new Vector3(0f, 0f, 0.9f);
                DiveBellDraw.Cable(top, top + new Vector3(0f, 0f, 9f), AltitudeLayer.MoteOverhead.AltitudeFor());
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref davit, "davit");
            Scribe_Values.Look(ref reserve, "reserve", BellMath.ReserveOccupantTicks);
        }
    }

    public static class DiveBellDraw
    {
        private static Material cableMat;

        /// <summary>一條細纜（往遠處淡掉的幾段）。</summary>
        public static void Cable(Vector3 from, Vector3 to, float altitude)
        {
            cableMat ??= SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.12f, 0.12f, 0.12f, 0.85f));
            const int segments = 6;
            for (int i = 0; i < segments; i++)
            {
                Vector3 a = Vector3.Lerp(from, to, i / (float)segments).WithY(altitude);
                Vector3 b = Vector3.Lerp(from, to, (i + 1) / (float)segments).WithY(altitude);
                GenDraw.DrawLineBetween(a, b, cableMat, 0.08f * (1f - 0.12f * i));
            }
        }
    }

    /// <summary>
    /// 吊架（設計稿 §7.3–7.7b）：潛水鐘的母港與狀態機。
    /// <list type="bullet">
    /// <item>Stowed：鐘停在吊架旁 2 格內。</item>
    /// <item>Deploying：倒數（只在通電時前進）；鐘還在甲板上、人在鐘裡。倒數完：鐘從這張地圖消失，在正下方的海床生成，人與貨放出來。</item>
    /// <item>Deployed：鐘在海床上，吊架有電就供氧。</item>
    /// <item>Retracting：倒數（只在通電時前進）；鐘還在海床、人要先登鐘。倒數完：鐘連內容物回到吊架旁，人與貨放出來。</item>
    /// </list>
    /// 任何中斷都把鐘留在它此刻實際所在的地圖，不瞬移（§7.7b）。
    /// </summary>
    public class Building_BellDavit : Building, IThingHolder
    {
        public const float BellRange = 2f;

        /// <summary>
        /// 收放途中的鐘（2026-11-22：收放一開始鐘就播離場動畫、離開地圖，倒數結束才出現在目的地）。
        /// 鐘裡的人與貨留在鐘自己的容器裡；吊架每 tick 讓容器跟著跑（人的需求、鐘的氧氣）。
        /// </summary>
        private ThingOwner<Building_DivingBell> transit;

        /// <summary>收鐘前鐘在海床上的位置與那一格（收到一半放棄時放回那裡）。</summary>
        private IntVec3 seabedCell = IntVec3.Invalid;

        private PlanetTile seabedTile = PlanetTile.Invalid;

        public Building_BellDavit()
        {
            transit = new ThingOwner<Building_DivingBell>(this, oneStackOnly: true);
        }

        public ThingOwner GetDirectlyHeldThings() => transit;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        /// <summary>鐘正在收放途中（不在任何地圖上，在吊架的容器裡）。</summary>
        public bool InTransit => bell != null && transit.Contains(bell);

        /// <summary>鐘離開地圖：播離場動畫（<paramref name="sink"/>：在水面往下沉；否則從海床往上拉），收進容器。</summary>
        private void TakeIntoTransit(bool sink)
        {
            if (bell == null || !bell.Spawned)
            {
                return;
            }
            BellFx.Play(bell, sink);
            bell.PrepareToMove();
            bell.DeSpawn(DestroyMode.WillReplace);
            transit.TryAdd(bell);
        }

        /// <summary>鐘從容器出來，放到 <paramref name="map"/> 的 <paramref name="spot"/>。</summary>
        private void ReleaseFromTransit(Map map, IntVec3 spot)
        {
            if (bell == null)
            {
                return;
            }
            if (transit.Contains(bell))
            {
                transit.Remove(bell);
            }
            if (!bell.Spawned)
            {
                GenSpawn.Spawn(bell, spot, map, Rot4.North);
            }
        }

        public const int CheckInterval = 60;

        private Building_DivingBell bell;

        private BellState state = BellState.Stowed;

        private int progress;

        private int transitTicks;

        /// <summary>鐘出發前停的位置（收回時放回這裡）。</summary>
        private IntVec3 stowCell = IntVec3.Invalid;

        public Building_DivingBell Bell => bell;

        public BellState State => state;

        public int Progress => progress;

        public int TransitTicks => transitTicks;

        public bool PowerOn => GetComp<CompPowerTrader>()?.PowerOn ?? true;

        // ── 鐘 ──

        /// <summary>吊架旁 2 格內、沒被別的吊架連著的鐘。</summary>
        public Building_DivingBell FindNearbyBell()
        {
            if (!Spawned)
            {
                return null;
            }
            return Map.listerBuildings.allBuildingsColonist.OfType<Building_DivingBell>()
                .Where(b => (b.davit == null || b.davit == this || b.davit.Destroyed || b.davit.Bell != b) && b.OccupiedRect().ClosestCellTo(Position).DistanceTo(Position) <= BellRange)
                .OrderBy(b => b.Position.DistanceToSquared(Position)).FirstOrDefault();
        }

        /// <summary>
        /// 正下方的海床：吊架所在的地圖是海面地點（水面層）或停著的潛艇；海洋層那一格的海底是淺海才有可以停鐘的海床（§7.2、§7.11）。
        /// 港口（原版地表地圖）一律不行（§5.4）。
        /// </summary>
        public static PlanetTile SeabedTile(Map map, out string reason)
        {
            reason = null;
            PlanetTile tile = PlanetTile.Invalid;
            if (map?.Parent is WorldObject_Submarine sub)
            {
                if (sub.InFlight)
                {
                    reason = "DepthRim.Bell.UnderWay".Translate();
                    return PlanetTile.Invalid;
                }
                tile = map.Tile;
            }
            else if (map != null && SeaLevels.IsSeaLevel(map))
            {
                if (map.Parent is WorldObject_SurfaceShip ship && ship.underWay)
                {
                    reason = "DepthRim.Bell.UnderWay".Translate();
                    return PlanetTile.Invalid;
                }
                PlanetLayer ocean = Find.WorldGrid.FirstLayerOfDef(DepthRimDefOf.DepthRim_Ocean);
                tile = ocean == null ? PlanetTile.Invalid : OceanWorldView.TileBelow(map.Tile, ocean);
            }
            else
            {
                reason = SeaLevels.IsVanillaSurface(map) ? "DepthRim.Bell.InPort".Translate() : "DepthRim.Bell.NotAtSea".Translate();
                return PlanetTile.Invalid;
            }
            if (!tile.Valid || !OceanRouting.Navigable(tile))
            {
                reason = "DepthRim.Bell.NoSeaBelow".Translate();
                return PlanetTile.Invalid;
            }
            // 每個海洋生態區都有海床（層級是海床在多深）：深海平原也能下鐘（2026-11-22 使用者決定），越深收放越久（BellMath.TransitTicks）
            MapParent other = Find.WorldObjects.MapParentAt(tile);
            if (other != null && !(other is WorldObject_OceanSite) && !(other is WorldObject_Submarine))
            {
                reason = "DepthRim.Surface.Occupied".Translate(other.LabelCap);
                return PlanetTile.Invalid;
            }
            return tile;
        }

        public string DeployBlockReason()
        {
            if (state != BellState.Stowed)
            {
                return "DepthRim.Bell.Busy".Translate();
            }
            if (FindNearbyBell() == null)
            {
                return "DepthRim.Bell.NoBell".Translate(BellRange);
            }
            if (!PowerOn)
            {
                return "DepthRim.Bell.NoPower".Translate();
            }
            SeabedTile(Map, out string reason);
            return reason;
        }

        public string RetractBlockReason()
        {
            if (state != BellState.Deployed)
            {
                return "DepthRim.Bell.NotDeployed".Translate();
            }
            if (bell == null || bell.Destroyed || !bell.Spawned)
            {
                return "DepthRim.Bell.NotDeployed".Translate();
            }
            if (!PowerOn)
            {
                return "DepthRim.Bell.NoPower".Translate();
            }
            return null;
        }

        public void StartDeploy()
        {
            string block = DeployBlockReason();
            if (block != null)
            {
                Messages.Message(block, this, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            bell = FindNearbyBell();
            bell.davit = this;
            stowCell = bell.Position;
            PlanetTile tile = SeabedTile(Map, out _);
            transitTicks = BellMath.TransitTicks(WorldObject_Submarine.SeafloorAt(tile));
            progress = 0;
            state = BellState.Deploying;
            TakeIntoTransit(sink: true);
            Messages.Message("DepthRim.Bell.Lowering".Translate(transitTicks.ToStringTicksToPeriod()), this, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        public void StartRetract()
        {
            string block = RetractBlockReason();
            if (block != null)
            {
                Messages.Message(block, this, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            int left = bell.Map.mapPawns.FreeColonistsSpawned.Count;
            if (left > 0)
            {
                Messages.Message("DepthRim.Bell.PeopleLeftBelow".Translate(left), bell, MessageTypeDefOf.CautionInput, historical: false);
            }
            progress = 0;
            state = BellState.Retracting;
            seabedCell = bell.Position;
            seabedTile = bell.Map.Tile;
            TakeIntoTransit(sink: false);
            Messages.Message("DepthRim.Bell.Raising".Translate(transitTicks.ToStringTicksToPeriod()), this, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        protected override void Tick()
        {
            base.Tick();
            // 途中的鐘不在地圖上：讓它（與裡面的人）照常跑
            transit.DoTick();
            if (!Spawned || !this.IsHashIntervalTick(CheckInterval))
            {
                return;
            }
            AdvanceTicks(CheckInterval);
        }

        /// <summary>推進狀態機 <paramref name="ticks"/> ticks（驗收測試也直接呼叫）：倒數只在通電時前進；鐘被毀時收場。</summary>
        public void AdvanceTicks(int ticks)
        {
            if (state != BellState.Stowed && (bell == null || bell.Destroyed))
            {
                // 鐘被毀：寄存清空，回到沒有鐘的 Stowed（§7.7b）
                Messages.Message("DepthRim.Bell.Destroyed".Translate(), this, MessageTypeDefOf.NegativeEvent);
                bell = null;
                state = BellState.Stowed;
                progress = 0;
                return;
            }
            if (state != BellState.Deploying && state != BellState.Retracting)
            {
                return;
            }
            if (!PowerOn)
            {
                return;
            }
            progress += ticks;
            if (progress < transitTicks)
            {
                return;
            }
            if (state == BellState.Deploying)
            {
                CompleteDeploy();
            }
            else
            {
                CompleteRetract();
            }
        }

        private void CompleteDeploy()
        {
            PlanetTile tile = SeabedTile(Map, out string reason);
            if (!tile.Valid)
            {
                Messages.Message(reason, this, MessageTypeDefOf.RejectInput);
                ReturnToDeck();
                return;
            }
            Map seabed = SeabedMap(tile);
            IntVec3 preferred = stowCell.IsValid ? stowCell : Position;
            if (seabed == null || !TryFindLandingSpot(seabed, preferred, out IntVec3 spot))
            {
                Messages.Message("DepthRim.Bell.NoLandingSpot".Translate(), this, MessageTypeDefOf.RejectInput);
                ReturnToDeck();
                return;
            }
            bell.PrepareToMove();
            if (bell.Spawned)
            {
                bell.DeSpawn(DestroyMode.WillReplace);
            }
            ReleaseFromTransit(seabed, spot);
            FloodFillerFog.FloodUnfog(spot, seabed);
            bell.Eject();
            state = BellState.Deployed;
            progress = 0;
            Messages.Message("DepthRim.Bell.Landed".Translate(), new TargetInfo(spot, seabed), MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>下鐘失敗或下到一半放棄：鐘回到甲板（原本的位置，放不下就吊架旁），人與貨放出來，回到 Stowed。</summary>
        private void ReturnToDeck()
        {
            if (bell != null && InTransit)
            {
                IntVec3 spot;
                if (!TryFindStowSpot(out spot) && !CellFinder.TryFindRandomCellNear(Position, Map, 6, c => BellFits(Map, c), out spot))
                {
                    spot = Position;
                }
                ReleaseFromTransit(Map, spot);
            }
            bell?.Eject();
            state = BellState.Stowed;
            progress = 0;
        }

        private void CompleteRetract()
        {
            Map from = bell.Map;
            if (!TryFindStowSpot(out IntVec3 spot))
            {
                // 甲板上放不下：倒數停在終點，等騰出位置
                progress = transitTicks;
                Messages.Message("DepthRim.Bell.NoStowRoom".Translate(), this, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (bell.Spawned)
            {
                bell.PrepareToMove();
                bell.DeSpawn(DestroyMode.WillReplace);
            }
            ReleaseFromTransit(Map, spot);
            bell.Eject();
            state = BellState.Stowed;
            progress = 0;
            seabedTile = PlanetTile.Invalid;
            seabedCell = IntVec3.Invalid;
            Messages.Message("DepthRim.Bell.Stowed".Translate(), this, MessageTypeDefOf.PositiveEvent);
            _ = from;
        }

        /// <summary>正下方的海床地圖：建立或重用（<see cref="WorldObject_OceanSite"/>：有玩家建築就保留，鐘就是）。</summary>
        public Map SeabedMap(PlanetTile tile)
        {
            MapParent parent = Find.WorldObjects.MapParentAt(tile);
            if (parent == null)
            {
                parent = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
                parent.Tile = tile;
                parent.SetFaction(Faction.OfPlayer);
                Find.WorldObjects.Add(parent);
            }
            if (parent.HasMap)
            {
                return parent.Map;
            }
            return GetOrGenerateMapUtility.GetOrGenerateMap(tile, Map.Size, parent.def);
        }

        /// <summary>鐘的 2×2 放得下：在地圖內、離邊 3 格、每格能站、沒有建築。</summary>
        public static bool BellFits(Map map, IntVec3 pos)
        {
            CellRect r = GenAdj.OccupiedRect(pos, Rot4.North, new IntVec2(2, 2));
            if (!r.ExpandedBy(3).InBounds(map))
            {
                return false;
            }
            foreach (IntVec3 c in r)
            {
                if (!c.Standable(map) || c.GetEdifice(map) != null)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>垂直放下：同一個座標放得下就放那裡，否則找最近放得下的海床。</summary>
        public static bool TryFindLandingSpot(Map seabed, IntVec3 preferred, out IntVec3 spot)
        {
            spot = preferred;
            if (preferred.InBounds(seabed) && BellFits(seabed, preferred))
            {
                return true;
            }
            IntVec3 start = preferred.InBounds(seabed) ? preferred : seabed.Center;
            for (int i = 0; i < GenRadial.NumCellsInRadius(56f); i++)
            {
                IntVec3 c = start + GenRadial.RadialPattern[i];
                if (c.InBounds(seabed) && BellFits(seabed, c))
                {
                    spot = c;
                    return true;
                }
            }
            return false;
        }

        private bool TryFindStowSpot(out IntVec3 spot)
        {
            bool Ok(IntVec3 p)
            {
                CellRect r = GenAdj.OccupiedRect(p, Rot4.North, new IntVec2(2, 2));
                return r.InBounds(Map) && r.Cells.All(c => c.Standable(Map) && c.GetEdifice(Map) == null)
                    && r.ClosestCellTo(Position).DistanceTo(Position) <= BellRange;
            }
            spot = stowCell;
            if (stowCell.IsValid && Ok(stowCell))
            {
                return true;
            }
            for (int i = 0; i < GenRadial.NumCellsInRadius(4f); i++)
            {
                IntVec3 c = Position + GenRadial.RadialPattern[i];
                if (Ok(c))
                {
                    spot = c;
                    return true;
                }
            }
            return false;
        }

        // ── 放棄、拆除 ──

        /// <summary>放棄潛水鐘（§7.7）：下潛中＝取消（鐘留在甲板、人放出來）；在海床上＝鐘與人失聯，吊架回到沒有鐘的 Stowed。</summary>
        public void Abandon()
        {
            if (state == BellState.Stowed)
            {
                return;
            }
            if (state == BellState.Deploying)
            {
                // 還在往下：鐘回到甲板、人出來
                ReturnToDeck();
                if (bell != null)
                {
                    bell.davit = null;
                }
                bell = null;
                return;
            }
            Building_DivingBell lost = bell;
            // 往上拉到一半：鐘落回原本在海床上的位置，失聯
            if (lost != null && InTransit)
            {
                Map seabed = seabedTile.Valid ? Find.WorldObjects.MapParentAt(seabedTile)?.Map : null;
                transit.Remove(lost);
                if (seabed != null && TryFindLandingSpot(seabed, seabedCell.IsValid ? seabedCell : seabed.Center, out IntVec3 back))
                {
                    GenSpawn.Spawn(lost, back, seabed, Rot4.North);
                }
                else if (Spawned)
                {
                    // 海床地圖已經不在：只好放回甲板
                    IntVec3 deck = CellFinder.TryFindRandomCellNear(Position, Map, 6, c => BellFits(Map, c), out IntVec3 found) ? found : Position;
                    GenSpawn.Spawn(lost, deck, Map, Rot4.North);
                    lost.Eject();
                }
            }
            if (lost != null)
            {
                lost.davit = null;
            }
            bell = null;
            state = BellState.Stowed;
            progress = 0;
            if (lost != null && lost.Spawned)
            {
                Find.LetterStack.ReceiveLetter("DepthRim.Bell.LostLabel".Translate(), "DepthRim.Bell.LostText".Translate(),
                    LetterDefOf.NegativeEvent, new TargetInfo(lost.Position, lost.Map));
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // 吊架被拆或被毀（不是隨船搬移）：等同放棄（§7.7b）
            if (mode != DestroyMode.WillReplace && state != BellState.Stowed)
            {
                Abandon();
            }
            base.DeSpawn(mode);
        }

        // ── 指令 ──

        public Command RetractGizmo()
        {
            Command_Action cmd = new Command_Action
            {
                defaultLabel = "DepthRim.Bell.RaiseLabel".Translate(),
                defaultDesc = "DepthRim.Bell.RaiseDesc".Translate(),
                icon = SubmarineIcons.Ascent,
                action = StartRetract,
            };
            string block = RetractBlockReason();
            if (block != null)
            {
                cmd.Disable(block);
            }
            return cmd;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            if (Faction != Faction.OfPlayer)
            {
                yield break;
            }
            if (state == BellState.Stowed)
            {
                Command_Action lower = new Command_Action
                {
                    defaultLabel = "DepthRim.Bell.LowerLabel".Translate(),
                    defaultDesc = "DepthRim.Bell.LowerDesc".Translate(),
                    icon = SubmarineIcons.Descent,
                    action = StartDeploy,
                };
                string block = DeployBlockReason();
                if (block != null)
                {
                    lower.Disable(block);
                }
                yield return lower;
            }
            if (state == BellState.Deployed)
            {
                yield return RetractGizmo();
            }
            if (state != BellState.Stowed)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DepthRim.Bell.AbandonLabel".Translate(),
                    defaultDesc = "DepthRim.Bell.AbandonDesc".Translate(),
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel", reportFailure: false) ?? BaseContent.BadTex,
                    action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("DepthRim.Bell.AbandonConfirm".Translate(), Abandon, destructive: true)),
                };
            }
            if (DebugSettings.ShowDevGizmos && (state == BellState.Deploying || state == BellState.Retracting))
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: finish winch",
                    action = () => AdvanceTicks(Mathf.Max(1, transitTicks - progress)),
                };
            }
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string line;
            switch (state)
            {
                case BellState.Deploying:
                    line = "DepthRim.Bell.StateLowering".Translate(((float)progress / Mathf.Max(1, transitTicks)).ToStringPercent());
                    break;
                case BellState.Retracting:
                    line = "DepthRim.Bell.StateRaising".Translate(((float)progress / Mathf.Max(1, transitTicks)).ToStringPercent());
                    break;
                case BellState.Deployed:
                    line = "DepthRim.Bell.StateDeployed".Translate();
                    break;
                default:
                    line = FindNearbyBell() != null ? "DepthRim.Bell.StateStowed".Translate().ToString() : "DepthRim.Bell.StateNoBell".Translate().ToString();
                    break;
            }
            if ((state == BellState.Deploying || state == BellState.Retracting) && !PowerOn)
            {
                line += " " + "DepthRim.Bell.Paused".Translate();
            }
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }

        /// <summary>鐘下去之後畫往下的臍帶（純渲染）。</summary>
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            base.DrawAt(drawLoc, flip);
            if (state != BellState.Stowed)
            {
                DiveBellDraw.Cable(drawLoc, drawLoc + new Vector3(0f, 0f, -3f), AltitudeLayer.MoteOverhead.AltitudeFor());
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref bell, "bell");
            Scribe_Values.Look(ref state, "state", BellState.Stowed);
            Scribe_Values.Look(ref progress, "progress");
            Scribe_Values.Look(ref transitTicks, "transitTicks");
            Scribe_Values.Look(ref stowCell, "stowCell", IntVec3.Invalid);
            Scribe_Values.Look(ref seabedCell, "seabedCell", IntVec3.Invalid);
            Scribe_Values.Look(ref seabedTile, "seabedTile", PlanetTile.Invalid);
            Scribe_Deep.Look(ref transit, "transit", this);
            if (transit == null)
            {
                transit = new ThingOwner<Building_DivingBell>(this, oneStackOnly: true);
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit && state != BellState.Stowed && bell == null)
            {
                state = BellState.Stowed;
                progress = 0;
            }
        }
    }

    /// <summary>吊架只能蓋在船體平台（船甲板）或月池上；碼頭（陸地）不行（§5.4）。</summary>
    public class PlaceWorker_BellDavit : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            if (!loc.InBounds(map))
            {
                return false;
            }
            bool deck = MapComponent_SeabedMemory.IsPlatform(map.terrainGrid.TerrainAt(loc)) || MapComponent_SeabedMemory.IsPlatform(map.terrainGrid.UnderTerrainAt(loc));
            bool moonPool = loc.GetThingList(map).Any(t => t.def == BoardingDefOf.DepthRim_MoonPool || t.def.entityDefToBuild == BoardingDefOf.DepthRim_MoonPool);
            return deck || moonPool ? (AcceptanceReport)true : (AcceptanceReport)"DepthRim.Bell.DavitPlacement".Translate().ToString();
        }
    }

    /// <summary>出航封鎖（§7.7）：船體上（或整張地圖上）有吊架的鐘不在 Stowed 時，船與潛艇不能出航、下潛、上浮、離港、靠岸。</summary>
    public static class DiveBellLock
    {
        public static string Reason(Map map, ICollection<IntVec3> footprint = null)
        {
            if (map == null)
            {
                return null;
            }
            foreach (Building_BellDavit d in map.listerBuildings.allBuildingsColonist.OfType<Building_BellDavit>())
            {
                if (d.State != BellState.Stowed && (footprint == null || footprint.Contains(d.Position)))
                {
                    return "DepthRim.Bell.NotStowed".Translate();
                }
            }
            return null;
        }
    }

    /// <summary>DMSE 的起飛整備檢查（潛艇出港、航行）：鐘沒收回就擋下。掛在 DMSE 的 <see cref="TransferReadiness.ExtraChecks"/>。</summary>
    [StaticConstructorOnStartup]
    public static class DiveBellTransferCheck
    {
        static DiveBellTransferCheck()
        {
            TransferReadiness.ExtraChecks -= Check;
            TransferReadiness.ExtraChecks += Check;
        }

        private static void Check(CompTransferConsole console, TransferPlan plan, bool includeWarnings, List<TransferIssue> issues)
        {
            string reason = DiveBellLock.Reason(console?.parent?.MapHeld);
            if (reason != null)
            {
                issues.Add(new TransferIssue(TransferIssueSeverity.Error, reason));
            }
        }
    }
}
