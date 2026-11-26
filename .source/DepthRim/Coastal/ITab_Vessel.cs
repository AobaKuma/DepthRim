using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    // ── 設計約束（Refactoring UI，2026-11-21 重新設計艦橋分頁）──
    // 原本直接用 DMSE 的 ITab_TransferShip：在船塢／海面上顯示太空轉移的資料（「航程 無法起飛」「燃料 0」「抵達後安全期」
    // 「船必須位於軌道上」），和這艘船現在能做的事無關；三個 Medium 標題和船名搶層級；600 高的分頁一大半空著。
    // 內容排序：
    //   主要（一個）：這艘船現在能做什麼——依所在情境（船塢／海面／水面航行中／海中／水下航行中）列出可下的指令，
    //               能做就寫「可以」，不能就把原因寫在下一行（文字＋顏色，不只靠顏色）。
    //   次要：船體（格數、龍骨、耐壓、壓載）與動力（電池、推力、航程）的關鍵數字。
    //   第三：可展開的設備明細（推進器、電池；點一下跳到那個建築）、海中時 DMSE 的整備檢查。
    // 數值沿用 DMSE 的 DMSEUI（internal，這裡照抄）：間距 8／區塊 16（區塊間距 > 區塊內），文字三色（白／Muted／更淡），
    // 狀態色 Success／Warning／Error。只有船名用 Medium；區塊標題是小字標籤（Muted）＋細線，不和船名搶。
    // 分頁高度跟著內容（UpdateSize），最多 600、超過才捲動。
    /// <summary>
    /// 艦橋的檢視分頁（取代 DMSE 的 <see cref="ITab_TransferShip"/>）：依船現在的情境列出能做的事、船體與動力的關鍵數字、可展開的設備明細。
    /// </summary>
    public class ITab_Vessel : ITab
    {
        private const float Width = 440f;
        private const float MaxHeight = 600f;
        private const float MinHeight = 220f;
        private const float Pad = 12f;
        private const float Gap = 8f;
        private const float SectionGap = 16f;
        private const float Indent = 12f;

        private static readonly Color Muted = new Color(0.65f, 0.65f, 0.65f);
        private static readonly Color Faint = new Color(0.5f, 0.5f, 0.5f);
        private static readonly Color Success = new Color(0.55f, 0.9f, 0.55f);
        private static readonly Color Warning = new Color(1f, 0.85f, 0.35f);
        private static readonly Color Error = ColorLibrary.RedReadable;

        private Vector2 scrollPos;
        private float lastHeight;
        private bool showDetails;
        private Thing lastHelm;

        private CompTransferConsole Console => SelThing?.TryGetComp<CompTransferConsole>();

        public override bool IsVisible => Console != null;

        public ITab_Vessel()
        {
            size = new Vector2(Width, MaxHeight);
            labelKey = "FFF.Transfer.Tab";
        }

        protected override void UpdateSize()
        {
            base.UpdateSize();
            size = new Vector2(Width, Mathf.Clamp(lastHeight + Pad * 2f, MinHeight, MaxHeight));
        }

        protected override void FillTab()
        {
            using (TransferText.Using(Console?.parent.MapHeld))
            {
                Fill();
            }
        }

        // ==================================================================
        //  版面
        // ==================================================================

        private void Fill()
        {
            CompTransferConsole console = Console;
            Thing helm = console?.parent;
            if (helm == null)
            {
                return;
            }
            if (lastHelm != helm)
            {
                lastHelm = helm;
                scrollPos = Vector2.zero;
                showDetails = false;
            }
            GlobalTargetInfo jump = GlobalTargetInfo.Invalid;
            Rect outRect = new Rect(0f, 0f, size.x, size.y).ContractedBy(Pad);
            bool scrolls = lastHeight > outRect.height;
            Rect viewRect = new Rect(0f, 0f, outRect.width - (scrolls ? 16f : 0f), Mathf.Max(lastHeight, outRect.height));
            Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);
            Listing_Standard listing = new Listing_Standard { verticalSpacing = 2f };
            listing.Begin(new Rect(0f, 0f, viewRect.width, 99999f));

            Map map = helm.MapHeld;
            PileNetwork net = map == null ? null : MapComponent_PileNetwork.For(map)?.NetworkContaining(helm);
            net?.RecalculateFootprint();
            HashSet<IntVec3> footprint = net != null ? new HashSet<IntVec3>(net.Footprint) : new HashSet<IntVec3>();

            DrawTitle(listing, console, Context(helm, net, footprint));
            if (net == null || footprint.Count == 0)
            {
                listing.Gap(Gap);
                Colored(listing, "DepthRim.Surface.NotOnHull".Translate(), Error);
            }
            else
            {
                listing.Gap(SectionGap);
                DrawActions(listing, helm);
                DrawVoyage(listing, helm);
                listing.Gap(SectionGap);
                DrawHull(listing, helm, net, footprint);
                listing.Gap(SectionGap);
                DrawPower(listing, helm, console, footprint);
                listing.Gap(SectionGap);
                if (listing.ButtonText((showDetails ? "DepthRim.VesselTab.HideDetails" : "DepthRim.VesselTab.ShowDetails").Translate()))
                {
                    showDetails = !showDetails;
                }
                if (showDetails)
                {
                    listing.Gap(Gap);
                    DrawDetails(listing, helm, console, footprint, ref jump);
                }
            }

            lastHeight = listing.CurHeight + 4f;
            listing.End();
            Widgets.EndScrollView();
            if (jump.IsValid)
            {
                CameraJumper.TryJumpAndSelect(jump);
            }
        }

        /// <summary>船名（唯一的大字）＋改名按鈕，下面一行淡色的情境。</summary>
        private static void DrawTitle(Listing_Standard listing, CompTransferConsole console, string context)
        {
            Text.Font = GameFont.Medium;
            Rect rect = listing.GetRect(Text.LineHeight);
            Rect btn = new Rect(rect.xMax - 24f, rect.y + (rect.height - 24f) / 2f, 24f, 24f);
            Rect label = new Rect(rect.x, rect.y, rect.width - btn.width - Gap, rect.height);
            string name = console.ShipName;
            if (name.NullOrEmpty())
            {
                GUI.color = Muted;
                Widgets.Label(label, TransferText.Key("FFF.Transfer.Console.Unnamed").Translate());
            }
            else
            {
                Widgets.Label(label, name.Truncate(label.width));
            }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            TooltipHandler.TipRegion(btn, TransferText.Key("FFF.Transfer.Console.Rename").Translate());
            if (Widgets.ButtonImage(btn, TexButton.Rename))
            {
                Find.WindowStack.Add(new Dialog_RenameTransferShip(console));
            }
            Colored(listing, context, Muted);
        }

        // ── 主要：現在能做什麼 ──

        private static void DrawActions(Listing_Standard listing, Thing helm)
        {
            SectionLabel(listing, "DepthRim.VesselTab.Actions".Translate());
            Map map = helm.Map;
            if (map == null)
            {
                return;
            }
            WorldObject_Submarine sub = SubmarineNavigation.SubOf(helm);
            WorldObject_SurfaceShip ship = SurfaceVoyage.ShipOf(helm);
            if (CoastalLaunch.IsShipyardMap(map))
            {
                ActionRow(listing, "DepthRim.Launch.Label".Translate(), CoastalLaunch.BlockReason(helm, out _, out _));
                return;
            }
            if (SubmarineSurfacing.IsSurfacedMap(map))
            {
                if (ship != null && ship.underWay)
                {
                    ActionRow(listing, "DepthRim.Sail.Stop".Translate(), null);
                    return;
                }
                if (SeaLevels.IsSeaLevel(map))
                {
                    string sail = SurfaceVoyage.BlockReason(helm, out SurfaceVoyage.Plan plan);
                    ActionRow(listing, "DepthRim.Sail.Option".Translate(), sail,
                        sail == null ? "DepthRim.VesselTab.RangeTiles".Translate(Mathf.Min(plan.range, SubmarineEnergyWorker.MaxRangeTiles)).ToString() : null);
                    Map colony = Docking.AdjacentColony(map.Tile);
                    if (colony != null)
                    {
                        ActionRow(listing, "DepthRim.Dock.Option".Translate(colony.Parent.LabelCap), Docking.BlockReason(helm, out _, out _));
                    }
                }
                ActionRow(listing, "DepthRim.VesselTab.Dive".Translate(), SubmarineSurfacing.DiveBlockReason(helm, out _, out _));
                return;
            }
            if (sub != null)
            {
                if (sub.InFlight)
                {
                    if (!sub.isImpact)
                    {
                        ActionRow(listing, "DepthRim.Sail.Stop".Translate(), null);
                    }
                    return;
                }
                ActionRow(listing, "DepthRim.Nav.Label".Translate(), SubmarineNavigation.NavigateBlockReason(helm));
                ActionRow(listing, "DepthRim.VesselTab.Surface".Translate(), SubmarineSurfacing.SurfaceBlockReason(sub, out _));
                return;
            }
            ActionRow(listing, "DepthRim.Nav.Label".Translate(), SubmarineNavigation.NavigateBlockReason(helm));
        }

        /// <summary>航行中：目的地、剩幾格、還要多久。</summary>
        private static void DrawVoyage(Listing_Standard listing, Thing helm)
        {
            WorldObject_SurfaceShip ship = SurfaceVoyage.ShipOf(helm);
            if (ship != null && ship.underWay)
            {
                listing.Gap(SectionGap);
                SectionLabel(listing, "DepthRim.VesselTab.Voyage".Translate());
                Row(listing, "DepthRim.VesselTab.TilesLeft".Translate(), "DepthRim.VesselTab.RangeTiles".Translate(ship.TilesLeft));
                Row(listing, "DepthRim.VesselTab.Eta".Translate(), ship.stalled
                    ? "DepthRim.VesselTab.Stalled".Translate().ToString()
                    : VesselTabMath.EtaTicks(ship.TilesLeft, ship.progress, ship.ticksPerTile, ship.thrustFraction).ToStringTicksToPeriod(),
                    ship.stalled ? Error : (Color?)null);
                return;
            }
            WorldObject_Submarine sub = SubmarineNavigation.SubOf(helm);
            if (sub != null && sub.InFlight)
            {
                listing.Gap(SectionGap);
                SectionLabel(listing, "DepthRim.VesselTab.Voyage".Translate());
                Row(listing, TransferText.Key("FFF.Transfer.UI.Phase").Translate(), sub.CurrentPhaseLabel);
                Row(listing, TransferText.Key("FFF.Transfer.UI.FlightProgress").Translate(), sub.Progress.ToStringPercent());
                Row(listing, (sub.isImpact ? "FFF.Transfer.UI.ImpactIn" : "FFF.Transfer.UI.ArrivalIn").Translate(), sub.TotalTicksLeft.ToStringTicksToPeriod());
                if (sub.Stalled)
                {
                    Colored(listing, "DepthRim.VesselTab.Stalled".Translate(), Error);
                }
            }
        }

        // ── 次要：船體與動力 ──

        private static void DrawHull(Listing_Standard listing, Thing helm, PileNetwork net, HashSet<IntVec3> footprint)
        {
            SectionLabel(listing, "DepthRim.VesselTab.Hull".Translate());
            Row(listing, "DepthRim.VesselTab.HullSize".Translate(), "DepthRim.VesselTab.HullSizeValue".Translate(footprint.Count, net.piles.Count));
            WorldObject_Submarine sub = SubmarineNavigation.SubOf(helm);
            Building weakest;
            DepthTier rating = sub != null ? HullPressure.RatingFor(helm.Map, out weakest) : VesselTabMath.EstimatedRating(helm.Map, footprint, out weakest);
            string ratingText = weakest == null ? "DepthRim.VesselTab.NoHullPieces".Translate().ToString() : rating.Label();
            Row(listing, sub != null ? "DepthRim.VesselTab.Rating".Translate() : "DepthRim.VesselTab.RatingEstimate".Translate(), ratingText,
                sub != null && sub.HullOverloaded ? Error : (Color?)null);
            if (weakest != null)
            {
                Colored(listing, "DepthRim.VesselTab.Weakest".Translate(weakest.LabelCap), Faint, Indent);
            }
            int tanks = Ballast.TanksIn(helm.Map, footprint);
            int needed = Ballast.Needed(footprint.Count);
            Row(listing, "DepthRim.VesselTab.Ballast".Translate(), tanks + " / " + needed,
                tanks <= 0 ? Faint : (tanks < needed ? Warning : (Color?)null));
            if (tanks <= 0)
            {
                Colored(listing, (sub != null ? "DepthRim.VesselTab.NoBallastSubmerged" : "DepthRim.VesselTab.NoBallast").Translate(), Faint, Indent);
            }
            else if (tanks < needed)
            {
                Colored(listing, "DepthRim.VesselTab.BallastShort".Translate(), Faint, Indent);
            }
        }

        private static void DrawPower(Listing_Standard listing, Thing helm, CompTransferConsole console, HashSet<IntVec3> footprint)
        {
            SectionLabel(listing, "DepthRim.VesselTab.Power".Translate());
            Map map = helm.Map;
            VesselTabMath.Batteries(map, footprint, out float stored, out float max);
            Row(listing, "DepthRim.VesselTab.Battery".Translate(), max > 0f ? stored.ToString("0") + " / " + max.ToString("0") + " Wd" : "-",
                max <= 0f || stored <= 0f ? Error : (Color?)null);
            WorldObject_Submarine sub = SubmarineNavigation.SubOf(helm);
            if (sub != null)
            {
                // 水下：DMSE 的轉移估算（電池當燃料、推進器的推重比）
                TransferShipSurvey survey = console.Survey;
                if (survey != null)
                {
                    int active = survey.engines.Count(e => e.Operational);
                    Row(listing, "DepthRim.VesselTab.Thrusters".Translate(), active + " / " + survey.engines.Count, active == 0 ? Error : (Color?)null);
                    TransferEstimate estimate = TransferEstimate.For(survey, -1);
                    Row(listing, "DepthRim.VesselTab.SubRange".Translate(), estimate.maxDistance >= 1
                        ? "DepthRim.VesselTab.RangeTiles".Translate(estimate.maxDistance).ToString()
                        : "DepthRim.VesselTab.CannotGo".Translate().ToString(), estimate.maxDistance < 1 ? Error : (Color?)null);
                }
                return;
            }
            SurfaceVoyage.Plan plan = SurfaceVoyage.PlanFor(helm);
            List<Building> thrusters = SurfaceThrusters(map, footprint);
            int working = thrusters.Count(SurfaceVoyage.Operational);
            Row(listing, "DepthRim.VesselTab.SurfaceThrusters".Translate(), working + " / " + thrusters.Count, working == 0 ? Error : (Color?)null);
            if (plan != null && plan.thrust > 0f)
            {
                Row(listing, "DepthRim.VesselTab.PerTile".Translate(), Mathf.RoundToInt(plan.hoursPerTile * GenDate.TicksPerHour).ToStringTicksToPeriod());
                Row(listing, "DepthRim.VesselTab.SurfaceRange".Translate(), "DepthRim.VesselTab.RangeTiles".Translate(Mathf.Min(plan.range, SubmarineEnergyWorker.MaxRangeTiles)),
                    plan.range < 1 ? Error : (Color?)null);
            }
        }

        // ── 第三：設備明細 ──

        private static void DrawDetails(Listing_Standard listing, Thing helm, CompTransferConsole console, HashSet<IntVec3> footprint, ref GlobalTargetInfo jump)
        {
            Map map = helm.Map;
            WorldObject_Submarine sub = SubmarineNavigation.SubOf(helm);
            if (sub != null)
            {
                TransferShipSurvey survey = console.Survey;
                SectionLabel(listing, "DepthRim.VesselTab.Thrusters".Translate());
                if (survey == null || survey.engines.Count == 0)
                {
                    Colored(listing, "DepthRim.VesselTab.None".Translate(), Faint);
                }
                else
                {
                    foreach (TransferShipSurvey.EngineEntry e in survey.engines)
                    {
                        if (LinkRow(listing, e.engine.parent.LabelCap, CompTransferEngine.StatusLabel(e.status, e.blocker), e.Operational ? (Color?)null : Error))
                        {
                            jump = e.engine.parent;
                        }
                    }
                }
                listing.Gap(SectionGap);
                SectionLabel(listing, TransferText.Key("FFF.Transfer.UI.IssuesHeader").Translate());
                TransferUIUtility.DrawIssues(listing, console.Issues, ref jump);
                listing.Gap(SectionGap);
            }
            else
            {
                SectionLabel(listing, "DepthRim.VesselTab.SurfaceThrusters".Translate());
                List<Building> thrusters = SurfaceThrusters(map, footprint);
                if (thrusters.Count == 0)
                {
                    Colored(listing, "DepthRim.VesselTab.None".Translate(), Faint);
                }
                foreach (Building b in thrusters)
                {
                    bool ok = SurfaceVoyage.Operational(b);
                    if (LinkRow(listing, b.LabelCap, (ok ? "DepthRim.VesselTab.Working" : "DepthRim.VesselTab.Idle").Translate(), ok ? (Color?)null : Warning))
                    {
                        jump = b;
                    }
                }
                listing.Gap(SectionGap);
            }
            SectionLabel(listing, "DepthRim.VesselTab.Battery".Translate());
            List<Building> batteries = map.listerBuildings.allBuildingsColonist
                .Where(b => footprint.Contains(b.Position) && b.TryGetComp<CompPowerBattery>() != null).ToList();
            if (batteries.Count == 0)
            {
                Colored(listing, "DepthRim.VesselTab.None".Translate(), Faint);
            }
            foreach (Building b in batteries)
            {
                CompPowerBattery battery = b.TryGetComp<CompPowerBattery>();
                if (LinkRow(listing, b.LabelCap, battery.StoredEnergy.ToString("0") + " / " + battery.Props.storedEnergyMax.ToString("0") + " Wd", null))
                {
                    jump = b;
                }
            }
        }

        // ==================================================================
        //  情境與元件
        // ==================================================================

        /// <summary>船名下面那一行：在哪裡、在做什麼。</summary>
        private static string Context(Thing helm, PileNetwork net, HashSet<IntVec3> footprint)
        {
            Map map = helm.Map;
            if (map == null)
            {
                return "";
            }
            if (CoastalLaunch.IsShipyardMap(map))
            {
                return "DepthRim.VesselTab.InPort".Translate(map.Parent.LabelCap);
            }
            WorldObject_SurfaceShip ship = SurfaceVoyage.ShipOf(helm);
            if (ship != null && ship.underWay)
            {
                return "DepthRim.VesselTab.SailingSurface".Translate();
            }
            if (SubmarineSurfacing.IsSurfacedMap(map))
            {
                return "DepthRim.VesselTab.Surfaced".Translate();
            }
            WorldObject_Submarine sub = SubmarineNavigation.SubOf(helm);
            if (sub != null)
            {
                if (sub.InFlight)
                {
                    return "DepthRim.VesselTab.SailingSubmerged".Translate(sub.CurrentDepthTier.Label());
                }
                return sub.Diving
                    ? "DepthRim.VesselTab.Diving".Translate(sub.CurrentDepthTier.Label(), sub.targetTier.Label())
                    : "DepthRim.VesselTab.Submerged".Translate(sub.CurrentDepthTier.Label());
            }
            return "";
        }

        private static List<Building> SurfaceThrusters(Map map, HashSet<IntVec3> footprint)
        {
            return map.listerBuildings.allBuildingsColonist.Where(b => footprint.Contains(b.Position) && b.TryGetComp<CompSurfaceThruster>() != null).ToList();
        }

        /// <summary>區塊標題：小字、淡色、底下一條細線（標題是標籤，不和船名搶）。</summary>
        private static void SectionLabel(Listing_Standard listing, string text)
        {
            GUI.color = Muted;
            listing.Label(text);
            Rect line = listing.GetRect(1f);
            GUI.color = new Color(1f, 1f, 1f, 0.15f);
            Widgets.DrawLineHorizontal(line.x, line.y, line.width);
            GUI.color = Color.white;
            listing.Gap(4f);
        }

        /// <summary>一個指令：左邊指令名，右邊「可以」（或給的值）／「不行」；不行時原因寫在下一行。</summary>
        private static void ActionRow(Listing_Standard listing, string label, string reason, string okValue = null)
        {
            bool ok = reason == null;
            label = label.TrimEnd('.', '…', ' ');
            Row(listing, label,ok ? okValue ?? "DepthRim.VesselTab.Ready".Translate().ToString() : "DepthRim.VesselTab.Blocked".Translate().ToString(),
                ok ? Success : Error, labelColor: Color.white);
            if (!ok)
            {
                Colored(listing, reason, Muted, Indent);
                listing.Gap(2f);
            }
        }

        /// <summary>標籤（Muted）＋右對齊的值；長文字會換行，不會疊在一起。</summary>
        private static Rect Row(Listing_Standard listing, string label, string value, Color? valueColor = null, Color? labelColor = null)
        {
            float width = listing.ColumnWidth;
            float labelWidth = width * 0.55f - Gap;
            float valueWidth = width * 0.45f;
            Rect row = listing.GetRect(Mathf.Max(Text.CalcHeight(label, labelWidth), Text.CalcHeight(value, valueWidth)));
            TextAnchor anchor = Text.Anchor;
            GUI.color = labelColor ?? Muted;
            Text.Anchor = TextAnchor.UpperLeft;
            Widgets.Label(new Rect(row.x, row.y, labelWidth, row.height), label);
            GUI.color = valueColor ?? Color.white;
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(new Rect(row.xMax - valueWidth, row.y, valueWidth, row.height), value);
            Text.Anchor = anchor;
            GUI.color = Color.white;
            return row;
        }

        /// <summary>可以點的列（跳到那個建築）。</summary>
        private static bool LinkRow(Listing_Standard listing, string label, string value, Color? valueColor)
        {
            Rect rect = Row(listing, label, value, valueColor, Color.white);
            Widgets.DrawHighlightIfMouseover(rect);
            TooltipHandler.TipRegion(rect, "FFF.UI.Locate".Translate());
            return Widgets.ButtonInvisible(rect);
        }

        private static void Colored(Listing_Standard listing, string text, Color color, float indent = 0f)
        {
            Rect rect = listing.GetRect(Text.CalcHeight(text, listing.ColumnWidth - indent));
            GUI.color = color;
            Widgets.Label(new Rect(rect.x + indent, rect.y, rect.width - indent, rect.height), text);
            GUI.color = Color.white;
        }
    }

    /// <summary>艦橋分頁用到的計算（不畫東西）。</summary>
    public static class VesselTabMath
    {
        /// <summary>水面航行還要多久（ticks）：剩下的格數扣掉這一格走了的比例，照現在的推力比例。</summary>
        public static int EtaTicks(int tilesLeft, float progress, int ticksPerTile, float thrustFraction)
        {
            if (ticksPerTile <= 0 || ticksPerTile == int.MaxValue || tilesLeft <= 0)
            {
                return 0;
            }
            float tiles = Mathf.Max(0f, tilesLeft - Mathf.Clamp01(progress));
            return Mathf.RoundToInt(tiles * ticksPerTile / Mathf.Max(0.05f, thrustFraction));
        }

        public static void Batteries(Map map, ICollection<IntVec3> footprint, out float stored, out float max)
        {
            stored = 0f;
            max = 0f;
            if (map == null)
            {
                return;
            }
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (footprint.Contains(b.Position) && b.TryGetComp<CompPowerBattery>() is CompPowerBattery battery)
                {
                    stored += battery.StoredEnergy;
                    max += battery.Props.storedEnergyMax;
                }
            }
        }

        /// <summary>
        /// 還沒下水時預估的耐壓等級：船體外緣（旁邊有一格不在船體內）的船殼建築裡最弱的那一塊。
        /// 下水後用 <see cref="HullPressure.RatingFor"/>（實際面向開放水域的那些）。
        /// </summary>
        public static DepthTier EstimatedRating(Map map, HashSet<IntVec3> footprint, out Building weakest)
        {
            weakest = null;
            DepthTier rating = HullPressure.Unlimited;
            if (map == null)
            {
                return rating;
            }
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (!footprint.Contains(b.Position) || !HullPressure.IsHullPiece(b) || !GenAdj.CellsAdjacentCardinal(b).Any(c => !footprint.Contains(c)))
                {
                    continue;
                }
                DepthTier r = HullPressure.RatingOf(b.def);
                if (weakest == null || r < rating)
                {
                    rating = r;
                    weakest = b;
                }
            }
            return rating;
        }
    }
}
