using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>
    /// 驗收測試用：以 <c>-depthrim-scenario=&lt;ScenarioDef&gt;</c> 啟動時，-quicktest 改用這個場景開新遊戲（原版寫死「墜機」）。
    /// 其餘和原版 <see cref="Root_Play.SetupForQuickTestPlay"/> 一樣：產生世界、隨機開局地點、地圖 250；
    /// 之後原版照常 PrepForMapGen → Scenario.PreMapGenerate → Game.InitNewGame，所以場景自己的開局流程（換開局格、地圖生成器、
    /// 放殖民者）完整跑一遍，只少了選地點的頁面（隨機地點由場景的補丁挑）。只在驗收測試期間載入。
    /// 這時不跑 <see cref="DepthRimFixture.PrepareGameOnce"/>（它會在「家」地圖上挖測試區），測試用 <c>-depthrim-only</c> 選新遊戲的測試。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ScenarioOverride
    {
        public const string ArgKey = "depthrim-scenario";

        public static ScenarioDef Scenario { get; private set; }

        public static bool Active => Scenario != null;

        /// <summary>開新遊戲之後的錯誤與例外（模組載入時別的模組報的錯不算）。原版的 Log.Messages 有上限，舊訊息會被擠掉，所以自己收。</summary>
        public static readonly System.Collections.Generic.List<string> Errors = new System.Collections.Generic.List<string>();

        static ScenarioOverride()
        {
            if (!AcceptanceDriver.Enabled || !GenCommandLine.TryGetCommandLineArg(ArgKey, out string defName) || defName.NullOrEmpty())
            {
                return;
            }
            Scenario = DefDatabase<ScenarioDef>.GetNamedSilentFail(defName);
            if (Scenario == null)
            {
                Log.Error($"[DepthRim acceptance] -{ArgKey}={defName}: no such ScenarioDef; using Crashlanded.");
                return;
            }
            new Harmony("DepthRim.Acceptance.Scenario").Patch(AccessTools.Method(typeof(Root_Play), nameof(Root_Play.SetupForQuickTestPlay)),
                prefix: new HarmonyMethod(typeof(ScenarioOverride), nameof(Prefix)));
        }

        public static bool Prefix()
        {
            Log.Message($"[DepthRim acceptance] quicktest with scenario {Scenario.defName}");
            UnityEngine.Application.logMessageReceivedThreaded += (text, trace, type) =>
            {
                if (type == UnityEngine.LogType.Error || type == UnityEngine.LogType.Exception)
                {
                    lock (Errors) { Errors.Add(text.Split(new[] { '\n' })[0]); }
                }
            };
            Current.ProgramState = ProgramState.Entry;
            Game.ClearCaches();
            Current.Game = new Game();
            Current.Game.InitData = new GameInitData();
            Current.Game.Scenario = Scenario.scenario.CopyForEditing();
            Find.Scenario.PreConfigure();
            Current.Game.storyteller = new Storyteller(StorytellerDefOf.Cassandra, DifficultyDefOf.Rough);
            Current.Game.World = WorldGenerator.GenerateWorld(0.3f, GenText.RandomSeedString(), OverallRainfall.Normal, OverallTemperature.Normal,
                OverallPopulation.Normal, LandmarkDensity.Normal);
            Find.GameInitData.ChooseRandomStartingTile();
            Find.GameInitData.mapSize = 250;
            Find.Scenario.PostIdeoChosen();
            return false;
        }
    }
}
