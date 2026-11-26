using DMSE;
using Fortified;
using RimWorld;
using Verse;

namespace DepthRim
{
    public class CompProperties_ThrusterDraw : CompProperties
    {
        /// <summary>沒有在航行時的耗電比例。</summary>
        public float standbyFactor = VoyageMath.StandbyFactor;

        public CompProperties_ThrusterDraw()
        {
            compClass = typeof(CompThrusterDraw);
        }
    }

    /// <summary>
    /// 推進器只有在潛艇航行中才全速耗電，其他時候（停泊、在船塢）待機。航程的成本就是這段耗電
    /// （出航不預扣，見 <see cref="SubmarineEnergy"/>）。推進器每 250 ticks 檢查一次（tickerType Rare）。
    /// </summary>
    public class CompThrusterDraw : ThingComp
    {
        private CompPowerTrader power;

        public CompProperties_ThrusterDraw Props => (CompProperties_ThrusterDraw)props;

        /// <summary>
        /// 這座推進器現在在出力：水下推進器看潛艇（DMSE 轉移飛行）在不在航行，水面推進器看水面船（M2）在不在航行。
        /// 浮在水面上的船，水下推進器不出力；潛航中的潛艇，水面推進器也不出力。
        /// </summary>
        public bool UnderWay => parent.TryGetComp<CompSurfaceThruster>() != null
            ? parent.MapHeld?.Parent is WorldObject_SurfaceShip surface && surface.underWay
            : parent.MapHeld?.Parent is WorldObject_TransferShip ship && ship.InFlight;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            power = parent.GetComp<CompPowerTrader>();
            UpdateDraw();
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            UpdateDraw();
        }

        public void UpdateDraw()
        {
            if (power == null)
            {
                return;
            }
            float full = power.Props.PowerConsumption;
            power.PowerOutput = -(UnderWay ? full : full * Props.standbyFactor);
        }

        public override string CompInspectStringExtra()
        {
            return UnderWay ? null : "DepthRim.Voyage.ThrusterStandby".Translate();
        }
    }
}
