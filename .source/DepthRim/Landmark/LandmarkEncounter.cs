using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>一個選項處理完的結果（給對話與測試看）。</summary>
    public class LandmarkResolution
    {
        public LandmarkResult result;

        public Pawn actor;

        public float chance;

        public string text;

        public List<Thing> cargo = new List<Thing>();
    }

    /// <summary>
    /// 遭遇型地標的處理：抵達時暫停遊戲、跳出對話（原版 <see cref="Dialog_NodeTree"/>，不能用 Esc 關掉）。
    /// 每個選項由乘員裡該技能最高的人擔任主角，成功率見 <see cref="LandmarkMath.Chance"/>；「離開」不擲骰。
    /// 選了任何非「離開」的選項，地標就消失。
    /// </summary>
    public static class LandmarkEncounter
    {
        /// <summary>擔任主角的人：活著、沒倒地、是人形、這項技能沒有被完全停用，挑技能最高的。</summary>
        public static Pawn ChooseActor(IEnumerable<Pawn> crew, SkillDef skill)
        {
            return crew.Where(p => p != null && !p.Dead && !p.Downed && p.RaceProps.Humanlike && p.skills != null
                    && !(p.skills.GetSkill(skill)?.TotallyDisabled ?? true))
                .OrderByDescending(p => p.skills.GetSkill(skill).Level)
                .FirstOrDefault();
        }

        public static float Capability(Pawn pawn)
        {
            PawnCapacitiesHandler caps = pawn?.health?.capacities;
            if (caps == null)
            {
                return 1f;
            }
            return Mathf.Min(1f, caps.GetLevel(PawnCapacityDefOf.Consciousness)) * Mathf.Min(1f, caps.GetLevel(PawnCapacityDefOf.Manipulation));
        }

        public static float ChanceFor(LandmarkOption option, Pawn actor)
        {
            if (actor == null)
            {
                return 0f;
            }
            return LandmarkMath.Chance(option.baseChance, actor.skills.GetSkill(option.skill).Level, option.difficulty, Capability(actor));
        }

        /// <summary>
        /// 擲骰並套用結果。<paramref name="roll"/> 給值時用它（測試用），否則擲 [0,1)。
        /// 收穫在 <see cref="LandmarkResolution.cargo"/>，由呼叫端放好。
        /// </summary>
        public static LandmarkResolution Resolve(OceanLandmarkDef def, LandmarkOption option, List<Pawn> crew, Map homeMap, float? roll = null)
        {
            LandmarkResolution res = new LandmarkResolution();
            Pawn actor = ChooseActor(crew, option.skill);
            res.actor = actor;
            if (actor == null)
            {
                res.result = LandmarkResult.Failure;
                res.text = "DepthRim.Landmark.NoActor".Translate(option.skill.label);
                return res;
            }
            res.chance = ChanceFor(option, actor);
            res.result = LandmarkMath.Resolve(res.chance, roll ?? Rand.Value);
            LandmarkContext ctx = new LandmarkContext { def = def, crew = crew, actor = actor, homeMap = homeMap, cargo = res.cargo };
            StringBuilder sb = new StringBuilder();
            string flavor = option.TextFor(res.result);
            if (!flavor.NullOrEmpty())
            {
                sb.AppendLine(flavor.Formatted(actor.Named("PAWN")).Resolve());
            }
            sb.AppendLine();
            sb.AppendLine(("DepthRim.Landmark.Result." + res.result).Translate(actor.LabelShortCap, option.skill.label, res.chance.ToStringPercent()));
            foreach (LandmarkOutcome outcome in option.OutcomesFor(res.result))
            {
                string line = outcome.Apply(ctx);
                if (!line.NullOrEmpty())
                {
                    sb.AppendLine("  - " + line);
                }
            }
            actor.skills.Learn(option.skill, LandmarkMath.Xp(option.xp, res.result), direct: true);
            res.text = sb.ToString().TrimEndNewlines();
            return res;
        }

        /// <summary>
        /// 開對話。<paramref name="deliver"/> 放好收穫；<paramref name="finished"/> 在對話結束時呼叫（不論選了什麼）。
        /// </summary>
        public static void Open(WorldObject_OceanLandmark landmark, List<Pawn> crew, Map homeMap, Action<List<Thing>> deliver, Action finished)
        {
            OceanLandmarkDef def = landmark.landmarkDef;
            if (def == null)
            {
                finished?.Invoke();
                return;
            }
            StringBuilder intro = new StringBuilder(def.description);
            intro.AppendLine();
            intro.AppendLine();
            intro.Append("DepthRim.Landmark.Crew".Translate(string.Join(", ", crew.Select(p => p.LabelShortCap))));
            DiaNode root = new DiaNode(intro.ToString());
            foreach (LandmarkOption option in def.options)
            {
                Pawn actor = ChooseActor(crew, option.skill);
                string label = actor == null
                    ? option.label
                    : "DepthRim.Landmark.OptionLabel".Translate(option.label, actor.LabelShortCap, option.skill.label, actor.skills.GetSkill(option.skill).Level,
                        ChanceFor(option, actor).ToStringPercent()).ToString();
                DiaOption dia = new DiaOption(label);
                if (actor == null)
                {
                    dia.Disable("DepthRim.Landmark.NoActor".Translate(option.skill.label));
                }
                else
                {
                    LandmarkOption chosen = option;
                    dia.action = () =>
                    {
                        LandmarkResolution res = Resolve(def, chosen, crew, homeMap);
                        deliver?.Invoke(res.cargo);
                        if (!landmark.Destroyed)
                        {
                            landmark.Destroy();
                        }
                        DiaNode result = new DiaNode(res.text);
                        result.options.Add(new DiaOption("OK".Translate()) { resolveTree = true, action = finished });
                        Find.WindowStack.Add(MakeDialog(result, def));
                    };
                    dia.resolveTree = true;
                }
                root.options.Add(dia);
            }
            root.options.Add(new DiaOption("DepthRim.Landmark.Leave".Translate()) { resolveTree = true, action = finished });
            Find.WindowStack.Add(MakeDialog(root, def));
        }

        /// <summary>最後一個打開的對話節點（驗收測試用來代替點擊）。</summary>
        public static DiaNode lastShown;

        private static Dialog_NodeTree MakeDialog(DiaNode node, OceanLandmarkDef def)
        {
            lastShown = node;
            Dialog_NodeTree dialog = new Dialog_NodeTree(node, delayInteractivity: true, radioMode: false, title: def.LabelCap);
            dialog.forcePause = true;
            dialog.closeOnCancel = false;
            dialog.closeOnClickedOutside = false;
            dialog.closeOnAccept = false;
            return dialog;
        }

        // ── 潛艇本身處理（乘員 = 全船）────────────────────────────────────────

        /// <summary>潛艇旁（同一格或相鄰格）的遭遇型地標。</summary>
        public static WorldObject_OceanLandmark NearSubmarine(WorldObject_Submarine sub)
        {
            WorldObject_OceanLandmark here = OceanLandmarkUtility.EncounterAt(sub.Tile);
            if (here != null)
            {
                return here;
            }
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(sub.Tile, neighbors);
            foreach (PlanetTile n in neighbors)
            {
                WorldObject_OceanLandmark l = OceanLandmarkUtility.EncounterAt(n);
                if (l != null)
                {
                    return l;
                }
            }
            return null;
        }

        public static List<Pawn> SubmarineCrew(Map map) =>
            map?.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList() ?? new List<Pawn>();

        public static Command SubmarineGizmo(WorldObject_Submarine sub)
        {
            WorldObject_OceanLandmark landmark = NearSubmarine(sub);
            if (landmark == null)
            {
                return null;
            }
            Command_Action cmd = new Command_Action
            {
                defaultLabel = "DepthRim.Landmark.Investigate".Translate(),
                defaultDesc = "DepthRim.Landmark.InvestigateDesc".Translate(landmark.Label),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Explore", reportFailure: false) ?? BaseContent.BadTex,
                action = () => InvestigateFromSubmarine(sub, landmark),
            };
            if (sub.InFlight)
            {
                cmd.Disable("DepthRim.Anchor.InFlight".Translate());
            }
            else if (SubmarineCrew(sub.Map).Count == 0)
            {
                cmd.Disable("DepthRim.Boarding.NoCrew".Translate());
            }
            return cmd;
        }

        public static void InvestigateFromSubmarine(WorldObject_Submarine sub, WorldObject_OceanLandmark landmark)
        {
            Map map = sub.Map;
            Open(landmark, SubmarineCrew(map), map, cargo => DeliverToMap(map, SubmarineNavigation.HelmOf(sub), cargo), null);
        }

        /// <summary>把收穫放在地圖上（艦橋旁邊；沒有艦橋就找殖民者附近）。</summary>
        public static void DeliverToMap(Map map, Thing near, List<Thing> cargo)
        {
            if (map == null)
            {
                return;
            }
            IntVec3 cell = near != null && near.Spawned ? near.Position
                : map.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.Position ?? map.Center;
            foreach (Thing t in cargo)
            {
                if (t is Pawn p)
                {
                    IntVec3 at = CellFinder.StandableCellNear(cell, map, 6f);
                    GenSpawn.Spawn(p, at.IsValid ? at : cell, map);
                }
                else
                {
                    GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near);
                }
            }
        }
    }
}
