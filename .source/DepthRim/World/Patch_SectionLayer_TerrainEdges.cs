using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;

namespace DepthRim
{
    /// <summary>
    /// 讓深淵的崖壁邊緣（二期 P2-10）也畫在冰洋層。
    ///
    /// <para>原版 <see cref="SectionLayer_TerrainEdges.Regenerate"/> 一開頭就是
    /// <c>if (Map.Tile.Valid &amp;&amp; !Map.Tile.LayerDef.isSpace) return;</c>——崖壁只給太空地圖用。
    /// 冰洋層不是太空（不能設 <c>isSpace</c>，否則會變成真空、DMSE 也會把它當成軌道），
    /// 所以把那一個 <c>isSpace</c> 欄位讀取換成 <see cref="DrawsTerrainEdges"/>：太空照舊，冰洋層也算。</para>
    ///
    /// <para>只改這一個判斷，崖壁的形狀、貼圖、材質全部沿用原版與 Odyssey（海床地形的 <c>spaceEdgeGraphicData</c>）。</para>
    /// </summary>
    [HarmonyPatch(typeof(SectionLayer_TerrainEdges), nameof(SectionLayer_TerrainEdges.Regenerate))]
    public static class Patch_SectionLayer_TerrainEdges
    {
        private static readonly FieldInfo IsSpaceField = AccessTools.Field(typeof(PlanetLayerDef), nameof(PlanetLayerDef.isSpace));

        private static readonly MethodInfo Replacement = AccessTools.Method(typeof(Patch_SectionLayer_TerrainEdges), nameof(DrawsTerrainEdges));

        /// <summary>這個圖層的地圖要不要畫崖壁：原版的太空圖層，加上冰洋層。</summary>
        public static bool DrawsTerrainEdges(PlanetLayerDef def)
        {
            return def != null && (def.isSpace || def == DepthRimDefOf.DepthRim_Ocean);
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            bool replaced = false;
            foreach (CodeInstruction instruction in instructions)
            {
                if (!replaced && instruction.LoadsField(IsSpaceField))
                {
                    // 堆疊上原本是 PlanetLayerDef，ldfld isSpace 換成 call DrawsTerrainEdges(def)，結果一樣是 bool
                    replaced = true;
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, Replacement).WithLabels(instruction.labels);
                    continue;
                }
                yield return instruction;
            }
            if (!replaced)
            {
                Verse.Log.Error("[DepthRim] SectionLayer_TerrainEdges.Regenerate no longer reads PlanetLayerDef.isSpace; abyss cliff edges will not be drawn on the ice ocean.");
            }
        }
    }
}
