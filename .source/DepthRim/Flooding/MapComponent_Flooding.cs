using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 艙室水位（設計稿 §2.2）：每間艙室（<see cref="Room"/>）一個水位值 0–1。
    ///
    /// <para><b>開放水域</b>：碰到地圖邊緣、或有任何沒有屋頂的格子的房間，水位固定為 1（室外就是海）。
    /// 冰洋地圖整片都有岩頂，所以只要一面牆被打穿、房間連到外面的大洞穴，就會變成開放水域。</para>
    ///
    /// <para><b>密閉艙室</b>：水位只會因為下列原因改變——受損牆體滲水（依受損比例 × 水壓 × 兩側水位差）、
    /// 打開的門兩側互相流通、排水泵抽水。</para>
    ///
    /// <para><b>存放方式</b>：水位存在每一格上（byte 0–255），房間的水位 = 房間內格子的平均值，更新後再寫回每一格。
    /// 原版每次牆體變動都會重建 Room 物件，用格子存可以讓新房間自然繼承舊房間的水位（合併時按格數加權平均），
    /// 不需要像原版真空系統那樣 patch 房間重建流程。</para>
    /// </summary>
    public class MapComponent_Flooding : MapComponent
    {
        public const int UpdateInterval = 250;

        private const float SecondsPerUpdate = UpdateInterval / 60f;

        private const float LevelEpsilon = FloodingMath.LevelEpsilon;

        private byte[] levels;

        /// <summary>
        /// 每格是否屬於開放水域（1）或密閉艙室／未知（0），每次更新時由房間判定寫入，不存檔。
        /// 渲染（外海層、艙室層、Pawn 水線）在平行執行緒也要查，所以存成格子陣列而不是每次查房間。
        /// </summary>
        private byte[] openFlags;

        /// <summary>
        /// 每格上一次更新時是否屬於「和外海相通」的房間（不論滿了沒）。不存檔。
        /// 和 <see cref="openFlags"/> 不同：開放房間的格子要灌滿才算開放水域（交給外海層畫），還在進水的格子由艙室層畫；
        /// 破艙判定看的是這個旗標從 0 變 1。
        /// </summary>
        private byte[] seaFlags;

        private bool active;

        private bool updatedOnce;

        /// <summary>
        /// 這張地圖上「穿著中的潛水服頭燈」追蹤清單（二期 P2-3）。存在這裡而不是靜態欄位，
        /// 所以地圖卸除時可以整份清掉，不會跨地圖殘留。
        /// </summary>
        private readonly List<CompSuitLamp> suitLamps = new List<CompSuitLamp>();

        internal List<CompSuitLamp> SuitLamps => suitLamps;

        /// <summary>目前追蹤幾個穿著中的頭燈（給驗收測試的殘留檢查用；清單本身是 internal）。</summary>
        public int TrackedSuitLampCount => suitLamps.Count;

        /// <summary>把所有追蹤中的頭燈忘掉（測試清理與地圖卸除用）。</summary>
        public void ReleaseSuitLamps()
        {
            SuitLampUpdater.ReleaseAll(this);
        }

        /// <summary>
        /// 開發用：強制套用的水體視覺層級（實作指南 C.3，不存檔）。null = 依地圖的層級。
        /// 只影響畫面（<see cref="RenderTier"/>），水壓、滲水速度等遊戲數值仍用 <see cref="DepthTier"/>。
        /// </summary>
        public DepthTier? debugTierOverride;

        private DepthTier lastRenderTier;

        private bool renderTierKnown;

        private readonly List<CompFloodPump> pumps = new List<CompFloodPump>();

        private FloodingAudio audio;

        /// <summary>這張地圖的事件音效（二期 P2-11）。</summary>
        public FloodingAudio Audio => audio ?? (audio = new FloodingAudio(map, this));

        /// <summary>上一次更新後格子的開放水域旗標已經有效（第一次更新的 0→1 不是破艙）。</summary>
        private bool openFlagsValid;

        // ── 音效用的每次更新暫存（P2-11）──
        private readonly List<IntVec3> inflowCells = new List<IntVec3>();

        /// <summary>和 <see cref="inflowCells"/> 一一對應：水流進的那一格（艙室內側），進水效果往這裡噴。</summary>
        private readonly List<IntVec3> inflowTargets = new List<IntVec3>();

        /// <summary>和 <see cref="inflowCells"/> 一一對應：進水強度階段（<see cref="FloodingMath.LeakStage"/>，0 滲水 … 3 湧入）。</summary>
        private readonly List<int> inflowStages = new List<int>();

        /// <summary>每個開放房間最多拿幾格屋頂破洞當進水效果點。</summary>
        private const int MaxRoofInflowPoints = 3;

        /// <summary>海水從破口湧進還沒滿的區域時，最多標幾處前緣當進水效果點。</summary>
        private const int MaxFrontInflowPoints = 2;

        /// <summary>最近一次更新時各進水點的強度階段（和 <see cref="InflowCells"/> 一一對應）。</summary>
        public IReadOnlyList<int> InflowStages => inflowStages;

        /// <summary>進水效果每隔幾 ticks 噴一次。</summary>
        public const int LeakEffectInterval = 30;

        /// <summary>同時最多幾個進水點有效果（取鏡頭範圍內的前幾個）。</summary>
        public const int MaxLeakEffects = 8;

        /// <summary>累計噴出的進水效果數（給驗收測試）。</summary>
        public int LeakEffectsSpawned { get; private set; }

        private readonly List<IntVec3> breachCells = new List<IntVec3>();

        private int damagedWallCount;

        private IntVec3 creakCell;

        private int creakCandidates;

        /// <summary>最近一次更新時正在進水的格子（受損牆、打開的門）。</summary>
        public IReadOnlyList<IntVec3> InflowCells => inflowCells;

        /// <summary>最近一次更新時剛被打通成開放水域、而且原本還沒滿水的艙室（每間一格）。</summary>
        public IReadOnlyList<IntVec3> BreachCells => breachCells;

        /// <summary>最近一次更新時密閉艙室邊界上的受損牆數。</summary>
        public int DamagedWallCount => damagedWallCount;


        // ── 每次更新的暫存 ──
        private readonly Dictionary<Room, float> roomLevel = new Dictionary<Room, float>();

        private readonly Dictionary<Room, float> roomDelta = new Dictionary<Room, float>();

        private readonly HashSet<Room> openRooms = new HashSet<Room>();

        private readonly List<Room> doorways = new List<Room>();

        private static readonly HashSet<IntVec3> tmpBorder = new HashSet<IntVec3>();

        private static readonly List<Room> tmpNeighbors = new List<Room>();

        public MapComponent_Flooding(Map map) : base(map)
        {
        }

        public bool Active => active;

        public DepthTier DepthTier => FloodingUtility.DepthTierOf(map);

        /// <summary>
        /// 渲染用的層級（實作指南 C.3）：除錯覆寫優先，否則用 <see cref="DepthTier"/>。
        /// 層級本身可由地圖父物件實作 <see cref="IDepthTierProvider"/> 提供（潛艇依下潛深度），
        /// 那條路徑在 <see cref="FloodingUtility.DepthTierOf"/> 裡。
        /// </summary>
        public DepthTier RenderTier => debugTierOverride ?? DepthTier;


        public override void FinalizeInit()
        {
            base.FinalizeInit();
            active = FloodingUtility.IsWaterMap(map);
            if (!active)
            {
                levels = null;
                return;
            }
            openFlags = new byte[map.cellIndices.NumGridCells];
            seaFlags = new byte[map.cellIndices.NumGridCells];
            updatedOnce = false;
            if (levels == null || levels.Length != map.cellIndices.NumGridCells)
            {
                float initial = map.Biome.GetModExtension<WaterMapExtension>().initialLevel;
                levels = new byte[map.cellIndices.NumGridCells];
                byte b = FloodingMath.ToByte(initial);
                for (int i = 0; i < levels.Length; i++)
                {
                    levels[i] = b;
                }
            }
            // 原版在地圖生成時就建好區塊網格，那時這個元件還沒啟用（水體網格是空的）；開放水域的水位之後也不會再變，
            // 所以啟用後整張地圖重建一次水體網格
            map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
        }

        // ── 查詢 ─────────────────────────────────────────────────────────────

        public float LevelAt(IntVec3 c)
        {
            if (!active || !c.InBounds(map))
            {
                return 0f;
            }
            return FloodingMath.FromByte(levels[map.cellIndices.CellToIndex(c)]);
        }

        /// <summary>此格屬於開放水域（依最近一次更新）。可在任何執行緒呼叫。</summary>
        public bool IsOpenAt(IntVec3 c)
        {
            return active && c.InBounds(map) && openFlags[map.cellIndices.CellToIndex(c)] != 0;
        }

        /// <summary>房間目前的水位（直接由格子平均，不需要等下一次更新）。</summary>
        public float LevelOf(Room room)
        {
            if (!active || room == null)
            {
                return 0f;
            }
            if (IsOpenWater(room))
            {
                return 1f;
            }
            return AverageLevel(room);
        }

        /// <summary>開放水域：碰到地圖邊緣，或有沒有屋頂的格子。</summary>
        public bool IsOpenWater(Room room)
        {
            return room.TouchesMapEdge || room.OpenRoofCount > 0;
        }

        // ── 註冊 ─────────────────────────────────────────────────────────────

        public void Register(CompFloodPump pump)
        {
            if (!pumps.Contains(pump))
            {
                pumps.Add(pump);
            }
        }

        public void Deregister(CompFloodPump pump)
        {
            pumps.Remove(pump);
        }

        // ── 更新 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 地圖卸除（回到世界地圖、或地圖被丟棄）：把這張地圖的頭燈全部忘掉。
        /// 這裡不能呼叫 <see cref="MapComponent_Flooding"/> 以外的東西去碰 glowGrid——地圖正在消失（二期 P2-3）。
        /// </summary>
        public override void MapRemoved()
        {
            base.MapRemoved();
            SuitLampUpdater.ReleaseAll(this);
            audio?.EndAll();
        }

        public override void MapComponentTick()
        {            // 潛水服頭燈：每 15 ticks 檢查一次，只有換格時才搬移光源（實作指南 B.3）
            if (active && map.IsHashIntervalTick(SuitLampUpdater.Interval))
            {
                SuitLampUpdater.Update(map);
            }
            if (active && map.IsHashIntervalTick(LeakEffectInterval))
            {
                SpawnLeakEffects();
            }
            // 第一次 tick 立刻更新一次，開放水域旗標才不用等 250 ticks（外海層要靠它）
            if (!active || (updatedOnce && !map.IsHashIntervalTick(UpdateInterval)))
            {
                return;
            }
            updatedOnce = true;
            UpdateLevels();
            openFlagsValid = true;
            List<Building_WatertightDoor> watertight = map.listerBuildings.AllBuildingsColonistOfClass<Building_WatertightDoor>().ToList();
            for (int i = 0; i < watertight.Count; i++)
            {
                watertight[i].Notify_WaterUpdated();
            }
            Audio.AfterLevelUpdate(inflowCells, inflowStages, breachCells, damagedWallCount, creakCell);
            FloodingPawnEffects.Apply(map, this);
        }

        private void UpdateLevels()
        {
            roomLevel.Clear();
            roomDelta.Clear();
            openRooms.Clear();
            doorways.Clear();
            inflowCells.Clear();
            inflowTargets.Clear();
            inflowStages.Clear();
            breachCells.Clear();
            damagedWallCount = 0;
            creakCell = IntVec3.Invalid;
            creakCandidates = 0;

            IReadOnlyList<Room> rooms = map.regionGrid.AllRooms;
            for (int i = 0; i < rooms.Count; i++)
            {
                Room room = rooms[i];
                if (room.IsDoorway)
                {
                    doorways.Add(room);
                    continue;
                }
                if (IsOpenWater(room))
                {
                    openRooms.Add(room);
                    roomLevel[room] = 1f;
                }
                else
                {
                    roomLevel[room] = AverageLevel(room);
                }
                roomDelta[room] = 0f;
            }

            AccumulateLeaks();
            AccumulateDoorFlow();
            AccumulatePumps();

            foreach (KeyValuePair<Room, float> kv in roomLevel)
            {
                Room room = kv.Key;
                if (openRooms.Contains(room))
                {
                    // 和外海相通：每格各自往上漲，滿了才算開放水域（不再一打通就整間瞬間灌滿）
                    FillOpenRoom(room);
                    continue;
                }
                WriteLevel(room, FloodingMath.ApplyVolume(kv.Value, roomDelta[room], room.CellCount), false);
            }
            // 門口格：取兩側的平均，讓站在門上的人也有合理水位
            for (int i = 0; i < doorways.Count; i++)
            {
                Room door = doorways[i];
                CollectNeighborRooms(door, tmpNeighbors);
                if (tmpNeighbors.Count == 0)
                {
                    continue;
                }
                float sum = 0f;
                bool allOpen = true;
                for (int j = 0; j < tmpNeighbors.Count; j++)
                {
                    allOpen &= openRooms.Contains(tmpNeighbors[j]);
                    sum += LevelOf(tmpNeighbors[j]);
                }
                // 門口只有兩側都是開放水域、而且已經灌滿時才算外海；任一側是艙室的門屬於艙室邊界
                float doorLevel = sum / tmpNeighbors.Count;
                WriteLevel(door, doorLevel, allOpen && doorLevel >= FullLevel, allOpen);
            }
        }

        /// <summary>受損牆體（含門）：從另一側水位較高的房間滲入，速度 ∝ 受損比例 × 水壓 × 水位差。</summary>
        private void AccumulateLeaks()
        {
            DepthTier tier = DepthTier;
            foreach (KeyValuePair<Room, float> kv in roomLevel)
            {
                Room room = kv.Key;
                if (openRooms.Contains(room))
                {
                    continue;
                }
                float level = kv.Value;
                tmpBorder.Clear();
                foreach (IntVec3 b in room.BorderCellsCardinal)
                {
                    if (!tmpBorder.Add(b))
                    {
                        continue;
                    }
                    Building edifice = b.GetEdifice(map);
                    if (edifice == null || !edifice.def.useHitPoints || edifice.HitPoints >= edifice.MaxHitPoints)
                    {
                        // 沒有受損牆時，承壓聲從任一面艙壁發出（蓄水池抽樣，只在還沒找到受損牆時）
                        if (edifice != null && damagedWallCount == 0 && Rand.Chance(1f / ++creakCandidates))
                        {
                            creakCell = b;
                        }
                        continue;
                    }
                    if (damagedWallCount == 0)
                    {
                        creakCandidates = 0;
                    }
                    damagedWallCount++;
                    if (Rand.Chance(1f / ++creakCandidates))
                    {
                        creakCell = b;
                    }
                    Room source = HighestNeighborRoom(b, room, out float sourceLevel);
                    if (source == null)
                    {
                        continue;
                    }
                    float volume = FloodingMath.LeakVolume(edifice.HitPoints, edifice.MaxHitPoints, tier, sourceLevel, level, SecondsPerUpdate);
                    // 水密門、抗壓水密牆：受損時滲水較少（LeakResistanceExtension）
                    volume *= LeakResistanceExtension.FactorFor(edifice.def);
                    if (volume <= 0f)
                    {
                        continue;
                    }
                    roomDelta[room] += volume;
                    inflowCells.Add(b);
                    inflowTargets.Add(InsideNeighbor(b, room));
                    inflowStages.Add(FloodingMath.LeakStage((float)edifice.HitPoints / edifice.MaxHitPoints));
                    if (!openRooms.Contains(source))
                    {
                        roomDelta[source] -= volume;
                    }
                }
            }
        }

        /// <summary>打開的門：兩側水位往平均值流動；一側是開放水域時另一側會很快灌滿。</summary>
        private void AccumulateDoorFlow()
        {
            for (int i = 0; i < doorways.Count; i++)
            {
                Room doorway = doorways[i];
                Building_Door door = doorway.Cells.FirstOrFallback(IntVec3.Invalid).GetDoor(map);
                if (door == null || !door.Open)
                {
                    continue;
                }
                CollectNeighborRooms(doorway, tmpNeighbors);
                for (int a = 0; a < tmpNeighbors.Count; a++)
                {
                    for (int b = a + 1; b < tmpNeighbors.Count; b++)
                    {
                        Room ra = tmpNeighbors[a];
                        Room rb = tmpNeighbors[b];
                        if (!roomLevel.TryGetValue(ra, out float la) || !roomLevel.TryGetValue(rb, out float lb))
                        {
                            continue;
                        }
                        Room high = la >= lb ? ra : rb;
                        Room low = la >= lb ? rb : ra;
                        float volume = FloodingMath.DoorFlowVolume(roomLevel[high], roomLevel[low], OpenAwareCells(high), OpenAwareCells(low), SecondsPerUpdate);
                        if (volume <= 0f)
                        {
                            continue;
                        }
                        if (!openRooms.Contains(low))
                        {
                            roomDelta[low] += volume;
                            inflowCells.Add(door.Position);
                            inflowTargets.Add(InsideNeighbor(door.Position, low));
                            // 打開的門是整片水牆湧入，水位差越大越猛
                            inflowStages.Add(roomLevel[high] - roomLevel[low] > 0.5f ? 3 : 2);
                        }
                        if (!openRooms.Contains(high))
                        {
                            roomDelta[high] -= volume;
                        }
                    }
                }
            }
        }

        private float OpenAwareCells(Room room)
        {
            return openRooms.Contains(room) ? float.MaxValue : room.CellCount;
        }

        private void AccumulatePumps()
        {
            for (int i = 0; i < pumps.Count; i++)
            {
                CompFloodPump pump = pumps[i];
                if (!pump.Working)
                {
                    continue;
                }
                Room room = pump.DrainedRoom;
                if (room == null || openRooms.Contains(room) || !roomDelta.ContainsKey(room))
                {
                    continue;
                }
                roomDelta[room] -= pump.Props.cellsPerSecond * SecondsPerUpdate;
            }
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        private float AverageLevel(Room room)
        {
            int count = 0;
            int sum = 0;
            foreach (IntVec3 c in room.Cells)
            {
                sum += levels[map.cellIndices.CellToIndex(c)];
                count++;
            }
            return count == 0 ? 0f : sum / (255f * count);
        }

        /// <summary>把水位與開放水域旗標寫回房間每一格；改變的格子標記水體網格重建（含相鄰格，頂點由四格共用）。</summary>
        /// <summary>水位位元組到這個值以上就算「灌滿」（開放水域、交給外海層畫）。</summary>
        private const byte FullByte = 254;

        private const float FullLevel = FullByte / 255f;

        /// <summary>把同一個水位寫進房間每一格（密閉艙室、門口）；改變的格子標記水體網格重建（含相鄰格，頂點由四格共用）。</summary>
        private void WriteLevel(Room room, float level, bool open, bool sea = false)
        {
            byte b = FloodingMath.ToByte(level);
            byte o = open ? (byte)1 : (byte)0;
            byte s = sea ? (byte)1 : (byte)0;
            ulong flag = DepthRimDefOf.DepthRim_Water;
            foreach (IntVec3 c in room.Cells)
            {
                int index = map.cellIndices.CellToIndex(c);
                seaFlags[index] = s;
                if (levels[index] != b || openFlags[index] != o)
                {
                    levels[index] = b;
                    openFlags[index] = o;
                    map.mapDrawer.MapMeshDirty(c, flag, regenAdjacentCells: true, regenAdjacentSections: false);
                }
            }
        }

        /// <summary>
        /// 和外海相通的房間：每一格各自往上漲（速度見 <see cref="FloodingMath.OpenRoomFill"/>），滿了才標成開放水域。
        /// 原本沒有和外海相通、也還沒滿的格子這次變成相通 = 破艙（每間記一格，給破艙聲）。
        /// 只靠屋頂破洞進水的房間，拿幾格破洞當進水效果點。
        /// </summary>
        private void FillOpenRoom(Room room)
        {
            int unfilled = 0;
            bool breachRecorded = false;
            foreach (IntVec3 c in room.Cells)
            {
                int index = map.cellIndices.CellToIndex(c);
                if (levels[index] < FullByte)
                {
                    unfilled++;
                }
                if (openFlagsValid && !breachRecorded && seaFlags[index] == 0 && levels[index] < 250)
                {
                    breachRecorded = true;
                    breachCells.Add(c);
                }
            }
            int rise = unfilled == 0 ? 0 : Mathf.Max(1, Mathf.CeilToInt(FloodingMath.OpenRoomFill(room.TouchesMapEdge, room.OpenRoofCount, unfilled, DepthTier, SecondsPerUpdate) * 255f));
            ulong flag = DepthRimDefOf.DepthRim_Water;
            int roofPoints = 0;
            int frontPoints = 0;
            foreach (IntVec3 c in room.Cells)
            {
                int index = map.cellIndices.CellToIndex(c);
                seaFlags[index] = 1;
                // 破口前緣：還沒滿的格子緊鄰已經滿的格子 = 海水正從這裡湧進來（效果與進水聲，最多兩處）
                if (room.TouchesMapEdge && unfilled > 0 && frontPoints < MaxFrontInflowPoints && levels[index] < FullByte)
                {
                    for (int d = 0; d < 4; d++)
                    {
                        IntVec3 n = c + GenAdj.CardinalDirections[d];
                        if (n.InBounds(map) && levels[map.cellIndices.CellToIndex(n)] >= FullByte && n.GetRoom(map) == room)
                        {
                            frontPoints++;
                            inflowCells.Add(n);
                            inflowTargets.Add(c);
                            inflowStages.Add(3);
                            break;
                        }
                    }
                }
                byte b = (byte)Mathf.Min(255, levels[index] + rise);
                byte o = b >= FullByte ? (byte)1 : (byte)0;
                if (b < FullByte && !room.TouchesMapEdge && roofPoints < MaxRoofInflowPoints && !c.Roofed(map))
                {
                    // 屋頂破洞：水從上面灌下來，效果點就是破洞本身
                    roofPoints++;
                    inflowCells.Add(c);
                    inflowTargets.Add(c);
                    inflowStages.Add(2);
                }
                if (levels[index] != b || openFlags[index] != o)
                {
                    levels[index] = b;
                    openFlags[index] = o;
                    map.mapDrawer.MapMeshDirty(c, flag, regenAdjacentCells: true, regenAdjacentSections: false);
                }
            }
        }

        /// <summary>牆格 b 旁邊屬於 room 的一格（進水效果的方向）；找不到就回 b。</summary>
        private IntVec3 InsideNeighbor(IntVec3 b, Room room)
        {
            for (int i = 0; i < 4; i++)
            {
                IntVec3 c = b + GenAdj.CardinalDirections[i];
                if (c.InBounds(map) && c.GetRoom(map) == room)
                {
                    return c;
                }
            }
            return b;
        }

        /// <summary>
        /// 進水效果：在鏡頭看得到的進水點，從牆或門往艙室內側噴水花，落點加漣漪。
        /// 只是畫面，和水量無關；最多 <see cref="MaxLeakEffects"/> 個點。
        /// </summary>
        private void SpawnLeakEffects()
        {
            if (inflowCells.Count == 0 || Find.CurrentMap != map || Find.CameraDriver == null)
            {
                return;
            }
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
            int shown = 0;
            for (int i = 0; i < inflowCells.Count && shown < MaxLeakEffects; i++)
            {
                IntVec3 from = inflowCells[i];
                IntVec3 to = inflowTargets[i];
                if (!view.Contains(from) || from.Fogged(map))
                {
                    continue;
                }
                int stage = i < inflowStages.Count ? inflowStages[i] : 1;
                // 滲水只是偶爾一滴：一半的時間不噴
                if (stage == 0 && !Rand.Chance(0.5f))
                {
                    continue;
                }
                shown++;
                FloodingMath.LeakEffect(stage, out int count, out float scale, out float speed);
                Vector3 dir = (to - from).ToVector3();
                bool fromAbove = from == to;
                Vector3 origin = from.ToVector3Shifted() + dir * 0.45f;
                for (int k = 0; k < count; k++)
                {
                    FleckCreationData spray = FleckMaker.GetDataStatic(origin + new Vector3(Rand.Range(-0.15f, 0.15f), 0f, Rand.Range(-0.15f, 0.15f)), map, DepthRimDefOf.DepthRim_LeakSpray, scale * Rand.Range(0.8f, 1.2f));
                    // 屋頂破洞往四面散開；牆與門往艙室內側噴，越破噴得越遠越散
                    spray.velocityAngle = fromAbove ? Rand.Range(0f, 360f) : dir.AngleFlat() + Rand.Range(-15f - 8f * stage, 15f + 8f * stage);
                    spray.velocitySpeed = speed * Rand.Range(0.8f, 1.25f);
                    spray.rotation = Rand.Range(0f, 360f);
                    map.flecks.CreateFleck(spray);
                }
                if (Rand.Chance(0.3f + 0.2f * stage))
                {
                    FleckMaker.Static(to.ToVector3Shifted() + new Vector3(Rand.Range(-0.3f, 0.3f), 0f, Rand.Range(-0.3f, 0.3f)), map, FleckDefOf.WaterRipple, 0.5f + 0.2f * stage);
                }
                LeakEffectsSpawned++;
            }
        }

        /// <summary>牆格 b 另一側（排除 self 與門口）水位最高的房間。</summary>
        private Room HighestNeighborRoom(IntVec3 b, Room self, out float level)
        {
            Room best = null;
            level = -1f;
            for (int i = 0; i < 4; i++)
            {
                IntVec3 c = b + GenAdj.CardinalDirections[i];
                if (!c.InBounds(map))
                {
                    continue;
                }
                Room r = c.GetRoom(map);
                if (r == null || r == self || r.IsDoorway || !roomLevel.TryGetValue(r, out float l))
                {
                    continue;
                }
                if (l > level)
                {
                    level = l;
                    best = r;
                }
            }
            return best;
        }

        private void CollectNeighborRooms(Room doorway, List<Room> outRooms)
        {
            outRooms.Clear();
            foreach (IntVec3 cell in doorway.Cells)
            {
                for (int i = 0; i < 4; i++)
                {
                    IntVec3 c = cell + GenAdj.CardinalDirections[i];
                    if (!c.InBounds(map))
                    {
                        continue;
                    }
                    Room r = c.GetRoom(map);
                    if (r != null && r != doorway && !r.IsDoorway && !outRooms.Contains(r))
                    {
                        outRooms.Add(r);
                    }
                }
            }
        }

        // ── 顯示 ─────────────────────────────────────────────────────────────
        // 水體由 SectionLayer_WaterVolume 畫；這裡只在主執行緒建立 Pawn 水線材質。

        public override void MapComponentUpdate()
        {
            base.MapComponentUpdate();
            if (!active)
            {
                return;
            }
            PawnWaterRendering.ProcessPending();
            if (Time.frameCount % UnderwaterAudio.SustainerUpdateFrames == 0)
            {
                Audio.FrameUpdate(pumps);
            }
            if (Find.CurrentMap == map && WorldRendererUtility.DrawingMap)
            {
                WaterTierMaterials.UpdateCameraZoom(Find.CameraDriver.RootSize);
                WaterTierMaterials.UpdateSkyLight(map);
                SectionLayer_AbyssBackdrop.UpdateSkyLight(map);
            }
            // 地圖的層級改變（潛艇下潛、除錯覆寫）：整張重建，所有區塊改用新材質
            DepthTier tier = RenderTier;
            if (!renderTierKnown || tier != lastRenderTier)
            {
                renderTierKnown = true;
                lastRenderTier = tier;
                map.mapDrawer.WholeMapChanged(DepthRimDefOf.DepthRim_Water);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            DataExposeUtility.LookByteArray(ref levels, "waterLevels");
        }
    }
}
