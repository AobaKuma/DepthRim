using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace DepthRim
{
    public class CompProperties_UnderwaterShuttle : CompProperties_Shuttle
    {
        public CompProperties_UnderwaterShuttle()
        {
            compClass = typeof(CompUnderwaterShuttle);
        }
    }

    /// <summary>
    /// 突擊艇的乘員規則，父類別是原版 Odyssey 的 <see cref="CompShuttle"/>：
    /// 登艇浮動選單、多選登艇、載入/卸下 gizmo、艇上內容物的檢視字串與存檔全部沿用原版，
    /// 這裡只覆寫「誰可以上艇」和右鍵文字。
    /// 敵方艇不開放給玩家登艇，襲擊者照舊走 <see cref="JobDriver_BoardCraft"/>（lord 的 duty）。
    /// </summary>
    public class CompUnderwaterShuttle : CompShuttle
    {
        public Building_BoardingCraft Craft => parent as Building_BoardingCraft;

        public override bool IsAllowed(Thing t)
        {
            Building_BoardingCraft craft = Craft;
            if (craft == null || !craft.IsPlayerCraft)
            {
                return false;
            }
            if (t is Pawn pawn)
            {
                return !pawn.Dead && !pawn.Destroyed && pawn.Faction == Faction.OfPlayer;
            }
            return base.IsAllowed(t);
        }

        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn selPawn)
        {
            Building_BoardingCraft craft = Craft;
            if (craft == null || !craft.IsPlayerCraft || !selPawn.CanReach(parent, PathEndMode.Touch, Danger.Deadly))
            {
                yield break;
            }
            string label = "DepthRim.Boarding.BoardOption".Translate(parent.LabelShort);
            if (!IsAllowedNow(selPawn))
            {
                yield return new FloatMenuOption(label + " (" + "NotAllowed".Translate() + ")", null);
                yield break;
            }
            yield return new FloatMenuOption(label, delegate { OrderEnter(selPawn); }, MenuOptionPriority.High);
        }

        public override IEnumerable<FloatMenuOption> CompMultiSelectFloatMenuOptions(IEnumerable<Pawn> selPawns)
        {
            Building_BoardingCraft craft = Craft;
            if (craft == null || !craft.IsPlayerCraft)
            {
                yield break;
            }
            List<Pawn> able = new List<Pawn>();
            foreach (Pawn pawn in selPawns)
            {
                if (pawn.CanReach(parent, PathEndMode.Touch, Danger.Deadly) && IsAllowedNow(pawn))
                {
                    able.Add(pawn);
                }
            }
            string label = "DepthRim.Boarding.BoardOption".Translate(parent.LabelShort);
            if (able.Count == 0)
            {
                yield return new FloatMenuOption(label + " (" + "NotAllowed".Translate() + ")", null);
                yield break;
            }
            yield return new FloatMenuOption(label, delegate
            {
                foreach (Pawn pawn in able)
                {
                    OrderEnter(pawn);
                }
            }, MenuOptionPriority.High);
        }

        /// <summary>
        /// 和原版一樣：先讓載入程序有個 groupID，再派原版的 <see cref="JobDefOf.EnterTransporter"/> 工作。
        /// 真正把人放進 <see cref="CompTransporter.innerContainer"/> 的是原版的 <see cref="JobDriver_EnterTransporter"/>。
        /// </summary>
        private void OrderEnter(Pawn pawn)
        {
            if (!Transporter.LoadingInProgressOrReadyToLaunch)
            {
                TransporterUtility.InitiateLoading(Gen.YieldSingle(Transporter));
            }
            pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.EnterTransporter, parent), JobTag.Misc);
        }

        /// <summary>
        /// 原版的 <c>CompTick</c> 每 600 tick 會把待在艇上超過 60000 tick 的人交給
        /// <see cref="RimWorld.ShipJob_Unload.UnloadThingFromShuttle"/> 從 <see cref="CompShuttle.shipParent"/> 卸下；
        /// 這艘艇沒有 <c>TransportShipDef</c>（<c>shipParent</c> 永遠是 null），那條路會丟 NullReferenceException。
        /// 這裡的艇是「點對點飛過去、落地就放人」，本來就不需要那套穿梭機的卸載排程，所以整個跳過。
        /// 載入中的自動裝載（<c>CheckAutoload</c>）對這艘艇也永遠是關的。
        /// </summary>
        public override void CompTick()
        {
        }

        /// <summary>
        /// 原版只有「玩家的穿梭機」（shipDef.playerShuttle）才關掉 autoload。這裡沒有 TransportShipDef，
        /// 地圖上剛好沒有可送的人或物時 <c>Autoloadable</c> 會回 true，畫出一個沒有意義的自動裝載鈕；
        /// 把它濾掉，其餘 gizmo（載入清單、卸下、任務圖示）照舊。
        /// </summary>
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                if (gizmo is Command_Toggle)
                {
                    continue;
                }
                yield return gizmo;
            }
        }
    }
}
