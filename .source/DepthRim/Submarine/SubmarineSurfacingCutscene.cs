using System;
using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 出水演出要用的資料：在哪張地圖、船體落在哪些格（目的地座標），破冰時再加上開裂圖樣與接觸點。
    /// 冰面的地形變化（裂紋、碎冰、開放水）照圖樣的出現時間一格一格套上去（<see cref="ApplyUpTo"/>），
    /// 演出被略過或關掉時一次套完（<see cref="ApplyAll"/>）。
    /// </summary>
    public class SurfacingShow
    {
        public Map map;
        public HashSet<IntVec3> hull;
        public bool ice;
        public bool violent;
        public float ratio;
        public IceCrackPattern.Result pattern;
        public List<IntVec3> contacts = new List<IntVec3>();

        /// <summary>變化的格子依出現時間排序。</summary>
        private List<(IntVec3 cell, float time, IceCell kind)> changes;
        private int next;

        /// <summary>圖樣離船體最遠的變化（格），決定鏡頭要拉多遠。</summary>
        public float reach;

        public static SurfacingShow OpenSea(Map map, HashSet<IntVec3> hull)
        {
            return new SurfacingShow { map = map, hull = hull, changes = new List<(IntVec3, float, IceCell)>() };
        }

        public static SurfacingShow IceBreach(Map map, HashSet<IntVec3> hull, List<IntVec3> contacts, float thickness, float ratio, int seed)
        {
            SurfacingShow show = new SurfacingShow { map = map, hull = hull, ice = true, ratio = ratio, violent = ratio >= IceBreachMath.ViolentFrom, contacts = contacts };
            show.pattern = IceCrackPattern.Generate(map.Size.x, map.Size.z, hull.Select(c => (c.x, c.z)).ToList(), contacts.Select(c => (c.x, c.z)).ToList(),
                IceBreachMath.CharacteristicLengthCells(thickness), ratio, seed);
            show.changes = new List<(IntVec3, float, IceCell)>();
            for (int i = 0; i < show.pattern.cells.Length; i++)
            {
                IceCell kind = show.pattern.cells[i];
                if (kind == IceCell.Intact || kind == IceCell.Hull)
                {
                    continue;
                }
                IntVec3 c = new IntVec3(i % map.Size.x, 0, i / map.Size.x);
                show.changes.Add((c, show.pattern.reveal[i], kind));
                show.reach = Mathf.Max(show.reach, show.pattern.distance[i]);
            }
            show.changes.Sort((a, b) => a.time.CompareTo(b.time));
            return show;
        }

        public int Remaining => changes == null ? 0 : changes.Count - next;

        public int Total => changes?.Count ?? 0;

        /// <summary>套上出現時間不晚於 <paramref name="time"/> 的變化，回傳這次新出現的裂紋格（演出在那裡冒雪粉）。</summary>
        public List<IntVec3> ApplyUpTo(float time)
        {
            List<IntVec3> cracks = new List<IntVec3>();
            if (changes == null)
            {
                return cracks;
            }
            MapComponent_IceSheet sheet = MapComponent_IceSheet.For(map);
            while (next < changes.Count && changes[next].time <= time)
            {
                (IntVec3 c, float _, IceCell kind) = changes[next++];
                if (hull.Contains(c) || c.GetEdifice(map) != null)
                {
                    continue;
                }
                switch (kind)
                {
                    case IceCell.OpenWater:
                        map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_BreachWater);
                        sheet?.TrackOpenWater(c);
                        break;
                    case IceCell.Rubble:
                        map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_IceRubble);
                        break;
                    case IceCell.Crack:
                        map.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_IceCracked);
                        sheet?.TrackCrack(c);
                        cracks.Add(c);
                        break;
                }
            }
            return cracks;
        }

        public void ApplyAll()
        {
            ApplyUpTo(float.MaxValue);
        }
    }

    /// <summary>
    /// 出水演出（設計稿 §6）。沿用 DMSE 離港過場的流程（擷取船體影像、暫停遊戲、可略過），演出反過來：船體從水下浮上來。
    /// 遊戲暫停中原版的 fleck 與 effecter 不會動，所以雪粉、水花、海煙用自己手動推進的 fleck 系統，碎冰塊是自己畫的小方塊。
    /// <list type="bullet">
    /// <item><b>破冰（10 秒）</b>：0～2 s 擠壓（冰下暗影、放射裂紋從接觸點長出、雪粉）；2～3.5 s 隆起（冰面泛白、輕震）；
    /// 3.5 s 破裂（環、碎冰、開放水一次出現，大震、巨響、碎冰塊四射、水花、雪霧）；3.5～7 s 浮出（船體放大、從深藍變回原色，碎冰從船邊滑落）；
    /// 7～10 s 穩定（上下輕晃、開放水冒海煙）。猛烈出水時震動、碎冰與水花都更多。</item>
    /// <item><b>開放海面（7 秒）</b>：0～2.5 s 水下暗影上升、水面翻起白沫；2.5 s 破水（水花圈、浪沫）；2.5～5 s 浮出；5～7 s 輕晃、船邊淌水。</item>
    /// </list>
    /// 兩種都一樣（2026-11-15）：照原版生物出水（PawnEmergeFromWater / Large）。破水時一圈大出水沿船體輪廓炸開，
    /// 接著從船頭到船尾一路大出水（劈開海面），船頭一彈；破水後兩秒多船邊不斷有小出水。
    /// 這些水花畫在船體上面，蓋住船體浮現的瞬間。時間曲線見 <see cref="SurfacingCurves"/>。
    /// 演出結束（或被略過、關掉）時才真正把船體搬上來（<see cref="SubmarineSurfacing.MoveInto"/>），地形一次補齊。
    /// </summary>
    public class SubmarineSurfacingCutscene : TransferTakeoffCutscene
    {
        private readonly SurfacingShow show;
        private readonly CellRect hullRect;
        private readonly float hullHalf;

        private FleckSystem flecks;
        private readonly EventQueue fleckTicker = new EventQueue(1f / 60f);
        private readonly DrawBatch drawBatch = new DrawBatch();
        private readonly List<Chunk> chunks = new List<Chunk>();
        private Material chunkMat;
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private bool broke;
        private bool heaved;
        private float nextAmbient;
        private float lastElapsed;
        private readonly System.Random rand = new System.Random();

        private FleckDef puff;
        private FleckDef foam;
        private FleckDef steam;

        // 裂隙與噴氣（2026-11-14）：原版的地面裂隙 fleck（GroundCrack，靜態、畫在地面）沿裂紋出現，
        // 冰層崩裂時被壓住的空氣與水氣沿著裂縫往上噴（自己畫的柔邊白色水氣：原版的煙與塵霧貼圖是灰色、土黃色，在冰上不像水氣）
        private FleckSystem groundFlecks;
        private FleckDef fissure;
        private Texture2D vaporTex;
        private readonly List<IntVec3> openCracks = new List<IntVec3>();
        private float gasCarry;

        // 劈開海面（2026-11-15）：照原版生物出水（PawnEmergeFromWater / Large）。破水時從船頭切到船尾，
        // 切到的地方來一次原版的大出水；破水後船邊一直有小出水，兩秒多內收掉，蓋住船體浮現的邊緣。
        // 水柱是原版 GroundWaterSplash 的複本（只把 renderQueue 拉到船體上面，見 Surfacing_Effects.xml）；水沫直接用原版 WaterChurnNoise（海面上、船體下面），
        // 船體的時間曲線用 SurfacingCurves（原版 SimpleCurve）。
        private FleckDef splash;
        private FleckDef churn;
        private FleckDef ripple;
        private List<Slice> slices;
        private bool alongX;
        private int bowSign = 1;
        private float splashCarry;

        private struct Slice
        {
            public float along;
            public float center;
            public float half;
            public float splitAt;
            public bool split;
        }

        /// <summary>測試用：原版出水的水柱（GroundWaterSplash）、水沫（WaterChurnNoise）fleck 數，與已經被劈開的船段數／總段數。</summary>
        public int Splashes { get; private set; }

        public int Churns { get; private set; }

        public int SlicesSplit { get; private set; }

        public int SliceCount => slices?.Count ?? 0;

        /// <summary>測試用：長軸是不是 x，船頭在長軸座標大的那端（+1）還是小的那端（-1）。</summary>
        public bool AlongX => alongX;

        public int BowSign => bowSign;

        /// <summary>測試用：演出中長出的裂隙 fleck 數與噴氣次數。</summary>
        public int Fissures { get; private set; }

        public int GasJets { get; private set; }

        private float BreakAt => show.ice ? IceCrackPattern.BreakTime : 2.5f;
        private float EmergedAt => show.ice ? 7f : 5f;

        private static readonly Color DeepWater = new Color(0.05f, 0.12f, 0.2f);
        private static readonly Color IceWhite = new Color(0.92f, 0.96f, 1f);

        private struct Chunk
        {
            public Vector3 pos;
            public Vector3 vel;
            public float height;
            public float vHeight;
            public float size;
            public float rot;
            public float rotVel;
            public float age;
            public float life;
            public Color color;
        }

        private readonly Map captureMap;

        /// <summary>船體在擷取地圖（潛艇地圖）上的格子；找推進器、舵輪來判斷船頭船尾。</summary>
        private readonly HashSet<IntVec3> captureCells;

        /// <summary>水下船影用的柔邊影像（擷取完成後產生一次）。</summary>
        private Texture2D softTex;
        private Material softMat;
        private bool softTried;

        /// <summary>水下船影模糊半徑（格）。</summary>
        public const float SoftEdgeCells = 1.1f;

        public SubmarineSurfacingCutscene(Map displayMap, Vector3 center, Action onComplete, Map captureMap, HashSet<IntVec3> cells, SurfacingShow show)
            : base(displayMap, center, onComplete, captureMap, cells)
        {
            this.captureMap = captureMap;
            captureCells = cells;
            this.show = show;
            hullRect = CellRect.FromCellList(show.hull);
            hullHalf = Mathf.Max(hullRect.Width, hullRect.Height) * 0.5f;
        }

        protected override float Duration => show.ice ? 10f : 7f;


        /// <summary>測試用：水下船影的柔邊影像有沒有產生。</summary>
        public bool HasSoftSilhouette => softMat != null;

        /// <summary>演出進行到第幾秒（擷取船體影像期間是 0）。測試用。</summary>
        public float Seconds => Elapsed;

        public SurfacingShow Show => show;

        public int FlyingChunks => chunks.Count;

        // ── 聲音、鏡頭 ─────────────────────────────────────────────────

        protected override void StartEffects()
        {
            puff = DefDatabase<FleckDef>.GetNamedSilentFail("DustPuffThick");
            foam = DefDatabase<FleckDef>.GetNamedSilentFail("FoamSpray") ?? puff;
            steam = DefDatabase<FleckDef>.GetNamedSilentFail("Steam") ?? puff;
            fissure = DefDatabase<FleckDef>.GetNamedSilentFail("GroundCrack");
            splash = DefDatabase<FleckDef>.GetNamedSilentFail("DepthRim_SurfacingSplash");
            churn = DefDatabase<FleckDef>.GetNamedSilentFail("WaterChurnNoise");
            ripple = DefDatabase<FleckDef>.GetNamedSilentFail("WaterRipple");
            BuildSlices();
            BuildStrips();
            chunkMat = new Material(ShaderDatabase.Transparent) { mainTexture = BaseContent.WhiteTex, renderQueue = 3250 };
            vaporTex = MakeSoftDisc();
            vaporMat = new Material(ShaderDatabase.Transparent) { mainTexture = vaporTex, renderQueue = 3260 };
            if (show.ice)
            {
                // 冰層擠壓的低沉吱嘎
                DepthRimDefOf.DepthRim_HullCreak?.PlayOneShot(new TargetInfo(hullRect.CenterCell, DisplayMap));
            }
        }

        protected override void UpdateEffects(float elapsed, float progress)
        {
        }

        protected override void StopEffects()
        {
            if (chunkMat != null)
            {
                UnityEngine.Object.Destroy(chunkMat);
                chunkMat = null;
            }
            if (vaporMat != null)
            {
                UnityEngine.Object.Destroy(vaporMat);
                vaporMat = null;
            }
            if (softMat != null)
            {
                UnityEngine.Object.Destroy(softMat);
                softMat = null;
            }
            if (shipMat != null)
            {
                // 貼圖是 DMSE 擷取的，由它自己收；這裡只收材質
                UnityEngine.Object.Destroy(shipMat);
                shipMat = null;
            }
            if (strips != null)
            {
                foreach ((Mesh mesh, int _) in strips)
                {
                    UnityEngine.Object.Destroy(mesh);
                }
                strips = null;
            }
            if (softTex != null)
            {
                UnityEngine.Object.Destroy(softTex);
                softTex = null;
            }
            if (vaporTex != null)
            {
                UnityEngine.Object.Destroy(vaporTex);
                vaporTex = null;
            }
        }

        protected override float ZoomAt(float progress)
        {
            // 鏡頭整段固定（2026-11-14：不再隨裂紋拉遠）。冰面一開始就取景到整片會裂開的冰，海面維持原本的大小
            return show.ice ? Mathf.Clamp(hullHalf + show.reach * 0.8f, InitialZoom, MaxZoom) : InitialZoom;
        }

        /// <summary>浮出前船體只比原尺寸小一點點（2026-11-14：原本 0.85，縮放太明顯）。</summary>
        private const float SubmergedScale = 0.96f;

        // ── 船體 ───────────────────────────────────────────────────────

        protected override Vector3 ShipOffset(float progress)
        {
            float t = Elapsed;
            if (t < BreakAt)
            {
                return Vector3.zero;
            }
            // 破水：船頭衝出水面往上一彈、落回去（SurfacingCurves.ShipLift）；冰面上被冰卡住，彈得比較小
            float z = SurfacingCurves.ShipLift.Evaluate(t - BreakAt) * (show.ice ? 0.6f : 1f) * (show.violent ? 1.4f : 1f);
            if (t >= EmergedAt)
            {
                // 阻尼正弦：振幅 0.08 格（猛烈出水 0.14），週期 1.6 s
                float amp = show.violent ? 0.14f : 0.08f;
                float s = t - EmergedAt;
                z += amp * Mathf.Sin(s * Mathf.PI * 2f / 1.6f) * Mathf.Exp(-s * 1.2f);
            }
            return new Vector3(0f, 0f, z);
        }

        protected override float ShipScale(float progress)
        {
            float t = Elapsed;
            return t < BreakAt ? SubmergedScale : SurfacingCurves.ShipScale.Evaluate(t - BreakAt);
        }

        protected override Color ShipColor(float progress)
        {
            // 清楚的船體由 DrawShipStrips 一條一條畫（從船頭到船尾浮現），DMSE 整張的那一份就不畫。
            // 切不出條（沒有擷取到影像）時才退回整張一起浮現。
            float t = Elapsed;
            if (t < BreakAt || StripCount > 0)
            {
                return new Color(1f, 1f, 1f, 0f);
            }
            float s = t - BreakAt;
            Color c = Color.Lerp(DeepWater, Color.white, SurfacingCurves.ShipTint.Evaluate(s));
            c.a = SurfacingCurves.ShipAlpha.Evaluate(s);
            return c;
        }

        // ── 劈開海面 ───────────────────────────────────────────────────

        /// <summary>
        /// 沿船體長軸把船體切成一格寬的段，記下每段的中心與半寬（照船體真正的形狀）。船頭是比較窄的那一頭（潛艇的艏比艉尖）。
        /// 每一處出水的時間都照它在船體上的位置排（<see cref="SplitTime"/>）：從船頭到船尾，同一段從中線往兩舷。
        /// </summary>
        private void BuildSlices()
        {
            alongX = hullRect.Width >= hullRect.Height;
            slices = show.hull.GroupBy(c => alongX ? c.x : c.z).OrderBy(g => g.Key).Select(g =>
            {
                int lo = g.Min(c => alongX ? c.z : c.x);
                int hi = g.Max(c => alongX ? c.z : c.x);
                return new Slice { along = g.Key + 0.5f, center = (lo + hi + 1) * 0.5f, half = (hi - lo + 1) * 0.5f };
            }).ToList();
            if (slices.Count == 0)
            {
                return;
            }
            int n = Mathf.Min(3, slices.Count);
            float low = 0f, high = 0f;
            for (int i = 0; i < n; i++)
            {
                low += slices[i].half;
                high += slices[slices.Count - 1 - i].half;
            }
            // 船頭：推進器在船尾、舵輪偏船頭（開局潛艇的艉是細長的龍骨，比艏窄，不能只看寬窄）；都沒有才用「比較窄的那頭」
            bowSign = BowFromBuildings() ?? (low < high ? -1 : 1);
            sweep =SurfacingCurves.SweepSeconds(slices.Count);
            for (int i = 0; i < slices.Count; i++)
            {
                Slice s = slices[i];
                s.splitAt = SplitTime(SliceToWorld(s.along, s.center));
                slices[i] = s;
            }
        }

        /// <summary>從船頭切到船尾要幾秒。</summary>
        private float sweep;

        /// <summary>
        /// 這個位置的海面幾秒被劈開：照它沿長軸離船頭多遠（船頭最先、船尾最後），
        /// 再照它離那一段中線多遠晚一點（水從龍骨往兩舷翻，船邊外的水最晚），所以順序跟著船體的形狀走。
        /// </summary>
        private float SplitTime(Vector3 pos)
        {
            if (slices == null || slices.Count == 0)
            {
                return BreakAt;
            }
            float along = alongX ? pos.x : pos.z;
            float first = slices[0].along, last = slices[slices.Count - 1].along;
            float frac = last > first ? Mathf.Clamp01((along - first) / (last - first)) : 0f;
            if (bowSign > 0)
            {
                frac = 1f - frac;
            }
            int k = Mathf.Clamp(Mathf.RoundToInt(frac * (slices.Count - 1)), 0, slices.Count - 1);
            Slice s = slices[bowSign > 0 ? slices.Count - 1 - k : k];
            float across = alongX ? pos.z : pos.x;
            float side = Mathf.Abs(across - s.center) / Mathf.Max(0.5f, s.half);
            return BreakAt + sweep * frac + SurfacingCurves.SideDelay * Mathf.Min(side, 2f);
        }

        /// <summary>段落座標（沿長軸、沿短軸）換成地圖座標。</summary>
        private Vector3 SliceToWorld(float along, float across)
        {
            return alongX ? new Vector3(along, 0f, across) : new Vector3(across, 0f, along);
        }

        /// <summary>
        /// 船頭在長軸的哪一端（+1 = 座標大的那端），看擷取地圖上船體裡的推進器（DMSE 的 CompTransferEngine，在船尾）
        /// 與舵輪（CompTransferConsole，偏船頭）。擷取地圖與目的地方向相同，只比較沿長軸的相對位置。都找不到回傳 null。
        /// </summary>
        private int? BowFromBuildings()
        {
            if (captureMap == null || captureCells == null || captureCells.Count == 0)
            {
                return null;
            }
            CellRect rect = CellRect.FromCellList(captureCells);
            float mid = alongX ? (rect.minX + rect.maxX + 1) * 0.5f : (rect.minZ + rect.maxZ + 1) * 0.5f;
            float engines = 0f, helms = 0f;
            int ne = 0, nh = 0;
            foreach (Building b in captureMap.listerBuildings.allBuildingsColonist)
            {
                if (!captureCells.Contains(b.Position))
                {
                    continue;
                }
                float along = alongX ? b.TrueCenter().x : b.TrueCenter().z;
                if (b.TryGetComp<CompTransferEngine>() != null)
                {
                    engines += along;
                    ne++;
                }
                else if (b.TryGetComp<CompTransferConsole>() != null)
                {
                    helms += along;
                    nh++;
                }
            }
            if (ne > 0 && Mathf.Abs(engines / ne - mid) > 0.5f)
            {
                return engines / ne < mid ? 1 : -1;
            }
            if (nh > 0 && Mathf.Abs(helms / nh - mid) > 0.5f)
            {
                return helms / nh > mid ? 1 : -1;
            }
            return null;
        }

        /// <summary>
        /// 切到這一段：隨機 0～3 處原版的出水，橫向位置亂撒（中線、舷邊、舷外都有），時間照位置排、再加一點抖動。
        /// （2026-11-15：原本固定兩舷／中線輪流，排成三條整齊的直線，太規律。）
        /// </summary>
        private void SplitSlice(Slice s, int index)
        {
            SlicesSplit++;
            int n = rand.Next(0, 3) + (Rand01() < 0.35f ? 1 : 0);
            for (int k = 0; k < n; k++)
            {
                // 偏向舷邊：水被船殼推開的地方最猛，但中線上也有
                float u = Rand01() * 2f - 1f;
                float across = s.center + Mathf.Sign(u) * Mathf.Sqrt(Mathf.Abs(u)) * (s.half + 1.2f);
                ScheduleEmerge(SliceToWorld(s.along + Rand01() * 1.2f - 0.6f, across));
            }
        }

        /// <summary>從船頭往船尾，時間到的段落被切開。</summary>
        private void UpdateSplit(float t)
        {
            if (slices == null)
            {
                return;
            }
            for (int i = 0; i < slices.Count; i++)
            {
                Slice s = slices[i];
                if (!s.split && t >= s.splitAt)
                {
                    s.split = true;
                    slices[i] = s;
                    SplitSlice(s, i);
                }
            }
        }

        /// <summary>破水後船邊還在嘩啦嘩啦：原版的小出水（PawnEmergeFromWater），密度照 SurfacingCurves.SplashRate 收掉。</summary>
        private void PourOff(float t, float dt)
        {
            List<IntVec3> rim = Outline;
            if (rim.Count == 0)
            {
                return;
            }
            float rate = SurfacingCurves.SplashRate.Evaluate(t - BreakAt) * Mathf.Min(12f, rim.Count * 0.12f) * (show.violent ? 1.4f : 1f);
            splashCarry += rate * dt;
            while (splashCarry >= 1f)
            {
                splashCarry -= 1f;
                // 只在已經被劈開的地方（船頭先、船尾後）
                for (int tries = 0; tries < 4; tries++)
                {
                    Vector3 at = rim[rand.Next(rim.Count)].ToVector3Shifted();
                    if (SplitTime(at) <= t)
                    {
                        float small = 0.8f + Rand01() * 1.8f;
                        Emerge(at + new Vector3(Rand01() * 1.4f - 0.7f, 0f, Rand01() * 1.4f - 0.7f), small, small + 0.6f, 0.3f + Rand01() * 0.4f, 0.2f + Rand01() * 0.6f);
                        break;
                    }
                }
            }
        }

        /// <summary>還沒輪到的大出水（照位置排的時間）。</summary>
        private readonly List<(float at, Vector3 pos, float min, float max, float radius)> pendingEmerges = new List<(float, Vector3, float, float, float)>();

        /// <summary>測試用：大出水依時間排序後，沿船頭→船尾方向的位置（0 = 船頭），照實際發生的順序記下。</summary>
        public readonly List<float> EmergeOrder = new List<float>();

        /// <summary>
        /// 排一處出水：時間照位置（<see cref="SplitTime"/>）加 ±0.1 秒抖動；尺寸不固定——
        /// 多半中等（3～6），也有小的（1.5～3）和偶爾的大浪頭（6.5～10）；水沫散開的半徑也各不相同。
        /// </summary>
        private void ScheduleEmerge(Vector3 at)
        {
            float roll = Rand01();
            float size = roll < 0.25f ? 1.5f + Rand01() * 1.5f
                : roll < 0.85f ? 3f + Rand01() * 3f
                : 6.5f + Rand01() * 3.5f;
            size *= show.violent ? 1.25f : 1f;
            float spread = size * (0.1f + Rand01() * 0.15f);
            float radius = 0.3f + Rand01() * (0.4f + size * 0.12f);
            pendingEmerges.Add((SplitTime(at) + (Rand01() - 0.5f) * 0.2f, at, Mathf.Max(0.8f, size - spread), size + spread, radius));
        }

        private void UpdatePendingEmerges()
        {
            float t = Elapsed;
            for (int i = pendingEmerges.Count - 1; i >= 0; i--)
            {
                var p = pendingEmerges[i];
                if (t >= p.at)
                {
                    pendingEmerges.RemoveAt(i);
                    Emerge(p.pos, p.min, p.max, 1f, p.radius);
                    EmergeOrder.Add(FromBow(p.pos));
                }
            }
        }

        /// <summary>沿長軸離船頭多遠（0 = 船頭、1 = 船尾）。</summary>
        public float FromBow(Vector3 pos)
        {
            if (slices == null || slices.Count < 2)
            {
                return 0f;
            }
            float along = alongX ? pos.x : pos.z;
            float frac = Mathf.Clamp01((along - slices[0].along) / (slices[slices.Count - 1].along - slices[0].along));
            return bowSign > 0 ? 1f - frac : frac;
        }

        // ── 原版出水（EffecterDef PawnEmergeFromWater / Large 的兩個子效果），方向沿船體法線 ──

        /// <summary>還沒到時間的第二波水柱（原版每 7 tick 一波、14 tick 內兩波）。</summary>
        private readonly List<(float at, Vector3 pos, Vector3 dir, float min, float max, float chance)> pendingSplashes = new List<(float, Vector3, Vector3, float, float, float)>();

        /// <summary>
        /// 一處出水：SprayerChance（GroundWaterSplash，2～3 個、0 與 7 tick 各一次機會）
        /// 加 SprayerTriggered（WaterChurnNoise，10～12 個、半徑內、轉速 30～50）。
        /// （2026-11-15）水柱與水沫都朝這裡的船體法線（<see cref="HullNormal"/>）噴：側舷往兩旁、船尾細長的龍骨也往兩旁，不再全部往四周放射。
        /// </summary>
        private void Emerge(Vector3 at, float min, float max, float chance, float radius)
        {
            Vector3 dir = HullNormal(at);
            SplashBurst(at, dir, min, max, chance);
            pendingSplashes.Add((Elapsed + 7f / 60f, at, dir, min, max, chance));
            if (churn == null)
            {
                return;
            }
            float normalAngle = dir.AngleFlat();
            int n = 10 + rand.Next(3);
            for (int i = 0; i < n; i++)
            {
                float ang = Rand01() * Mathf.PI * 2f;
                float r = Rand01() * radius;
                FleckCreationData d = FleckMaker.GetDataStatic(at + new Vector3(Mathf.Cos(ang) * r, 0f, Mathf.Sin(ang) * r), DisplayMap, churn, 4f + Rand01() * 6f);
                d.velocityAngle = normalAngle + (Rand01() * 100f - 50f);
                d.velocitySpeed = 0.5f + Rand01() * 2.5f;
                d.rotationRate = 30f + Rand01() * 20f;
                flecks.CreateFleck(d);
                Churns++;
            }
        }

        /// <summary>
        /// 水柱轉到法線方向、往外飄。複本的 scalingAnchor 是中心（原版 (.5,.2) 的錨點偏移只往北補、不跟著旋轉，
        /// 轉向後底部會漂走），所以生成點往法線推出 0.3 倍尺寸，讓水柱的根留在船邊。
        /// </summary>
        private void SplashBurst(Vector3 at, Vector3 dir, float min, float max, float chance)
        {
            if (splash == null || Rand01() >= chance)
            {
                return;
            }
            int n = 2 + rand.Next(2);
            for (int i = 0; i < n; i++)
            {
                float size = min + Rand01() * (max - min);
                Vector3 jet = Quaternion.AngleAxis(Rand01() * 40f - 20f, Vector3.up) * dir;
                FleckCreationData d = FleckMaker.GetDataStatic(at + jet * size * 0.3f, DisplayMap, splash, size);
                d.rotation = jet.AngleFlat();
                d.velocityAngle = d.rotation;
                d.velocitySpeed = (0.3f + Rand01() * 0.9f) * Mathf.Sqrt(size);
                flecks.CreateFleck(d);
                Splashes++;
            }
        }

        private void UpdatePendingSplashes()
        {
            for (int i = pendingSplashes.Count - 1; i >= 0; i--)
            {
                var p = pendingSplashes[i];
                if (Elapsed >= p.at)
                {
                    pendingSplashes.RemoveAt(i);
                    SplashBurst(p.pos, p.dir, p.min, p.max, p.chance);
                }
            }
        }

        /// <summary>
        /// 這個位置的船體法線（地圖平面上、指向船外）：把附近 3 格內的船體格子當成推力，越近推得越大，加起來就是離開船殼的方向。
        /// 船身中間（四周都是船體、推力互相抵消）退回成往最近一舷。
        /// </summary>
        public Vector3 HullNormal(Vector3 pos)
        {
            IntVec3 c = pos.ToIntVec3();
            Vector3 sum = Vector3.zero;
            for (int dx = -3; dx <= 3; dx++)
            {
                for (int dz = -3; dz <= 3; dz++)
                {
                    IntVec3 h = new IntVec3(c.x + dx, 0, c.z + dz);
                    if (!show.hull.Contains(h))
                    {
                        continue;
                    }
                    Vector3 d = pos - h.ToVector3Shifted();
                    d.y = 0f;
                    float m2 = d.sqrMagnitude;
                    if (m2 < 0.01f)
                    {
                        continue;
                    }
                    sum += d / m2;
                }
            }
            if (sum.sqrMagnitude > 0.04f)
            {
                return sum.normalized;
            }
            // 船身中間：往最近的一舷
            float across = alongX ? pos.z : pos.x;
            float along = alongX ? pos.x : pos.z;
            float centerAcross = slices == null || slices.Count == 0 ? (alongX ? hullRect.CenterVector3.z : hullRect.CenterVector3.x)
                : slices.OrderBy(s => Mathf.Abs(s.along - along)).First().center;
            float side = across > centerAcross ? 1f : (across < centerAcross ? -1f : (Rand01() < 0.5f ? -1f : 1f));
            return SliceToWorld(0f, side);
        }

        // ── 船體一段一段浮現 ─────────────────────────────────────────────

        /// <summary>擷取到的船體影像切成沿長軸的條（一段船一條，UV 對到同一張影像），每條自己照劈開的時間浮現。</summary>
        private List<(Mesh mesh, int slice)> strips;
        private Material shipMat;

        private void BuildStrips()
        {
            strips = new List<(Mesh, int)>();
            if (slices == null || slices.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                return;
            }
            float w = Bounds.Width, h = Bounds.Height;
            float len = alongX ? w : h;
            float mid = alongX ? Center.x : Center.z;
            for (int i = 0; i < slices.Count; i++)
            {
                float lo = i == 0 ? -len * 0.5f : (slices[i - 1].along + slices[i].along) * 0.5f - mid;
                float hi = i == slices.Count - 1 ? len * 0.5f : (slices[i].along + slices[i + 1].along) * 0.5f - mid;
                lo = Mathf.Clamp(lo, -len * 0.5f, len * 0.5f);
                hi = Mathf.Clamp(hi, -len * 0.5f, len * 0.5f);
                if (hi <= lo)
                {
                    continue;
                }
                float x0 = alongX ? lo : -w * 0.5f, x1 = alongX ? hi : w * 0.5f;
                float z0 = alongX ? -h * 0.5f : lo, z1 = alongX ? h * 0.5f : hi;
                Mesh mesh = new Mesh
                {
                    vertices = new[] { new Vector3(x0, 0f, z0), new Vector3(x0, 0f, z1), new Vector3(x1, 0f, z1), new Vector3(x1, 0f, z0) },
                    uv = new[] { new Vector2(x0 / w + 0.5f, z0 / h + 0.5f), new Vector2(x0 / w + 0.5f, z1 / h + 0.5f), new Vector2(x1 / w + 0.5f, z1 / h + 0.5f), new Vector2(x1 / w + 0.5f, z0 / h + 0.5f) },
                    triangles = new[] { 0, 1, 2, 0, 2, 3 },
                    normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                };
                strips.Add((mesh, i));
            }
        }

        /// <summary>測試用：船體切成幾條、現在幾條看得到（不透明度 &gt; 0.5）。</summary>
        public int StripCount => strips?.Count ?? 0;

        public int StripsShown { get; private set; }

        /// <summary>清楚的船體：每條在它那段被劈開、水柱蓋上去之後才浮現（ShipAlpha、ShipTint 從那一刻算起）。</summary>
        private void DrawShipStrips(Vector3 offset)
        {
            StripsShown = 0;
            if (strips == null || strips.Count == 0)
            {
                return;
            }
            if (shipMat == null)
            {
                Texture2D tex = CapturedTexture;
                if (tex == null)
                {
                    return;
                }
                shipMat = new Material(ShaderDatabase.Transparent) { mainTexture = tex, renderQueue = 3200 };
            }
            float t = Elapsed;
            float scale = ShipScale(0f);
            Matrix4x4 m = Matrix4x4.TRS((Center + offset).WithY(AltitudeLayer.Skyfaller.AltitudeFor()), Quaternion.identity, new Vector3(scale, 1f, scale));
            foreach ((Mesh mesh, int slice) in strips)
            {
                float local = t - slices[slice].splitAt - SurfacingCurves.StripRevealDelay;
                float a = SurfacingCurves.ShipAlpha.Evaluate(local);
                if (a <= 0.001f)
                {
                    continue;
                }
                if (a > 0.5f)
                {
                    StripsShown++;
                }
                Color c = Color.Lerp(DeepWater, Color.white, SurfacingCurves.ShipTint.Evaluate(local));
                c.a = a;
                block.SetColor(ShaderPropertyIDs.Color, c);
                Graphics.DrawMesh(mesh, m, shipMat, 0, null, 0, block);
            }
        }

        /// <summary>水下船影：柔邊、深藍、隨上浮變濃；破水後每一條在它那段被劈開後 1.2 秒內淡掉，換成清楚的船體。</summary>
        private void DrawSoft(Vector3 offset)
        {
            if (!softTried)
            {
                softTried = true;
                Texture2D src = CapturedTexture;
                if (src != null && Bounds.Height > 0)
                {
                    float pixelsPerCell = src.height / (float)Bounds.Height;
                    int down = Mathf.Clamp(Mathf.RoundToInt(pixelsPerCell / 8f), 1, 8);
                    int radius = Mathf.Max(1, Mathf.RoundToInt(pixelsPerCell / down * SoftEdgeCells * 0.5f));
                    softTex = CutsceneCapture.SoftCopy(src, down, radius);
                    softMat = new Material(ShaderDatabase.Transparent) { mainTexture = softTex, renderQueue = 3190 };
                }
            }
            if (softMat == null)
            {
                return;
            }
            float t = Elapsed;
            float peak = show.ice ? 0.5f : 0.65f;
            float scale = ShipScale(0f);
            Vector3 pos = (Center + offset).WithY(AltitudeLayer.Skyfaller.AltitudeFor() - 0.05f);
            if (t < BreakAt || strips == null || strips.Count == 0)
            {
                float a = t < BreakAt
                    ? (show.ice ? Mathf.Lerp(0.2f, 0.5f, t / BreakAt) : Mathf.Lerp(0.15f, 0.65f, t / BreakAt))
                    : Mathf.Lerp(peak, 0f, Mathf.InverseLerp(BreakAt, BreakAt + 1.5f, t));
                if (a <= 0.001f)
                {
                    return;
                }
                Color c = DeepWater;
                c.a = a;
                block.SetColor(ShaderPropertyIDs.Color, c);
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(Bounds.Width * scale, 1f, Bounds.Height * scale)), softMat, 0, null, 0, block);
                return;
            }
            Matrix4x4 m = Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(scale, 1f, scale));
            foreach ((Mesh mesh, int slice) in strips)
            {
                float splitAt = slices[slice].splitAt;
                float a = Mathf.Lerp(peak, 0f, Mathf.InverseLerp(splitAt, splitAt + 1.2f, t));
                if (a <= 0.001f)
                {
                    continue;
                }
                Color c = DeepWater;
                c.a = a;
                block.SetColor(ShaderPropertyIDs.Color, c);
                Graphics.DrawMesh(mesh, m, softMat, 0, null, 0, block);
            }
        }

        protected override float FadeAt(float progress)
        {
            return 0f;
        }

        protected override float FlameAt(float progress)
        {
            return 0f;
        }

        // ── 每一幀 ─────────────────────────────────────────────────────

        protected override void DrawExtras(Vector3 offset, float progress)
        {
            DrawSoft(offset);
            DrawShipStrips(offset);
            float t = Elapsed;
            float dt = Mathf.Clamp(t - lastElapsed, 0f, 0.1f);
            lastElapsed = t;
            Map map = DisplayMap;
            if (flecks == null)
            {
                flecks = new FleckSystemThrown(map.flecks);
                groundFlecks = new FleckSystemStatic(map.flecks);
            }

            if (show.ice)
            {
                // 裂紋照出現時間長出，新裂開的地方冒雪粉
                List<IntVec3> cracked = show.ApplyUpTo(t);
                for (int i = 0; i < cracked.Count; i++)
                {
                    IntVec3 c = cracked[i];
                    openCracks.Add(c);
                    if (Rand01() < 0.45f)
                    {
                        Fissure(c.ToVector3Shifted(), 2f + Rand01() * 2.5f);
                    }
                    if (i % 3 == 0)
                    {
                        Puff(c.ToVector3Shifted(), IceWhite, 0.8f + Rand01() * 0.6f, 0.4f);
                    }
                    if (Rand01() < 0.12f)
                    {
                        Vent(c, 0.6f);
                    }
                }
                // 噴氣：裂縫一出現就開始冒，破裂瞬間最猛，之後慢慢減弱
                if (openCracks.Count > 0)
                {
                    float rate = t < BreakAt ? Mathf.Lerp(6f, 22f, t / BreakAt)
                        : Mathf.Lerp(show.violent ? 70f : 50f, 8f, Mathf.InverseLerp(BreakAt, Duration, t));
                    gasCarry += rate * dt;
                    while (gasCarry >= 1f)
                    {
                        gasCarry -= 1f;
                        Vent(openCracks[rand.Next(openCracks.Count)], t < BreakAt ? 0.8f : 1.3f);
                    }
                }
                if (!heaved && t >= 2f)
                {
                    heaved = true;
                    Find.CameraDriver.shaker.DoShake(show.violent ? 1.2f : 0.8f);
                }
                if (t >= 2f && t < BreakAt)
                {
                    Heave(Mathf.InverseLerp(2f, BreakAt, t), dt);
                }
            }
            else if (t < BreakAt)
            {
                // 水面開始翻：原版的小出水，越來越密
                if (t >= nextAmbient)
                {
                    nextAmbient = t + Mathf.Lerp(0.3f, 0.08f, t / BreakAt);
                    Emerge(RandomEdgeCell(rand.Next(0, 2)).ToVector3Shifted(), 1f, 2f + t * 0.8f, 0.3f + 0.7f * t / BreakAt, 0.5f);
                }
            }

            if (!broke && t >= BreakAt)
            {
                broke = true;
                OnBreak();
            }
            if (broke)
            {
                UpdateSplit(t);
                if (t < BreakAt + sweep + 2.5f)
                {
                    PourOff(t, dt);
                }
            }

            if (broke && t < EmergedAt + 1.5f)
            {
                // 浮出：碎冰從船邊滑落、海水從甲板淌下
                if (t >= nextAmbient)
                {
                    nextAmbient = t + (show.violent ? 0.04f : 0.07f);
                    IntVec3 edge = RandomEdgeCell(1);
                    Vector3 from = edge.ToVector3Shifted();
                    if (show.ice && Rand01() < 0.6f)
                    {
                        Throw(from, Outward(from) * (1f + Rand01() * 1.5f), 0.3f + Rand01() * 0.8f, 0.5f);
                    }
                }
            }
            else if (broke && show.ice && t >= EmergedAt && t >= nextAmbient)
            {
                // 穩定：開放水冒海煙（冷空氣遇到比較暖的海水）
                nextAmbient = t + 0.08f;
                IntVec3 water = RandomEdgeCell(2);
                if (water.InBounds(map) && water.GetTerrain(map) == DepthRimDefOf.DepthRim_BreachWater && steam != null)
                {
                    FleckCreationData d = FleckMaker.GetDataStatic(water.ToVector3Shifted(), map, steam, 1.2f + Rand01());
                    d.velocityAngle = Rand01() * 30f - 15f;
                    d.velocitySpeed = 0.3f + Rand01() * 0.3f;
                    d.instanceColor = new Color(0.95f, 0.97f, 1f, 0.5f);
                    flecks.CreateFleck(d);
                }
            }

            UpdatePendingEmerges();
            UpdatePendingSplashes();
            UpdateChunks(dt);
            DrawChunks();
            UpdateVapors(dt);
            DrawVapors();
            fleckTicker.Push(dt);
            while (fleckTicker.Pop())
            {
                groundFlecks.Tick();
                flecks.Tick();
            }
            groundFlecks.Update(dt);
            flecks.Update(dt);
            groundFlecks.ForceDraw(drawBatch);
            flecks.ForceDraw(drawBatch);
            drawBatch.Flush(true);
        }

        /// <summary>破裂（冰）或破水（海面）的那一瞬間。</summary>
        private void OnBreak()
        {
            Map map = DisplayMap;
            if (show.ice)
            {
                show.ApplyUpTo(BreakAt + 0.01f);
            }
            DepthRimDefOf.DepthRim_Breach?.PlayOneShot(new TargetInfo(hullRect.CenterCell, map));
            Find.CameraDriver.shaker.DoShake(show.ice ? (show.violent ? 5f : 3.5f) : 1.5f);
            int perimeter = 2 * (hullRect.Width + hullRect.Height);
            float scale = show.violent ? 1.6f : 1f;

            // 碎冰塊四射（數量 ∝ 船體周長）
            if (show.ice)
            {
                int count = Mathf.Min(show.violent ? 220 : 140, perimeter * (show.violent ? 4 : 3));
                for (int i = 0; i < count; i++)
                {
                    IntVec3 edge = RandomEdgeCell(Rand01() < 0.7f ? 1 : 3);
                    Vector3 from = edge.ToVector3Shifted();
                    Vector3 dir = Outward(from);
                    Throw(from, dir * (2f + Rand01() * 6f * scale), 0.25f + Rand01() * (show.violent ? 1.3f : 0.9f), 3f + Rand01() * 6f * scale);
                }
                // 大裂隙：接觸點一道巨大的、船身四周一圈較小的
                foreach (IntVec3 c in show.contacts)
                {
                    Fissure(c.ToVector3Shifted(), 11f + Rand01() * 4f);
                }
                int rims = Mathf.Min(40, perimeter / 3);
                for (int i = 0; i < rims; i++)
                {
                    Fissure(RandomEdgeCell(rand.Next(3, Mathf.Max(4, show.pattern.rubbleRadius + 3))).ToVector3Shifted(), 4.5f + Rand01() * 2.5f);
                }
                // 崩裂瞬間沿裂縫一齊噴氣
                for (int i = 0; i < (show.violent ? 120 : 80) && openCracks.Count > 0; i++)
                {
                    Vent(openCracks[rand.Next(openCracks.Count)], 1.8f);
                }
                // 接觸點的冰被頂起：一團雪霧
                foreach (IntVec3 c in show.contacts)
                {
                    for (int i = 0; i < 10; i++)
                    {
                        Puff(c.ToVector3Shifted() + new Vector3(Rand01() * 4f - 2f, 0f, Rand01() * 4f - 2f), IceWhite, 3f + Rand01() * 2f, 1.2f);
                    }
                }
            }

            // 原版的出水沿船體真正的輪廓（隨機挑格子，間距忽疏忽密，往外隨機偏 0～1.5 格），船身上也冒幾處，
            // 都照位置從船頭排到船尾：水柱畫在船體上面，蓋住浮現的邊緣
            List<IntVec3> rim = Outline;
            int rimCount = Mathf.Min(show.violent ? 90 : 70, Mathf.RoundToInt(rim.Count / (3.5f + Rand01() * 2f)));
            for (int i = 0; i < rimCount && rim.Count > 0; i++)
            {
                Vector3 at = rim[rand.Next(rim.Count)].ToVector3Shifted();
                ScheduleEmerge(at + HullNormal(at) * (Rand01() * 1.5f) + new Vector3(Rand01() - 0.5f, 0f, Rand01() - 0.5f));
            }
            List<IntVec3> hullCells = show.hull.ToList();
            int over = rand.Next(2, Mathf.Max(3, Mathf.Min(12, 3 + hullCells.Count / 25)));
            for (int i = 0; i < over && hullCells.Count > 0; i++)
            {
                ScheduleEmerge(hullCells[rand.Next(hullCells.Count)].ToVector3Shifted());
            }
            // 往外擴的漣漪（原版 WaterRipple）
            if (ripple != null)
            {
                for (int i = 0; i < 18; i++)
                {
                    FleckCreationData d = FleckMaker.GetDataStatic(RandomEdgeCell(rand.Next(1, 4)).ToVector3Shifted(), map, ripple, (3f + Rand01() * 4f) * scale);
                    flecks.CreateFleck(d);
                }
            }
        }

        private List<IntVec3> outline;
        private float heaveCarry;

        /// <summary>緊貼船體真正輪廓外的一圈格子。</summary>
        private List<IntVec3> Outline
        {
            get
            {
                if (outline == null)
                {
                    HashSet<IntVec3> hull = show.hull;
                    outline = hull.SelectMany(c => GenAdj.CellsAdjacent8Way(new TargetInfo(c, DisplayMap)))
                        .Where(c => !hull.Contains(c)).Distinct().ToList();
                }
                return outline;
            }
        }

        /// <summary>
        /// 隆起（2026-11-14：原本在船體外框畫一塊閃爍的白色長方形，不好看，拿掉）：冰面沿著船體真正的輪廓被頂起，
        /// 輪廓外一兩格冒出雪粉、長出細小的裂隙，越接近破裂越密。
        /// </summary>
        private void Heave(float u, float dt)
        {
            List<IntVec3> outline = Outline;
            if (outline.Count == 0)
            {
                return;
            }
            heaveCarry += Mathf.Lerp(20f, 90f, u) * dt;
            while (heaveCarry >= 1f)
            {
                heaveCarry -= 1f;
                IntVec3 c = outline[rand.Next(outline.Count)];
                Vector3 at = c.ToVector3Shifted() + Outward(c.ToVector3Shifted()) * Rand01();
                Puff(at, IceWhite, 1f + Rand01() * 1.2f, 0.6f);
                if (Rand01() < 0.25f)
                {
                    Fissure(at, 1.5f + Rand01() * 1.5f);
                }
            }
        }

        // ── 粒子 ───────────────────────────────────────────────────────

        private void Throw(Vector3 from, Vector3 vel, float size, float vHeight)
        {
            float shade = 0.82f + Rand01() * 0.18f;
            chunks.Add(new Chunk
            {
                pos = from,
                vel = vel,
                vHeight = vHeight,
                size = size,
                rot = Rand01() * 360f,
                rotVel = (Rand01() * 2f - 1f) * 360f,
                life = 2.5f + Rand01() * 2f,
                color = new Color(shade * 0.9f, shade * 0.96f, shade, 0.95f),
            });
        }

        private void UpdateChunks(float dt)
        {
            for (int i = chunks.Count - 1; i >= 0; i--)
            {
                Chunk c = chunks[i];
                c.age += dt;
                if (c.age >= c.life)
                {
                    chunks.RemoveAt(i);
                    continue;
                }
                c.pos += c.vel * dt;
                c.vHeight -= 18f * dt;
                c.height += c.vHeight * dt;
                if (c.height <= 0f)
                {
                    // 落在冰上：彈一下、滑一段
                    c.height = 0f;
                    c.vHeight = Mathf.Abs(c.vHeight) * 0.25f;
                    c.vel *= 0.55f;
                    c.rotVel *= 0.5f;
                }
                c.rot += c.rotVel * dt;
                chunks[i] = c;
            }
        }

        private void DrawChunks()
        {
            if (chunkMat == null)
            {
                return;
            }
            float alt = AltitudeLayer.MoteOverhead.AltitudeFor();
            foreach (Chunk c in chunks)
            {
                Color col = c.color;
                col.a *= 1f - Mathf.InverseLerp(c.life * 0.7f, c.life, c.age);
                block.SetColor(ShaderPropertyIDs.Color, col);
                // 高度畫成往北偏（原版拋物物件的作法）
                Vector3 pos = new Vector3(c.pos.x, alt + c.height * 0.01f, c.pos.z + c.height);
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.AngleAxis(c.rot, Vector3.up), new Vector3(c.size, 1f, c.size * 0.75f)), chunkMat, 0, null, 0, block);
            }
        }

        /// <summary>原版地面裂隙（靜態 fleck，畫在地面上），染成冰下的暗藍灰。</summary>
        private void Fissure(Vector3 at, float scale)
        {
            if (fissure == null)
            {
                return;
            }
            FleckCreationData d = FleckMaker.GetDataStatic(at + new Vector3(Rand01() - 0.5f, 0f, Rand01() - 0.5f), DisplayMap, fissure, scale);
            d.rotation = Rand01() * 360f;
            d.instanceColor = new Color(0.32f, 0.42f, 0.54f, 1f);
            groundFlecks.CreateFleck(d);
            Fissures++;
        }

        /// <summary>沿裂縫噴出的氣：兩三團往上（畫面北方）衝的冰白色煙霧。</summary>
        private void Vent(IntVec3 cell, float strength)
        {
            GasJets++;
            int puffs = 3 + rand.Next(3);
            Vector3 from = cell.ToVector3Shifted();
            for (int i = 0; i < puffs; i++)
            {
                // 往上（畫面北方）衝，帶一點左右偏；越飛越大、越淡
                float angle = (Rand01() * 50f - 25f) * Mathf.Deg2Rad;
                float speed = (1.5f + Rand01() * 2.5f) * strength;
                vapors.Add(new Vapor
                {
                    pos = from + new Vector3(Rand01() * 0.6f - 0.3f, 0f, Rand01() * 0.6f - 0.3f),
                    vel = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * speed,
                    size = (0.8f + Rand01() * 0.8f) * strength,
                    grow = (1.2f + Rand01() * 1.2f) * strength,
                    life = 1.4f + Rand01() * 1.6f,
                    alpha = Mathf.Clamp01(0.45f + 0.15f * strength) * (0.7f + Rand01() * 0.3f),
                });
            }
        }

        private struct Vapor
        {
            public Vector3 pos;
            public Vector3 vel;
            public float size;
            public float grow;
            public float age;
            public float life;
            public float alpha;
        }

        private readonly List<Vapor> vapors = new List<Vapor>();

        private Material vaporMat;

        /// <summary>柔邊的白色圓（中心不透明、往外漸淡），程式產生，給水氣用。</summary>
        internal static Texture2D MakeSoftDisc()
        {
            const int n = 64;
            Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            Color32[] px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f;
                    float dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    a = a * a * (3f - 2f * a);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }

        private void UpdateVapors(float dt)
        {
            for (int i = vapors.Count - 1; i >= 0; i--)
            {
                Vapor v = vapors[i];
                v.age += dt;
                if (v.age >= v.life)
                {
                    vapors.RemoveAt(i);
                    continue;
                }
                v.pos += v.vel * dt;
                v.vel *= 1f - 1.2f * dt;
                v.size += v.grow * dt;
                vapors[i] = v;
            }
        }

        private void DrawVapors()
        {
            if (vaporMat == null)
            {
                return;
            }
            float alt = AltitudeLayer.MoteOverhead.AltitudeFor() + 0.1f;
            foreach (Vapor v in vapors)
            {
                float u = v.age / v.life;
                float a = v.alpha * Mathf.Min(1f, u * 8f) * (1f - u * u);
                block.SetColor(ShaderPropertyIDs.Color, new Color(0.97f, 0.99f, 1f, a));
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(v.pos.WithY(alt), Quaternion.identity, new Vector3(v.size, 1f, v.size)), vaporMat, 0, null, 0, block);
            }
        }

        /// <summary>
        /// 演出結束、船體搬上冰面之後，把一部分裂隙留在地圖上（原版的 fleck，遊戲時間；恢復遊戲後幾秒內慢慢消失），
        /// 演出的私有 fleck 系統跟著視窗關掉時，畫面才不會一下子變乾淨。
        /// </summary>
        public static void LeaveTraces(SurfacingShow show)
        {
            Map map = show.map;
            if (!show.ice || map == null)
            {
                return;
            }
            FleckDef fissureDef = DefDatabase<FleckDef>.GetNamedSilentFail("GroundCrack");
            List<IntVec3> cracks = map.AllCells.Where(c => c.GetTerrain(map) == DepthRimDefOf.DepthRim_IceCracked).ToList();
            foreach (IntVec3 c in cracks)
            {
                if (fissureDef != null && Rand.Chance(0.4f))
                {
                    FleckCreationData d = FleckMaker.GetDataStatic(c.ToVector3Shifted(), map, fissureDef, Rand.Range(2f, 4.5f));
                    d.rotation = Rand.Range(0f, 360f);
                    d.instanceColor = new Color(0.32f, 0.42f, 0.54f, 1f);
                    map.flecks.CreateFleck(d);
                }
            }
        }

        private void Puff(Vector3 at, Color color, float scale, float speed)
        {
            if (puff == null)
            {
                return;
            }
            FleckCreationData d = FleckMaker.GetDataStatic(at, DisplayMap, puff, scale);
            d.velocityAngle = Rand01() * 360f;
            d.velocitySpeed = speed * (0.5f + Rand01());
            d.rotationRate = (Rand01() * 2f - 1f) * 60f;
            d.instanceColor = color;
            flecks.CreateFleck(d);
        }

        private void Spray(Vector3 at, float scale, float speed)
        {
            if (foam == null)
            {
                return;
            }
            FleckCreationData d = FleckMaker.GetDataStatic(at, DisplayMap, foam, scale);
            d.velocityAngle = Vector3.SignedAngle(Vector3.forward, Outward(at), Vector3.up) + (Rand01() * 60f - 30f);
            d.velocitySpeed = speed * (0.5f + Rand01());
            d.rotationRate = (Rand01() * 2f - 1f) * 90f;
            d.instanceColor = new Color(0.9f, 0.95f, 1f, 0.9f);
            flecks.CreateFleck(d);
        }

        /// <summary>船體邊緣往外 <paramref name="ring"/> 格的隨機格（0 = 船體邊上）。</summary>
        private IntVec3 RandomEdgeCell(int ring)
        {
            CellRect r = hullRect.ExpandedBy(ring);
            int perimeter = 2 * (r.Width + r.Height);
            int k = rand.Next(Math.Max(1, perimeter));
            if (k < r.Width) return new IntVec3(r.minX + k, 0, r.minZ);
            k -= r.Width;
            if (k < r.Width) return new IntVec3(r.minX + k, 0, r.maxZ);
            k -= r.Width;
            if (k < r.Height) return new IntVec3(r.minX, 0, r.minZ + k);
            k -= r.Height;
            return new IntVec3(r.maxX, 0, r.minZ + Math.Min(k, r.Height - 1));
        }

        private Vector3 Outward(Vector3 from)
        {
            Vector3 d = from - hullRect.CenterVector3;
            d.y = 0f;
            if (d.sqrMagnitude < 0.01f)
            {
                d = Vector3.forward;
            }
            return Quaternion.AngleAxis(Rand01() * 40f - 20f, Vector3.up) * d.normalized;
        }

        private float Rand01() => (float)rand.NextDouble();
    }
}
