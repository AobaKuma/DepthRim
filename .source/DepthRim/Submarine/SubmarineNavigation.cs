using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇的單一「航行」指令：取代巡航深度、上浮、破冰上浮、下錨、下潛與 DMSE 的規劃航線
    /// （艦橋的 DMSE 控制台設 <c>showPlanGizmo = false</c>）。
    /// 在世界地圖上點一格，再用浮動選單選終端深度（或在原地上浮、下錨、停靠地點）：
    /// <list type="bullet">
    /// <item>自己這一格：換巡航深度、上浮、破冰上浮。</item>
    /// <item>旁邊的空海域：下錨（DMSE 的降落選位決定船體停在哪裡）。</item>
    /// <item>已有地點（地圖）：停靠，終端深度就是那裡的海床；抵達且到達那個深度後選擇停船位置。</item>
    /// <item>其他海域：航線，終端深度在出發後開始換。確認計畫後直接進入 DMSE 的啟航程序。</item>
    /// </list>
    /// 航行中與浮在海面上時不選格子，直接開選單（改終端深度／下潛到哪個深度）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class SubmarineNavigation
    {
        public static readonly Texture2D NavigateIcon = ContentFinder<Texture2D>.Get("UI/Commands/ViewOrbit", reportFailure: false) ?? BaseContent.BadTex;

        // ==================================================================
        //  情境
        // ==================================================================

        /// <summary>艦橋所在的潛艇（在海中）；在船塢或海面上時 null。</summary>
        public static WorldObject_Submarine SubOf(Thing helm) => helm?.Map?.Parent as WorldObject_Submarine;

        /// <summary>潛艇地圖上的艦橋（有 <see cref="CompDiveControl"/> 與 DMSE 控制台的那一座）。</summary>
        public static Thing HelmOf(WorldObject_Submarine sub)
        {
            if (sub == null || !sub.HasMap)
            {
                return null;
            }
            return sub.Map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b.TryGetComp<CompDiveControl>() != null && b.TryGetComp<CompTransferConsole>() != null);
        }

        /// <summary>出發時的深度：在海中是巡航目標，在船塢是船塢的海床。</summary>
        private static DepthTier CurrentTier(Thing helm)
        {
            WorldObject_Submarine sub = SubOf(helm);
            return sub != null ? sub.targetTier : FloodingUtility.DepthTierOf(helm.Map);
        }

        private static DepthTier HullRating(Map map)
        {
            return map != null ? HullPressure.RatingFor(map, out _) : HullPressure.Unlimited;
        }

        /// <summary>航行指令整個不能用的原因（可以時 null）。</summary>
        public static string NavigateBlockReason(Thing helm)
        {
            if (helm == null || !helm.Spawned)
            {
                return "DepthRim.Sub.NotAboard".Translate();
            }
            if (SubmarineSurfacing.IsSurfacedMap(helm.Map))
            {
                return SubmarineSurfacing.DiveBlockReason(helm, out _, out _);
            }
            WorldObject_Submarine sub = SubOf(helm);
            if (sub != null && sub.InFlight)
            {
                return sub.isImpact ? "DepthRim.Surface.InFlight".Translate().ToString() : null;
            }
            if (sub != null && sub.iceBreachAtTick >= 0)
            {
                return "DepthRim.IceBreach.Hovering".Translate();
            }
            if (!OceanRouting.IsOcean(helm.Map.Tile))
            {
                return "DepthRim.Sub.NotAboard".Translate();
            }
            CompTransferConsole console = helm.TryGetComp<CompTransferConsole>();
            if (console == null)
            {
                return "DepthRim.Sub.NotAboard".Translate();
            }
            return TransferPlanningUtility.CannotPlanReason(console);
        }

        // ==================================================================
        //  指令
        // ==================================================================

        public static Command NavigateGizmo(Thing helm)
        {
            WorldObject_Submarine sub = SubOf(helm);
            DepthTier rating = HullRating(helm?.Map);
            Building weakest = null;
            if (helm?.Map != null)
            {
                HullPressure.RatingFor(helm.Map, out weakest);
            }
            string weak = weakest != null ? "DepthRim.Sub.WeakestPiece".Translate(weakest.LabelCap).ToString() : "";
            string here = sub != null ? sub.Seafloor.Label() : (helm?.Map != null ? FloodingUtility.DepthTierOf(helm.Map).Label() : "");
            Command_Action cmd = new Command_Action
            {
                defaultLabel = sub != null ? "DepthRim.Nav.LabelDepth".Translate(sub.targetTier.Label()) : "DepthRim.Nav.Label".Translate(),
                defaultDesc = "DepthRim.Nav.Desc".Translate(here, rating.Label() + weak),
                icon = NavigateIcon,
                action = () => Begin(helm),
            };
            string reason = NavigateBlockReason(helm);
            if (reason != null)
            {
                cmd.Disable(reason);
            }
            return cmd;
        }

        /// <summary>按下航行：海面上與航行中直接開選單，其他時候先在世界地圖上選格子。</summary>
        public static void Begin(Thing helm)
        {
            string reason = NavigateBlockReason(helm);
            if (reason != null)
            {
                Messages.Message(reason, helm, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (SubmarineSurfacing.IsSurfacedMap(helm.Map))
            {
                Find.WindowStack.Add(new FloatMenu(SurfacedOptions(helm)));
                return;
            }
            WorldObject_Submarine sub = SubOf(helm);
            if (sub != null && sub.InFlight)
            {
                Find.WindowStack.Add(new FloatMenu(InFlightOptions(sub)));
                return;
            }
            BeginPicking(helm);
        }

        private static void BeginPicking(Thing helm)
        {
            PlanetTile origin = helm.Map.Tile;
            CompTransferConsole console = helm.TryGetComp<CompTransferConsole>();
            int maxDistance = console?.Survey != null ? TransferEstimate.For(console.Survey, -1).maxDistance : 0;
            CameraJumper.TryJump(CameraJumper.GetWorldTarget(helm), CameraJumper.MovementMode.Pan);
            Find.WorldSelector.ClearSelection();
            Find.WorldTargeter.BeginTargeting(target =>
            {
                string block = TileBlockReason(helm, target.Tile, maxDistance);
                if (block != null)
                {
                    Messages.Message(block, MessageTypeDefOf.RejectInput, historical: false);
                    return false;
                }
                List<FloatMenuOption> options = OptionsFor(helm, target.Tile);
                LongEventHandler.ExecuteWhenFinished(() => Find.WindowStack.Add(new FloatMenu(options)));
                return true;
            }, canTargetTiles: true, mouseAttachment: NavigateIcon, closeWorldTabWhenFinished: false,
            onUpdate: () =>
            {
                if (helm.Spawned && maxDistance > 0)
                {
                    GenDraw.DrawWorldRadiusRing(origin, maxDistance);
                }
            },
            extraLabelGetter: target => TileBlockReason(helm, target.Tile, maxDistance) ?? "DepthRim.Nav.Choose".Translate().ToString(),
            canSelectTarget: target => TileBlockReason(helm, target.Tile, maxDistance) == null,
            originForClosest: origin, showCancelButton: true);
        }

        /// <summary>這一格完全不能選的原因（可以時 null；選了之後個別選項另有停用原因）。</summary>
        public static string TileBlockReason(Thing helm, PlanetTile tile, int maxDistance)
        {
            PlanetTile origin = helm.Map.Tile;
            if (!tile.Valid || tile.Layer != origin.Layer)
            {
                return TransferText.Key("DMSE.Transfer.Dest.OtherLayer", origin).Translate();
            }
            WorldObject_Submarine sub = SubOf(helm);
            if (tile == origin)
            {
                return sub == null ? "DepthRim.Nav.Docked".Translate().ToString() : null;
            }
            if (sub != null && Find.WorldGrid.IsNeighbor(origin, tile) && (Find.WorldObjects.MapParentAt(tile) != null || sub.AnchorageBlockReason(tile) == null))
            {
                return null;
            }
            if (TransferLandingUtility.IsLandableSite(tile, origin, out _))
            {
                return null;
            }
            if (TransferPlanningUtility.ValidateDestination(origin, tile, maxDistance, showMessage: false))
            {
                return null;
            }
            // 與 DMSE 的 ValidateDestination 同順序，取得要顯示的原因
            string route = TransferEnvironment.RouteWorker(origin)?.DestinationBlockReason(origin, tile);
            if (route != null)
            {
                return route;
            }
            if (!DebugSettings.ignoreGravshipRange && TransferFlightMath.Distance(origin, tile) > maxDistance)
            {
                return TransferText.Key("DMSE.Transfer.Dest.OutOfRange", origin).Translate(Mathf.Max(0, maxDistance));
            }
            return TransferText.Key("DMSE.Transfer.Dest.Occupied", origin).Translate();
        }

        // ==================================================================
        //  選單
        // ==================================================================

        private static FloatMenuOption Option(string label, string block, System.Action action, string tooltip = null)
        {
            FloatMenuOption option = block == null ? new FloatMenuOption(label, action) : new FloatMenuOption(label + " (" + block + ")", null);
            if (tooltip != null)
            {
                option.tooltip = tooltip;
            }
            return option;
        }

        private static string DiveBlockText(DiveMath.DiveBlock block)
        {
            return block == DiveMath.DiveBlock.None ? null : ("DepthRim.Sub.Block." + block).Translate().ToString();
        }

        /// <summary>在世界地圖上選了 <paramref name="tile"/> 之後的選項（給測試用也公開）。</summary>
        public static List<FloatMenuOption> OptionsFor(Thing helm, PlanetTile tile)
        {
            WorldObject_Submarine sub = SubOf(helm);
            PlanetTile origin = helm.Map.Tile;
            if (sub != null && tile == origin)
            {
                return OwnTileOptions(sub);
            }
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            bool adjacent = sub != null && Find.WorldGrid.IsNeighbor(origin, tile);
            MapParent site = Find.WorldObjects.MapParentAt(tile);
            if (site != null && !(site is WorldObject_TransferShip other && other.InFlight))
            {
                options.Add(DockOption(helm, site, adjacent));
                return options;
            }
            if (adjacent && sub.AnchorageBlockReason(tile) == null)
            {
                options.Add(Option("DepthRim.Nav.Anchor".Translate(), sub.AnchorBlockedReason(out _), () => sub.AnchorAt(tile)));
            }
            DepthTier current = CurrentTier(helm);
            DepthTier floor = WorldObject_Submarine.SeafloorAt(tile);
            DepthTier rating = HullRating(helm.Map);
            foreach (DepthTier t in System.Enum.GetValues(typeof(DepthTier)))
            {
                DepthTier tier = t;
                DiveMath.DiveBlock block = DiveMath.CanSet(current, tier, floor, rating);
                if (block == DiveMath.DiveBlock.AlreadyThere)
                {
                    block = DiveMath.DiveBlock.None;
                }
                options.Add(Option("DepthRim.Nav.SailTo".Translate(tier.Label()), DiveBlockText(block), () => PlanVoyage(helm, tile, tier, landOnSite: false)));
            }
            return options;
        }

        /// <summary>自己這一格：換巡航深度、上浮、破冰上浮。</summary>
        public static List<FloatMenuOption> OwnTileOptions(WorldObject_Submarine sub)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            foreach (DepthTier t in System.Enum.GetValues(typeof(DepthTier)))
            {
                DepthTier tier = t;
                options.Add(Option("DepthRim.Sub.Cruise".Translate(tier.Label()), DiveBlockText(sub.CanDiveTo(tier)), () =>
                {
                    sub.landingTarget = PlanetTile.Invalid;
                    sub.SetTargetTier(tier);
                }));
            }
            options.Add(Option("DepthRim.Surface.Label".Translate(), SubmarineSurfacing.SurfaceBlockReason(sub, out _), () => SubmarineSurfacing.Surface(sub), "DepthRim.Surface.Desc".Translate()));
            options.Add(Option("DepthRim.IceBreach.Label".Translate(), SubmarineSurfacing.IceBreachBlockReason(sub, out _, out _), () => SubmarineSurfacing.BeginIceBreach(sub), "DepthRim.IceBreach.Desc".Translate()));
            return options;
        }

        /// <summary>
        /// 停靠一個已有的地點：終端深度就是那裡的海床。就在旁邊時直接開始（還沒到那個深度就先換，到了自動選停船位置）；
        /// 遠的走 DMSE 的「降落到地點」航線，抵達後選停船位置。
        /// </summary>
        private static FloatMenuOption DockOption(Thing helm, MapParent site, bool adjacent)
        {
            WorldObject_Submarine sub = SubOf(helm);
            DepthTier floor = SubmarineLandingWorker.SiteSeafloor(site.Tile);
            string label = "DepthRim.Nav.Dock".Translate(site.LabelCap, floor.Label());
            string block = null;
            if (!DiveMath.Anchorable(floor))
            {
                block = "DepthRim.Anchor.Block.NoBottom".Translate(floor.Label());
            }
            else
            {
                // 就在旁邊：在這一格換深度，受這裡的海床限制；遠的在目的地換
                DiveMath.DiveBlock dive = DiveMath.CanSet(CurrentTier(helm), floor, adjacent ? sub.Seafloor : floor, HullRating(helm.Map));
                if (dive != DiveMath.DiveBlock.AlreadyThere)
                {
                    block = DiveBlockText(dive);
                }
            }
            if (adjacent)
            {
                return Option(label, block, () => DockAdjacent(sub, site, floor));
            }
            return Option(label, block, () => PlanVoyage(helm, site.Tile, floor, landOnSite: true));
        }

        private static void DockAdjacent(WorldObject_Submarine sub, MapParent site, DepthTier floor)
        {
            sub.landingTarget = site.Tile;
            if (sub.cruiseTier == floor && sub.targetTier == floor)
            {
                TransferLandingUtility.BeginLanding(sub);
                return;
            }
            sub.SetTargetTier(floor);
            Messages.Message("DepthRim.Nav.DockWhenReady".Translate(sub.Label, floor.Label(), site.LabelCap), sub, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>航行中：只改終端深度（以目的地的海床判斷）。</summary>
        public static List<FloatMenuOption> InFlightOptions(WorldObject_Submarine sub)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            DepthTier floor = WorldObject_Submarine.SeafloorAt(sub.destination);
            DepthTier rating = sub.HullRating(out _);
            foreach (DepthTier t in System.Enum.GetValues(typeof(DepthTier)))
            {
                DepthTier tier = t;
                options.Add(Option("DepthRim.Nav.EndAt".Translate(tier.Label()), DiveBlockText(DiveMath.CanSet(sub.targetTier, tier, floor, rating)), () => sub.SetTargetTier(tier)));
            }
            return options;
        }

        /// <summary>浮在海面上：下潛到哪個深度。</summary>
        public static List<FloatMenuOption> SurfacedOptions(Thing helm)
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            string block = SubmarineSurfacing.DiveBlockReason(helm, out _, out PlanetTile below);
            DepthTier floor = below.Valid ? WorldObject_Submarine.SeafloorAt(below) : DepthTier.Shallow;
            DepthTier rating = HullRating(helm.Map);
            foreach (DepthTier t in System.Enum.GetValues(typeof(DepthTier)))
            {
                DepthTier tier = t;
                DiveMath.DiveBlock dive = DiveMath.CanSet(DepthTier.Shallow, tier, floor, rating);
                string reason = block ?? (dive == DiveMath.DiveBlock.AlreadyThere ? null : DiveBlockText(dive));
                options.Add(Option("DepthRim.Nav.DiveTo".Translate(tier.Label()), reason, () => SubmarineSurfacing.Dive(helm, tier), "DepthRim.Surface.DiveDesc".Translate()));
            }
            return options;
        }

        // ==================================================================
        //  航線
        // ==================================================================

        /// <summary>
        /// 建立 DMSE 的轉移計畫並開確認視窗；終端深度記在艦橋上，啟航後新的潛艇第一次 tick 時接手。
        /// 確認計畫後直接開始啟航程序（<see cref="TransferPlan.launchOnConfirm"/>）。
        /// </summary>
        public static void PlanVoyage(Thing helm, PlanetTile tile, DepthTier endTier, bool landOnSite)
        {
            CompTransferConsole console = helm.TryGetComp<CompTransferConsole>();
            CompDiveControl dive = helm.TryGetComp<CompDiveControl>();
            if (console == null || dive == null)
            {
                return;
            }
            PlanetTile origin = helm.Map.Tile;
            TransferPlan plan;
            if (landOnSite)
            {
                plan = new TransferPlan(origin, tile, false) { landOnSite = true };
            }
            else
            {
                bool parked = false;
                if (Find.WorldObjects.AnyWorldObjectAt(tile))
                {
                    TransferEstimate estimate = TransferEstimate.For(console.Survey, -1);
                    tile = TransferPlanningUtility.FindFreeNeighbor(origin, tile, estimate.maxDistance);
                    parked = true;
                }
                plan = new TransferPlan(origin, tile, parked);
            }
            WorldObject_Submarine sub = SubOf(helm);
            dive.SetVoyageDepth(sub != null ? sub.cruiseTier : FloodingUtility.DepthTierOf(helm.Map), endTier);
            // 確認後直接開始點火程序；「重新選擇」回到航行指令的選格（DMSE 的計畫擴充點）
            plan.launchOnConfirm = true;
            plan.reselect = () => Begin(helm);
            Find.WindowStack.Add(new Dialog_TransferPlan(console, plan));
        }
    }
}
