using RimWorld;

namespace DepthRim
{
    /// <summary>
    /// 有事做時吃滿額電力、閒置時只吃待機電力的建築（排水泵、加壓機）。
    /// 待機耗電取自 XML 的 <c>idlePowerDraw</c>（原版欄位，檢視面板也會顯示「運作時需要 X W」）；沒設時一律滿額。
    /// </summary>
    public static class IdlePowerUtility
    {
        public static void SetBusy(CompPowerTrader power, bool busy)
        {
            if (power == null)
            {
                return;
            }
            CompProperties_Power props = power.Props;
            float draw = busy || props.idlePowerDraw < 0f ? props.PowerConsumption : props.idlePowerDraw;
            power.PowerOutput = -draw;
        }
    }
}
