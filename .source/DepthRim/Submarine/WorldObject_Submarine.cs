using System.Collections.Generic;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇：DMSE 轉移飛行的船體地圖父物件（海洋層 <c>TransferEnvironmentExtension.shipWorldObject</c>），
    /// 多了巡航深度。航行、點火、降落、安全期全部沿用 DMSE。
    ///
    /// <para><b>深度</b>：<see cref="cruiseTier"/> 是玩家選的巡航深度，每小時往 <see cref="targetTier"/> 走一層；
    /// 實際所在的層級（<see cref="CurrentDepthTier"/>，水壓、水體外觀、中層水域都看它）不會比腳下的海床更深
    /// （<see cref="DiveMath.Effective"/>）。往下潛不能超過海床與船殼耐壓（<see cref="HullPressure"/>）。</para>
    /// </summary>
    public class WorldObject_Submarine : WorldObject_TransferShip, IDepthTierProvider
    {
        public DepthTier cruiseTier;

        public DepthTier targetTier;

        /// <summary>下一次換層的時刻（-1 = 沒有在換）。</summary>
        public int nextDiveTick = -1;

        private bool depthInitialized;

        // ── 破冰上浮（SubmarineSurfacing）──────────────────────────────────
        /// <summary>懸停結束、頂冰的時刻（-1 = 沒有在懸停）。</summary>
        public int iceBreachAtTick = -1;

        /// <summary>下一次可以頂冰的時刻（每次頂冰後六小時）。</summary>
        public int nextIceBreachTick = -1;

        /// <summary>冰被頂裂過、抗彎厚度打折到這個時刻。</summary>
        public int crackedUntilTick = -1;

        /// <summary>從海面或冰面下潛時直接設定深度（不從腳下的海床出發）。</summary>
        public void InitDepth(DepthTier tier)
        {
            cruiseTier = targetTier = tier;
            nextDiveTick = -1;
            depthInitialized = true;
        }

        /// <summary>上一次通知地圖時的層級（不存檔：讀檔後第一次 tick 會重新通知一次，無害）。</summary>
        private DepthTier? notifiedTier;

        /// <summary>腳下海床的層級：目前所在海洋格的生態區。陸塊或不是海洋層時當淺海。</summary>
        public DepthTier Seafloor => SeafloorAt(Tile);

        public static DepthTier SeafloorAt(PlanetTile tile)
        {
            if (!tile.Valid)
            {
                return DepthTier.Shallow;
            }
            return tile.Tile?.PrimaryBiome?.GetModExtension<WaterMapExtension>()?.depthTier ?? DepthTier.Shallow;
        }

        public DepthTier CurrentDepthTier => DiveMath.Effective(cruiseTier, Seafloor);

        public bool Diving => nextDiveTick >= 0 && cruiseTier != targetTier;

        public DepthTier HullRating(out Building weakest)
        {
            weakest = null;
            return HasMap ? HullPressure.RatingFor(Map, out weakest) : HullPressure.Unlimited;
        }

        public DiveMath.DiveBlock CanDiveTo(DepthTier target)
        {
            return DiveMath.CanSet(targetTier, target, Seafloor, HullRating(out _));
        }

        /// <summary>設定巡航深度（已檢查過）；一小時後開始換第一層。</summary>
        public void SetTargetTier(DepthTier target)
        {
            targetTier = target;
            nextDiveTick = target == cruiseTier ? -1 : Find.TickManager.TicksGame + DiveMath.TicksPerTier;
        }

        // ── 航行推進（和 DMSE 不同：推進器要一直運作才會前進）────────────────

        /// <summary>出航時可用的推力（第一次在航行中 tick 時記下）。</summary>
        public float plannedThrust;

        /// <summary>最近一次確認的可用推力。</summary>
        public float currentThrust;

        private float stallCarry;

        private int nextThrustCheckTick = -1;

        private bool stallNotified;

        /// <summary>目前以出航推力的多少比例前進（0 = 停在海中）。</summary>
        public float ThrustFraction => VoyageMath.ThrustFraction(currentThrust, plannedThrust);

        public bool Stalled => InFlight && ThrustFraction <= 0f;

        /// <summary>重新確認推進器（勘查會重算哪些推進器通電、開著、沒壞）。</summary>
        public void RefreshThrust()
        {
            TransferShipSurvey survey = null;
            if (HasMap)
            {
                MapComponent_PileNetwork comp = MapComponent_PileNetwork.For(Map);
                if (comp != null)
                {
                    foreach (PileNetwork net in comp.Networks)
                    {
                        net.InvalidateSurvey();
                    }
                }
                survey = FlightSurvey();
            }
            currentThrust = survey?.totalThrust ?? 0f;
            if (plannedThrust <= 0f)
            {
                plannedThrust = currentThrust;
            }
        }

        /// <summary>
        /// 推力不足時把航程時鐘往後推：DMSE 的進度是「現在 − 起飛時刻」換算的，起飛時刻往後推就等於這段時間沒有前進。
        /// 入軌點火階段另有自己的起點，一起推。
        /// </summary>
        private void ApplyThrust(int delta)
        {
            if (!InFlight)
            {
                stallCarry = 0f;
                nextThrustCheckTick = -1;
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (nextThrustCheckTick < 0 || now >= nextThrustCheckTick)
            {
                RefreshThrust();
                nextThrustCheckTick = now + VoyageMath.ThrustCheckInterval;
                if (Stalled && !stallNotified)
                {
                    stallNotified = true;
                    Messages.Message("DepthRim.Voyage.Stalled".Translate(Label), this, MessageTypeDefOf.NegativeEvent);
                }
                else if (!Stalled && stallNotified)
                {
                    stallNotified = false;
                    Messages.Message("DepthRim.Voyage.Resumed".Translate(Label), this, MessageTypeDefOf.NeutralEvent, historical: false);
                }
            }
            int stall = VoyageMath.StallTicks(delta, ThrustFraction, ref stallCarry);
            if (stall <= 0)
            {
                return;
            }
            departTick += stall;
            if (phase == TransferPhase.ArrivalBurn && arrivalStartTick >= 0)
            {
                arrivalStartTick += stall;
            }
        }

        /// <summary>中途停船：結束航程，停在現在所在的格子（推進器下次檢查時回到待機）。</summary>
        public void StopHere()
        {
            if (!InFlight)
            {
                return;
            }
            phase = TransferPhase.Arrived;
            destination = Tile;
            landingTarget = PlanetTile.Invalid;
            plannedThrust = 0f;
            stallCarry = 0f;
            stallNotified = false;
            Messages.Message("DepthRim.Voyage.Stopped".Translate(Label), this, MessageTypeDefOf.NeutralEvent);
        }

        public Command StopGizmo()
        {
            return new Command_Action
            {
                defaultLabel = "DepthRim.Voyage.Stop".Translate(),
                defaultDesc = "DepthRim.Voyage.StopDesc".Translate(),
                icon = DiveIcon,
                action = StopHere,
            };
        }

        protected override void TickInterval(int delta)
        {
            if (Destroyed)
            {
                return;
            }
            ApplyThrust(delta);
            if (!InFlight)
            {
                plannedThrust = 0f;
            }
            base.TickInterval(delta);
            if (!depthInitialized)
            {
                InitDepthAfterLaunch();
            }
            if (nextDiveTick >= 0 && Find.TickManager.TicksGame >= nextDiveTick)
            {
                cruiseTier = DiveMath.Step(cruiseTier, targetTier);
                nextDiveTick = cruiseTier == targetTier ? -1 : Find.TickManager.TicksGame + DiveMath.TicksPerTier;
                if (cruiseTier == targetTier)
                {
                    Messages.Message("DepthRim.Sub.DepthReached".Translate(Label, cruiseTier.Label()), this, MessageTypeDefOf.NeutralEvent, historical: false);
                    if (!InFlight && landingTarget.Valid && HasMap)
                    {
                        // 停靠地點：到了那裡的深度才選停船位置（航行指令的停靠、或抵達時深度還沒到）
                        LongEventHandler.ExecuteWhenFinished(() => TransferLandingUtility.BeginLanding(this));
                    }
                }
            }
            NotifyIfTierChanged();
            TickIceBreach();
            TickOverboard();
        }

        /// <summary>下一次檢查艙外人員的時刻（不存檔：讀檔後立刻檢查一次，無害）。</summary>
        private int nextOverboardCheckTick = -1;

        /// <summary>航行中待在艙外的我方人員失蹤（<see cref="VoyageOverboard"/>）。</summary>
        private void TickOverboard()
        {
            int now = Find.TickManager.TicksGame;
            if (!VoyageOverboard.Underway(this))
            {
                nextOverboardCheckTick = -1;
                return;
            }
            if (nextOverboardCheckTick >= 0 && now < nextOverboardCheckTick)
            {
                return;
            }
            nextOverboardCheckTick = now + VoyageOverboard.CheckInterval;
            VoyageOverboard.Sweep(this);
        }

        /// <summary>
        /// 剛分離：航行指令規劃過就從出發時的深度開始、往終端深度走；否則從腳下的海床出發。
        /// </summary>
        private void InitDepthAfterLaunch()
        {
            depthInitialized = true;
            CompDiveControl dive = SubmarineNavigation.HelmOf(this)?.TryGetComp<CompDiveControl>();
            if (dive != null && dive.TakeVoyageDepth(out DepthTier start, out DepthTier end))
            {
                cruiseTier = targetTier = start;
                if (end != start && DiveMath.CanSet(start, end, end, HullRating(out _)) == DiveMath.DiveBlock.None)
                {
                    SetTargetTier(end);
                }
                return;
            }
            cruiseTier = targetTier = Seafloor;
        }

        private void TickIceBreach()
        {
            if (iceBreachAtTick < 0)
            {
                return;
            }
            if (InFlight || cruiseTier != DepthTier.Shallow || targetTier != DepthTier.Shallow)
            {
                SubmarineSurfacing.CancelIceBreach(this, "DepthRim.IceBreach.CancelledMoved".Translate());
                return;
            }
            if (Find.TickManager.TicksGame >= iceBreachAtTick)
            {
                SubmarineSurfacing.ResolveIceBreach(this);
            }
        }

        /// <summary>實際層級變了（換層，或航行經過較淺的海床）：整張地圖的水體重建成新層級的樣子。</summary>
        private void NotifyIfTierChanged()
        {
            DepthTier now = CurrentDepthTier;
            if (notifiedTier == now)
            {
                return;
            }
            notifiedTier = now;
            if (HasMap)
            {
                FloodingUtility.WaterChanged(Map);
            }
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            Thing helm = SubmarineNavigation.HelmOf(this);
            if (helm != null)
            {
                yield return SubmarineNavigation.NavigateGizmo(helm);
            }
            if (InFlight && !isImpact)
            {
                yield return StopGizmo();
            }
            if (HasMap && !landingTarget.Valid && !InFlight)
            {
                foreach (Gizmo g in SubmarineSurfacing.SubmarineGizmos(this))
                {
                    yield return g;
                }
            }
        }

        // ── 下錨（設計稿 §4.7）──────────────────────────────────────────────

        /// <summary>
        /// 下錨的地點：旁邊一格沒有任何世界物件、海床深度和這裡相同的海洋格（潛艇自己這一格已經是潛艇的地圖，
        /// 不能再放一個地點）。找不到時無效。
        /// </summary>
        public PlanetTile FindAnchorage()
        {
            List<PlanetTile> all = Anchorages();
            return all.Count > 0 ? all[0] : PlanetTile.Invalid;
        }

        /// <summary>所有能下錨的鄰格（給玩家在世界地圖上挑）。</summary>
        public List<PlanetTile> Anchorages()
        {
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(Tile, neighbors);
            neighbors.RemoveAll(n => AnchorageBlockReason(n) != null);
            return neighbors;
        }

        /// <summary>這一格不能當下錨點的原因（可以時 null）。</summary>
        public string AnchorageBlockReason(PlanetTile tile)
        {
            if (!tile.Valid || !Find.WorldGrid.IsNeighbor(Tile, tile))
            {
                return "DepthRim.Anchor.Spot.NotAdjacent".Translate();
            }
            if (!OceanRouting.Navigable(tile))
            {
                return "DepthRim.Anchor.Spot.NotOcean".Translate();
            }
            if (Find.WorldObjects.AnyWorldObjectAt(tile))
            {
                return "DepthRim.Anchor.Spot.Occupied".Translate();
            }
            DepthTier there = SeafloorAt(tile);
            if (there != Seafloor)
            {
                return "DepthRim.Anchor.Spot.OtherDepth".Translate(there.Label(), Seafloor.Label());
            }
            return null;
        }

        /// <summary>現在能不能下錨；不能時回傳原因。</summary>
        public string AnchorBlockedReason(out PlanetTile anchorage)
        {
            anchorage = PlanetTile.Invalid;
            if (InFlight)
            {
                return "DepthRim.Anchor.InFlight".Translate();
            }
            DiveMath.AnchorBlock block = DiveMath.CanAnchor(cruiseTier, targetTier, Seafloor);
            if (block == DiveMath.AnchorBlock.NoBottom)
            {
                return "DepthRim.Anchor.Block.NoBottom".Translate(Seafloor.Label());
            }
            if (block == DiveMath.AnchorBlock.NotAtBottom)
            {
                return "DepthRim.Anchor.Block.NotAtBottom".Translate(Seafloor.Label());
            }
            anchorage = FindAnchorage();
            if (!anchorage.Valid)
            {
                return "DepthRim.Anchor.NoSpot".Translate();
            }
            return null;
        }

        /// <summary>自動挑第一個能下錨的鄰格（測試與除錯用；玩家用航行指令（<see cref="SubmarineNavigation"/>））。</summary>
        public void Anchor()
        {
            AnchorAt(FindAnchorage());
        }

        /// <summary>在指定的鄰格建立海洋地點，接著走 DMSE 的降落流程（生成地圖 → 選位 → 船體搬進去）。</summary>
        public void AnchorAt(PlanetTile anchorage)
        {
            string reason = AnchorBlockedReason(out _) ?? AnchorageBlockReason(anchorage);
            if (reason != null)
            {
                Messages.Message(reason, this, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            MapParent site = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
            site.Tile = anchorage;
            site.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(site);
            landingTarget = anchorage;
            TransferLandingUtility.BeginLanding(this);
        }

        /// <summary>下潛指令的圖示：艦橋的建築圖示。</summary>
        public static Texture2D DiveIcon => DefDatabase<ThingDef>.GetNamedSilentFail("DepthRim_Helm")?.uiIcon ?? BaseContent.BadTex;

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string thrust = InFlight
                ? (Stalled ? "DepthRim.Voyage.InspectStalled".Translate().ToString() : "DepthRim.Voyage.InspectThrust".Translate(ThrustFraction.ToStringPercent()).ToString())
                : null;
            string depth = Diving
                ? "DepthRim.Sub.InspectDiving".Translate(CurrentDepthTier.Label(), targetTier.Label()).ToString()
                : "DepthRim.Sub.InspectDepth".Translate(CurrentDepthTier.Label()).ToString();
            if (thrust != null)
            {
                depth = thrust + "\n" + depth;
            }
            if (iceBreachAtTick >= 0)
            {
                depth += "\n" + "DepthRim.IceBreach.InspectHovering".Translate((iceBreachAtTick - Find.TickManager.TicksGame).ToStringTicksToPeriod());
            }
            return s.NullOrEmpty() ? depth : s + "\n" + depth;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref cruiseTier, "cruiseTier");
            Scribe_Values.Look(ref targetTier, "targetTier");
            Scribe_Values.Look(ref nextDiveTick, "nextDiveTick", -1);
            Scribe_Values.Look(ref depthInitialized, "depthInitialized");
            Scribe_Values.Look(ref plannedThrust, "plannedThrust");
            Scribe_Values.Look(ref currentThrust, "currentThrust");
            Scribe_Values.Look(ref stallCarry, "stallCarry");
            Scribe_Values.Look(ref stallNotified, "stallNotified");
            Scribe_Values.Look(ref iceBreachAtTick, "iceBreachAtTick", -1);
            Scribe_Values.Look(ref nextIceBreachTick, "nextIceBreachTick", -1);
            Scribe_Values.Look(ref crackedUntilTick, "crackedUntilTick", -1);
        }
    }
}
