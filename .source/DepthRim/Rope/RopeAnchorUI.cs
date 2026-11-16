using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 錨點的放置限制（§1）：只能裝在船體平台或海上平台上（耐壓甲板也帶船體平台承載力）。
    /// 放置時畫出繩長半徑，能扣繩的格子（牆擋不到的）以淡綠色標出；只在滑鼠換格時重算（§7.3）。
    /// </summary>
    public class PlaceWorker_RopeAnchor : PlaceWorker
    {
        private static readonly RopeCoverage preview = new RopeCoverage();

        private static readonly List<IntVec3> previewCells = new List<IntVec3>();

        private static IntVec3 previewAt = IntVec3.Invalid;

        private static Map previewMap;

        private static int previewKey;

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            if (!loc.InBounds(map))
            {
                return false;
            }
            TerrainDef terrain = map.terrainGrid.TerrainAt(loc);
            if (terrain == DepthRimDefOf.DepthRim_OceanPlatform || terrain != null && terrain.affordances != null && terrain.affordances.Contains(DepthRimDefOf.DepthRim_HullPlatform))
            {
                return true;
            }
            return "DepthRim.Rope.MustBeOnDeck".Translate();
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map map = Find.CurrentMap;
            if (map == null || !center.InBounds(map))
            {
                return;
            }
            GenDraw.DrawRadiusRing(center, RopeTetherMath.RopeLength);
            int key = RopeCoverage.KeyOf(map);
            if (center != previewAt || map != previewMap || key != previewKey)
            {
                previewAt = center;
                previewMap = map;
                previewKey = key;
                preview.Rebuild(map, center);
                previewCells.Clear();
                foreach (IntVec3 c in CellRect.CenteredOn(center, RopeTetherMath.Reach))
                {
                    if (c.InBounds(map) && preview.Covers(c) && c.Standable(map))
                    {
                        previewCells.Add(c);
                    }
                }
            }
            GenDraw.DrawFieldEdges(previewCells, new Color(0.45f, 0.9f, 0.5f, 0.6f));
        }
    }

    /// <summary>
    /// 警報（§5）：有落海風險時艙外有我方的人沒扣繩。只讀各地圖掃描時收集好的名單（§7.7）。
    /// 航行中是紅色（一定會失蹤），惡劣海況是黃色。
    /// </summary>
    public class Alert_UntetheredOutside : Alert
    {
        private readonly List<Pawn> culprits = new List<Pawn>();

        private bool anyVoyage;

        public Alert_UntetheredOutside()
        {
            defaultLabel = "DepthRim.Alert.Untethered".Translate();
            defaultPriority = AlertPriority.High;
        }

        private List<Pawn> Culprits
        {
            get
            {
                culprits.Clear();
                anyVoyage = false;
                List<Map> maps = Find.Maps;
                for (int m = 0; m < maps.Count; m++)
                {
                    MapComponent_RopeTethers comp = MapComponent_RopeTethers.For(maps[m]);
                    if (comp == null || comp.Hazard == HazardKind.None)
                    {
                        continue;
                    }
                    List<Pawn> list = comp.UntetheredOutside;
                    for (int i = 0; i < list.Count; i++)
                    {
                        Pawn p = list[i];
                        // 名單最多晚一次掃描：已經走回艙內、扣上或離開的人不算
                        if (p.Spawned && p.Map == maps[m] && !RopeTether.IsTethered(p) && VoyageOverboard.IsOutside(p))
                        {
                            culprits.Add(p);
                            anyVoyage |= comp.Hazard == HazardKind.Voyage;
                        }
                    }
                }
                return culprits;
            }
        }

        protected override Color BGColor => anyVoyage ? new Color(0.6f, 0.1f, 0.1f, 0.35f) : base.BGColor;

        public override TaggedString GetExplanation()
        {
            List<Pawn> pawns = Culprits;
            List<string> names = new List<string>();
            for (int i = 0; i < pawns.Count; i++)
            {
                names.Add(pawns[i].LabelShortCap);
            }
            return (anyVoyage ? "DepthRim.Alert.UntetheredVoyageDesc" : "DepthRim.Alert.UntetheredSeaDesc").Translate(names.ToLineList("  - "));
        }

        public override AlertReport GetReport()
        {
            List<Pawn> pawns = Culprits;
            defaultPriority = anyVoyage ? AlertPriority.Critical : AlertPriority.High;
            return AlertReport.CulpritsAre(pawns);
        }
    }

    /// <summary>扣著繩的 Pawn 多一個「解開安全繩」指令（§5）：解開並暫時不再自動扣這個錨點。</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_PawnRopeGizmo
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo g in __result)
            {
                yield return g;
            }
            Building_RopeAnchor anchor = RopeTether.AnchorOf(__instance);
            if (anchor == null || __instance.Faction != Faction.OfPlayer && !__instance.IsPrisonerOfColony)
            {
                yield break;
            }
            Pawn pawn = __instance;
            yield return new Command_Action
            {
                defaultLabel = "DepthRim.Rope.Untie".Translate(),
                defaultDesc = "DepthRim.Rope.UntieDesc".Translate(anchor.Position.ToString()),
                icon = RopeAssets.UntieIcon,
                action = () =>
                {
                    anchor.StartCooldown(pawn);
                    MapComponent_RopeTethers.For(pawn.Map)?.Untie(pawn);
                },
            };
        }
    }

    /// <summary>扣著繩的 Pawn 在說明欄多一行（§5）。</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetInspectString))]
    public static class Patch_PawnRopeInspect
    {
        public static void Postfix(Pawn __instance, ref string __result)
        {
            Building_RopeAnchor anchor = RopeTether.AnchorOf(__instance);
            if (anchor == null)
            {
                return;
            }
            string line = "DepthRim.Rope.PawnTethered".Translate(anchor.Position.ToString());
            __result = __result.NullOrEmpty() ? line : __result + "\n" + line;
        }
    }
}
