using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 每一格的空氣壓力（艙壓，0–3 級）：密閉的艙室會一直記住自己的壓力，不會因為潛艇換深度、斷電而改變。
    ///
    /// <para><b>規則</b>（每 <see cref="UpdateInterval"/> ticks 結算一次，只處理有艙壓或有加壓機的艙室）：</para>
    /// <list type="bullet">
    /// <item>同一間艙室的格子取平均（牆拆掉、兩間合併時壓力按格數混合），再寫回每一格。</item>
    /// <item>「外面」（碰到地圖邊緣或有沒屋頂的格子；水下地圖上就是開放水域）沒有艙壓，一律 0。</item>
    /// <item>撐不住壓力的艙室（地板不是耐壓甲板／船體平台，或有一般的門，見 <see cref="CanHoldPressure"/>）
    /// 每小時洩 <see cref="PressureAdaptationMath.UnheldLeakPerHour"/> 級。</item>
    /// <item>開著的門：兩側艙室的艙壓互相靠近（每小時 <see cref="PressureAdaptationMath.OpenDoorLeakPerHour"/> 級）。
    /// 門外是地表的外面時往 0 洩；門外是開放水域時不交換空氣（海水灌進來，由水位系統處理）。</item>
    /// <item>運作中的加壓機把它的艙室往目標壓力推。</item>
    /// </list>
    ///
    /// <para>水不會改變艙壓：淹水的艙室壓力由水位決定（見 <see cref="PressureEnvironment"/>），水抽乾後回到原本的艙壓。
    /// 所以從海面出航的潛艇，乾艙一直是常壓。</para>
    /// </summary>
    public class MapComponent_PressureChambers : MapComponent
    {
        public const int UpdateInterval = 250;

        /// <summary>和水位（0）與牆體水壓（125）錯開。</summary>
        public const int UpdateOffset = 60;

        private readonly List<CompPressurizer> pressurizers = new List<CompPressurizer>();

        /// <summary>每格的艙壓（<see cref="ToByte"/>）；沒有任何艙壓時為 null。</summary>
        private byte[] air;

        /// <summary>艙壓 &gt; 0 的格子。</summary>
        private readonly HashSet<int> pressurised = new HashSet<int>();

        /// <summary>最近一次結算時各艙室的艙壓（查詢用，避免每次都對整間取平均）。</summary>
        private readonly Dictionary<Room, float> roomAir = new Dictionary<Room, float>();

        private int reachabilityHash;

        private readonly HashSet<Room> tmpRooms = new HashSet<Room>();

        private readonly List<Room> tmpRoomList = new List<Room>();

        private readonly Dictionary<Room, float> tmpOld = new Dictionary<Room, float>();

        private readonly Dictionary<Room, float> tmpNew = new Dictionary<Room, float>();

        private readonly List<int> tmpCells = new List<int>();

        private static readonly List<Room> tmpAround = new List<Room>();

        public MapComponent_PressureChambers(Map map) : base(map)
        {
        }

        public IReadOnlyList<CompPressurizer> Pressurizers => pressurizers;

        /// <summary>這張地圖上有沒有任何一格有艙壓。</summary>
        public bool AnyPressure => pressurised.Count > 0;

        /// <summary>最近一次結算時有艙壓的艙室。</summary>
        public IEnumerable<Room> PressurisedRooms
        {
            get
            {
                foreach (KeyValuePair<Room, float> kv in roomAir)
                {
                    if (kv.Value > 0f)
                    {
                        yield return kv.Key;
                    }
                }
            }
        }

        public void Register(CompPressurizer comp)
        {
            if (!pressurizers.Contains(comp))
            {
                pressurizers.Add(comp);
            }
        }

        public void Deregister(CompPressurizer comp)
        {
            pressurizers.Remove(comp);
        }

        // ── 查詢 ─────────────────────────────────────────────────────────────

        public static byte ToByte(float level)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(level / PressureAdaptationMath.MaxLevel * 255f), 0, 255);
        }

        public static float FromByte(byte b)
        {
            return b * PressureAdaptationMath.MaxLevel / 255f;
        }

        public float AirAt(IntVec3 c)
        {
            if (air == null || !c.InBounds(map))
            {
                return 0f;
            }
            return FromByte(air[map.cellIndices.CellToIndex(c)]);
        }

        /// <summary>艙室的艙壓：最近一次結算的值；剛形成、還沒結算過的艙室取它第一格的值。</summary>
        public float AirOf(Room room)
        {
            if (room == null || air == null)
            {
                return 0f;
            }
            if (roomAir.TryGetValue(room, out float v))
            {
                return v;
            }
            foreach (IntVec3 c in room.Cells)
            {
                return AirAt(c);
            }
            return 0f;
        }

        /// <summary>這間房間是「外面」：碰到地圖邊緣或有沒屋頂的格子（水下地圖上就是開放水域）。外面沒有艙壓。</summary>
        public static bool IsOutside(Room room)
        {
            return room == null || room.TouchesMapEdge || room.OpenRoofCount > 0;
        }

        /// <summary>這間艙室撐得住壓力：密閉、地板全是耐壓甲板或船體平台、門都是水密門（不限大小）。</summary>
        public bool CanHoldPressure(Room room)
        {
            return PressureChamberUtility.Problem(room, map, int.MaxValue) == null;
        }

        /// <summary>決定這間艙室壓力的加壓機：艙裡第一台符合加壓艙條件的（大小限制看那台加壓機）。</summary>
        public CompPressurizer ControllerOf(Room room)
        {
            if (room == null)
            {
                return null;
            }
            for (int i = 0; i < pressurizers.Count; i++)
            {
                CompPressurizer comp = pressurizers[i];
                if (comp.ChamberSealed && comp.Room == room)
                {
                    return comp;
                }
            }
            return null;
        }

        /// <summary>直接設定一間艙室的艙壓（除錯、驗收測試）。</summary>
        public void SetRoomAir(Room room, float level)
        {
            if (room == null || room.IsDoorway || IsOutside(room))
            {
                return;
            }
            WriteRoom(room, level);
            roomAir[room] = FromByte(ToByte(level));
            map.reachability.ClearCache();
        }

        // ── 結算 ─────────────────────────────────────────────────────────────

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            pressurised.Clear();
            if (air == null)
            {
                return;
            }
            if (air.Length != map.cellIndices.NumGridCells)
            {
                air = null;
                return;
            }
            for (int i = 0; i < air.Length; i++)
            {
                if (air[i] > 0)
                {
                    pressurised.Add(i);
                }
            }
        }

        public override void MapComponentTick()
        {
            if ((Find.TickManager.TicksGame + UpdateOffset) % UpdateInterval != 0)
            {
                return;
            }
            UpdateNow(UpdateInterval);
        }

        /// <summary>結算 <paramref name="ticks"/>（驗收測試也直接呼叫）。</summary>
        public void UpdateNow(int ticks)
        {
            for (int i = 0; i < pressurizers.Count; i++)
            {
                pressurizers[i].RefreshProblem();
            }
            if (pressurizers.Count == 0 && pressurised.Count == 0)
            {
                roomAir.Clear();
                return;
            }
            CollectRooms();
            float hours = ticks / 2500f;

            // 先算舊值，再全部用舊值算新值：開著的門兩側互相靠近時不受處理順序影響
            tmpOld.Clear();
            foreach (Room room in tmpRooms)
            {
                tmpOld[room] = IsOutside(room) ? 0f : Average(room);
            }
            tmpNew.Clear();
            foreach (Room room in tmpRooms)
            {
                if (IsOutside(room))
                {
                    tmpNew[room] = 0f;
                    continue;
                }
                float v = tmpOld[room];
                if (!CanHoldPressure(room))
                {
                    v = PressureAdaptationMath.MoveTowards(v, 0f, PressureAdaptationMath.UnheldLeakPerHour * hours);
                }
                if (OpenDoorTarget(room, out float other))
                {
                    v = PressureAdaptationMath.MoveTowards(v, other, PressureAdaptationMath.OpenDoorLeakPerHour * hours);
                }
                CompPressurizer controller = ControllerOf(room);
                if (controller != null && controller.Working)
                {
                    v = PressureAdaptationMath.MoveTowards(v, controller.TargetLevel, controller.Props.levelsPerHour * hours);
                }
                tmpNew[room] = v;
            }

            roomAir.Clear();
            foreach (KeyValuePair<Room, float> kv in tmpNew)
            {
                WriteRoom(kv.Key, kv.Value);
                roomAir[kv.Key] = FromByte(ToByte(kv.Value));
            }
            ClearStrayCells();
            AfterUpdate();
        }

        /// <summary>要處理的艙室：有艙壓的格子所在的艙室、有加壓機的艙室，以及和它們只隔一扇門的艙室。</summary>
        private void CollectRooms()
        {
            tmpRooms.Clear();
            foreach (int i in pressurised)
            {
                Room room = map.cellIndices.IndexToCell(i).GetRoom(map);
                if (room != null && !room.IsDoorway)
                {
                    tmpRooms.Add(room);
                }
            }
            for (int i = 0; i < pressurizers.Count; i++)
            {
                Room room = pressurizers[i].Room;
                if (room != null && !room.IsDoorway)
                {
                    tmpRooms.Add(room);
                }
            }
            tmpRoomList.Clear();
            tmpRoomList.AddRange(tmpRooms);
            for (int r = 0; r < tmpRoomList.Count; r++)
            {
                if (IsOutside(tmpRoomList[r]))
                {
                    continue;
                }
                foreach (IntVec3 c in tmpRoomList[r].BorderCellsCardinal)
                {
                    if (c.InBounds(map) && c.GetEdifice(map) is Building_Door)
                    {
                        RoomsAround(c, tmpAround);
                        for (int k = 0; k < tmpAround.Count; k++)
                        {
                            tmpRooms.Add(tmpAround[k]);
                        }
                    }
                }
            }
        }

        /// <summary>開著的門外，和這間艙室壓力差最多的那一側（只算會交換空氣的：其他艙室，或地表的外面）。</summary>
        private bool OpenDoorTarget(Room room, out float other)
        {
            other = 0f;
            bool found = false;
            float self = tmpOld[room];
            bool water = map.Flooding() is MapComponent_Flooding flooding && flooding.Active;
            foreach (IntVec3 c in room.BorderCellsCardinal)
            {
                if (!c.InBounds(map) || !(c.GetEdifice(map) is Building_Door door) || !door.Open)
                {
                    continue;
                }
                RoomsAround(c, tmpAround);
                for (int k = 0; k < tmpAround.Count; k++)
                {
                    Room n = tmpAround[k];
                    if (n == room || (water && IsOutside(n)))
                    {
                        continue;
                    }
                    float v = IsOutside(n) ? 0f : tmpOld.TryGetValue(n, out float o) ? o : Average(n);
                    if (!found || Mathf.Abs(v - self) > Mathf.Abs(other - self))
                    {
                        other = v;
                    }
                    found = true;
                }
            }
            return found;
        }

        /// <summary>門每開一次，門兩側的艙室各往另一側的艙壓靠近 <see cref="PressureAdaptationMath.DoorOpeningLeak"/> 級。</summary>
        public void Notify_DoorOpened(Building_Door door)
        {
            if (pressurised.Count == 0 || !door.Spawned)
            {
                return;
            }
            bool water = map.Flooding() is MapComponent_Flooding flooding && flooding.Active;
            RoomsAround(door.Position, tmpAround);
            if (tmpAround.Count < 2)
            {
                return;
            }
            tmpRoomList.Clear();
            tmpRoomList.AddRange(tmpAround);
            tmpOld.Clear();
            for (int i = 0; i < tmpRoomList.Count; i++)
            {
                tmpOld[tmpRoomList[i]] = IsOutside(tmpRoomList[i]) ? 0f : Average(tmpRoomList[i]);
            }
            for (int i = 0; i < tmpRoomList.Count; i++)
            {
                Room room = tmpRoomList[i];
                if (IsOutside(room))
                {
                    continue;
                }
                float self = tmpOld[room];
                float target = self;
                for (int j = 0; j < tmpRoomList.Count; j++)
                {
                    Room n = tmpRoomList[j];
                    if (n == room || (water && IsOutside(n)))
                    {
                        continue;
                    }
                    if (Mathf.Abs(tmpOld[n] - self) > Mathf.Abs(target - self))
                    {
                        target = tmpOld[n];
                    }
                }
                float v = PressureAdaptationMath.MoveTowards(self, target, PressureAdaptationMath.DoorOpeningLeak);
                WriteRoom(room, v);
                roomAir[room] = FromByte(ToByte(v));
            }
            AfterUpdate();
        }

        /// <summary>從 <paramref name="from"/> 這張地圖搬來的格子帶著艙壓（潛艇出航、下錨）。</summary>
        public void CopyFrom(MapComponent_PressureChambers from, IEnumerable<IntVec3> cells, IntVec3 offset)
        {
            if (from?.air == null)
            {
                return;
            }
            foreach (IntVec3 c in cells)
            {
                if (!c.InBounds(from.map))
                {
                    continue;
                }
                int src = from.map.cellIndices.CellToIndex(c);
                byte b = from.air[src];
                if (b == 0)
                {
                    continue;
                }
                IntVec3 d = c + offset;
                if (d.InBounds(map))
                {
                    WriteCell(map.cellIndices.CellToIndex(d), b);
                }
                from.WriteCell(src, 0);
            }
        }

        private void AfterUpdate()
        {
            // 艙壓（以 0.1 級為單位）變了：水密門依壓差放行的結果跟著變，清掉可達性快取
            int hash = 17;
            foreach (KeyValuePair<Room, float> kv in roomAir)
            {
                hash = unchecked(hash * 31 + kv.Key.ID * 7 + Mathf.RoundToInt(kv.Value * 10f));
            }
            if (hash != reachabilityHash)
            {
                reachabilityHash = hash;
                map.reachability.ClearCache();
            }
            // 進到有艙壓的地方：開始記錄適應值
            if (pressurised.Count == 0)
            {
                return;
            }
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = pawns.Count - 1; i >= 0; i--)
            {
                Pawn pawn = pawns[i];
                if (PressureAdaptationUtility.Applies(pawn) && PressureAdaptationUtility.Get(pawn) == null)
                {
                    float level = PressureEnvironment.At(map, pawn.Position);
                    if (level > 0f)
                    {
                        PressureAdaptationUtility.Ensure(pawn, level);
                    }
                }
            }
        }

        // ── 工具 ─────────────────────────────────────────────────────────────

        /// <summary>門格四周的房間（不含門口本身、不重複）。</summary>
        public void RoomsAround(IntVec3 doorCell, List<Room> result)
        {
            result.Clear();
            for (int i = 0; i < 4; i++)
            {
                IntVec3 n = doorCell + GenAdj.CardinalDirections[i];
                if (!n.InBounds(map))
                {
                    continue;
                }
                Room room = n.GetRoom(map);
                if (room != null && !room.IsDoorway && !result.Contains(room))
                {
                    result.Add(room);
                }
            }
        }

        private float Average(Room room)
        {
            if (air == null)
            {
                return 0f;
            }
            int sum = 0;
            int count = 0;
            foreach (IntVec3 c in room.Cells)
            {
                sum += air[map.cellIndices.CellToIndex(c)];
                count++;
            }
            return count == 0 ? 0f : FromByte((byte)Mathf.RoundToInt((float)sum / count));
        }

        private void WriteRoom(Room room, float level)
        {
            byte b = ToByte(level);
            if (b == 0 && air == null)
            {
                return;
            }
            foreach (IntVec3 c in room.Cells)
            {
                WriteCell(map.cellIndices.CellToIndex(c), b);
            }
        }

        private void WriteCell(int index, byte b)
        {
            if (air == null)
            {
                if (b == 0)
                {
                    return;
                }
                air = new byte[map.cellIndices.NumGridCells];
            }
            air[index] = b;
            if (b > 0)
            {
                pressurised.Add(index);
            }
            else
            {
                pressurised.Remove(index);
            }
        }

        /// <summary>有艙壓、卻不屬於任何艙室的格子（蓋了牆、變成深淵）歸零。</summary>
        private void ClearStrayCells()
        {
            tmpCells.Clear();
            foreach (int i in pressurised)
            {
                Room room = map.cellIndices.IndexToCell(i).GetRoom(map);
                if (room == null || room.IsDoorway || IsOutside(room))
                {
                    tmpCells.Add(i);
                }
            }
            for (int i = 0; i < tmpCells.Count; i++)
            {
                WriteCell(tmpCells[i], 0);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving && pressurised.Count == 0)
            {
                air = null;
            }
            DataExposeUtility.LookByteArray(ref air, "airPressure");
        }
    }
}
