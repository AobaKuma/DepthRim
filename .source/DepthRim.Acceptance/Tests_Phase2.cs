using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>二期補完的驗收：深淵挖洞分支與崖壁邊緣（P2-10）、玩家設定（P2-5）。</summary>
    public static class Tests_Phase2
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        /// <summary>
        /// V11：以深海層級在冰洋測試地圖上執行 <see cref="GenStep_AbyssCarve.Carve"/>（真正的挖洞分支），
        /// 檢查挖出的格子、屋頂規則、Odyssey 崖壁邊緣，最後把地形還原，避免影響後面的測試。
        /// </summary>
        public static IEnumerable<Wait> V11_AbyssCarveAndCliffs(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            TerrainDef abyss = DepthRimDefOf.DepthRim_Abyss;

            // 記下原本的地形，測試結束時還原
            Dictionary<IntVec3, TerrainDef> before = new Dictionary<IntVec3, TerrainDef>();
            foreach (IntVec3 c in map.AllCells)
            {
                before[c] = map.terrainGrid.TerrainAt(c);
            }
            int roofedBefore = map.AllCells.Count(c => c.Roofed(map) && before[c] == abyss);

            ctx.Check(GenStep_AbyssCarve.Carve(map, DepthTier.Shallow, 10f, 20f) == 0, "shallow tier carves nothing");
            int carved = GenStep_AbyssCarve.Carve(map, DepthTier.Deep, 10f, 20f);
            ctx.Note($"deep tier carved {carved} cell(s)");
            ctx.Require(carved > 0, "deep tier carves abyss cells (the carve branch actually runs)");
            List<IntVec3> abyssCells = map.AllCells.Where(c => map.terrainGrid.TerrainAt(c) == abyss && before[c] != abyss).ToList();
            ctx.Check(abyssCells.Count == carved, $"carve count matches the changed cells ({abyssCells.Count})");
            ctx.Check(abyssCells.All(c => !c.Roofed(map)), "no roofed cell was carved (an abyss is a hole without a roof)");
            ctx.Check(map.AllCells.Count(c => c.Roofed(map) && map.terrainGrid.TerrainAt(c) == abyss) == roofedBefore, "roofed cells keep their terrain");

            // 崖壁：深淵旁邊的海床格要有 Odyssey 崖壁資料，而且 TerrainEdges 層真的輸出網格
            IntVec3 rim = IntVec3.Invalid;
            foreach (IntVec3 c in abyssCells)
            {
                foreach (IntVec3 n in GenAdj.CardinalDirections.Select(d => c + d))
                {
                    if (!n.InBounds(map)) { continue; }
                    TerrainDef t = map.terrainGrid.TerrainAt(n);
                    if (t != null && !t.dontRender && t.spaceEdgeGraphicData != null) { rim = n; break; }
                }
                if (rim.IsValid) { break; }
            }
            ctx.Require(rim.IsValid, "found a seabed cell next to the abyss that has Odyssey cliff edge data");
            TerrainDef rimTerrain = map.terrainGrid.TerrainAt(rim);
            ctx.Note($"rim terrain: {rimTerrain.defName}");
            ctx.Check(rimTerrain.spaceEdgeGraphicData.FlatTexPath == "Terrain/Surfaces/Asteroid/BorderInside/AsteroidBorderInside_Flat",
                "rim terrain uses the Odyssey asteroid cliff textures");
            SectionLayer edges = map.mapDrawer.SectionAt(rim).GetLayer(typeof(SectionLayer_TerrainEdges));
            ctx.Require(edges != null, "TerrainEdges layer exists");
            edges.Regenerate();
            int edgeVerts = edges.subMeshes.Where(s => s.verts != null).Sum(s => s.verts.Count);
            ctx.Check(edgeVerts > 0, $"cliff edges are drawn next to the abyss ({edgeVerts} vertices)");

            // 深淵格水位 100%、Pawn 走不進去（P2-10 的既有規則在真正挖出的深淵上也成立）
            yield return Updates(1);
            IntVec3 sample = abyssCells[abyssCells.Count / 2];
            ctx.Check(DepthRimFixture.Flooding.LevelAt(sample) >= 0.999f, "carved abyss is full of water");
            ctx.Check(!sample.Walkable(map), "carved abyss is impassable");

            foreach (Wait w in ctx.ScreenshotOn(map, rim, 12f, "abyss_cliff_edges", 40)) { yield return w; }
            yield return new WaitFrames(20);

            // 還原地形
            foreach (IntVec3 c in abyssCells)
            {
                map.terrainGrid.SetTerrain(c, before[c]);
            }
            yield return Updates(1);
            ctx.Check(abyssCells.All(c => map.terrainGrid.TerrainAt(c) == before[c]), "terrain restored after the test");
        }

        /// <summary>
        /// V12：玩家設定（P2-5）。改設定、呼叫 <c>WriteSettings()</c>，確認材質重建、參數跟著改、整張地圖標記重建；
        /// 打開設定視窗截圖；最後還原設定。設定檔寫在驗收用的暫存存檔資料夾，不影響玩家自己的設定。
        /// </summary>
        public static IEnumerable<Wait> V12_ModSettings(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            yield return Updates(1);
            DepthRimSettingsMod mod = LoadedModManager.GetMod<DepthRimSettingsMod>();
            ctx.Require(mod != null, "settings mod is registered");
            DepthRimModSettings settings = mod.GetSettings<DepthRimModSettings>();
            ctx.Check(ReferenceEquals(settings, DepthRimModSettings.Current), "the mod's settings are the ones the renderer reads");

            bool oldParticles = settings.particles;
            bool oldHints = settings.hints;
            float oldMurk = settings.murkScale;
            float oldLamp = settings.lampRadiusOverride;

            WaterVisualTierDef deepDef = WaterVisualTierDef.For(DepthTier.Deep);
            Material deepBefore = WaterTierMaterials.Outer(DepthTier.Deep);
            ctx.Check(deepBefore.GetFloat("_SnowDensity") > 0f, "deep sea has marine snow with default settings");

            settings.particles = false;
            settings.murkScale = 0.5f;
            settings.hints = false;
            settings.lampRadiusOverride = 6f;
            mod.WriteSettings();

            Material deepAfter = WaterTierMaterials.Outer(DepthTier.Deep);
            ctx.Check(!ReferenceEquals(deepBefore, deepAfter), "WriteSettings rebuilds the water materials");
            ctx.Check(deepAfter.GetFloat("_SnowDensity") == 0f && deepAfter.GetFloat("_BiolumDensity") == 0f,
                "particles off: marine snow and bioluminescence are zero");
            ctx.Check(Mathf.Abs(deepAfter.GetFloat("_MurkAlpha") - deepDef.murkAlpha * 0.5f) < 0.01f,
                $"murk scale 0.5 halves the murk alpha ({deepAfter.GetFloat("_MurkAlpha")})");
            Section far = map.mapDrawer.SectionAt(new IntVec3(map.Size.x / 2, 0, map.Size.z / 2));
            ctx.Check((far.dirtyFlags & (ulong)DepthRimDefOf.DepthRim_Water) != 0, "WriteSettings marks the whole map for rebuild");
            ctx.Check(DepthRimModSettings.LampRadiusOf(4.5f) == 6f, "lamp radius override applies");
            IntVec3 open = DepthRimFixture.OpenWaterCell();
            if (open.IsValid)
            {
                Pawn pawn = DepthRimFixture.SpawnColonist(map, open);
                ctx.Check(MapComponent_DarkWaterHints.HintAlphaFor(pawn, DepthRimFixture.Flooding) == 0f, "hints off: no dark-water hint");
            }

            // 設定視窗：真的渲染一次並截圖
            Dialog_ModSettings dialog = new Dialog_ModSettings(mod);
            Find.WindowStack.Add(dialog);
            dialog.forcePause = false;
            dialog.absorbInputAroundWindow = false;
            yield return new WaitFrames(30);
            ctx.Check(Find.WindowStack.IsOpen(dialog), "settings window opens and renders");
            ctx.Screenshot("settings_window");
            yield return new WaitFrames(20);
            dialog.Close(doCloseSound: false);

            // 還原
            settings.particles = oldParticles;
            settings.hints = oldHints;
            settings.murkScale = oldMurk;
            settings.lampRadiusOverride = oldLamp;
            mod.WriteSettings();
            ctx.Check(WaterTierMaterials.Outer(DepthTier.Deep).GetFloat("_SnowDensity") > 0f, "settings restored: marine snow is back");
            yield return new WaitFrames(5);
        }
    }
}
