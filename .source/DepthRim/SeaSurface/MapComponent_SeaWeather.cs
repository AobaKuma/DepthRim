using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海面地圖的天氣與海況（2026-11-17）：
    /// <list type="bullet">
    /// <item>平台層的天氣跟著水面層（同一片天空；平台層自己不選天氣，<see cref="Patch_PlatformLevelWeather"/>）。</item>
    /// <item>水面層在惡劣海況的天氣下（<see cref="SeaStateExtension"/>），每 <see cref="CheckInterval"/> ticks：
    ///   戶外存放的物品有機率被浪捲走（銷毀），靠海邊的建築有機率被浪打到受損。平台層高出海面 14.1 格，浪打不到。</item>
    /// </list>
    /// 艙外的人被浪捲走另見 <see cref="MapComponent_RoughSeaOverboard"/>（海況的天氣倍率下限讓惡劣海況一定算落海風險）。
    /// </summary>
    public class MapComponent_SeaWeather : MapComponent
    {
        public const int CheckInterval = 250;

        /// <summary>甲板中央（2 格內沒有開放海面）、但仍在戶外的東西，被浪打中的倍率。</summary>
        public const float InnerFactor = 0.25f;

        /// <summary>建築被浪打不會低於最大耐久的這個比例：浪會打壞東西，但不會把整座基地拆掉。</summary>
        public const float BuildingHpFloor = 0.2f;

        /// <summary>建築受損的訊息最少隔多久才再發一次（ticks）。</summary>
        public const int DamageMessageCooldown = GenDate.TicksPerDay / 2;

        /// <summary>測試用：每次擲骰都中。</summary>
        public static bool debugAlwaysHit;

        private int lastDamageMessageTick = -999999;

        private readonly List<Thing> tmpThings = new List<Thing>();

        // ── 給測試看的計數 ──
        public int ItemsSwept { get; private set; }

        public int BuildingHits { get; private set; }

        public MapComponent_SeaWeather(Map map) : base(map)
        {
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref lastDamageMessageTick, "lastDamageMessageTick", -999999);
        }

        public override void MapComponentTick()
        {
            if ((Find.TickManager.TicksGame + map.uniqueID + 101) % CheckInterval != 0 || !SeaLevels.IsSeaLevel(map))
            {
                return;
            }
            SyncPlatformWeather();
            SeaStateValues state = SeaState.Of(map);
            if (state.itemSweepMtbHours > 0f)
            {
                SweepItems(state.itemSweepMtbHours);
            }
            if (state.buildingHitMtbHours > 0f)
            {
                BatterBuildings(state);
            }
        }

        /// <summary>平台層的天氣跟著水面層（轉換照原版慢慢過去）。</summary>
        public void SyncPlatformWeather()
        {
            Map platform = SeaLevels.PlatformLevelOf(map);
            WeatherDef cur = map.weatherManager?.curWeather;
            if (platform?.weatherManager == null || cur == null || platform.weatherManager.curWeather == cur)
            {
                return;
            }
            platform.weatherManager.TransitionTo(cur);
        }

        /// <summary>戶外存放的物品：靠海邊的照平均間隔、甲板中央的少很多。被捲走的物品直接銷毀，合併成一則訊息。</summary>
        public int SweepItems(float mtbHours)
        {
            tmpThings.Clear();
            tmpThings.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver));
            int swept = 0;
            string firstLabel = null;
            IntVec3 firstCell = IntVec3.Invalid;
            for (int i = 0; i < tmpThings.Count; i++)
            {
                Thing t = tmpThings[i];
                if (t is Pawn || !t.Spawned || t.Map != map || t.ParentHolder != map)
                {
                    continue;
                }
                IntVec3 c = t.Position;
                if (!Outdoors(c))
                {
                    continue;
                }
                float mtb = mtbHours / (SeaEdge.NearOpenSea(map, c) ? 1f : InnerFactor);
                if (!debugAlwaysHit && !Rand.MTBEventOccurs(mtb, GenDate.TicksPerHour, CheckInterval))
                {
                    continue;
                }
                if (firstLabel == null)
                {
                    firstLabel = t.LabelCap;
                    firstCell = c;
                }
                SeaEdge.Splash(map, c);
                t.Destroy(DestroyMode.Vanish);
                swept++;
            }
            tmpThings.Clear();
            if (swept > 0)
            {
                ItemsSwept += swept;
                string text = swept == 1 ? "DepthRim.SeaWeather.ItemSwept".Translate(firstLabel) : "DepthRim.SeaWeather.ItemsSwept".Translate(firstLabel, swept - 1);
                Messages.Message(text, new TargetInfo(firstCell, map), MessageTypeDefOf.NegativeEvent);
            }
            return swept;
        }

        /// <summary>靠海邊（或戶外）的人造建築被浪打：受損，但不會低於 <see cref="BuildingHpFloor"/>。</summary>
        public int BatterBuildings(SeaStateValues state)
        {
            tmpThings.Clear();
            tmpThings.AddRange(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial));
            int hits = 0;
            Thing first = null;
            for (int i = 0; i < tmpThings.Count; i++)
            {
                Thing b = tmpThings[i];
                if (!b.Spawned || !b.def.useHitPoints || b.def.category != ThingCategory.Building)
                {
                    continue;
                }
                IntVec3 c = b.Position;
                bool edge = SeaEdge.NearOpenSea(map, c);
                if (!edge && !Outdoors(c))
                {
                    continue;
                }
                int floor = Mathf.CeilToInt(b.MaxHitPoints * BuildingHpFloor);
                if (b.HitPoints <= floor)
                {
                    continue;
                }
                float mtb = state.buildingHitMtbHours / (edge ? 1f : InnerFactor);
                if (!debugAlwaysHit && !Rand.MTBEventOccurs(mtb, GenDate.TicksPerHour, CheckInterval))
                {
                    continue;
                }
                int amount = Mathf.Min(Mathf.Max(1, Mathf.RoundToInt(state.buildingDamage.RandomInRange)), b.HitPoints - floor);
                if (amount <= 0)
                {
                    continue;
                }
                // 原版對建築的鈍傷有倍率（buildingDamageFactor 1.5、可通行／不可通行另乘）：先除掉，實際扣的才是 amount；
                // 其他倍率（例如 mod 的抗性）讓它低於下限時再補回下限
                DamageDef dmg = DamageDefOf.Blunt;
                float factor = dmg.buildingDamageFactor * (b.def.passability == Traversability.Impassable ? dmg.buildingDamageFactorImpassable : dmg.buildingDamageFactorPassable);
                b.TakeDamage(new DamageInfo(dmg, amount / Mathf.Max(factor, 0.01f)));
                if (!b.Destroyed && b.HitPoints < floor)
                {
                    b.HitPoints = floor;
                }
                SeaEdge.Splash(map, c);
                hits++;
                first = first ?? b;
            }
            tmpThings.Clear();
            if (hits > 0)
            {
                BuildingHits += hits;
                int now = Find.TickManager.TicksGame;
                if (now - lastDamageMessageTick >= DamageMessageCooldown)
                {
                    lastDamageMessageTick = now;
                    Messages.Message("DepthRim.SeaWeather.BuildingsBattered".Translate(), new LookTargets(first), MessageTypeDefOf.NegativeEvent);
                }
            }
            return hits;
        }

        /// <summary>戶外：沒有屋頂，或所在的房間是開放的（同落海的「艙外」）。</summary>
        public bool Outdoors(IntVec3 c)
        {
            return !c.Roofed(map) || MapComponent_PressureChambers.IsOutside(c.GetRoom(map));
        }
    }

    /// <summary>海邊的判定與水花（落海、捲走物品、打壞建築共用）。</summary>
    public static class SeaEdge
    {
        /// <summary>2 格內有開放海面（浪直接打上來的地方）或地圖邊緣。</summary>
        public static bool NearOpenSea(Map map, IntVec3 cell)
        {
            TerrainDef sea = DepthRimDefOf.DepthRim_OpenSea;
            int n = GenRadial.NumCellsInRadius(2.9f);
            for (int i = 0; i < n; i++)
            {
                IntVec3 c = cell + GenRadial.RadialPattern[i];
                if (!c.InBounds(map) || map.terrainGrid.TerrainAt(c) == sea)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>原版「從水中冒出」的大水花。</summary>
        public static void Splash(Map map, IntVec3 cell)
        {
            if (map != Find.CurrentMap || !cell.InBounds(map))
            {
                return;
            }
            EffecterDef def = DefDatabase<EffecterDef>.GetNamedSilentFail("PawnEmergeFromWaterLarge");
            if (def == null)
            {
                return;
            }
            Effecter effecter = def.Spawn(cell, map);
            map.effecterMaintainer.AddEffecterToMaintain(effecter, cell, 60);
        }
    }

    /// <summary>平台層（口袋地圖）不自己選天氣：直接用水面層目前的天氣。</summary>
    [HarmonyPatch(typeof(WeatherDecider), "ChooseNextWeather")]
    public static class Patch_PlatformLevelWeather
    {
        public static bool Prefix(Map ___map, ref WeatherDef __result)
        {
            if (!SeaLevels.IsPlatformLevel(___map))
            {
                return true;
            }
            WeatherDef sea = SeaLevels.SeaLevelOf(___map)?.weatherManager?.curWeather;
            if (sea == null)
            {
                return true;
            }
            __result = sea;
            return false;
        }
    }
}
