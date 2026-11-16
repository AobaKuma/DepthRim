using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>海面的惡劣海況天氣（2026-11-17）。</summary>
    public static class Tests_SeaWeather
    {
        private static void SetWeather(Map map, WeatherDef def)
        {
            WeatherManager wm = map.weatherManager;
            wm.lastWeather = def;
            wm.curWeather = def;
            HarmonyLib.Traverse.Create(wm).Field("curWeatherAge").SetValue(99999);
        }

        private static void Snap(Map sea)
        {
            sea.GetComponent<MapComponent_SeaSurface>()?.SnapSwayFactor();
            Map platform = SeaLevels.PlatformLevelOf(sea);
            platform?.GetComponent<MapComponent_SeaSurface>()?.SnapSwayFactor();
        }

        /// <summary>
        /// A3：海面地圖的天氣表有兩種惡劣海況；平靜、惡劣海況、暴風雨各拍一張（白浪、浪花、海色）；
        /// 海況決定海面擺動的下限、著色器參數與落海風險；平台層的天氣跟著水面層；
        /// 戶外靠海邊的物品被捲走、屋頂下的不會；海邊的建築被打到受損，但不低於最大耐久的 20%。
        /// </summary>
        public static IEnumerable<Wait> A3_SeaWeather(TestContext ctx)
        {
            Map sea = Tests_SeaSurface.CreateSeaMap(ctx);
            WeatherDef clear = WeatherDefOf.Clear;
            WeatherDef rough = DefDatabase<WeatherDef>.GetNamed("DepthRim_RoughSea");
            WeatherDef storm = DefDatabase<WeatherDef>.GetNamed("DepthRim_RoughSeaStorm");
            List<WeatherDef> table = sea.Biome.baseWeatherCommonalities.Where(r => r.commonality > 0f).Select(r => r.weather).ToList();
            ctx.Check(table.Contains(rough) && table.Contains(storm) && table.Contains(clear), $"the sea map's weather table has both rough-sea weathers ({string.Join(", ", table.Select(w => w.defName))})");

            IntVec3 c = sea.Center;
            CellRect hull = new CellRect(c.x - 10, c.z - 3, 21, 7);
            Tests_SeaSurface.LayHull(sea, hull);
            Building_PillarFoot foot = Tests_SeaSurface.BuildPillar(sea, new IntVec3(c.x, 0, hull.minZ - 5));
            yield return new WaitFrames(3);
            Map platform = SeaLevels.PlatformLevelOf(sea);
            ctx.Require(platform != null, "a pillar made the platform level");
            MapComponent_SeaSurface seaComp = sea.GetComponent<MapComponent_SeaSurface>();
            MapComponent_SeaWeather weatherComp = sea.GetComponent<MapComponent_SeaWeather>();
            ctx.Require(seaComp != null && weatherComp != null, "the sea level has the sea surface and sea weather components");
            Current.Game.CurrentMap = sea;
            IntVec3 view = new IntVec3(hull.minX + 3, 0, hull.minZ - 1);

            try
            {
                // ── 平靜 ──
                SetWeather(sea, clear);
                Snap(sea);
                ctx.Check(seaComp.TargetRoughness() == 0f && seaComp.Roughness == 0f, "clear weather: a calm-looking sea");
                ctx.Check(OverboardHazard.HazardOf(sea) == HazardKind.None || sea.windManager.WindSpeed > 1.1f, "clear weather: no overboard hazard (unless the wind alone is strong)");
                foreach (Wait w in ctx.ScreenshotOn(sea, view, 14f, "sea_calm", 40)) { yield return w; }

                // ── 惡劣海況 ──
                SetWeather(sea, rough);
                Snap(sea);
                ctx.Check(seaComp.TargetSwayFactor() >= 2f - 0.001f, $"rough sea: the sea sways at least 2x ({seaComp.TargetSwayFactor():F2})");
                ctx.Check(Mathf.Abs(seaComp.Roughness - 0.65f) < 0.01f, $"rough sea: roughness 0.65 ({seaComp.Roughness:F2})");
                Vector4 r1 = seaComp.Material.GetVector("_Rough");
                ctx.Check(r1.x > 0.6f && r1.y > 0.3f, $"the sea material gets whitecaps and a darker colour ({r1})");
                ctx.Check(seaComp.Material.GetFloat("_FoamAmount") > 1.5f, "and more foam around pillars and hulls");
                ctx.Check(OverboardHazard.HazardOf(sea) == HazardKind.RoughSea, "rough sea is an overboard hazard");
                weatherComp.SyncPlatformWeather();
                ctx.Check(platform.weatherManager.curWeather == rough, "the platform level follows the water level's weather");
                ctx.Check(platform.GetComponent<MapComponent_SeaSurface>().TargetRoughness() > 0.6f, "and its view of the sea is rough too");
                foreach (Wait w in ctx.ScreenshotOn(sea, view, 14f, "sea_rough", 40)) { yield return w; }

                // ── 暴風雨 ──
                SetWeather(sea, storm);
                Snap(sea);
                ctx.Check(seaComp.TargetSwayFactor() >= 2.5f - 0.001f && seaComp.Roughness > 0.99f, $"storm: the roughest sea ({seaComp.TargetSwayFactor():F2}, {seaComp.Roughness:F2})");
                ctx.Check(storm.rainRate > 0f && storm.eventMakers.Count > 0, "storm: rain and lightning");
                foreach (Wait w in ctx.ScreenshotOn(sea, view, 14f, "sea_storm", 40)) { yield return w; }
                foreach (Wait w in ctx.ScreenshotOn(sea, new IntVec3(c.x, 0, hull.minZ - 6), 24f, "sea_storm_wide", 40)) { yield return w; }

                // ── 捲走物品 ──
                IntVec3 edgeCell = new IntVec3(hull.minX, 0, c.z);
                IntVec3 roomCenter = new IntVec3(hull.maxX - 3, 0, c.z);
                CellRect room = CellRect.CenteredOn(roomCenter, 1).ExpandedBy(1);
                foreach (IntVec3 wc in room.EdgeCells)
                {
                    DepthRimFixture.Spawn(ThingDefOf.Wall, wc, sea, ThingDefOf.Steel);
                }
                foreach (IntVec3 rc in room)
                {
                    sea.roofGrid.SetRoof(rc, RoofDefOf.RoofConstructed);
                }
                yield return new WaitTicks(2);
                Thing outside = DepthRimFixture.Spawn(ThingDefOf.Steel, edgeCell, sea);
                Thing inside = DepthRimFixture.Spawn(ThingDefOf.Steel, roomCenter, sea);
                ctx.Check(weatherComp.Outdoors(edgeCell) && !weatherComp.Outdoors(roomCenter), "the hull edge is outdoors, the roofed room is not");
                MapComponent_SeaWeather.debugAlwaysHit = true;
                int swept = weatherComp.SweepItems(SeaState.Of(sea).itemSweepMtbHours);
                ctx.Check(outside.Destroyed && !inside.Destroyed, $"the waves carry off the stack outside, not the one under the roof ({swept} swept)");

                // 地圖元件自己每 250 ticks 也會掃
                Thing outside2 = DepthRimFixture.Spawn(ThingDefOf.Steel, edgeCell + IntVec3.North, sea);
                yield return new WaitTicks(MapComponent_SeaWeather.CheckInterval + 5);
                ctx.Check(outside2.Destroyed, "the sea weather component sweeps on its own while the storm lasts");

                // ── 打壞建築 ──
                Thing edgeWall = DepthRimFixture.Spawn(ThingDefOf.Wall, new IntVec3(hull.minX, 0, hull.maxZ), sea, ThingDefOf.Steel);
                int max = edgeWall.MaxHitPoints;
                int floor = Mathf.CeilToInt(max * MapComponent_SeaWeather.BuildingHpFloor);
                weatherComp.BatterBuildings(SeaState.Of(sea));
                ctx.Check(edgeWall.HitPoints < max, $"a wall at the water's edge is damaged ({edgeWall.HitPoints}/{max})");
                for (int i = 0; i < 200 && edgeWall.HitPoints > floor; i++)
                {
                    weatherComp.BatterBuildings(SeaState.Of(sea));
                }
                ctx.Check(!edgeWall.Destroyed && edgeWall.HitPoints == floor, $"but never below {MapComponent_SeaWeather.BuildingHpFloor:P0} ({edgeWall.HitPoints}/{max})");
                ctx.Check(foot.HitPoints >= Mathf.CeilToInt(foot.MaxHitPoints * MapComponent_SeaWeather.BuildingHpFloor), "the pillar stands too");
                MapComponent_SeaWeather.debugAlwaysHit = false;

                // ── 平靜下來 ──
                SetWeather(sea, clear);
                Snap(sea);
                Thing calmItem = DepthRimFixture.Spawn(ThingDefOf.Steel, edgeCell + IntVec3.South, sea);
                int hitsBefore = weatherComp.BuildingHits;
                yield return new WaitTicks(MapComponent_SeaWeather.CheckInterval + 5);
                ctx.Check(!calmItem.Destroyed && weatherComp.BuildingHits == hitsBefore && seaComp.Roughness == 0f, "clear weather again: nothing swept or battered, a calm sea");
            }
            finally
            {
                MapComponent_SeaWeather.debugAlwaysHit = false;
                SetWeather(sea, clear);
            }
            Tests_SeaSurface.RemoveSeaMaps(sea);
            yield return new WaitTicks(5);
        }
    }
}
