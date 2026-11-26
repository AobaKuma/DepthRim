using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 平台支柱基座的設定（掛在 ThingDef 上）。支柱頂是甲板上的 3×3 建築（支柱腳在水面層是 9×9）；柱身是往下畫的背景（<see cref="SectionLayer_PlatformPillars"/>）。
    /// </summary>
    public class PlatformPillarExtension : DefModExtension
    {
        /// <summary>柱身貼圖（畫在海面之上、甲板之下）。</summary>
        public string pillarTexPath;

        /// <summary>柱身大小（格）。貼圖 384×640，等比放到寬 11 格。</summary>
        public Vector2 pillarDrawSize = new Vector2(11f, 18.333f);

        /// <summary>
        /// 柱身中心相對基座中心的南北偏移（格）。-7.5：柱頂在基座中心上方 1.67 格，入水處在基座中心往南 14.1 格（<see cref="MapComponent_SeaSurface.SeaDropCells"/>）。
        /// 改這個值時 SeaDropCells 與著色器的 _Height 預設值要一起改（單元測試會比對）。
        /// </summary>
        public float pillarOffsetZ = -7.5f;

        /// <summary>支撐半徑（格，從基座中心算的圓）：範圍內才能鋪海上平台，範圍外的平台會崩塌。</summary>
        public float supportRadius = 18f;

        /// <summary>入水處白沫的寬度（格）：柱身下方混凝土基座的寬。</summary>
        public int waterlineWidth = 9;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (pillarTexPath.NullOrEmpty())
            {
                yield return "pillarTexPath is empty";
            }
            if (supportRadius <= 0f)
            {
                yield return "supportRadius must be positive";
            }
        }
    }

    /// <summary>柱身位置、入水處、支撐範圍：純函式，可單元測試。</summary>
    public static class PlatformPillarGeometry
    {
        /// <summary>柱身貼圖裡混凝土基座中段（入水處）在從上往下的幾成位置（貼圖 75%～97% 是混凝土）。</summary>
        public const float WaterlineFraction = 0.86f;

        /// <summary>柱身四邊形（世界座標，格）：x0, z0, x1, z1。</summary>
        public static Rect PillarRect(float centerX, float centerZ, Vector2 size, float offsetZ)
        {
            return new Rect(centerX - size.x / 2f, centerZ + offsetZ - size.y / 2f, size.x, size.y);
        }

        /// <summary>
        /// 入水處離基座中心往南幾格：海面在甲板下方這麼多格（<see cref="MapComponent_SeaSurface.SeaDropCells"/> 要等於這個值）。
        /// </summary>
        public static float SeaDrop(float offsetZ, float height)
        {
            float top = offsetZ + height / 2f;
            return -(top - height * WaterlineFraction);
        }

        /// <summary>入水處在海面座標裡的格子：基座正下方，寬 <paramref name="width"/>、兩排。</summary>
        public static IEnumerable<IntVec3> WaterlineCells(IntVec3 basePos, int width)
        {
            int minX = basePos.x - width / 2;
            for (int z = basePos.z - 1; z <= basePos.z; z++)
            {
                for (int x = minX; x < minX + width; x++)
                {
                    yield return new IntVec3(x, 0, z);
                }
            }
        }

        /// <summary>
        /// 支柱腳的升降點候選：佔地（9×9）四邊外側緊貼的格子（不含四角），正南中間優先，其次離它越近越先。
        /// 只要其中一格在浮船上，支柱就能蓋（玩家不用對準某一邊）。
        /// </summary>
        public static List<IntVec3> LandingCandidates(IntVec3 center, IntVec2 size)
        {
            CellRect rect = GenAdj.OccupiedRect(center, Rot4.North, size);
            IntVec3 preferred = new IntVec3(center.x, 0, rect.minZ - 1);
            List<IntVec3> cells = new List<IntVec3>();
            for (int x = rect.minX; x <= rect.maxX; x++)
            {
                cells.Add(new IntVec3(x, 0, rect.minZ - 1));
                cells.Add(new IntVec3(x, 0, rect.maxZ + 1));
            }
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                cells.Add(new IntVec3(rect.minX - 1, 0, z));
                cells.Add(new IntVec3(rect.maxX + 1, 0, z));
            }
            return cells.OrderBy(c => (c - preferred).LengthHorizontalSquared).ThenBy(c => c.z).ThenBy(c => c.x).ToList();
        }

        /// <summary>崩塌水花分塊的邊長（格）。</summary>
        public const int SplashChunk = 5;

        /// <summary>
        /// 把塌掉的格子分成 <paramref name="chunk"/>×<paramref name="chunk"/> 的塊：每塊回傳格子中心的平均（世界座標 XZ）與格數。
        /// </summary>
        public static List<(Vector2 center, int count)> SplashGroups(IEnumerable<IntVec3> cells, int chunk)
        {
            Dictionary<(int, int), (Vector2 sum, int count)> groups = new Dictionary<(int, int), (Vector2, int)>();
            foreach (IntVec3 c in cells)
            {
                (int, int) key = (Mathf.FloorToInt(c.x / (float)chunk), Mathf.FloorToInt(c.z / (float)chunk));
                groups.TryGetValue(key, out (Vector2 sum, int count) g);
                groups[key] = (g.sum + new Vector2(c.x + 0.5f, c.z + 0.5f), g.count + 1);
            }
            return groups.Values.Select(g => (g.sum / g.count, g.count)).ToList();
        }

        /// <summary>一塊的水花放大倍率：√格數 / 2，介於 1（零星幾格）與 2.5（整塊 5×5）之間。</summary>
        public static float SplashScale(int count)
        {
            return Mathf.Clamp(Mathf.Sqrt(count) / 2f, 1f, 2.5f);
        }

        /// <summary>格子中心是否在基座的圓形支撐範圍內。</summary>
        public static bool Supports(Vector3 baseCenter, IntVec3 cell, float radius)
        {
            float dx = cell.x + 0.5f - baseCenter.x;
            float dz = cell.z + 0.5f - baseCenter.z;
            return dx * dx + dz * dz <= radius * radius;
        }

        /// <summary>
        /// 把柱身四邊形裁進一個區塊（世界座標 [x0,x1)×[z0,z1)），回傳裁過的四邊形與對應的貼圖範圍；沒有交集時回 false。
        /// 長柱身會跨好幾個區塊，每個區塊只畫自己那一段，鏡頭只看到下半截時柱子也不會消失。
        /// </summary>
        public static bool Clip(Rect pillar, Rect section, out Rect quad, out Rect uv)
        {
            float x0 = Mathf.Max(pillar.xMin, section.xMin);
            float x1 = Mathf.Min(pillar.xMax, section.xMax);
            float z0 = Mathf.Max(pillar.yMin, section.yMin);
            float z1 = Mathf.Min(pillar.yMax, section.yMax);
            if (x1 <= x0 || z1 <= z0)
            {
                quad = default;
                uv = default;
                return false;
            }
            quad = Rect.MinMaxRect(x0, z0, x1, z1);
            uv = Rect.MinMaxRect((x0 - pillar.xMin) / pillar.width, (z0 - pillar.yMin) / pillar.height,
                (x1 - pillar.xMin) / pillar.width, (z1 - pillar.yMin) / pillar.height);
            return true;
        }
    }

    /// <summary>
    /// 支柱頂（平台層）：甲板上的 3×3 建築，往下畫一根柱身，並支撐半徑內的海上平台。
    /// 由水面層的支柱腳（<see cref="Building_PillarFoot"/>）自動生成，同時是升降梯的上端（<see cref="PocketMapExit"/>）。
    /// 生成與移除時通知 <see cref="MapComponent_PlatformSupport"/>，並把柱身蓋到的區塊標記重建。
    /// </summary>
    public class Building_PlatformPillar : PocketMapExit
    {
        public override string EnterString => "DepthRim.Pillar.GoDown".Translate();

        public override string CancelEnterString => "DepthRim.Pillar.CancelGoDown".Translate();

        /// <summary>下到水面層時落在支柱腳的升降點（旁邊的浮船），不是支柱腳本身（開放海面上站不住）。</summary>
        public override IntVec3 GetDestinationLocation()
        {
            if (entrance is Building_PillarFoot foot && foot.Spawned)
            {
                IntVec3 landing = foot.LandingCell;
                if (landing.IsValid)
                {
                    return landing;
                }
            }
            return base.GetDestinationLocation();
        }

        public override bool IsEnterable(out string reason)
        {
            if (!base.IsEnterable(out reason))
            {
                return false;
            }
            if (entrance is Building_PillarFoot foot && foot.Spawned && !foot.LandingStandable)
            {
                reason = "DepthRim.Pillar.NeedsLanding".Translate();
                return false;
            }
            return true;
        }

        public PlatformPillarExtension Ext => def.GetModExtension<PlatformPillarExtension>();

        public Rect PillarRect
        {
            get
            {
                PlatformPillarExtension ext = Ext;
                Vector3 c = this.TrueCenter();
                return PlatformPillarGeometry.PillarRect(c.x, c.z, ext.pillarDrawSize, ext.pillarOffsetZ);
            }
        }

        public bool Supports(IntVec3 cell)
        {
            return PlatformPillarGeometry.Supports(this.TrueCenter(), cell, Ext.supportRadius);
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            map.GetComponent<MapComponent_PlatformSupport>()?.Notify_BaseSpawned(this);
            DirtyPillar(map);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            Rect rect = PillarRect;
            IntVec3 pos = Position;
            base.DeSpawn(mode);
            DirtyPillar(map, rect);
            map.GetComponent<MapComponent_PlatformSupport>()?.Notify_BaseRemoved(this, pos);
        }

        private void DirtyPillar(Map map)
        {
            DirtyPillar(map, PillarRect);
        }

        /// <summary>柱身蓋到的每個區塊都要重建（基座所在的區塊以外的部分）。</summary>
        private static void DirtyPillar(Map map, Rect rect)
        {
            for (int x = Mathf.FloorToInt(rect.xMin); x < Mathf.CeilToInt(rect.xMax); x++)
            {
                for (int z = Mathf.FloorToInt(rect.yMin); z < Mathf.CeilToInt(rect.yMax); z++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (c.InBounds(map))
                    {
                        map.mapDrawer.MapMeshDirty(c, (ulong)MapMeshFlagDefOf.Buildings);
                    }
                }
            }
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            GenDraw.DrawRadiusRing(Position, Ext.supportRadius);
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string line = "DepthRim.Pillar.SupportRadius".Translate(Ext.supportRadius.ToString("0"));
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }
    }

    /// <summary>
    /// 支柱腳（水面層）：玩家在開放海面上蓋的平台支柱。蓋好時：
    /// <list type="number">
    /// <item>這片海還沒有平台層就建立一張（<see cref="SeaLevels.GetOrGeneratePlatformLevel"/>）；</item>
    /// <item>在平台層同一位置開出 <see cref="StartPlatformSize"/>×<see cref="StartPlatformSize"/> 的海上平台，中間放支柱頂（<see cref="Building_PlatformPillar"/>）。</item>
    /// </list>
    /// 支柱本身是升降梯（原版 <see cref="MapPortal"/> 的進出流程）：腳是下端、頂是上端。拆掉支柱腳時支柱頂一起消失，
    /// 平台層範圍外的平台照原本的規則崩塌。水面層看到的是從入水處往上伸的柱身（<see cref="SectionLayer_PlatformPillars"/>）。
    /// </summary>
    public class Building_PillarFoot : MapPortal
    {
        /// <summary>支柱蓋好時在平台層開出的初始平台邊長。</summary>
        public const int StartPlatformSize = 5;

        public PlatformPillarExtension Ext => def.GetModExtension<PlatformPillarExtension>();

        public Building_PlatformPillar Top => exit as Building_PlatformPillar;

        public override string EnterString => "DepthRim.Pillar.GoUp".Translate();

        public override string CancelEnterString => "DepthRim.Pillar.CancelGoUp".Translate();

        /// <summary>
        /// 升降點：緊貼支柱腳四邊、能站人的浮船格（<see cref="PlatformPillarGeometry.LandingCandidates"/> 的順序，正南中間優先）。
        /// 上下都從這一格（<see cref="Patch_PillarFootPathToInteractionCell"/>）；沒有就是 <see cref="IntVec3.Invalid"/>。
        /// </summary>
        public IntVec3 LandingCell => Spawned ? FindLanding(Position, def.size, Map) : IntVec3.Invalid;

        public bool LandingStandable => LandingCell.IsValid;

        public static IntVec3 FindLanding(IntVec3 center, IntVec2 size, Map sea)
        {
            foreach (IntVec3 c in PlatformPillarGeometry.LandingCandidates(center, size))
            {
                if (c.InBounds(sea) && !SeaSurface.IsOpenSea(c, sea) && c.Standable(sea))
                {
                    return c;
                }
            }
            return IntVec3.Invalid;
        }

        public override bool IsEnterable(out string reason)
        {
            if (!base.IsEnterable(out reason))
            {
                return false;
            }
            if (!LandingStandable)
            {
                reason = "DepthRim.Pillar.NeedsLanding".Translate();
                return false;
            }
            return true;
        }

        /// <summary>水面層看到的柱身：和平台層同一張貼圖，入水處在支柱腳中心，所以整根往北移 <see cref="MapComponent_SeaSurface.SeaDropCells"/> 格。</summary>
        public Rect BodyRect
        {
            get
            {
                PlatformPillarExtension ext = Ext;
                Vector3 c = this.TrueCenter();
                return PlatformPillarGeometry.PillarRect(c.x, c.z + MapComponent_SeaSurface.SeaDropCells, ext.pillarDrawSize, ext.pillarOffsetZ);
            }
        }

        public static IEnumerable<Building_PillarFoot> AllOn(Map map)
        {
            if (map == null)
            {
                yield break;
            }
            foreach (Thing t in map.listerThings.ThingsOfDef(DepthRimDefOf.DepthRim_PlatformPillar))
            {
                if (t is Building_PillarFoot foot && foot.Spawned)
                {
                    yield return foot;
                }
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (!respawningAfterLoad && SeaLevels.IsSeaLevel(map))
            {
                // 立刻建立（或接上）平台層與支柱頂；GetOtherMap 會在 currentlyGeneratingPortal 期間呼叫 GeneratePocketMapInt
                GetOtherMap();
            }
            DirtyBody(map, BodyRect);
            map.GetComponent<MapComponent_SeaSurface>()?.MarkDirty();
        }

        protected override Map GeneratePocketMapInt()
        {
            Map platform = SeaLevels.GetOrGeneratePlatformLevel(Map);
            if (platform != null)
            {
                SpawnTop(platform);
            }
            return platform;
        }

        /// <summary>在平台層開出初始平台並放支柱頂（已經是平台的格子不動，上面的東西保留）。</summary>
        private void SpawnTop(Map platform)
        {
            CellRect deck = CellRect.CenteredOn(Position, StartPlatformSize / 2).ClipInsideMap(platform);
            foreach (IntVec3 c in deck)
            {
                if (platform.terrainGrid.TerrainAt(c) == DepthRimDefOf.DepthRim_OpenAir)
                {
                    platform.terrainGrid.SetTerrain(c, DepthRimDefOf.DepthRim_OceanPlatform);
                }
            }
            Thing top = ThingMaker.MakeThing(DepthRimDefOf.DepthRim_PlatformPillarTop);
            if (Faction != null)
            {
                top.SetFaction(Faction);
            }
            GenSpawn.Spawn(top, Position, platform, Rot4.North, WipeMode.VanishOrMoveAside);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            Rect rect = BodyRect;
            Thing top = exit;
            base.DeSpawn(mode);
            DirtyBody(map, rect);
            map.GetComponent<MapComponent_SeaSurface>()?.MarkDirty();
            if (top != null && top.Spawned && mode != DestroyMode.WillReplace)
            {
                top.Destroy(DestroyMode.Vanish);
            }
        }

        public static void DirtyBody(Map map, Rect rect)
        {
            for (int x = Mathf.FloorToInt(rect.xMin); x < Mathf.CeilToInt(rect.xMax); x++)
            {
                for (int z = Mathf.FloorToInt(rect.yMin); z < Mathf.CeilToInt(rect.yMax); z++)
                {
                    IntVec3 c = new IntVec3(x, 0, z);
                    if (c.InBounds(map))
                    {
                        map.mapDrawer.MapMeshDirty(c, (ulong)MapMeshFlagDefOf.Buildings);
                    }
                }
            }
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            GenDraw.DrawRadiusRing(Position, Ext.supportRadius);
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string line = "DepthRim.Pillar.SupportRadius".Translate(Ext.supportRadius.ToString("0"));
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }
    }

    /// <summary>基座放置時畫出支撐範圍。</summary>
    public class PlaceWorker_PillarRange : PlaceWorker
    {
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            PlatformPillarExtension ext = def.GetModExtension<PlatformPillarExtension>();
            if (ext != null)
            {
                GenDraw.DrawRadiusRing(center, ext.supportRadius);
            }
        }
    }

    /// <summary>
    /// 新支柱的上下檢查（水面層放置時）：
    /// <list type="bullet">
    /// <item>下：支柱腳（9×9）四邊外側至少有一格是浮船（能走的地形，或已規劃的地形藍圖／框架），那一格就是升降點，否則 Pawn 上下不了。</item>
    /// <item>上：平台層上支柱頂（3×3，比支柱腳小）要蓋的位置不能有建築、藍圖或框架（原本的東西會被清掉）。</item>
    /// </list>
    /// 放置時把可以當升降點的格子框出來。
    /// </summary>
    public class PlaceWorker_PillarFoot : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            if (!(checkingDef is ThingDef def) || !SeaLevels.IsSeaLevel(map))
            {
                return AcceptanceReport.WasAccepted;
            }
            AcceptanceReport down = LandingAllowed(def, loc, rot, map);
            if (!down.Accepted)
            {
                return down;
            }
            return AboveClear(def, loc, rot, SeaLevels.PlatformLevelOf(map));
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map map = Find.CurrentMap;
            if (map == null || !SeaLevels.IsSeaLevel(map))
            {
                return;
            }
            List<IntVec3> spots = PlatformPillarGeometry.LandingCandidates(center, def.size).Where(c => c.InBounds(map) && IsLandingSpot(c, map)).ToList();
            if (spots.Count > 0)
            {
                GenDraw.DrawFieldEdges(spots, Color.green);
            }
        }

        /// <summary>這一格能當升降點：浮船（不是開放海面、能走），或那一格已經規劃了地形（藍圖／框架）。</summary>
        public static bool IsLandingSpot(IntVec3 c, Map sea)
        {
            if (!SeaSurface.IsOpenSea(c, sea) && c.Walkable(sea))
            {
                return true;
            }
            foreach (Thing t in c.GetThingList(sea))
            {
                if ((t is Blueprint || t is Frame) && t.def.entityDefToBuild is TerrainDef)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>下：支柱腳四邊外側至少一格是浮船（或那一格已經規劃了地形）。</summary>
        public static AcceptanceReport LandingAllowed(ThingDef def, IntVec3 loc, Rot4 rot, Map sea)
        {
            foreach (IntVec3 c in PlatformPillarGeometry.LandingCandidates(loc, def.size))
            {
                if (c.InBounds(sea) && IsLandingSpot(c, sea))
                {
                    return AcceptanceReport.WasAccepted;
                }
            }
            return new AcceptanceReport("DepthRim.Pillar.NeedsLanding".Translate());
        }

        /// <summary>上：平台層上支柱頂要蓋的格子沒有建築、藍圖或框架（平台層還沒建立時一定可以）。只看支柱頂的大小，不是支柱腳的。</summary>
        public static AcceptanceReport AboveClear(ThingDef def, IntVec3 loc, Rot4 rot, Map platform)
        {
            if (platform == null)
            {
                return AcceptanceReport.WasAccepted;
            }
            ThingDef topDef = def.portal?.exitDef ?? def;
            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, Rot4.North, topDef.size))
            {
                if (!c.InBounds(platform))
                {
                    continue;
                }
                foreach (Thing t in c.GetThingList(platform))
                {
                    if (t.def.category == ThingCategory.Building || t is Blueprint || t is Frame)
                    {
                        return new AcceptanceReport("DepthRim.Pillar.BlockedAbove".Translate(t.LabelShort));
                    }
                }
            }
            return AcceptanceReport.WasAccepted;
        }
    }

    /// <summary>
    /// 原版進出升降梯（<c>EnterPortal</c>、<c>HaulToPortal</c>）走到門「旁邊任一格」（Touch）；支柱腳改成一定走到升降點（<see cref="Building_PillarFoot.LandingCell"/>）。
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(Verse.AI.Pawn_PathFollower), nameof(Verse.AI.Pawn_PathFollower.StartPath))]
    public static class Patch_PillarFootPathToInteractionCell
    {
        [HarmonyLib.HarmonyPrefix]
        public static void Prefix(ref LocalTargetInfo dest, ref Verse.AI.PathEndMode peMode)
        {
            if (dest.Thing is Building_PillarFoot foot && foot.Spawned
                && (peMode == Verse.AI.PathEndMode.Touch || peMode == Verse.AI.PathEndMode.ClosestTouch || peMode == Verse.AI.PathEndMode.InteractionCell))
            {
                IntVec3 landing = foot.LandingCell;
                if (landing.IsValid)
                {
                    dest = landing;
                    peMode = Verse.AI.PathEndMode.OnCell;
                }
            }
        }
    }

    /// <summary>海上平台只能鋪在某座已完工基座的支撐範圍內；放置時畫出所有基座的範圍。</summary>
    public class PlaceWorker_PlatformSupported : PlaceWorker
    {
        /// <summary>建築分頁只在平台層（海上平台地圖）顯示海上平台；開發者模式（god mode）照原版一律顯示。</summary>
        public override bool IsBuildDesignatorVisible(BuildableDef def)
        {
            return SeaLevels.IsPlatformLevel(Find.CurrentMap);
        }

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            // 放置只看支撐半徑：一次拖拉的整片藍圖彼此還沒連上，連通與否等蓋好後由崩塌檢查決定
            MapComponent_PlatformSupport support = map.GetComponent<MapComponent_PlatformSupport>();
            if (support != null && support.InRange(loc))
            {
                return AcceptanceReport.WasAccepted;
            }
            return new AcceptanceReport("DepthRim.Platform.NeedsSupport".Translate());
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map map = Find.CurrentMap;
            MapComponent_PlatformSupport support = map?.GetComponent<MapComponent_PlatformSupport>();
            if (support == null)
            {
                return;
            }
            foreach (Building_PlatformPillar b in support.Bases)
            {
                GenDraw.DrawRadiusRing(b.Position, b.Ext.supportRadius);
            }
        }
    }

    /// <summary>
    /// 平台支撐：一格海上平台要「在某根支柱的支撐半徑內」而且「以共邊相連的方式連到某根支柱頂底下的平台」才撐得住。
    /// 連通用 DMSE 結構樁網路判定船體範圍的方式（<see cref="Fortified.GridTopology"/> 增量維護共邊連通塊，含支柱頂佔地格的塊才算數）：
    /// 平台層的地形一變（<see cref="Patch_PlatformStructureChanged"/>），下一次檢查就把斷開的碎塊與範圍外的平台一起排進崩塌倒數。
    /// 基座消失後，任何撐不住的海上平台進入崩塌倒數（紅框標出），
    /// 倒數結束就塌進海裡：上面的建築毀掉、物品沉下去、Pawn 摔回最近的平台並受傷（附近沒有平台就落海失蹤），
    /// 在海面上的落點濺起原版「從水中冒出」的大水花。倒數中新蓋的基座若撐得到，就取消崩塌。
    /// </summary>
    public class MapComponent_PlatformSupport : MapComponent
    {
        /// <summary>失去支撐到崩塌的時間（15 秒）。</summary>
        public const int CollapseDelayTicks = 900;

        /// <summary>
        /// 一次崩塌最多濺幾個水花。塌掉的格子先分成 <see cref="PlatformPillarGeometry.SplashChunk"/>×<see cref="PlatformPillarGeometry.SplashChunk"/> 的塊，
        /// 每塊一個放大的水花（<see cref="PlatformPillarGeometry.SplashScale"/>），而不是每格一個小水花。
        /// </summary>
        public const int MaxSplashes = 12;

        /// <summary>基座被拆、柱子倒進海裡的水花大小。</summary>
        public const float PillarSplashScale = 2.5f;

        /// <summary>從平台層掉下去時，在水面層找落點的半徑（格）。</summary>
        public const float SeaLandingRadius = 6f;

        private readonly List<Building_PlatformPillar> bases = new List<Building_PlatformPillar>();

        private Dictionary<IntVec3, int> pending = new Dictionary<IntVec3, int>();

        /// <summary>平台格（基底地形是海上平台）的共邊連通塊；第一次用到時從整張地圖建立，之後只餵地形變動。不存檔。</summary>
        private Fortified.GridTopology topology;

        private readonly HashSet<IntVec3> dirtyCells = new HashSet<IntVec3>();

        private readonly List<int> topoAdds = new List<int>();

        private readonly List<int> topoRemoves = new List<int>();

        /// <summary>含支柱頂佔地格的連通塊（連通塊代表只在兩次修改之間可比較，所以拓撲或基座一變就重算）。</summary>
        private readonly HashSet<int> anchorComponents = new HashSet<int>();

        private bool anchorsDirty = true;

        /// <summary>平台結構變了（地形變動），下一次檢查時重新判定支撐。</summary>
        private bool structureDirty;

        /// <summary>結構變動後多久檢查一次（tick）：同一批施工或崩塌的變動合併成一次檢查。</summary>
        public const int StructureCheckInterval = 10;

        private List<IntVec3> tmpKeys;
        private List<int> tmpValues;

        /// <summary>給驗收測試：最近一次崩塌塌掉幾格、濺了幾個水花、有幾個 Pawn 掉下去。</summary>
        public int lastCollapsedCells;
        public int lastSplashes;
        public int lastPawnsFell;

        public MapComponent_PlatformSupport(Map map) : base(map)
        {
        }

        public List<Building_PlatformPillar> Bases => bases;

        public int PendingCount => pending.Count;

        public bool IsPending(IntVec3 c)
        {
            return pending.ContainsKey(c);
        }

        /// <summary>這格在某根支柱的支撐半徑內（放置海上平台只看這個）。</summary>
        public bool InRange(IntVec3 c)
        {
            for (int i = 0; i < bases.Count; i++)
            {
                if (bases[i].Spawned && bases[i].Supports(c))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>這格的平台以共邊相連的方式連到某根支柱頂底下的平台（不是平台的格子一律 false）。</summary>
        public bool IsConnected(IntVec3 c)
        {
            SyncTopology();
            if (anchorsDirty)
            {
                RebuildAnchors();
            }
            int comp = topology.ComponentOf(c.x, c.z);
            return comp != 0 && anchorComponents.Contains(comp);
        }

        /// <summary>撐得住：在支撐半徑內，而且連到某根支柱。</summary>
        public bool IsSupported(IntVec3 c)
        {
            return InRange(c) && IsConnected(c);
        }

        private bool IsDeck(IntVec3 c)
        {
            return map.terrainGrid.BaseTerrainAt(c) == DepthRimDefOf.DepthRim_OceanPlatform;
        }

        /// <summary>平台層的地形變了（<see cref="Patch_PlatformStructureChanged"/>）：記下這格，下一次檢查時更新連通塊。</summary>
        public void Notify_TerrainChanged(IntVec3 c)
        {
            if (topology == null)
            {
                // 還沒建立：第一次用到時直接從目前的地形建立
                return;
            }
            dirtyCells.Add(c);
            structureDirty = true;
        }

        private void SyncTopology()
        {
            if (topology == null)
            {
                topology = new Fortified.GridTopology(map.Size.x, map.Size.z);
                topoAdds.Clear();
                foreach (IntVec3 c in map.AllCells)
                {
                    if (IsDeck(c))
                    {
                        topoAdds.Add(topology.Index(c.x, c.z));
                    }
                }
                if (topoAdds.Count > 0)
                {
                    topology.AddBatch(topoAdds);
                }
                dirtyCells.Clear();
                anchorsDirty = true;
                return;
            }
            if (dirtyCells.Count == 0)
            {
                return;
            }
            topoAdds.Clear();
            topoRemoves.Clear();
            foreach (IntVec3 c in dirtyCells)
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                bool want = IsDeck(c);
                if (want != topology.Present(c.x, c.z))
                {
                    (want ? topoAdds : topoRemoves).Add(topology.Index(c.x, c.z));
                }
            }
            dirtyCells.Clear();
            if (topoRemoves.Count > 0)
            {
                topology.RemoveBatch(topoRemoves);
            }
            if (topoAdds.Count > 0)
            {
                topology.AddBatch(topoAdds);
            }
            if (topoAdds.Count > 0 || topoRemoves.Count > 0)
            {
                anchorsDirty = true;
            }
        }

        private void RebuildAnchors()
        {
            anchorComponents.Clear();
            foreach (Building_PlatformPillar b in bases)
            {
                if (!b.Spawned)
                {
                    continue;
                }
                foreach (IntVec3 c in b.OccupiedRect())
                {
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    int comp = topology.ComponentOf(c.x, c.z);
                    if (comp != 0)
                    {
                        anchorComponents.Add(comp);
                    }
                }
            }
            anchorsDirty = false;
        }

        /// <summary>給驗收測試：連通塊數（平台被切成幾塊）。</summary>
        public int DeckPieces
        {
            get
            {
                SyncTopology();
                return topology.ComponentCount;
            }
        }

        public void Notify_BaseSpawned(Building_PlatformPillar b)
        {
            if (!bases.Contains(b))
            {
                bases.Add(b);
            }
            anchorsDirty = true;
            // 倒數中的格子被新基座撐住：取消崩塌
            ReleaseSupported();
        }

        /// <summary>倒數中、現在又撐得住的格子（新支柱、補上斷掉的平台）：取消崩塌。</summary>
        private int ReleaseSupported()
        {
            if (pending.Count == 0)
            {
                return 0;
            }
            int released = 0;
            foreach (IntVec3 c in pending.Keys.ToList())
            {
                if (IsSupported(c))
                {
                    pending.Remove(c);
                    released++;
                }
            }
            return released;
        }

        public void Notify_BaseRemoved(Building_PlatformPillar b, IntVec3 pos)
        {
            bases.Remove(b);
            anchorsDirty = true;
            // 基座沒了，柱子倒進海裡
            Splash(pos, PillarSplashScale);
            CheckSupport();
        }

        /// <summary>找出撐不住的海上平台（範圍外或和支柱斷開），開始崩塌倒數；又撐得住的取消倒數。回傳新加入倒數的格數。</summary>
        public int CheckSupport()
        {
            structureDirty = false;
            ReleaseSupported();
            TerrainDef deck = DepthRimDefOf.DepthRim_OceanPlatform;
            int due = Find.TickManager.TicksGame + CollapseDelayTicks;
            List<IntVec3> added = new List<IntVec3>();
            foreach (IntVec3 c in map.AllCells)
            {
                if (map.terrainGrid.BaseTerrainAt(c) != deck || pending.ContainsKey(c) || IsSupported(c))
                {
                    continue;
                }
                pending[c] = due;
                added.Add(c);
            }
            if (added.Count > 0)
            {
                Messages.Message("DepthRim.Platform.Unsupported".Translate(added.Count, (CollapseDelayTicks / 60).ToString()),
                    new LookTargets(new TargetInfo(added[added.Count / 2], map)), MessageTypeDefOf.ThreatBig);
            }
            return added.Count;
        }

        public override void MapComponentTick()
        {
            if (structureDirty && Find.TickManager.TicksGame % StructureCheckInterval == 0)
            {
                CheckSupport();
            }
            if (pending.Count == 0 || Find.TickManager.TicksGame % 30 != 0)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            List<IntVec3> dueCells = null;
            foreach (KeyValuePair<IntVec3, int> kv in pending)
            {
                if (kv.Value <= now)
                {
                    dueCells ??= new List<IntVec3>();
                    dueCells.Add(kv.Key);
                }
            }
            if (dueCells != null)
            {
                Collapse(dueCells);
            }
        }

        public override void MapComponentUpdate()
        {
            if (pending.Count > 0 && Find.CurrentMap == map)
            {
                GenDraw.DrawFieldEdges(pending.Keys.ToList(), Color.red);
            }
        }

        /// <summary>這些格子塌進海裡（不論還有沒有支撐；正常流程只會收到倒數到期的格子）。</summary>
        public void Collapse(List<IntVec3> cells)
        {
            HashSet<IntVec3> falling = new HashSet<IntVec3>(cells);
            int pawnsFell = 0;
            List<Thing> tmp = new List<Thing>();
            foreach (IntVec3 c in cells)
            {
                pending.Remove(c);
                tmp.Clear();
                tmp.AddRange(c.GetThingList(map));
                foreach (Thing t in tmp)
                {
                    if (t.Destroyed || !t.Spawned)
                    {
                        continue;
                    }
                    if (t is Pawn pawn)
                    {
                        Fall(pawn, falling);
                        pawnsFell++;
                    }
                    else if (t.def.category == ThingCategory.Building || t is Blueprint || t is Frame)
                    {
                        t.Destroy(DestroyMode.Vanish);
                    }
                }
            }
            foreach (IntVec3 c in cells)
            {
                // 換成這一層的「空」（平台層是高空）：上面的物品、植物、碎屑由 Patch_AbyssSwallowsItems 處理
                map.terrainGrid.SetTerrain(c, SeaLevels.VoidTerrain(map));
            }
            // 一塊一個放大的水花：大面積崩塌時畫的水花少，但每個都大；最大的幾塊優先
            int splashes = 0;
            foreach ((Vector2 center, int count) in PlatformPillarGeometry.SplashGroups(cells, PlatformPillarGeometry.SplashChunk)
                         .OrderByDescending(g => g.count).Take(MaxSplashes))
            {
                if (Splash(center, PlatformPillarGeometry.SplashScale(count)))
                {
                    splashes++;
                }
            }
            lastCollapsedCells = cells.Count;
            lastSplashes = splashes;
            lastPawnsFell = pawnsFell;
            Messages.Message("DepthRim.Platform.Collapsed".Translate(cells.Count), new LookTargets(new TargetInfo(cells[0], map)), MessageTypeDefOf.NegativeEvent);
        }

        /// <summary>
        /// Pawn 隨平台掉下去：平台層下面是水面層，附近有能站的地方（浮船、支柱腳）就掉到那裡並受傷；
        /// 否則爬回最近還撐得住的平台並受傷；都沒有就落海失蹤。
        /// </summary>
        private void Fall(Pawn pawn, HashSet<IntVec3> falling)
        {
            IntVec3 from = pawn.Position;
            Map sea = SeaLevels.SeaLevelOf(map);
            if (sea != null)
            {
                IntVec3 landing = IntVec3.Invalid;
                for (int i = 0; i < GenRadial.NumCellsInRadius(SeaLandingRadius); i++)
                {
                    IntVec3 c = from + GenRadial.RadialPattern[i];
                    if (c.InBounds(sea) && c.Standable(sea))
                    {
                        landing = c;
                        break;
                    }
                }
                if (landing.IsValid)
                {
                    Splash(from);
                    pawn.pather?.StopDead();
                    pawn.DeSpawn(DestroyMode.Vanish);
                    GenSpawn.Spawn(pawn, landing, sea);
                    pawn.TakeDamage(new DamageInfo(DamageDefOf.Blunt, Rand.Range(12f, 22f)));
                    Messages.Message("DepthRim.Platform.PawnFellToSea".Translate(pawn.LabelShort, pawn), new LookTargets(pawn), MessageTypeDefOf.NegativeHealthEvent);
                    return;
                }
            }
            IntVec3 safe = IntVec3.Invalid;
            for (int i = 0; i < GenRadial.NumCellsInRadius(20f); i++)
            {
                IntVec3 c = from + GenRadial.RadialPattern[i];
                if (c.InBounds(map) && !falling.Contains(c) && !pending.ContainsKey(c) && c.Standable(map))
                {
                    safe = c;
                    break;
                }
            }
            Splash(from);
            if (!safe.IsValid)
            {
                Messages.Message("DepthRim.Platform.PawnLost".Translate(pawn.LabelShort, pawn), new LookTargets(new TargetInfo(from, map)), MessageTypeDefOf.PawnDeath);
                pawn.Kill(null);
                if (pawn.Corpse != null && !pawn.Corpse.Destroyed)
                {
                    pawn.Corpse.Destroy();
                }
                return;
            }
            pawn.pather?.StopDead();
            pawn.Position = safe;
            pawn.Notify_Teleported(true, true);
            pawn.TakeDamage(new DamageInfo(DamageDefOf.Blunt, Rand.Range(8f, 16f)));
            Messages.Message("DepthRim.Platform.PawnFell".Translate(pawn.LabelShort, pawn), new LookTargets(pawn), MessageTypeDefOf.NegativeHealthEvent);
        }

        public bool Splash(IntVec3 deckCell, float scale = 1f)
        {
            return Splash(new Vector2(deckCell.x + 0.5f, deckCell.z + 0.5f), scale);
        }

        /// <summary>
        /// 在甲板上的點 <paramref name="deckPoint"/>（世界座標 XZ）正下方的海面濺水花：海面在甲板下方，畫面上往南偏
        /// <see cref="MapComponent_SeaSurface.SeaDropCells"/> 格。<paramref name="scale"/> 放大整個效果（原版 Effecter.scale）。
        /// 落點被別的平台蓋住（或在地圖外）就不畫。
        /// </summary>
        public bool Splash(Vector2 deckPoint, float scale)
        {
            Vector2 point = deckPoint - new Vector2(0f, SeaLevels.ViewDrop(map));
            IntVec3 spot = new IntVec3(Mathf.FloorToInt(point.x), 0, Mathf.FloorToInt(point.y));
            if (!SeaSurface.ShowsSea(spot, map))
            {
                return false;
            }
            // 原版「從水中冒出」的大水花（水下襲擊用的那個）。原版 Def，不放進 DepthRimDefOf（那裡只放本模組 XML 定義的 Def）
            EffecterDef def = DefDatabase<EffecterDef>.GetNamedSilentFail("PawnEmergeFromWaterLarge");
            if (def == null)
            {
                return false;
            }
            // 格子中心之外的小數部分用 offset 補上，水花落在塊的中心
            Vector3 offset = new Vector3(point.x - (spot.x + 0.5f), 0f, point.y - (spot.z + 0.5f));
            Effecter effecter = def.Spawn(spot, map, offset, scale);
            map.effecterMaintainer.AddEffecterToMaintain(effecter, spot, 60);
            return true;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref pending, "pendingCollapse", LookMode.Value, LookMode.Value, ref tmpKeys, ref tmpValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                pending ??= new Dictionary<IntVec3, int>();
            }
        }
    }

    /// <summary>
    /// 平台層的基底地形在海上平台與高空之間變動（鋪好、拆掉、崩塌）時通知 <see cref="MapComponent_PlatformSupport"/>，
    /// 下一次檢查會把和支柱斷開的平台排進崩塌倒數。
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    public static class Patch_PlatformStructureChanged
    {
        [HarmonyLib.HarmonyPostfix]
        public static void Postfix(IntVec3 c, TerrainDef newTerr, Map ___map)
        {
            // 地圖生成時 SetTerrain 會被呼叫幾萬次：只有平台與高空會改變平台結構
            if ((newTerr != DepthRimDefOf.DepthRim_OceanPlatform && newTerr != DepthRimDefOf.DepthRim_OpenAir) || ___map == null || !SeaLevels.IsPlatformLevel(___map))
            {
                return;
            }
            ___map.GetComponent<MapComponent_PlatformSupport>()?.Notify_TerrainChanged(c);
        }
    }

    /// <summary>
    /// 柱身層：每個區塊只畫柱身落在自己範圍內的那一段（貼圖座標跟著裁）。平台層往下伸的柱身排在 1950：在海面與快照之上，
    /// 被甲板（2000）與平台側面蓋住；水面層往上伸的柱身排在 2910：在浮船、建築與 Pawn 之後。基座生成或移除時，柱身蓋到的區塊都會標記重建。
    /// <para>材質是這張地圖的（<see cref="MapComponent_SeaSurface.PillarMaterial"/>）：柱身依每個片段離海面的高度自己算平台的影子
    /// （DepthRim_Pillar.frag.glsl）。頂點色 a = 柱腳的燈光照亮影子的程度，燈光改變（GroundGlow）時重建。</para>
    /// </summary>
    public class SectionLayer_PlatformPillars : SectionLayer
    {
        public SectionLayer_PlatformPillars(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)MapMeshFlagDefOf.Buildings | (ulong)MapMeshFlagDefOf.GroundGlow;
        }

        public override bool Visible => DebugViewSettings.drawThingsPrinted;

        /// <summary>給驗收測試：最近一次重建畫了幾段柱身。</summary>
        public int lastPieces;

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            lastPieces = 0;
            MapComponent_SeaSurface comp = Map.GetComponent<MapComponent_SeaSurface>();
            Map sea = SeaLevels.IsSeaLevel(Map) ? Map : SeaLevels.SeaLevelOf(Map);
            if (comp == null || sea == null)
            {
                return;
            }
            List<(Rect body, IntVec3 foot, Material mat)> bodies = new List<(Rect, IntVec3, Material)>();
            MapComponent_PlatformSupport support = Map.GetComponent<MapComponent_PlatformSupport>();
            if (support != null)
            {
                foreach (Building_PlatformPillar b in support.Bases)
                {
                    if (b.Spawned)
                    {
                        bodies.Add((b.PillarRect, b.Position, comp.PillarMaterial(b.Ext)));
                    }
                }
            }
            // 水面層：支柱腳的柱身從入水處往上伸
            foreach (Building_PillarFoot foot in Building_PillarFoot.AllOn(Map))
            {
                bodies.Add((foot.BodyRect, foot.Position, comp.PillarMaterial(foot.Ext)));
            }
            if (bodies.Count == 0)
            {
                return;
            }
            CellRect cr = section.CellRect;
            Rect sectionRect = Rect.MinMaxRect(cr.minX, cr.minZ, cr.maxX + 1, cr.maxZ + 1);
            float y = AltitudeLayer.Terrain.AltitudeFor();
            foreach ((Rect body, IntVec3 foot, Material mat) in bodies)
            {
                if (!PlatformPillarGeometry.Clip(body, sectionRect, out Rect quad, out Rect uv))
                {
                    continue;
                }
                if (mat == null)
                {
                    continue;
                }
                LayerSubMesh sm = GetSubMesh(mat);
                int start = sm.verts.Count;
                // 柱腳（海面座標）的燈光照亮柱身的影子
                Color32 lamp = PlatformShadow.LampColor(sea, foot.x + 0.5f, foot.z);
                for (int i = 0; i < 4; i++)
                {
                    sm.colors.Add(lamp);
                }
                sm.verts.Add(new Vector3(quad.xMin, y, quad.yMin));
                sm.verts.Add(new Vector3(quad.xMin, y, quad.yMax));
                sm.verts.Add(new Vector3(quad.xMax, y, quad.yMax));
                sm.verts.Add(new Vector3(quad.xMax, y, quad.yMin));
                sm.uvs.Add(new Vector3(uv.xMin, uv.yMin, 0f));
                sm.uvs.Add(new Vector3(uv.xMin, uv.yMax, 0f));
                sm.uvs.Add(new Vector3(uv.xMax, uv.yMax, 0f));
                sm.uvs.Add(new Vector3(uv.xMax, uv.yMin, 0f));
                sm.tris.Add(start);
                sm.tris.Add(start + 1);
                sm.tris.Add(start + 2);
                sm.tris.Add(start);
                sm.tris.Add(start + 2);
                sm.tris.Add(start + 3);
                lastPieces++;
            }
            FinalizeMesh(MeshParts.All);
        }
    }
}
