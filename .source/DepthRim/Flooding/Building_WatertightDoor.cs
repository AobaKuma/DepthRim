using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水密門：沒有水中呼吸能力、而且自己目前站在乾處的角色，不會穿過這扇門走進淹水的一側。
    ///
    /// <para>原版的區域可達性（<c>Region.Allows</c> → <c>CanPhysicallyPass</c>）與路徑成本（<c>PathUtility</c>）都問
    /// <see cref="Building_Door.PawnCanOpen"/>，所以只要在這裡多一個條件，閒逛、搬運、工作都不會把殖民者帶進水裡溺死。
    /// 規則本身是純函式 <see cref="FloodingMath.WatertightDoorAllows"/>。</para>
    ///
    /// <para>例外：徵召中的角色照玩家的命令走；已經泡在水裡的角色可以通過（不然氣瓶用完的人會被關在外面）；
    /// 非血肉（機械體）不需要呼吸。</para>
    ///
    /// <para>門的狀態會隨水位改變，<see cref="MapComponent_Flooding"/> 每次更新後呼叫 <see cref="Notify_WaterUpdated"/>；
    /// 「淹水側」狀態一變就清掉可達性快取，和原版門開關時的做法一樣。關著的水密門受損時滲水量只有一般牆的 <see cref="LeakFactor"/>。</para>
    /// </summary>
    public class Building_WatertightDoor : Building_Door
    {
        /// <summary>受損時的滲水倍率（一般牆與門是 1）。實際值由 Def 的 <see cref="LeakResistanceExtension"/> 決定，這裡是預期值（單元測試對照用）。</summary>
        public const float LeakFactor = 0.25f;

        private bool floodedSide;

        /// <summary>最近一次水位更新時，門的任一側是否深到會淹過站著的人。</summary>
        public bool FloodedSide => floodedSide;

        /// <summary>門四周（上下左右）最高的水位；開放水域 = 1。</summary>
        public float MaxSideLevel()
        {
            MapComponent_Flooding flooding = Map?.Flooding();
            if (flooding == null || !flooding.Active)
            {
                return 0f;
            }
            float max = 0f;
            for (int i = 0; i < 4; i++)
            {
                IntVec3 c = Position + GenAdj.CardinalDirections[i];
                if (!c.InBounds(Map) || c.Filled(Map))
                {
                    continue;
                }
                // 直接看房間（不是每格的旗標）：旗標要等下一次 250-tick 更新，剛蓋好或剛打通時會是舊的
                Room room = c.GetRoom(Map);
                float level = room == null ? flooding.LevelAt(c) : room.IsDoorway ? 0f : flooding.IsOpenWater(room) ? 1f : flooding.LevelOf(room);
                if (level > max)
                {
                    max = level;
                }
            }
            return max;
        }

        /// <summary>水位更新後呼叫：淹水側狀態改變時清掉可達性快取。回傳狀態是否改變。</summary>
        public bool Notify_WaterUpdated()
        {
            bool now = MaxSideLevel() >= FloodingPawnEffects.SubmergedLevelStanding;
            if (now == floodedSide)
            {
                return false;
            }
            floodedSide = now;
            Map.reachability.ClearCache();
            return true;
        }

        /// <summary>蓋好（或讀檔）時立刻判斷一次，不用等下一次 250-tick 水位更新——那段空檔殖民者會直接走出去。</summary>
        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            if (!respawningAfterLoad && map.Flooding() is MapComponent_Flooding flooding && flooding.Active)
            {
                Notify_WaterUpdated();
            }
        }

        public override bool PawnCanOpen(Pawn p)
        {
            if (!base.PawnCanOpen(p))
            {
                return false;
            }
            if (!floodedSide || p.Map != Map)
            {
                return true;
            }
            MapComponent_Flooding flooding = Map.Flooding();
            bool pawnInWater = flooding != null && FloodingPawnEffects.IsSubmerged(p, flooding.LevelAt(p.Position));
            return FloodingMath.WatertightDoorAllows(p.Drafted, FloodingPawnEffects.CanBreatheUnderwater(p), pawnInWater, MaxSideLevel());
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string line = (floodedSide ? "DepthRim.WatertightDoor.Sealed" : "DepthRim.WatertightDoor.Open").Translate(MaxSideLevel().ToStringPercent());
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref floodedSide, "floodedSide", false);
        }
    }
}
