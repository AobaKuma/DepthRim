using System.Reflection;
using HarmonyLib;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>
    /// 驗收測試用：同步的長時間事件（下水、靠岸、上浮、下潛……）要等畫面畫過一次「載入中」才會執行（原版 <c>ShouldWaitUntilDisplayed</c>，
    /// 在 OnGUI 裡設 <c>alreadyDisplayed</c>）。遊戲視窗在背景、沒有輸入時這個引擎不跑 OnGUI，事件就永遠卡住（2026-11-20 查到：
    /// C1／S2 卡在下水、出港，<c>Root.OnGUI</c> 整場 0 次）。等了 <see cref="Frames"/> 個畫面還沒畫過，就當作畫過了。只在驗收測試期間載入。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class LongEventNudge
    {
        public const int Frames = 10;

        private static readonly FieldInfo CurrentEvent = AccessTools.Field(typeof(LongEventHandler), "currentEvent");

        private static object waiting;

        private static int waitedFrames;

        static LongEventNudge()
        {
            if (!AcceptanceDriver.Enabled)
            {
                return;
            }
            new Harmony("DepthRim.Acceptance.LongEventNudge").Patch(AccessTools.Method(typeof(LongEventHandler), nameof(LongEventHandler.LongEventsUpdate)),
                prefix: new HarmonyMethod(typeof(LongEventNudge), nameof(Prefix)));
        }

        public static void Prefix()
        {
            object ev = CurrentEvent?.GetValue(null);
            if (ev == null)
            {
                waiting = null;
                return;
            }
            Traverse t = Traverse.Create(ev);
            if (t.Field("doAsynchronously").GetValue<bool>() || t.Field("alreadyDisplayed").GetValue<bool>())
            {
                return;
            }
            if (!ReferenceEquals(ev, waiting))
            {
                waiting = ev;
                waitedFrames = 0;
            }
            if (++waitedFrames < Frames)
            {
                return;
            }
            t.Field("alreadyDisplayed").SetValue(true);
            Log.Message($"[DepthRim acceptance] long event '{t.Field("eventTextKey").GetValue<string>()}' was never drawn (window in the background); running it anyway");
        }
    }
}
