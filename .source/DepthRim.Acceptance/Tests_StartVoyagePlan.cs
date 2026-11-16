using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>
    /// 開局潛艇用艦橋的「航行」指令選目的地（玩家實際的流程，G2 是用除錯指令直接出港）。
    /// 只在 run.ps1 -Scenario DepthRim_SubmarineStart -Only G3 時跑。
    /// </summary>
    public static class Tests_StartVoyagePlan
    {
        /// <summary>
        /// G3：開局地圖的艦橋 → 航行指令可用 → 航程內的海洋格能選 → 選了之後有可按的「前往此處」選項 →
        /// 按下去開出 DMSE 的航行計畫視窗 → 計畫能確認、啟航沒有錯誤。
        /// </summary>
        public static IEnumerable<Wait> G3_StartVoyagePlan(TestContext ctx)
        {
            ScenPart_SubmarineStart part = ScenPart_SubmarineStart.Current;
            ctx.Require(part != null, "the game started with the submarine start");
            Map home = Find.CurrentMap;
            CellRect rect = SubmarinePrefab.Footprint(part.Structure, home.Center, part.rot);
            Building helm = rect.Cells.SelectMany(c => c.GetThingList(home)).OfType<Building>()
                .FirstOrDefault(b => b.TryGetComp<CompTransferConsole>() != null && b.TryGetComp<CompDiveControl>() != null);
            ctx.Require(helm != null, "the start submarine has a helm");
            CompTransferConsole console = helm.TryGetComp<CompTransferConsole>();
            MapComponent_PileNetwork.For(home).MarkDirty();
            yield return new WaitTicks(5);

            TransferShipSurvey survey = console.Survey;
            ctx.Require(survey != null, "the helm sees the hull (survey)");
            int maxDistance = TransferEstimate.For(survey, -1).maxDistance;
            ctx.Note($"max voyage distance {maxDistance} tiles; {survey.network.piles.Count} keels");
            foreach (TransferShipSurvey.EngineEntry e in survey.engines)
            {
                Thing eng = e.engine.parent;
                CompPowerTrader power = eng.TryGetComp<CompPowerTrader>();
                ctx.Note($"engine {eng.def.defName} at {eng.Position} rot {eng.Rotation}: {e.status}" + (e.blocker != null ? $" (blocked by {e.blocker.def.defName} at {e.blocker.Position}, faction {e.blocker.Faction?.Name ?? "none"})" : "")
                    + (power != null ? $", power {(power.PowerOn ? "on" : "off")} net {(power.PowerNet != null ? power.PowerNet.CurrentEnergyGainRate().ToString("0.##") + "/" + power.PowerNet.CurrentStoredEnergy().ToString("0") : "none")}" : ""));
            }
            ctx.Check(survey.operationalEngines > 0, $"the start submarine has working thrusters ({survey.operationalEngines}/{survey.engines.Count})");
            ctx.Check(maxDistance > 0, $"the submarine has range ({maxDistance})");

            // 航程內的每一格：能不能選、不能的原因
            PlanetTile origin = home.Tile;
            PlanetLayer layer = origin.Layer;
            var reasons = new Dictionary<string, int>();
            List<PlanetTile> pickable = new List<PlanetTile>();
            for (int i = 0; i < layer.TilesCount; i++)
            {
                PlanetTile t = new PlanetTile(i, layer);
                if (t == origin || TransferFlightMath.Distance(origin, t) > maxDistance)
                {
                    continue;
                }
                string why = SubmarineNavigation.TileBlockReason(helm, t, maxDistance);
                if (why == null)
                {
                    pickable.Add(t);
                }
                else
                {
                    reasons[why] = reasons.TryGetValue(why, out int n) ? n + 1 : 1;
                }
            }
            ctx.Note($"in range: {pickable.Count} pickable; blocked: " + string.Join(" | ", reasons.Select(kv => $"{kv.Key} ×{kv.Value}")));
            ctx.Require(pickable.Count > 0, "some ocean tiles in range can be picked as a destination");

            // 選一格：有可按的「前往此處」
            PlanetTile dest = pickable.OrderByDescending(t => TransferFlightMath.Distance(origin, t)).First();
            List<FloatMenuOption> options = SubmarineNavigation.OptionsFor(helm, dest);
            ctx.Note($"destination {dest} (floor {WorldObject_Submarine.SeafloorAt(dest)}): " + string.Join(" | ", options.Select(o => o.Label + (o.Disabled ? " [disabled]" : ""))));
            FloatMenuOption go = options.FirstOrDefault(o => !o.Disabled);
            ctx.Require(go != null, "picking it offers at least one enabled option");

            // 按下去：DMSE 的航行計畫視窗
            AcceptanceDriver.KeepWindowOpen = typeof(Dialog_TransferPlan);
            go.action();
            yield return new WaitFrames(3);
            Dialog_TransferPlan dialog = Find.WindowStack.WindowOfType<Dialog_TransferPlan>();
            ctx.Require(dialog != null, "the voyage plan window opens");
            TransferPlan plan = (TransferPlan)typeof(Dialog_TransferPlan).GetField("plan", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).GetValue(dialog);
            List<TransferIssue> issues = TransferReadiness.Check(console, plan, includeWarnings: true);
            ctx.Note("plan issues: " + (issues.Count == 0 ? "none" : string.Join(" | ", issues.Select(i => $"{i.severity}: {i.text}"))));
            ctx.Check(!issues.Any(i => i.IsError), "the plan can be confirmed (no errors)");
            dialog.Close();
            AcceptanceDriver.KeepWindowOpen = null;

            console.SetPlan(plan);
            string launch = console.LaunchBlockedReason();
            ctx.Check(launch == null, $"the launch is not blocked ({launch})");
            yield return new WaitFrames(2);
        }
    }
}
