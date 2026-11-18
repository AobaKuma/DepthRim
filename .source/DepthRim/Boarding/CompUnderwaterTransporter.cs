using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DepthRim
{
    public class CompProperties_UnderwaterTransporter : CompProperties_Transporter
    {
        public CompProperties_UnderwaterTransporter()
        {
            compClass = typeof(CompUnderwaterTransporter);
        }
    }

    /// <summary>
    /// 突擊艇的容器（原版 <see cref="CompTransporter"/>）的存檔修正。
    /// 艇離場後整個艇（連同艇上的人與貨）被放進世界物件裡，艇和容器都沒有 spawned 的父物件；
    /// 原版在這種情況下會把容器裡的人從內容物拿出來、改用 reference（世界 pawn）存，
    /// 但這裡的人是跟著艇 deep-scribe 的普通 pawn，不是世界 pawn，存檔會掉人。
    /// 所以不論艇在哪裡，內容物一律 deep-scribe——和重構前自製的 <c>aboard</c> 容器一致。
    /// </summary>
    public class CompUnderwaterTransporter : CompTransporter
    {
        public override void PostExposeData()
        {
            Scribe_Values.Look(ref groupID, "groupID", 0);
            Scribe_Deep.Look(ref innerContainer, "innerContainer", this);
            Scribe_Collections.Look(ref leftToLoad, "leftToLoad", LookMode.Deep);
            Scribe_Values.Look(ref massCapacityOverride, "massCapacityOverride", 0f);
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                leftToLoad?.RemoveWhere(t => t == null);
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                innerContainer ??= new ThingOwner<Thing>(this);
                leftToLoad ??= new List<TransferableOneWay>();
            }
        }
    }
}
