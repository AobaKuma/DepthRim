using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 下錨在海床上的船體平台底下原本是什麼地形。船塢只能蓋在深淵上，但下錨（DMSE 降落）可以停在海床上：
    /// 降落時平台蓋過海床，這裡記下被蓋掉的地形；之後潛艇離開（DMSE 把船體範圍設成虛空 = 深淵）或平台被拆掉
    /// （拆完露出圖層的虛空：DMSE 讀海洋層的 <c>TransferEnvironmentExtension.voidTerrain</c>，也就是深淵），就把海床還回去，不會在海床上留下一個深淵洞。
    /// <para>地表圖層（海面地點、原版海岸地圖）上 DMSE 鋪的是太空：由 <see cref="Patch_SeaSurfaceVoid"/> 用 <see cref="TryTake"/> 取出記錄，
    /// 把原版海水（深／淺）或開放海面鋪回去。</para>
    /// </summary>
    public class MapComponent_SeabedMemory : MapComponent
    {
        private Dictionary<IntVec3, TerrainDef> under = new Dictionary<IntVec3, TerrainDef>();

        private List<IntVec3> tmpKeys;

        private List<TerrainDef> tmpValues;

        public MapComponent_SeabedMemory(Map map) : base(map)
        {
        }

        public int Count => under.Count;

        public bool TryGetUnder(IntVec3 c, out TerrainDef terrain)
        {
            return under.TryGetValue(c, out terrain);
        }

        /// <summary>平台蓋到這格上：記下原本的地形（深淵、平台本身不記）。</summary>
        public void Notify_Covered(IntVec3 c, TerrainDef old)
        {
            if (old == null || old.dontRender || old == DepthRimDefOf.DepthRim_Abyss || IsPlatform(old) || under.ContainsKey(c))
            {
                return;
            }
            under[c] = old;
        }

        /// <summary>這格變回深淵：有記錄就換回原本的海床（回傳是否還原了）。</summary>
        public bool TryRestore(IntVec3 c)
        {
            if (!under.TryGetValue(c, out TerrainDef old))
            {
                return false;
            }
            under.Remove(c);
            map.terrainGrid.SetTerrain(c, old);
            return true;
        }

        /// <summary>取出並刪除這格的記錄（不設地形；由呼叫端決定怎麼鋪回去）。</summary>
        public bool TryTake(IntVec3 c, out TerrainDef terrain)
        {
            if (!under.TryGetValue(c, out terrain))
            {
                return false;
            }
            under.Remove(c);
            return true;
        }

        /// <summary>這格不再是平台（例如改鋪別的地形）：記錄作廢。</summary>
        public void Forget(IntVec3 c)
        {
            under.Remove(c);
        }

        public static bool IsPlatform(TerrainDef t)
        {
            return t != null && t.HasModExtension<HullPlatformPropsExtension>();
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref under, "under", LookMode.Value, LookMode.Def, ref tmpKeys, ref tmpValues);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                under ??= new Dictionary<IntVec3, TerrainDef>();
                List<IntVec3> bad = new List<IntVec3>();
                foreach (KeyValuePair<IntVec3, TerrainDef> kv in under)
                {
                    if (kv.Value == null) { bad.Add(kv.Key); }
                }
                foreach (IntVec3 c in bad) { under.Remove(c); }
            }
        }
    }

    /// <summary>
    /// 地形改變時通知 <see cref="MapComponent_SeabedMemory"/>：平台蓋上去就記下舊地形，變回深淵就還原海床。
    /// 還原時再呼叫一次 SetTerrain（新地形不是深淵，不會遞迴）。
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.SetTerrain))]
    public static class Patch_SeabedMemory
    {
        [HarmonyPrefix]
        public static void Prefix(IntVec3 c, Map ___map, out TerrainDef __state)
        {
            __state = ___map != null && c.InBounds(___map) ? ___map.terrainGrid.TerrainAt(c) : null;
        }

        [HarmonyPostfix]
        [HarmonyAfter("DepthRim.Core")]
        public static void Postfix(IntVec3 c, TerrainDef newTerr, Map ___map, TerrainDef __state)
        {
            if (___map == null || newTerr == null || newTerr == __state)
            {
                return;
            }
            // 地圖生成時 SetTerrain 會被呼叫幾萬次：和平台、深淵無關的變動直接離開，不去找元件
            bool platformInvolved = MapComponent_SeabedMemory.IsPlatform(newTerr) || MapComponent_SeabedMemory.IsPlatform(__state);
            if (!platformInvolved && newTerr != DepthRimDefOf.DepthRim_Abyss)
            {
                return;
            }
            // 太空由 Patch_SeaSurfaceVoid 接手（取出記錄、鋪回原本的水域）；這裡先 Forget 會讓它找不到記錄
            if (newTerr == TerrainDefOf.Space && SeaLevels.ReplacesSpace(___map))
            {
                return;
            }
            MapComponent_SeabedMemory memory = ___map.GetComponent<MapComponent_SeabedMemory>();
            if (memory == null)
            {
                return;
            }
            if (MapComponent_SeabedMemory.IsPlatform(newTerr))
            {
                memory.Notify_Covered(c, __state);
            }
            else if (newTerr == DepthRimDefOf.DepthRim_Abyss)
            {
                memory.TryRestore(c);
            }
            else
            {
                memory.Forget(c);
            }
        }
    }
}
