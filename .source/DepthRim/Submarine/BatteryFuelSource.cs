using DMSE;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 潛艇的「燃料」：船上電池的儲電。DMSE 的轉移數值以化學燃料當量計，這裡 1 單位 = <see cref="WattDaysPerFuel"/> Wd。
    /// 只在海洋層的地圖上算數，所以 DMSE 太空船上的電池不會被當成燃料。
    /// </summary>
    public class BatteryFuelSource : ITransferFuelSource
    {
        /// <summary>
        /// 1 單位燃料 = 1 Wd。一艘 1 樁、兩具推進器的小潛艇走 5 格要 480 單位，約一顆原版電池（600 Wd）。
        /// </summary>
        public const float WattDaysPerFuel = 1f;

        private readonly CompPowerBattery battery;

        public BatteryFuelSource(CompPowerBattery battery)
        {
            this.battery = battery;
        }

        public Thing Parent => battery.parent;

        public float Available => battery.StoredEnergy / WattDaysPerFuel;

        public void Draw(float amount)
        {
            battery.DrawPower(UnityEngine.Mathf.Min(battery.StoredEnergy, amount * WattDaysPerFuel));
        }

        public void DebugFill()
        {
            battery.AddEnergy(battery.Props.storedEnergyMax);
        }

        /// <summary>DMSE 的燃料來源判定：海洋層地圖上的電池。</summary>
        public static ITransferFuelSource Provide(Thing thing)
        {
            if (thing?.Map == null || !thing.Map.Tile.Valid || thing.Map.Tile.LayerDef != DepthRimDefOf.DepthRim_Ocean)
            {
                return null;
            }
            CompPowerBattery battery = thing.TryGetComp<CompPowerBattery>();
            return battery != null ? new BatteryFuelSource(battery) : null;
        }
    }

    [StaticConstructorOnStartup]
    public static class BatteryFuelSourceRegistration
    {
        static BatteryFuelSourceRegistration()
        {
            TransferFuelSources.RegisterProvider(BatteryFuelSource.Provide);
        }
    }
}
