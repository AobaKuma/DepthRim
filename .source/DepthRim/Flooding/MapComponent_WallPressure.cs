using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 牆體水壓損傷（設計稿 §5.1）：牆承受的是兩側的壓力差，不是所在深度。
    ///
    /// <para><b>拓樸快取</b>：哪幾格牆隔開哪兩個房間、多厚，由 <see cref="WallPressureTopology"/> 算出後快取。
    /// 只有牆或門變動（原版 <c>RegionDirtyer</c>，見下方的 Harmony patch）或密閉艙室改變（屋頂）時才重建，
    /// 而且只在密閉艙室附近算；沒有基地的海洋地圖完全不花時間。</para>
    ///
    /// <para><b>每次更新</b>（250 ticks，與 <see cref="MapComponent_Flooding"/> 錯開）：兩側房間的壓力比例
    /// （開放水域 1、密閉艙室 = 它的水位）相減 × 層級水壓 ÷ 厚度，超過牆的耐壓上限（<see cref="HullPressure.RatingOf"/>）
    /// 就累積損傷，滿 1 點才扣一次血。自然岩不受損，打開的門兩側正在等化，不受力。淺海的水壓是 1，任何牆都撐得住，整段跳過。</para>
    /// </summary>
    public class MapComponent_WallPressure : MapComponent
    {
        public const int UpdateInterval = MapComponent_Flooding.UpdateInterval;

        /// <summary>和水位更新錯開半個週期，讀到的是已經更新好的水位。</summary>
        public const int UpdateOffset = UpdateInterval / 2;

        private bool dirty = true;

        private int sealedHash;

        private List<WallPressureTopology.Entry> entries = new List<WallPressureTopology.Entry>();

        private readonly Dictionary<int, float> damage = new Dictionary<int, float>();

        private int lastMessageTick = -99999;

        /// <summary>最近一次更新時承受負載（Δp &gt; 0）的牆格數、超載的牆格數（給驗收測試）。</summary>
        public int LastLoadedCount { get; private set; }

        public int LastOverloadedCount { get; private set; }

        public int CachedEntryCount => entries.Count;

        public int RebuildCount { get; private set; }

        public MapComponent_WallPressure(Map map) : base(map)
        {
        }

        public void MarkDirty()
        {
            dirty = true;
        }

        public override void MapComponentTick()
        {
            if ((Find.TickManager.TicksGame + UpdateOffset) % UpdateInterval != 0)
            {
                return;
            }
            UpdateNow();
        }

        /// <summary>立刻做一次更新（驗收測試也呼叫）。</summary>
        public void UpdateNow()
        {
            LastLoadedCount = 0;
            LastOverloadedCount = 0;
            MapComponent_Flooding flooding = map.Flooding();
            if (flooding == null || !flooding.Active)
            {
                return;
            }
            DepthTier tier = flooding.DepthTier;
            int hash = SealedRoomsHash(flooding);
            if (hash != sealedHash)
            {
                sealedHash = hash;
                dirty = true;
            }
            if (dirty)
            {
                Rebuild(flooding);
            }
            if (entries.Count == 0)
            {
                return;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                Apply(entries[i], flooding, tier);
            }
        }

        private int SealedRoomsHash(MapComponent_Flooding flooding)
        {
            unchecked
            {
                int h = 17;
                IReadOnlyList<Room> rooms = map.regionGrid.AllRooms;
                for (int i = 0; i < rooms.Count; i++)
                {
                    Room r = rooms[i];
                    if (r.IsDoorway || flooding.IsOpenWater(r))
                    {
                        continue;
                    }
                    h = h * 31 + r.ID;
                    h = h * 31 + r.CellCount;
                }
                return h;
            }
        }

        public static bool IsBarrier(Building b)
        {
            return b != null && (b is Building_Door || b.def.passability == Traversability.Impassable);
        }

        private void Rebuild(MapComponent_Flooding flooding)
        {
            dirty = false;
            RebuildCount++;
            entries.Clear();
            int w = map.Size.x;
            int h = map.Size.z;
            // 只在密閉艙室附近算：每間密閉艙室的外接矩形往外 2 × 最大厚度
            bool[] band = new bool[w * h];
            bool any = false;
            IReadOnlyList<Room> rooms = map.regionGrid.AllRooms;
            for (int i = 0; i < rooms.Count; i++)
            {
                Room r = rooms[i];
                if (r.IsDoorway || flooding.IsOpenWater(r))
                {
                    continue;
                }
                CellRect rect = r.ExtentsClose.ExpandedBy(WallPressureTopology.MaxThickness * 2).ClipInsideMap(map);
                foreach (IntVec3 c in rect)
                {
                    band[c.z * w + c.x] = true;
                }
                any = true;
            }
            if (!any)
            {
                return;
            }
            CellIndices idx = map.cellIndices;
            entries = WallPressureTopology.Build(w, h,
                i => IsBarrier(CellAt(i, w).GetEdifice(map)),
                i => CellAt(i, w).GetRoom(map)?.ID ?? -1,
                i => band[i]);
            damage.Clear();
        }

        private static IntVec3 CellAt(int i, int w)
        {
            return new IntVec3(i % w, 0, i / w);
        }

        private static float PressureOf(Room room, MapComponent_Flooding flooding)
        {
            if (room == null)
            {
                return 1f; // 沒有房間的格子（深淵、地圖外側）就是外海
            }
            return flooding.IsOpenWater(room) ? 1f : flooding.LevelOf(room);
        }

        private void Apply(WallPressureTopology.Entry e, MapComponent_Flooding flooding, DepthTier tier)
        {
            int w = map.Size.x;
            IntVec3 cell = CellAt(e.cell, w);
            Building b = cell.GetEdifice(map);
            if (b == null || !b.def.useHitPoints || b.def.building?.isNaturalRock == true || (b is Building_Door door && door.Open))
            {
                return;
            }
            float pA = PressureOf(CellAt(e.seedA, w).GetRoom(map), flooding);
            float pB = PressureOf(CellAt(e.seedB, w).GetRoom(map), flooding);
            float load = WallPressureTopology.Load(pA, pB, tier, e.thickness);
            if (load <= 0.001f)
            {
                return;
            }
            LastLoadedCount++;
            float dmg = WallPressureTopology.DamagePerUpdate(load, WallPressureTopology.Capacity(HullPressure.RatingOf(b.def)));
            if (dmg <= 0f)
            {
                return;
            }
            LastOverloadedCount++;
            damage.TryGetValue(e.cell, out float acc);
            acc += dmg;
            int whole = Mathf.FloorToInt(acc);
            if (whole > 0)
            {
                acc -= whole;
                b.TakeDamage(new DamageInfo(DamageDefOf.Deterioration, whole));
                if (Find.TickManager.TicksGame - lastMessageTick > GenDate.TicksPerDay && b.Faction == Faction.OfPlayer)
                {
                    lastMessageTick = Find.TickManager.TicksGame;
                    Messages.Message("DepthRim.Pressure.Crushing".Translate(b.LabelCap), b, MessageTypeDefOf.NegativeEvent);
                }
            }
            damage[e.cell] = acc;
        }

        /// <summary>某一格牆的快取資料（給驗收測試）。</summary>
        public bool TryGetEntry(IntVec3 c, out WallPressureTopology.Entry entry)
        {
            int i = c.z * map.Size.x + c.x;
            for (int k = 0; k < entries.Count; k++)
            {
                if (entries[k].cell == i)
                {
                    entry = entries[k];
                    return true;
                }
            }
            entry = default;
            return false;
        }
    }

    /// <summary>牆、門蓋好或拆掉時，原版會把附近的區域標髒；牆體水壓的拓樸快取跟著標髒（下一次更新才重建）。</summary>
    [HarmonyPatch(typeof(RegionDirtyer), "Notify_WalkabilityChanged")]
    public static class Patch_RegionDirtyer_Walkability
    {
        [HarmonyPostfix]
        public static void Postfix(Map ___map)
        {
            ___map?.GetComponent<MapComponent_WallPressure>()?.MarkDirty();
        }
    }

    [HarmonyPatch(typeof(RegionDirtyer), "DirtyRegionForThing")]
    public static class Patch_RegionDirtyer_Thing
    {
        [HarmonyPostfix]
        public static void Postfix(Map ___map)
        {
            ___map?.GetComponent<MapComponent_WallPressure>()?.MarkDirty();
        }
    }

    [HarmonyPatch(typeof(RegionDirtyer), "SetAllDirty")]
    public static class Patch_RegionDirtyer_All
    {
        [HarmonyPostfix]
        public static void Postfix(Map ___map)
        {
            ___map?.GetComponent<MapComponent_WallPressure>()?.MarkDirty();
        }
    }
}
