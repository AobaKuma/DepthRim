using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>中層水域：地形下方經過的生物（MapDrawLayer_Midwater）與巨物經過事件。</summary>
    public static class Tests_Midwater
    {
        private static WaitTicks Updates(int n) => new WaitTicks(MapComponent_Flooding.UpdateInterval * n + 5);

        /// <summary>
        /// W1：在冰洋測試地圖（淺海）上，先確認淺海不畫、不補背景生物、事件不會發生；
        /// 再以深海層級挖出深淵、把水體視覺切到深海，叫一隻大王魷魚正好游過一塊已探索的深淵，
        /// 檢查遮罩、網格、移動與截圖，最後把地形與層級還原。
        /// </summary>
        public static IEnumerable<Wait> W1_MidwaterLayer(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            MapComponent_Midwater comp = map.GetComponent<MapComponent_Midwater>();
            ctx.Require(comp != null, "every map has the midwater component");
            MapDrawLayer_Midwater layer = map.mapDrawer.GetGlobalLayer<MapDrawLayer_Midwater>();
            ctx.Require(layer != null, "vanilla created the midwater draw layer (global MapDrawLayer)");
            comp.ClearAll();
            yield return Updates(2);

            ctx.Check(comp.Tier == DepthTier.Shallow, $"test map is shallow ({comp.Tier})");
            ctx.Check(!comp.Active && !layer.Visible, "shallow maps neither simulate nor draw the midwater");
            ctx.Check(comp.Ambient.Count == 0, "no ambient creatures in the shallows");
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
            ctx.Check(map.IncidentTargetTags().Any(t => t.defName == "DepthRim_OceanMap"), "the ocean map carries the ocean-map incident tag");
            ctx.Check(!DepthRimDefOf.DepthRim_GiantPasses.Worker.CanFireNow(parms), "the giant event cannot fire in the shallows");

            // 深海層級挖出深淵（記下原本的地形，最後還原）
            Dictionary<IntVec3, TerrainDef> before = map.AllCells.ToDictionary(c => c, c => map.terrainGrid.TerrainAt(c));
            int carved = GenStep_AbyssCarve.Carve(map, DepthTier.Deep, 10f, 20f, 0.1f);
            ctx.Require(carved > 0, $"carved an abyss to look through ({carved} cells)");
            List<IntVec3> abyss = map.AllCells.Where(c => map.terrainGrid.TerrainAt(c) == DepthRimDefOf.DepthRim_Abyss && !c.Fogged(map)).ToList();
            // 焦點離地圖邊緣遠一點，鏡頭才不會被夾住
            List<IntVec3> inner = abyss.Where(c => c.x >= 30 && c.z >= 30 && c.x < map.Size.x - 30 && c.z < map.Size.z - 30).ToList();
            if (inner.Count > 0) { abyss = inner; }
            ctx.Require(abyss.Count > 0, $"some carved abyss is explored ({abyss.Count})");
            // 找周圍 12 格內深淵最多的格子，生物才看得清楚
            IntVec3 focus = abyss.OrderByDescending(c => GenRadial.RadialCellsAround(c, 12f, true).Count(n => n.InBounds(map) && map.terrainGrid.TerrainAt(n) == DepthRimDefOf.DepthRim_Abyss)).First();
            ctx.Note($"focus cell {focus}");

            flooding.debugTierOverride = DepthTier.Deep;
            // 切到這張地圖：原版只更新目前地圖的繪製層，挖洞留下的 Terrain 髒標記要在這裡觸發遮罩重建
            Current.Game.CurrentMap = map;
            CameraJumper.TryJump(focus, map);
            map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
            yield return new WaitFrames(10);
            ctx.Check(layer.Visible, "the layer draws once the visual tier is deep");
            ctx.Check(!comp.Active, "but the simulation still follows the real tier (no ambient spawning, no events)");

            Texture2D mask = comp.Mask;
            ctx.Require(mask != null && mask.width == map.Size.x && mask.height == map.Size.z, "abyss mask matches the map size");
            Color32[] px = mask.GetPixels32();
            IntVec3 rock = map.AllCells.First(c => map.terrainGrid.TerrainAt(c) != DepthRimDefOf.DepthRim_Abyss);
            ctx.Check(px[focus.z * map.Size.x + focus.x].r == 255, "mask is set on an explored abyss cell");
            ctx.Check(px[rock.z * map.Size.x + rock.x].r == 0, "mask is clear on the seabed");
            IntVec3 fogged = map.AllCells.FirstOrDefault(c => c.Fogged(map) && map.terrainGrid.TerrainAt(c) == DepthRimDefOf.DepthRim_Abyss);
            if (fogged.IsValid)
            {
                ctx.Check(px[fogged.z * map.Size.x + fogged.x].r == 0, "mask never reveals abyss under the fog");
            }
            ctx.Check(comp.AbyssCells >= abyss.Count, $"abyss count refreshed with the mask ({comp.AbyssCells})");

            // 叫一隻大王魷魚，路線改成正好經過焦點格，時間撥到一半（正好在焦點上）
            MidwaterCreatureDef squidDef = DefDatabase<MidwaterCreatureDef>.GetNamed("DepthRim_Midwater_GiantSquid");
            MidwaterCreature squid = comp.StartPass(squidDef, 0f, 0f);
            ctx.Require(squid != null && comp.AnyGiant, "the giant is swimming");
            Vector2 at = new Vector2(focus.x + 0.5f, focus.z + 0.5f);
            squid.from = at - new Vector2(60f, 10f);
            squid.to = at + new Vector2(60f, 10f);
            squid.durationTicks = 6000;
            squid.ageTicks = 3000;
            MidwaterCreature jelly = comp.StartPass(DefDatabase<MidwaterCreatureDef>.GetNamed("DepthRim_Midwater_Jellyfish"), 0f, 0f);
            jelly.from = at + new Vector2(-6f, 6f);
            jelly.to = at + new Vector2(6f, 4f);
            jelly.durationTicks = 6000;
            jelly.ageTicks = 3000;
            Vector2 p0 = squid.Position;
            ctx.Check(Vector2.Distance(p0, at) < 0.5f, $"the squid is over the focus cell ({p0})");

            // 暫停再拍：截圖要等幾十幀，期間魷魚會游走十幾格、游出深淵
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            foreach (Wait w in ctx.ScreenshotOn(map, focus, 18f, "midwater_giant_squid", 40)) { yield return w; }
            yield return new WaitFrames(30);
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            ctx.Check(layer.LastDrawnCount >= 2, $"the layer drew the squid and the jellyfish ({layer.LastDrawnCount})");
            ctx.Check(squidDef.Material != null && squidDef.Material.shader != null, "the midwater shader resolved");
            yield return new WaitTicks(120);
            ctx.Check(Vector2.Distance(squid.Position, p0) > 1f, $"the squid keeps swimming ({p0} -> {squid.Position})");

            // 遠景：看整隻游過的樣子
            foreach (Wait w in ctx.ScreenshotOn(map, focus, 40f, "midwater_giant_squid_wide", 40)) { yield return w; }
            yield return new WaitFrames(30);

            // 走完就消失
            squid.ageTicks = squid.durationTicks - 1;
            jelly.ageTicks = jelly.durationTicks - 1;
            yield return new WaitTicks(3);
            ctx.Check(!comp.AnyGiant && comp.Count == 0, "creatures are removed after crossing");

            // 還原
            comp.ClearAll();
            flooding.debugTierOverride = null;
            foreach (KeyValuePair<IntVec3, TerrainDef> kv in before)
            {
                if (map.terrainGrid.TerrainAt(kv.Key) != kv.Value)
                {
                    map.terrainGrid.SetTerrain(kv.Key, kv.Value);
                }
            }
            map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
            yield return Updates(1);
            ctx.Check(!layer.Visible, "back to shallow: the layer is hidden again");
        }
    }
}
