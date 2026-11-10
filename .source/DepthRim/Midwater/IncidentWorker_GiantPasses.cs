using System.Linq;
using RimWorld;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 巨物經過：一隻巨大的生物從深淵底下緩緩游過整張地圖（純演出，不會攻擊）。
    /// 只在變溫層與深海、而且看得到足夠大的深淵時才會發生；同一時間只有一隻。
    ///
    /// <para>原版說書人的元件只挑 <c>Map_PlayerHome</c> 之類的目標，不會挑海洋地圖，
    /// 所以平常由 <see cref="MapComponent_Midwater"/> 依平均間隔自己觸發；
    /// 這個 IncidentDef 帶有 <c>DepthRim_OceanMap</c> 標籤，開發者選單也能直接執行。</para>
    /// </summary>
    public class IncidentWorker_GiantPasses : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            if (!(parms.target is Map map))
            {
                return false;
            }
            MapComponent_Midwater comp = map.GetComponent<MapComponent_Midwater>();
            if (comp == null || !comp.Active || comp.AnyGiant)
            {
                return false;
            }
            if (comp.AbyssCells < MapComponent_Midwater.MinAbyssCellsForGiant)
            {
                return false;
            }
            return GiantsFor(comp.Tier).Any();
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            Map map = (Map)parms.target;
            MapComponent_Midwater comp = map.GetComponent<MapComponent_Midwater>();
            if (comp == null || !GiantsFor(comp.Tier).TryRandomElement(out MidwaterCreatureDef def))
            {
                return false;
            }
            MidwaterCreature giant = comp.StartPass(def);
            if (giant == null)
            {
                return false;
            }
            if (!def.passMessage.NullOrEmpty())
            {
                Messages.Message(def.passMessage, new LookTargets(new TargetInfo(map.Center, map)), MessageTypeDefOf.NeutralEvent);
            }
            if (map == Find.CurrentMap)
            {
                // 船殼與艙壁被巨物推開的水流壓得低鳴一聲
                DepthRimDefOf.DepthRim_HullCreak?.PlayOneShotOnCamera(map);
            }
            return true;
        }

        public static System.Collections.Generic.IEnumerable<MidwaterCreatureDef> GiantsFor(DepthTier tier)
        {
            return DefDatabase<MidwaterCreatureDef>.AllDefsListForReading.Where(d => d.giant && d.AllowedOn(tier));
        }
    }
}
