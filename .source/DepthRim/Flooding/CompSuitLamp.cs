using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    public class CompProperties_SuitLamp : CompProperties
    {
        /// <summary>頭燈照亮半徑（格）；原版 <see cref="Verse.GlowGrid"/> 會 clamp 到 0–40。</summary>
        public float glowRadius = 4.5f;

        /// <summary>頭燈顏色（實作指南 B.3：偏冷的白藍）。XML 寫成 <c>(200,225,255)</c>。</summary>
        public Color glowColor = new Color(200f / 255f, 225f / 255f, 1f);

        /// <summary>過曝半徑；0 = 不過曝。</summary>
        public float overlightRadius = 0f;

        /// <summary>穿著者倒地後多久關燈（ticks）；避免昏迷的人在水裡還亮著燈。</summary>
        public int downedOffTicks = 600;

        public CompProperties_SuitLamp()
        {
            compClass = typeof(CompSuitLamp);
        }
    }

    /// <summary>
    /// 潛水服的頭燈（實作指南 B.3）：穿著者在水中時，在腳下註冊一個原版自訂光源，
    /// 讓水體網格（<see cref="SectionLayer_WaterOuter"/>）透過 <see cref="Verse.GlowGrid.VisualGlowAt(IntVec3)"/> 讀到光照。
    ///
    /// <para>偏離指南：不用隱藏的燈光 ThingDef，改用 <see cref="Verse.GlowGrid.RegisterCustomGlower"/>。
    /// 這樣不需要生成／銷毀 Thing、不必存檔、換格也不必重建任何東西，脫下裝備時在
    /// <see cref="PostDeSpawn"/> 解除註冊即可。</para>
    ///
    /// <para>本身不 tick（省效能），由 <see cref="SuitLampUpdater"/> 每 15 ticks 統一掃描一次。</para>
    /// </summary>
    public class CompSuitLamp : ThingComp
    {
        private bool lit;

        /// <summary>注意：原版第一個 handle 是 0，所以不能用 0 當「沒有光源」，要用 <see cref="lit"/>。</summary>
        private int lampHandle;

        private Map lampMap;

        private IntVec3 lampCell;

        private int downedTicks;

        public CompProperties_SuitLamp Props => (CompProperties_SuitLamp)props;

        public bool Lit => lit;

        /// <summary>光源目前註冊在哪一格（未註冊時是上一次的位置）。給除錯與驗收讀取用。</summary>
        public IntVec3 RegisteredCell => lampCell;

        private Pawn Wearer => (parent as Apparel)?.Wearer;

        /// <summary>由 <see cref="SuitLampUpdater"/> 呼叫；只在必要時才動光源。</summary>
        public void UpdateLamp()
        {
            Pawn wearer = Wearer;
            Map map = parent.SpawnedOrAnyParentSpawned ? parent.MapHeld : null;
            if (wearer != null && wearer.Downed)
            {
                downedTicks += SuitLampUpdater.Interval;
            }
            else
            {
                downedTicks = 0;
            }
            if (!WantLamp(wearer, map))
            {
                TurnOff();
                return;
            }
            // 已經亮著、而且人還在同一格：什麼都不用做
            if (lit && lampMap == map && lampCell == parent.PositionHeld)
            {
                return;
            }
            TurnOff();
            lampMap = map;
            lampCell = parent.PositionHeld;
            lampHandle = lampMap.glowGrid.RegisterCustomGlower(lampCell,
                DepthRimModSettings.LampRadiusOf(Props.glowRadius), new ColorInt(Props.glowColor), Props.overlightRadius);
            lit = true;
        }

        private bool WantLamp(Pawn wearer, Map map)
        {
            if (wearer == null || wearer.Dead || wearer.Destroyed)
            {
                return false;
            }
            if (map == null || !FloodingUtility.IsWaterMap(map))
            {
                return false;
            }
            if (wearer.Downed && downedTicks >= Props.downedOffTicks)
            {
                return false;
            }
            MapComponent_Flooding flooding = map.Flooding();
            if (flooding == null || !flooding.Active)
            {
                return false;
            }
            // 遲滯（P2-3）：半淹艙室的水位在邊界值附近跳動時，頭燈不該跟著閃。
            // 已經亮著時用較低的門檻才關，暗著時用較高的門檻才開。
            float level = flooding.LevelAt(parent.PositionHeld);
            return level > (lit ? OffLevel : OnLevel);
        }

        /// <summary>水位高於這個值才開燈。</summary>
        private const float OnLevel = 0.06f;

        /// <summary>已經亮著時，水位低於這個值才關燈。</summary>
        private const float OffLevel = 0.02f;

        /// <summary>關燈並解除光源註冊；任何時候呼叫都安全。</summary>
        public void TurnOff()
        {
            if (lit)
            {
                if (lampMap != null && !lampMap.Disposed)
                {
                    lampMap.glowGrid.DeregisterCustomGlower(lampHandle);
                }
                lit = false;
            }
            lampMap = null;
            lampHandle = 0;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref downedTicks, "suitLampDownedTicks", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // 光源是執行期狀態，不存檔：讀檔後一律重新註冊
                lit = false;
                lampHandle = 0;
                lampMap = null;
            }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            TurnOff();
        }

        /// <summary>
        /// 地圖被卸除時用：地圖正在消失，不能再碰它的 <c>glowGrid</c>，只把狀態清掉
        /// （二期 P2-3：避免跨地圖殘留）。
        /// </summary>
        public void Forget()
        {
            lit = false;
            lampMap = null;
            lampHandle = 0;
            downedTicks = 0;
        }
    }

    /// <summary>
    /// 每 15 ticks 掃一次地圖上穿著的潛水服頭燈。只處理「穿著中的」裝備，
    /// 所以地上一堆潛水服不會有任何開銷。
    ///
    /// <para>追蹤清單存在 <see cref="MapComponent_Flooding"/> 上（每張地圖各自一份），
    /// 地圖卸除時由 <see cref="ReleaseAll"/> 清掉，不會跨地圖殘留（二期 P2-3）。</para>
    /// </summary>
    public static class SuitLampUpdater
    {
        public const int Interval = 15;

        /// <summary>本幀掃到的 comp（僅供反向清理比對，重用同一個集合避免每 15 ticks 就配置）。</summary>
        private static readonly HashSet<CompSuitLamp> visited = new HashSet<CompSuitLamp>();

        public static void Update(Map map)
        {
            MapComponent_Flooding flooding = map?.Flooding();
            if (flooding == null)
            {
                return;
            }
            List<CompSuitLamp> tracked = flooding.SuitLamps;
            visited.Clear();
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                List<Apparel> worn = pawn.apparel?.WornApparel;
                if (worn == null)
                {
                    continue;
                }
                for (int i = 0; i < worn.Count; i++)
                {
                    CompSuitLamp comp = worn[i].TryGetComp<CompSuitLamp>();
                    if (comp == null)
                    {
                        continue;
                    }
                    if (!visited.Add(comp))
                    {
                        continue;
                    }
                    if (!tracked.Contains(comp))
                    {
                        tracked.Add(comp);
                    }
                    comp.UpdateLamp();
                }
            }
            // 反向清掉已經失效的：裝備被銷毀／脫下後不再被掃到
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                CompSuitLamp comp = tracked[i];
                if (comp == null || comp.parent == null || comp.parent.Destroyed)
                {
                    // 裝備被銷毀／脫下後消失：地圖還在，所以一定要**真的**解除註冊（TurnOff），
                    // 不能用 Forget()——Forget 是給「地圖正在消失、不能再碰 glowGrid」用的，
                    // 它只清狀態不解除註冊，會留下一個 Lit == false 但光照還在的幽靈光源。
                    comp?.TurnOff();
                    tracked.RemoveAt(i);
                    continue;
                }
                if (visited.Contains(comp))
                {
                    continue;
                }
                comp.UpdateLamp();
                if (!comp.Lit)
                {
                    tracked.RemoveAt(i);
                }
            }
        }

        /// <summary>地圖卸除時清空（<see cref="MapComponent_Flooding.MapRemoved"/> 呼叫）。</summary>
        public static void ReleaseAll(MapComponent_Flooding flooding)
        {
            if (flooding == null)
            {
                return;
            }
            List<CompSuitLamp> tracked = flooding.SuitLamps;
            for (int i = 0; i < tracked.Count; i++)
            {
                tracked[i]?.Forget();
            }
            tracked.Clear();
            visited.Clear();
        }
    }
}
