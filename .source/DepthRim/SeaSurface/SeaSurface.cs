using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海面地圖的共用判斷。海不是原版深水，而是類虛空的 <see cref="DepthRimDefOf.DepthRim_OpenSea"/>：
    /// 地形本身不畫，海面是 <see cref="SectionLayer_SeaSurface"/> 畫的背景。
    /// </summary>
    public static class SeaSurface
    {
        public static bool IsOpenSea(IntVec3 c, Map map)
        {
            return c.InBounds(map) && map.terrainGrid.TerrainAt(c) == DepthRimDefOf.DepthRim_OpenSea;
        }

        /// <summary>水面層（海面地點的地圖）。平台層見 <see cref="SeaLevels"/>。</summary>
        public static bool IsSeaSurfaceMap(Map map)
        {
            return map?.Parent is WorldObject_SeaSurfaceSite;
        }

        /// <summary>這一格看得到海：水面層的開放海面，或平台層的高空（往下看到海）。</summary>
        public static bool ShowsSea(IntVec3 c, Map map)
        {
            if (!c.InBounds(map))
            {
                return false;
            }
            TerrainDef t = map.terrainGrid.TerrainAt(c);
            return t == DepthRimDefOf.DepthRim_OpenSea || t == DepthRimDefOf.DepthRim_OpenAir;
        }
    }

    /// <summary>海面地點：水面層或平台層有玩家建築、擋住移除的 Pawn，或玩家正在看其中一層時保留地圖（平台層跟著水面層一起移除）。</summary>
    public class WorldObject_SeaSurfaceSite : MapParent
    {
        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = true;
            if (!HasMap)
            {
                return false;
            }
            if (Keeps(Map))
            {
                return false;
            }
            Map platform = SeaLevels.PlatformLevelOf(Map);
            return platform == null || !Keeps(platform);
        }

        private static bool Keeps(Map map)
        {
            return map.listerBuildings.allBuildingsColonist.Count > 0 || map.mapPawns.AnyPawnBlockingMapRemoval || Find.CurrentMap == map;
        }
    }

    /// <summary>
    /// 海面地圖（水面層）生成：整張鋪開放海面。平台層由玩家蓋的第一根支柱建立（<see cref="Building_PillarFoot"/>）。
    /// <see cref="platformSize"/> &gt; 0 時照舊在中央鋪一塊海上平台（舊的單層做法，只留給測試與舊設定）。
    /// </summary>
    public class GenStep_SeaSurface : GenStep
    {
        public IntVec2 platformSize = new IntVec2(0, 0);

        public override int SeedPart => 0x5EA5F1;

        public override void Generate(Map map, GenStepParams parms)
        {
            TerrainGrid grid = map.terrainGrid;
            foreach (IntVec3 c in map.AllCells)
            {
                grid.SetTerrain(c, DepthRimDefOf.DepthRim_OpenSea);
            }
            if (platformSize.x <= 0 || platformSize.z <= 0)
            {
                return;
            }
            IntVec3 center = map.Center;
            CellRect rect = CellRect.CenteredOn(center, platformSize.x, platformSize.z).ClipInsideMap(map);
            foreach (IntVec3 c in rect)
            {
                grid.SetTerrain(c, DepthRimDefOf.DepthRim_OceanPlatform);
            }
            // 一座平台支柱基座（3×3）壓在南緣中央：柱身從平台南緣下方露出來，支撐範圍蓋住整塊起始平台
            IntVec3 basePos = new IntVec3(rect.CenterCell.x, 0, rect.minZ + 1);
            GenSpawn.Spawn(ThingMaker.MakeThing(DepthRimDefOf.DepthRim_PlatformPillar), basePos, map, Rot4.North);
        }
    }

    /// <summary>
    /// 海面地圖的畫面資料：每張地圖一份材質與遮罩（每格一個像素：r = 高出海面的平台，g = 支柱佔地，b = 浮在水面的平台（船體平台））。
    /// 海面在平台下方 <see cref="SeaDropCells"/> 格，著色器用遮罩畫平台落在海上的影子與支柱腳下的白沫。
    /// 遮罩在地形或建築改變時由 <see cref="SectionLayer_SeaSurface"/> 標記，下一幀在主執行緒重建。
    /// </summary>
    public class MapComponent_SeaSurface : MapComponent
    {
        /// <summary>
        /// 平台甲板離海面幾格（畫面上的南偏量）。和柱身（<see cref="PlatformPillarExtension"/>：11×18.33、偏移 -7.5，頂端在基座中心上方 1.67 格）對齊：
        /// 混凝土基座（貼圖 75%～97%）中段在頂端下方約 15.77 格，也就是基座中心往南約 14.1 格，看起來支柱就是在那裡入水。
        /// 改柱身貼圖、尺寸或偏移時要一起改（<see cref="PlatformPillarGeometry.SeaDrop"/> 會算出這個值，單元測試會比對）。
        /// </summary>
        public const float SeaDropCells = 14.1f;

        /// <summary>
        /// 原版深海的畫法（2026-11-14）：x = 原版配色的比重（1 = 完全照原版深海），y = DepthRim 自己的浪峰線還留多少，
        /// z = 漣漪密度（原版 _WaterRippleDensity），w = 漣漪強度（原版 _WaterDepthIntensity）。
        /// </summary>
        public static readonly Vector4 VanillaParams = new Vector4(1f, 0f, 1f, 1f);

        /// <summary>原版深海水（WaterOceanDeep）的光照色階貼圖：橫軸漫射、縱軸反光。</summary>
        public static Texture2D VanillaDeepRamp
        {
            get
            {
                string path = DefDatabase<TerrainDef>.GetNamedSilentFail("WaterOceanDeep")?.texturePath ?? "Terrain/Surfaces/WaterDeepRamp";
                return ContentFinder<Texture2D>.Get(path, reportFailure: false);
            }
        }

        private Material material;

        /// <summary>平台影子暗化層的材質（<see cref="SectionLayer_PlatformShadow"/>）。</summary>
        private Material shadowMaterial;

        /// <summary>這張地圖的柱身材質（每種柱身貼圖一份）：柱身依高度自己算影子，參數跟著這張地圖。</summary>
        private readonly Dictionary<string, Material> pillarMaterials = new Dictionary<string, Material>();

        // 以下只有水面層（Owner）用：遮罩資料與貼圖兩層共用一份
        private PlatformShadowField field;
        private bool fieldStale = true;
        private Texture2D mask;
        private bool textureStale = true;
        private float nextTextureRebuild;

        /// <summary>貼圖最快多久（真實秒數）重建一次：鋪平台時地形一格一格變，不用每幀重建。</summary>
        public const float TextureRebuildSeconds = 0.25f;

        private int sunTick = -1;
        private PlatformSun sun;
        private Texture2D boundMask;

        /// <summary>給驗收測試：最近一次重建時遮罩裡的平台格與支柱格數。</summary>
        public int lastPlatformCells;
        public int lastPillarCells;

        /// <summary>
        /// 水面層的地形或建築每改變一次就加一（<see cref="Patch_SeaLevelChanged"/>）。平台層記住看過的版本，
        /// 不一樣時重建「往下看」的快照（<see cref="SectionLayer_LowerLevel"/>）與海面遮罩。
        /// </summary>
        public int version;

        private int seenSeaVersion = -1;

        private float nextLowerCheck;

        /// <summary>平台層多久（真實秒數）檢查一次水面層有沒有變：快照不需要即時。</summary>
        public const float LowerLevelRefreshSeconds = 2f;

        private float swayFactor = 1f;

        /// <summary>海面擺動的天氣倍率（平滑過）：兩層都用水面層的風速。</summary>
        public float SwayFactor => swayFactor;

        /// <summary>天氣倍率每秒最多變多少（真實秒數）：起風時海面與鏡頭慢慢變大，不會突然跳。</summary>
        public const float SwayFactorChangePerSecond = 0.25f;

        /// <summary>
        /// 這張地圖目前的目標天氣倍率：用水面層的風速（平台層是口袋地圖，風以水面層為準），
        /// 惡劣海況的天氣另有下限（<see cref="SeaStateExtension.swayFloor"/>）。
        /// </summary>
        public float TargetSwayFactor()
        {
            Map windMap = SeaLevels.SeaLevelOf(map) ?? map;
            float wind = SeaSway.WeatherFactor(windMap.windManager?.WindSpeed ?? 0f);
            return Mathf.Max(wind, SeaState.Of(map).swayFloor);
        }

        private float roughness;

        /// <summary>海面看起來多兇（平滑過，0..1）：白浪、浪花、海色、漣漪（<see cref="SeaStateExtension.roughness"/>）。</summary>
        public float Roughness => roughness;

        /// <summary>海況每秒最多變多少（真實秒數）。</summary>
        public const float RoughnessChangePerSecond = 0.15f;

        public float TargetRoughness()
        {
            return Mathf.Clamp01(SeaState.Of(map).roughness);
        }

        /// <summary>立刻把天氣倍率與海況設成目標值（驗收測試）。</summary>
        public void SnapSwayFactor()
        {
            swayFactor = TargetSwayFactor();
            roughness = TargetRoughness();
            ApplySway();
        }

        private void ApplySway()
        {
            if (material == null)
            {
                return;
            }
            material.SetVector("_Sway", new Vector4(SeaSway.BaseAmplitude * swayFactor, SeaSway.VerticalShare, 0f, 0f));
            ApplyRoughness(material, roughness);
        }

        /// <summary>
        /// 海況對海面材質的影響：白浪與海色（_Rough）、支柱與船身的浪花（_FoamAmount、_Hull.z）、浪峰線與漣漪（_Vanilla.y／w）。
        /// 不改 _WaveSpeed：它乘在動畫時鐘上，中途改變會讓整片海跳一下。
        /// </summary>
        public static void ApplyRoughness(Material mat, float r)
        {
            mat.SetVector("_Rough", RoughParams(r));
            mat.SetFloat("_FoamAmount", 1f + 1.4f * r);
            Vector4 hull = HullParams;
            hull.z *= 1f + 1.5f * r;
            mat.SetVector("_Hull", hull);
            Vector4 vanilla = VanillaParams;
            vanilla.y = 0.35f * r;
            vanilla.w *= 1f + 1.6f * r;
            mat.SetVector("_Vanilla", vanilla);
        }

        /// <summary>著色器的 _Rough：x = 開放海面的白浪，y = 海色變暗、偏灰綠，z = 浪花範圍加大。</summary>
        public static Vector4 RoughParams(float r)
        {
            return new Vector4(r, 0.55f * r, r, 0f);
        }

        /// <summary>給驗收測試：平台層的往下看快照重建過幾次。</summary>
        public int lowerLevelRefreshes;

        /// <summary>給驗收測試：最近一次重建時遮罩裡浮在水面的平台格（b）。</summary>
        public int lastFloatingCells;

        /// <summary>上次重建時箱網浮框的格數（水線白沫）。</summary>
        public int lastCageCells;

        /// <summary>浮在水面的平台：水線在甲板下方幾格（= 船體平台的乾舷）、影子濃度、白沫強度。</summary>
        public static readonly Vector4 HullParams = new Vector4(0.5f, 0.45f, 1f, 0f);

        /// <summary>
        /// 浪花擺動的相位場（2026-11-15）：x = 雜訊圖一個像素幾格，y = 各處相位最多差幾秒（約半個主擺動週期 2π/0.55），zw = 雜訊圖平移（像素／秒）。
        /// 每個地方的浪花用自己的相位擺動，同一時間各處往不同方向擺（著色器的 foamPhase）。
        /// </summary>
        public static readonly Vector4 FoamPhaseParams = new Vector4(12f, 5.7f, 0.01f, 0.006f);

        public MapComponent_SeaSurface(Map map) : base(map)
        {
        }

        /// <summary>
        /// 擁有遮罩的那一層（水面層）的元件：兩層看到的是同一片海、同一批平台，遮罩資料與貼圖只算一份。
        /// 平台層還沒連上水面層（或不是海面地圖）時是自己。
        /// </summary>
        public MapComponent_SeaSurface Owner => SeaLevels.SeaLevelOf(map)?.GetComponent<MapComponent_SeaSurface>() ?? this;

        /// <summary>遮罩要重建（地形、支柱改變時）：資料下次用到時重算，貼圖節流後重建。</summary>
        public void MarkDirty()
        {
            MapComponent_SeaSurface owner = Owner;
            owner.fieldStale = true;
            owner.textureStale = true;
        }

        /// <summary>模糊好的遮罩資料（水面層的，過期時重算）。</summary>
        public PlatformShadowField Field
        {
            get
            {
                MapComponent_SeaSurface owner = Owner;
                if (owner.field == null || owner.fieldStale)
                {
                    owner.BuildField();
                }
                return owner.field;
            }
        }

        /// <summary>這張地圖這一刻的太陽（每個 tick 算一次；兩層都用水面層的天空）。</summary>
        public PlatformSun Sun
        {
            get
            {
                int tick = Find.TickManager?.TicksGame ?? 0;
                if (tick != sunTick)
                {
                    sunTick = tick;
                    sun = PlatformSun.For(SeaLevels.SeaLevelOf(map) ?? map);
                }
                return sun;
            }
        }

        /// <summary>
        /// 這一格（水面層）的天光被平台擋住多少：0 = 沒有，1 = 完全（遊戲邏輯的光照，<see cref="Patch_PlatformShadowGlow"/>）。
        /// 和畫面上的影子同一份資料、同一個算法（<see cref="PlatformShadowField.GameplayShadow"/>）。平台層一律 0。
        /// </summary>
        public float ShadowAt(IntVec3 c)
        {
            if (!SeaLevels.IsSeaLevel(map) || !c.InBounds(map))
            {
                return 0f;
            }
            return Field.GameplayShadow(new Vector2(c.x + 0.5f, c.z + 0.5f), Sun);
        }

        /// <summary>遮罩貼圖（水面層的，第一次用到時建立）。</summary>
        public Texture2D Mask
        {
            get
            {
                MapComponent_SeaSurface owner = Owner;
                if (owner.mask == null)
                {
                    owner.RebuildMask();
                }
                return owner.mask;
            }
        }

        /// <summary>
        /// 平台影子的暗化層材質（<see cref="SectionLayer_PlatformShadow"/>）。水面層排在建築與 Pawn 之後、柱身之前，
        /// 平台層排在往下看的快照之後、柱身之前（柱身自己依高度算影子）。著色器沒解析到時回 null。
        /// </summary>
        public Material ShadowMaterial
        {
            get
            {
                shadowMaterial ??= NewShadedMaterial(SeaLevels.IsPlatformLevel(map) ? "DepthRim_PlatformShadowLower" : "DepthRim_PlatformShadow", "DepthRim_PlatformShadow_", BaseContent.WhiteTex);
                return shadowMaterial;
            }
        }

        /// <summary>柱身材質：平台層往下伸（排在快照之後、甲板之前），水面層往上伸（排在 Pawn 之後）。每種柱身貼圖一份。</summary>
        public Material PillarMaterial(PlatformPillarExtension ext)
        {
            if (ext == null || ext.pillarTexPath.NullOrEmpty())
            {
                return null;
            }
            if (!pillarMaterials.TryGetValue(ext.pillarTexPath, out Material mat))
            {
                Texture2D tex = ContentFinder<Texture2D>.Get(ext.pillarTexPath, reportFailure: false);
                mat = tex == null ? null : NewShadedMaterial(SeaLevels.IsPlatformLevel(map) ? "DepthRim_Background" : "DepthRim_PillarRaised", "DepthRim_Pillar_", tex);
                if (mat != null)
                {
                    mat.SetColor("_Color", Color.white);
                    // 柱身貼圖：入水處在貼圖從下往上的幾成、貼圖高幾格（片段的高度 = (v − x) × y）
                    mat.SetVector("_Pillar", new Vector4(1f - PlatformPillarGeometry.WaterlineFraction, ext.pillarDrawSize.y, 0f, 0f));
                }
                pillarMaterials[ext.pillarTexPath] = mat;
            }
            return mat;
        }

        /// <summary>建立一個讀平台影子的材質（暗化層、柱身），設好這張地圖固定的參數與遮罩；每幀的太陽由 <see cref="ApplySun"/> 設。</summary>
        private Material NewShadedMaterial(string shaderDefName, string namePrefix, Texture mainTex)
        {
            Shader shader = DefDatabase<ShaderTypeDef>.GetNamedSilentFail(shaderDefName)?.Shader;
            if (shader == null)
            {
                Log.ErrorOnce($"[DepthRim] ShaderTypeDef {shaderDefName} is missing; platform shadows will not be drawn.", shaderDefName.GetHashCode());
                return null;
            }
            Material mat = new Material(shader)
            {
                name = namePrefix + map.uniqueID,
                mainTexture = mainTex,
            };
            ApplyShadowParams(mat);
            return mat;
        }

        /// <summary>
        /// 讀平台影子的材質（海面、暗化層、柱身）共用的參數：地圖大小；_Height = (看下去的落差, 甲板高, 遠近淡化, 0)；
        /// _Deck = (甲板輪廓濃度, 額外柔化, 陽光投影最暗, 天光遮蔽最暗)；遮罩；目前的太陽（<see cref="CastVector"/>）。
        /// </summary>
        private void ApplyShadowParams(Material mat)
        {
            bool lower = SeaLevels.IsPlatformLevel(map);
            mat.SetVector("_MapSize", new Vector4(map.Size.x, map.Size.z, 0f, 0f));
            mat.SetVector("_Height", new Vector4(SeaLevels.ViewDrop(map), SeaDropCells, lower ? PlatformShadow.LowerFade : 1f, 0f));
            mat.SetVector("_Deck", new Vector4(lower ? 0f : PlatformShadow.DeckOutline, lower ? PlatformShadow.LowerExtraSoftness : 0f, PlatformShadow.CastDarkness, PlatformShadow.SkyDarkness));
            mat.SetTexture("_MaskTex", Mask);
            mat.SetVector("_Cast", CastVector());
        }

        /// <summary>每幀的太陽（_Cast）：xy = 甲板影子位移（格），z = 影子強度（0..1：時刻、天氣、日夜）。</summary>
        public Vector4 CastVector()
        {
            PlatformSun s = Sun;
            return new Vector4(s.cast.x, s.cast.y, s.strength, 0f);
        }

        /// <summary>把目前的太陽與最新的遮罩設到這張地圖所有讀影子的材質上（只在看著這張地圖時，每幀）。</summary>
        private void ApplySun()
        {
            Vector4 cast = CastVector();
            Texture2D tex = Owner.mask;
            bool rebind = tex != null && tex != boundMask;
            boundMask = tex;
            void Apply(Material mat)
            {
                if (mat == null)
                {
                    return;
                }
                mat.SetVector("_Cast", cast);
                if (rebind)
                {
                    mat.SetTexture("_MaskTex", tex);
                }
            }
            Apply(material);
            Apply(shadowMaterial);
            foreach (Material mat in pillarMaterials.Values)
            {
                Apply(mat);
            }
        }

        /// <summary>這張地圖的海面材質（第一次要用時建立，同時建好遮罩）。著色器沒解析到時回 null。</summary>
        public Material Material
        {
            get
            {
                if (material == null)
                {
                    Shader shader = SectionLayer_SeaSurface.ShaderDef?.Shader;
                    if (shader == null)
                    {
                        return null;
                    }
                    material = new Material(shader);
                    material.name = "DepthRim_SeaSurface_" + map.uniqueID;
                    material.mainTexture = BaseContent.WhiteTex;
                    // 著色器的 @default 不會自動套到材質上（實測海面變成淡藍），每個參數都要在這裡設
                    material.SetColor("_Color", new Color(0.055f, 0.215f, 0.300f));
                    material.SetColor("_DeepColor", new Color(0.030f, 0.120f, 0.190f));
                    material.SetColor("_CrestColor", new Color(0.300f, 0.520f, 0.600f));
                    material.SetColor("_FoamColor", new Color(0.850f, 0.920f, 0.940f));
                    // 水面層看海不用偏移；平台層的海在下方 SeaDropCells 格（_Height.x）；遮罩與太陽和暗化層、柱身共用
                    ApplyShadowParams(material);
                    material.SetVector("_Hull", HullParams);
                    material.SetVector("_Sway", new Vector4(SeaSway.BaseAmplitude * swayFactor, SeaSway.VerticalShare, 0f, 0f));
                    material.SetFloat("_WaveSpeed", 1f);
                    material.SetFloat("_FoamAmount", 1f);
                    material.SetVector("_FoamPhase", FoamPhaseParams);                    // 2026-11-14：海水照原版深海畫（著色器的 vanillaDeepOcean），反光交給原版的日月反光，自己的閃光點與浪峰線不畫
                    material.SetFloat("_GlintAmount", 0f);
                    material.SetVector("_Vanilla", VanillaParams);
                    ApplyRoughness(material, roughness);
                    material.SetTexture("_RippleTex", TexGame.RippleTex);
                    material.SetTexture("_NoiseTex", TexGame.NoiseTex);
                    material.SetTexture("_WaterReflectionTex", ContentFinder<Texture2D>.Get("Other/WaterReflection", reportFailure: false) ?? BaseContent.GreyTex);
                    material.SetTexture("_RampTex", VanillaDeepRamp ?? BaseContent.GreyTex);
                    // 航跡（水面航行）：@default 不會套上，一開始全部設好（沒在航行時強度 0、海水不流）
                    ApplyWake(material);
                }
                return material;
            }
        }

        // ── 航跡（水面航行的船，SeaWake）──

        private float wakeSpeed;
        private Vector2 wakeHeading = Vector2.up;
        private Vector2 wakeFlow;
        private SeaWake.Hull wakeHull;
        private bool wakeHullDirty = true;
        private int wakeLastTick = -1;
        private float nextWakeHullCheck;

        /// <summary>給驗收測試：目前的航跡強度（0..1）、航向與累積的海水位移。</summary>
        public float WakeSpeed => wakeSpeed;

        public Vector2 WakeHeading => wakeHeading;

        public Vector2 WakeFlow => wakeFlow;

        public SeaWake.Hull WakeHull => wakeHull;

        /// <summary>立刻把航跡強度設成目標值並重算船體外框（驗收測試；平常用真實時間平滑，測試以高倍速跑時跟不上）。</summary>
        public void SnapWake()
        {
            wakeSpeed = (map.Parent as WorldObject_SurfaceShip)?.WakeSpeed ?? 0f;
            wakeHullDirty = true;
            nextWakeHullCheck = 0f;
            UpdateWake(0f);
        }

        /// <summary>船體移動或航向改變：下一幀重算航跡的船體外框。</summary>
        public void Notify_HullMoved()
        {
            wakeHullDirty = true;
        }

        /// <summary>
        /// 每幀：航跡強度往船的推進程度靠（真實時間平滑）、海水依遊戲時間往船尾流、船體外框（每 2 真實秒或船體變動時）重算，設進材質。
        /// 不是水面航行的船時強度歸零，海水停在原處。
        /// </summary>
        public void UpdateWake(float realDelta)
        {
            if (material == null)
            {
                return;
            }
            WorldObject_SurfaceShip ship = map.Parent as WorldObject_SurfaceShip;
            float target = ship?.WakeSpeed ?? 0f;
            Vector2? heading = ship?.MapHeading;
            if (heading.HasValue && (heading.Value - wakeHeading).sqrMagnitude > 1e-6f)
            {
                wakeHeading = heading.Value;
                wakeHullDirty = true;
            }
            wakeSpeed = Mathf.MoveTowards(wakeSpeed, target, SeaWake.SpeedChangePerSecond * realDelta);
            int tick = Find.TickManager.TicksGame;
            int ticks = wakeLastTick < 0 ? 0 : Mathf.Max(0, tick - wakeLastTick);
            wakeLastTick = tick;
            wakeFlow = SeaWake.Wrap(wakeFlow + SeaWake.FlowStep(wakeHeading, wakeSpeed, ticks / 60f));
            if (wakeSpeed > 0f && (wakeHullDirty || Time.realtimeSinceStartup >= nextWakeHullCheck))
            {
                wakeHullDirty = false;
                nextWakeHullCheck = Time.realtimeSinceStartup + 2f;
                wakeHull = SeaWake.HullAlong(map.AllCells.Where(c => MapComponent_SeabedMemory.IsPlatform(map.terrainGrid.TerrainAt(c))), wakeHeading);
            }
            ApplyWake(material);
        }

        private void ApplyWake(Material mat)
        {
            mat.SetVector("_Voyage", new Vector4(wakeHeading.x, wakeHeading.y, wakeSpeed, 0f));
            mat.SetVector("_Wake", wakeSpeed > 0f ? wakeHull.ToShader() : Vector4.zero);
            mat.SetVector("_Flow", new Vector4(wakeFlow.x, wakeFlow.y, 0f, 0f));
            mat.SetVector("_BowA", wakeHull.BowA);
            mat.SetVector("_BowB", wakeHull.BowB);
        }

        public override void MapComponentUpdate()
        {
            if (material != null)
            {
                swayFactor = Mathf.MoveTowards(swayFactor, TargetSwayFactor(), SwayFactorChangePerSecond * Time.deltaTime);
                roughness = Mathf.MoveTowards(roughness, TargetRoughness(), RoughnessChangePerSecond * Time.deltaTime);
                ApplySway();
                UpdateWake(Time.deltaTime);
            }
            if (SeaLevels.IsPlatformLevel(map) && Find.CurrentMap == map && Time.realtimeSinceStartup >= nextLowerCheck)
            {
                nextLowerCheck = Time.realtimeSinceStartup + LowerLevelRefreshSeconds;
                RefreshLowerLevelIfChanged();
            }
            if (Find.CurrentMap == map && (material != null || shadowMaterial != null || pillarMaterials.Count > 0))
            {
                MapComponent_SeaSurface owner = Owner;
                if (owner.textureStale && Time.realtimeSinceStartup >= owner.nextTextureRebuild)
                {
                    owner.RebuildMask();
                }
                ApplySun();
            }
        }

        /// <summary>平台層：水面層變過就重建往下看的快照與遮罩。回傳是否重建。</summary>
        public bool RefreshLowerLevelIfChanged()
        {
            MapComponent_SeaSurface sea = SeaLevels.SeaLevelOf(map)?.GetComponent<MapComponent_SeaSurface>();
            if (sea == null || sea.version == seenSeaVersion)
            {
                return false;
            }
            seenSeaVersion = sea.version;
            lowerLevelRefreshes++;
            map.mapDrawer.WholeMapChanged((ulong)DepthRimDefOf.DepthRim_LowerLevel);
            MarkDirty();
            return true;
        }

        /// <summary>
        /// 立刻重建遮罩資料與貼圖（不節流；驗收測試也直接呼叫）。兩層共用水面層的一份：
        /// r = 甲板（模糊，投影）、g = 支柱入水處（模糊，白沫與柱影）、b = 浮船（不模糊）、a = 甲板（大半徑模糊，天光遮蔽）。
        /// </summary>
        public void RebuildMask()
        {
            MapComponent_SeaSurface owner = Owner;
            if (owner != this)
            {
                owner.RebuildMask();
                lastPlatformCells = owner.lastPlatformCells;
                lastFloatingCells = owner.lastFloatingCells;
                lastCageCells = owner.lastCageCells;
                lastPillarCells = owner.lastPillarCells;
                return;
            }
            BuildField();
            int w = map.Size.x;
            int h = map.Size.z;
            if (mask == null || mask.width != w || mask.height != h)
            {
                DestroyMask();
                mask = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    name = "DepthRim_SeaMask_" + map.uniqueID,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
            }
            mask.SetPixels32(field.ToPixels());
            mask.Apply(false);
            textureStale = false;
            nextTextureRebuild = Time.realtimeSinceStartup + TextureRebuildSeconds;
        }

        /// <summary>收集兩層的平台、浮船與支柱，模糊成 <see cref="PlatformShadowField"/>（只在水面層呼叫）。</summary>
        private void BuildField()
        {
            int w = map.Size.x;
            int h = map.Size.z;
            Map platformLevel = SeaLevels.PlatformLevelOf(map);
            bool[] raised = new bool[w * h];
            bool[] hull = new bool[w * h];
            bool[] pillar = new bool[w * h];
            int platformCells = 0;
            int floatingCells = 0;
            int cageCells = 0;
            // 每種地形的 HullPlatformPropsExtension（沒有的是 null）
            Dictionary<TerrainDef, HullPlatformPropsExtension> props = new Dictionary<TerrainDef, HullPlatformPropsExtension>();
            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    IntVec3 c = new IntVec3(x, 0, z);
                    TerrainDef t = map.terrainGrid.TerrainAt(c);
                    if (t != null && t != DepthRimDefOf.DepthRim_OpenSea)
                    {
                        if (!props.TryGetValue(t, out HullPlatformPropsExtension p))
                        {
                            p = props[t] = t.GetModExtension<HullPlatformPropsExtension>();
                        }
                        // 浮在水面的平台（船體平台）在 b；水面層上不浮的平台地形（舊存檔的海上平台）當成高出海面。
                        // 其他地形（橋、橋上的地板等）貼著水面，不是高出海面的甲板（2026-11-21 使用者回報：搭橋多出平台的影子與輪廓）
                        if (p != null && p.FloatsOnSea)
                        {
                            hull[i] = true;
                            floatingCells++;
                        }
                        else if (p != null)
                        {
                            raised[i] = true;
                        }
                    }
                    if (!raised[i] && platformLevel != null && c.InBounds(platformLevel))
                    {
                        TerrainDef above = platformLevel.terrainGrid.TerrainAt(c);
                        raised[i] = above != null && above != DepthRimDefOf.DepthRim_OpenAir;
                    }
                    if (raised[i])
                    {
                        platformCells++;
                    }
                }
            }
            // 箱網（Docs/箱網養殖_內容設計.md）：浮框那一圈貼著水面，水線白沫加在 g（見 PlatformShadowField.CageFoamMax）
            bool[] cage = new bool[w * h];
            MapComponent_Aquaculture aqua = MapComponent_Aquaculture.For(map);
            if (aqua != null)
            {
                foreach (Building_NetCage net in aqua.Cages)
                {
                    if (!net.Spawned)
                    {
                        continue;
                    }
                    foreach (IntVec3 c in net.OccupiedRect().EdgeCells)
                    {
                        if (c.InBounds(map) && !cage[c.z * w + c.x])
                        {
                            cage[c.z * w + c.x] = true;
                            cageCells++;
                        }
                    }
                }
            }
            // 支柱入水處（海面座標＝基座正下方），寬度取柱身下方混凝土基座的寬
            int pillarCells = 0;
            foreach ((IntVec3 pos, PlatformPillarExtension ext) in PillarsOf(map, platformLevel))
            {
                foreach (IntVec3 c in PlatformPillarGeometry.WaterlineCells(pos, ext.waterlineWidth))
                {
                    if (c.InBounds(map) && !pillar[c.z * w + c.x])
                    {
                        pillar[c.z * w + c.x] = true;
                        pillarCells++;
                    }
                }
            }
            lastPlatformCells = platformCells;
            lastFloatingCells = floatingCells;
            lastPillarCells = pillarCells;
            lastCageCells = cageCells;
            field = new PlatformShadowField(w, h, raised, pillar, hull, cage);
            fieldStale = false;
        }

        /// <summary>兩層所有支柱的位置（水面層的支柱腳與平台層的支柱頂座標相同，重複的只算一次）。</summary>
        private static IEnumerable<(IntVec3, PlatformPillarExtension)> PillarsOf(Map seaLevel, Map platformLevel)
        {
            HashSet<IntVec3> seen = new HashSet<IntVec3>();
            foreach (Map m in new[] { seaLevel, platformLevel })
            {
                if (m == null)
                {
                    continue;
                }
                foreach (Building_PillarFoot foot in Building_PillarFoot.AllOn(m))
                {
                    if (seen.Add(foot.Position))
                    {
                        yield return (foot.Position, foot.Ext);
                    }
                }
                foreach (Building_PlatformPillar top in m.GetComponent<MapComponent_PlatformSupport>()?.Bases ?? new List<Building_PlatformPillar>())
                {
                    if (top.Spawned && seen.Add(top.Position))
                    {
                        yield return (top.Position, top.Ext);
                    }
                }
            }
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
            base.MapRemoved();
            DestroyMask();
            if (material != null)
            {
                Object.Destroy(material);
                material = null;
            }
            if (shadowMaterial != null)
            {
                Object.Destroy(shadowMaterial);
                shadowMaterial = null;
            }
            foreach (Material mat in pillarMaterials.Values)
            {
                if (mat != null)
                {
                    Object.Destroy(mat);
                }
            }
            pillarMaterials.Clear();
        }
    }

    /// <summary>
    /// 海面背景層：只在開放海面格輸出四邊形（地形本身 dontRender）。著色器排在 1900，比地形（2000）早，
    /// 背景物件（支柱，1950）蓋在它上面，平台地形與側面再蓋在支柱上面。
    /// 影子與白沫由著色器依 <see cref="MapComponent_SeaSurface"/> 的遮罩畫，這裡只標記遮罩需要重建。
    /// </summary>
    public class SectionLayer_SeaSurface : SectionLayer
    {
        private static ShaderTypeDef shaderDef;
        private static bool shaderLookupFailed;

        public SectionLayer_SeaSurface(Section section) : base(section)
        {
            // 建築（支柱）出現或消失也要重建：遮罩裡有支柱佔地
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Terrain | (ulong)MapMeshFlagDefOf.Buildings;
        }

        public override bool Visible => true;

        /// <summary>給驗收測試：最近一次重建輸出了幾格海面。</summary>
        public int lastCells;

        public static ShaderTypeDef ShaderDef
        {
            get
            {
                if (shaderDef == null && !shaderLookupFailed)
                {
                    shaderDef = DefDatabase<ShaderTypeDef>.GetNamedSilentFail("DepthRim_SeaSurface");
                    if (shaderDef == null)
                    {
                        shaderLookupFailed = true;
                        Log.ErrorOnce("[DepthRim] ShaderTypeDef DepthRim_SeaSurface is missing; the sea surface will not be drawn.", 0x5EA5F2);
                    }
                }
                return shaderDef;
            }
        }

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            lastCells = 0;
            Map map = Map;
            bool any = false;
            foreach (IntVec3 c in section.CellRect)
            {
                if (SeaSurface.ShowsSea(c, map))
                {
                    any = true;
                    break;
                }
            }
            MapComponent_SeaSurface comp = map.GetComponent<MapComponent_SeaSurface>();
            if (!any || comp == null)
            {
                return;
            }
            comp.MarkDirty();
            Material mat = comp.Material;
            if (mat == null)
            {
                return;
            }
            LayerSubMesh sm = GetSubMesh(mat);
            float y = AltitudeLayer.BelowTerrain.AltitudeFor();
            foreach (IntVec3 c in section.CellRect)
            {
                if (!SeaSurface.ShowsSea(c, map))
                {
                    continue;
                }
                int start = sm.verts.Count;
                sm.verts.Add(new Vector3(c.x, y, c.z));
                sm.verts.Add(new Vector3(c.x, y, c.z + 1));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                sm.verts.Add(new Vector3(c.x + 1, y, c.z));
                sm.tris.Add(start);
                sm.tris.Add(start + 1);
                sm.tris.Add(start + 2);
                sm.tris.Add(start);
                sm.tris.Add(start + 2);
                sm.tris.Add(start + 3);
                lastCells++;
            }
            FinalizeMesh(MeshParts.All);
        }
    }

    /// <summary>
    /// 海面地圖與原版地表地圖上變成太空的格改回水。DMSE 的「拆除平台」與船體分離讀圖層的
    /// <c>TransferEnvironmentExtension.voidTerrain</c>，地表圖層沒有，於是會鋪太空。
    /// 有 <see cref="MapComponent_SeabedMemory"/> 記錄（平台蓋掉了什麼）就還原那個地形，否則用 <see cref="SeaLevels.VoidTerrain"/>：
    /// 水面層→開放海面、平台層→高空、原版海岸地圖→原版深海水（海灣離港後不留太空）。
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    public static class Patch_SeaSurfaceVoid
    {
        [HarmonyPrefix]
        public static void Prefix(IntVec3 c, Map ___map, out TerrainDef __state)
        {
            __state = ___map != null && c.InBounds(___map) ? ___map.terrainGrid.TerrainAt(c) : null;
        }

        [HarmonyPostfix]
        public static void Postfix(IntVec3 c, TerrainDef newTerr, Map ___map, TerrainDef __state)
        {
            if (newTerr != TerrainDefOf.Space || ___map == null || !SeaLevels.ReplacesSpace(___map))
            {
                return;
            }
            // 原版地表地圖：只處理船體平台變成的太空（拆除、船體分離）；其他來源的太空（別的模組、地圖生成）不碰
            if (!SeaLevels.IsAnyLevel(___map) && !MapComponent_SeabedMemory.IsPlatform(__state))
            {
                return;
            }
            MapComponent_SeabedMemory memory = ___map.GetComponent<MapComponent_SeabedMemory>();
            TerrainDef restore = memory != null && memory.TryTake(c, out TerrainDef under) ? under : SeaLevels.VoidTerrain(___map);
            ___map.terrainGrid.SetTerrain(c, restore);
        }
    }

    public static class DebugActions_SeaSurface
    {
        /// <summary>在點到的地表海洋格建立海面地點並切過去（還沒有正式的前往方式）。</summary>
        [DebugAction("DepthRim", "Create sea-surface map", actionType = DebugActionType.ToolWorld, allowedGameStates = AllowedGameStates.PlayingOnWorld)]
        public static void CreateSeaSurfaceMap()
        {
            PlanetTile tile = GenWorld.MouseTile();
            if (!tile.Valid || tile.LayerDef == DepthRimDefOf.DepthRim_Ocean || !tile.Tile.WaterCovered)
            {
                Messages.Message("DepthRim.SeaSurface.NeedOceanTile".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (Find.WorldObjects.AnyMapParentAt(tile))
            {
                Messages.Message("DepthRim.SeaSurface.TileOccupied".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            Map map = CreateAt(tile);
            Current.Game.CurrentMap = map;
            CameraJumper.TryJump(map.Center, map);
        }

        public static Map CreateAt(PlanetTile tile)
        {
            MapParent parent = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_SeaSurfaceSite);
            parent.Tile = tile;
            parent.SetFaction(Faction.OfPlayer);
            Find.WorldObjects.Add(parent);
            return GetOrGenerateMapUtility.GetOrGenerateMap(tile, Find.World.info.initialMapSize, DepthRimDefOf.DepthRim_SeaSurfaceSite);
        }
    }
}
