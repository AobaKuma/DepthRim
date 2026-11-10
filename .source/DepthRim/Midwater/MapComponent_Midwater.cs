using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 中層水域：管理從深淵底下經過的生物（<see cref="MidwaterCreature"/>），畫面交給 <see cref="MapDrawLayer_Midwater"/>。
    ///
    /// <para>只在變溫層與深海（<see cref="MidwaterMath.ShowsMidwater"/>，無法下錨的層級）作用；
    /// 其他地圖（地表、淺海、海床）每 tick 只做一次層級判斷就離開。</para>
    ///
    /// <para>背景生物不存檔，讀檔後重新補上；事件叫出的巨物存檔，讀檔後繼續游完。
    /// 深淵遮罩貼圖（只給畫面用）也放在這裡，地圖卸除時一起銷毀。</para>
    /// </summary>
    public class MapComponent_Midwater : MapComponent
    {
        public const int UpdateInterval = 250;

        /// <summary>每多少深淵格多一隻背景生物。</summary>
        public const int AbyssCellsPerAmbient = 900;

        public const int MaxAmbient = 5;

        /// <summary>巨物事件的平均間隔（天），每張符合層級的地圖各自計算。</summary>
        public const float GiantMtbDays = 4f;

        /// <summary>至少要有這麼多深淵格才叫得出巨物（太小的洞看不到牠經過）。</summary>
        public const int MinAbyssCellsForGiant = 300;

        private List<MidwaterCreature> giants = new List<MidwaterCreature>();

        private readonly List<MidwaterCreature> ambient = new List<MidwaterCreature>();

        private int abyssCells = -1;

        private Texture2D mask;

        public MapComponent_Midwater(Map map) : base(map)
        {
        }

        /// <summary>地圖實際的層級（遊戲邏輯用）。</summary>
        public DepthTier Tier => FloodingUtility.DepthTierOf(map);

        /// <summary>畫面用的層級：除錯覆寫優先（和水體視覺同一個開關）。</summary>
        public DepthTier RenderTier => map.Flooding()?.RenderTier ?? Tier;

        public bool Active => MidwaterMath.ShowsMidwater(Tier);

        public IReadOnlyList<MidwaterCreature> Giants => giants;

        public IReadOnlyList<MidwaterCreature> Ambient => ambient;

        public int Count => giants.Count + ambient.Count;

        public bool AnyGiant => giants.Count > 0;

        /// <summary>深淵格數（快取；地形改變後由畫面層或下一次更新重算）。</summary>
        public int AbyssCells
        {
            get
            {
                if (abyssCells < 0)
                {
                    abyssCells = CountAbyss();
                }
                return abyssCells;
            }
        }

        public void Notify_TerrainChanged()
        {
            abyssCells = -1;
        }

        private int CountAbyss()
        {
            TerrainDef abyss = DepthRimDefOf.DepthRim_Abyss;
            TerrainGrid grid = map.terrainGrid;
            int n = 0;
            int count = map.cellIndices.NumGridCells;
            for (int i = 0; i < count; i++)
            {
                if (grid.TerrainAt(i) == abyss)
                {
                    n++;
                }
            }
            return n;
        }

        // ── 生成 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 叫出一隻生物穿越地圖（事件、除錯與驗收測試用；不檢查層級）。
        /// <paramref name="angle"/> 與 <paramref name="lateral"/> 為 null 時隨機。
        /// </summary>
        public MidwaterCreature StartPass(MidwaterCreatureDef def, float? angle = null, float? lateral = null)
        {
            if (def == null)
            {
                return null;
            }
            float a = angle ?? Rand.Range(0f, Mathf.PI * 2f);
            float l = lateral ?? Rand.Range(-1f, 1f);
            MidwaterMath.CrossingPath(map.Size.x, map.Size.z, a, l, MidwaterMath.PathMargin + def.drawSize.x * 0.5f, out Vector2 from, out Vector2 to);
            MidwaterCreature creature = new MidwaterCreature
            {
                def = def,
                from = from,
                to = to,
                durationTicks = MidwaterMath.TravelTicks(Vector2.Distance(from, to), def.speed.RandomInRange),
                scale = Rand.Range(0.85f, 1.15f),
                phase = Rand.Range(0f, 100f),
            };
            if (def.giant)
            {
                giants.Add(creature);
            }
            else
            {
                ambient.Add(creature);
            }
            return creature;
        }

        public void ClearAll()
        {
            giants.Clear();
            ambient.Clear();
        }

        // ── 更新 ─────────────────────────────────────────────────────────────

        public override void MapComponentTick()
        {
            if (giants.Count == 0 && ambient.Count == 0 && !Active)
            {
                return;
            }
            Advance(giants);
            Advance(ambient);
            if (Find.TickManager.TicksGame % UpdateInterval != 0)
            {
                return;
            }
            if (!Active)
            {
                // 層級改了（潛艇上浮到淺海）：背景生物不再補，已經在游的游完就好
                return;
            }
            TopUpAmbient();
            TryGiantEvent();
        }

        private static void Advance(List<MidwaterCreature> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                MidwaterCreature c = list[i];
                c.ageTicks++;
                if (c.Finished || c.def == null)
                {
                    list.RemoveAt(i);
                }
            }
        }

        private void TopUpAmbient()
        {
            int target = MidwaterMath.AmbientTarget(AbyssCells, AbyssCellsPerAmbient, MaxAmbient);
            if (ambient.Count >= target)
            {
                return;
            }
            DepthTier tier = Tier;
            // 一次只補一隻，避免讀檔後整群同時從邊緣冒出來
            if (DefDatabase<MidwaterCreatureDef>.AllDefsListForReading
                .Where(d => !d.giant && d.ambientWeight > 0f && d.AllowedOn(tier))
                .TryRandomElementByWeight(d => d.ambientWeight, out MidwaterCreatureDef def))
            {
                StartPass(def);
            }
        }

        private void TryGiantEvent()
        {
            IncidentDef incident = DepthRimDefOf.DepthRim_GiantPasses;
            if (incident == null || !Rand.MTBEventOccurs(GiantMtbDays, GenDate.TicksPerDay, UpdateInterval))
            {
                return;
            }
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(incident.category, map);
            if (incident.Worker.CanFireNow(parms))
            {
                incident.Worker.TryExecute(parms);
            }
        }

        // ── 畫面 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 深淵遮罩：每格一個像素，已探索的深淵格 = 1。著色器用它把生物裁在深淵裡面
        /// （海床、岩體、迷霧下都看不到下面經過的東西）。只在主執行緒、由畫面層呼叫。
        /// </summary>
        public Texture2D RebuildMask()
        {
            int w = map.Size.x;
            int h = map.Size.z;
            if (mask == null || mask.width != w || mask.height != h)
            {
                DestroyMask();
                mask = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    name = "DepthRim_MidwaterMask_" + map.uniqueID,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            Color32[] pixels = new Color32[w * h];
            TerrainDef abyss = DepthRimDefOf.DepthRim_Abyss;
            int n = 0;
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (map.terrainGrid.TerrainAt(c) != abyss)
                    {
                        continue;
                    }
                    n++;
                    // 北邊一兩格是實地（海床、平台）時，這格掛著那塊地的下垂側面（Loop 貼圖高兩格）：
                    // 生物在地板下方，不能蓋在側面上
                    if (!c.Fogged(map) && !UnderEdgeLoop(x, z, w, h, abyss))
                    {
                        pixels[z * w + x] = new Color32(255, 255, 255, 255);
                    }
                }
            }
            abyssCells = n;
            mask.SetPixels32(pixels);
            mask.Apply(false);
            return mask;
        }

        public Texture2D Mask => mask;

        /// <summary>這格深淵的正北方一或兩格內有實地（會往下畫側面）。地圖外不算。</summary>
        private bool UnderEdgeLoop(int x, int z, int w, int h, TerrainDef abyss)
        {
            for (int dz = 1; dz <= 2; dz++)
            {
                if (z + dz >= h)
                {
                    return false;
                }
                TerrainDef above = map.terrainGrid.TerrainAt(new IntVec3(x, 0, z + dz));
                if (above != abyss && above != null && !above.dontRender)
                {
                    return true;
                }
            }
            return false;
        }

        private void DestroyMask()
        {
            if (mask != null)
            {
                Object.Destroy(mask);
                mask = null;
            }
        }

        public override void MapRemoved()
        {
            DestroyMask();
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref giants, "giants", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                giants ??= new List<MidwaterCreature>();
                giants.RemoveAll(g => g?.def == null);
            }
        }
    }
}
