using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 一格變成深淵時，上面的物品沉下去（銷毀）：深淵沒有地面，什麼都放不住。
    ///
    /// <para>為什麼需要：海洋地圖的生成順序是 <c>RockChunks</c> → <c>RocksFromGrid</c> → <c>DepthRim_AbyssCarve</c>，
    /// 石塊先散在海床上，之後才挖出深淵，於是深淵上留著石塊。之後在深淵上鋪船體平台，石塊會原封不動地留在平台上，
    /// 而且擋住站立（2026-11-09 驗收 H1 發現）。</para>
    ///
    /// <para>掛在 <see cref="TerrainGrid.SetTerrain"/> 上，所以挖洞、拆平台（DMSE 依 voidTerrain 換回深淵）、
    /// 以後任何把格子變成深淵的方式都適用。Pawn 與建築不動（它們有自己的規則）；植物、石塊、碎屑、一般物品都沉下去。</para>
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    public static class Patch_AbyssSwallowsItems
    {
        private static readonly List<Thing> tmp = new List<Thing>();

        [HarmonyPostfix]
        public static void Postfix(IntVec3 c, TerrainDef newTerr, Map ___map)
        {
            if (newTerr != DepthRimDefOf.DepthRim_Abyss || ___map == null || !c.InBounds(___map))
            {
                return;
            }
            SwallowAt(c, ___map);
        }

        /// <summary>把這格上會沉下去的東西銷毀，回傳銷毀了幾個。</summary>
        public static int SwallowAt(IntVec3 c, Map map)
        {
            tmp.Clear();
            tmp.AddRange(c.GetThingList(map));
            int swallowed = 0;
            for (int i = 0; i < tmp.Count; i++)
            {
                Thing t = tmp[i];
                if (t.Destroyed || !Sinks(t))
                {
                    continue;
                }
                t.Destroy();
                swallowed++;
            }
            tmp.Clear();
            return swallowed;
        }

        /// <summary>會沉進深淵的東西：物品（含石塊）、植物、碎屑。Pawn 與建築不算。</summary>
        public static bool Sinks(Thing t)
        {
            if (t is Pawn || !t.def.destroyable)
            {
                return false;
            }
            ThingCategory cat = t.def.category;
            return cat == ThingCategory.Item || cat == ThingCategory.Plant || cat == ThingCategory.Filth;
        }
    }
}
