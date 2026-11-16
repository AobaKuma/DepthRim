using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 一個錨點的可扣繩格子遮罩（§7.3）：以錨點為中心 19×19，一格 =「在繩長內、拉過去不會被牆擋住」。
    /// 快取鍵是人造建築的狀態雜湊（<see cref="ListerThings.StateHashOfGroup"/>）加上錨點位置；
    /// 牆蓋好、被打掉時鍵會變，遮罩自動重算。天然岩石不在人造建築群組裡，所以另外每 <see cref="ForcedRebuildTicks"/> 強制重算。
    /// </summary>
    public class RopeCoverage
    {
        public const int ForcedRebuildTicks = 2500;

        private readonly bool[] mask = new bool[RopeTetherMath.MaskSize];

        private int key = int.MinValue;

        private IntVec3 origin = IntVec3.Invalid;

        private Map map;

        private int builtTick = -99999;

        /// <summary>重算過幾次（給測試看快取有沒有生效）。</summary>
        public int Rebuilds { get; private set; }

        public int CoveredCells { get; private set; }

        /// <summary>目前的快取鍵（建築狀態雜湊）。</summary>
        public static int KeyOf(Map map)
        {
            return map.listerThings.StateHashOfGroup(ThingRequestGroup.BuildingArtificial);
        }

        /// <summary>這一格擋不擋繩子：地圖外、或有不是門的「全滿」建築（牆）。門不管開關都讓繩子通過（§2.2）。</summary>
        public static bool BlocksRope(IntVec3 c, Map map)
        {
            if (!c.InBounds(map))
            {
                return true;
            }
            Building edifice = c.GetEdifice(map);
            return edifice != null && edifice.def.Fillage == FillCategory.Full && !(edifice is Building_Door);
        }

        /// <summary>快取失效時重算；回傳這次有沒有重算。</summary>
        public bool EnsureFresh(Map map, IntVec3 at)
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            int k = KeyOf(map);
            if (k == key && at == origin && map == this.map && now - builtTick < ForcedRebuildTicks)
            {
                return false;
            }
            Rebuild(map, at);
            key = k;
            builtTick = now;
            return true;
        }

        public void Rebuild(Map map, IntVec3 at)
        {
            this.map = map;
            origin = at;
            CoveredCells = RopeTetherMath.BuildMask(mask, (dx, dz) => BlocksRope(new IntVec3(at.x + dx, 0, at.z + dz), map));
            Rebuilds++;
        }

        /// <summary>這一格能扣上這個錨點的繩子（呼叫前先 <see cref="EnsureFresh"/>）。</summary>
        public bool Covers(IntVec3 c)
        {
            int i = RopeTetherMath.MaskIndex(c.x - origin.x, c.z - origin.z);
            return i >= 0 && mask[i];
        }

        /// <summary>讓下一次 <see cref="EnsureFresh"/> 一定重算（錨點剛生成、搬動）。</summary>
        public void Invalidate()
        {
            key = int.MinValue;
        }

        public int Key => key;
    }
}
