using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 安全繩錨點（<c>DR_RopeAnchor</c>）：甲板上的固定點，兩個扣環各一條繩子。
    /// 有落海風險時（<see cref="OverboardHazard"/>）範圍內、在艙外的我方 Pawn 自動扣上（<see cref="MapComponent_RopeTethers"/>），
    /// 扣著繩的人不會被沖走。自己不 tick，扣繩、解繩、畫繩子都在地圖元件裡做。
    /// </summary>
    public class Building_RopeAnchor : Building
    {
        /// <summary>兩個扣環上的人（null = 空著）。用固定的扣環位置，別人走掉時繩子不會換邊。</summary>
        private Pawn[] slots = new Pawn[RopeTetherMath.MaxTethers];

        /// <summary>上次驗證時那個人站的格子與遮罩的快取鍵（§7.5：都沒變就不重驗）。</summary>
        private readonly IntVec3[] lastCell = { IntVec3.Invalid, IntVec3.Invalid };

        private readonly int[] lastKey = { int.MinValue, int.MinValue };

        public bool autoTether = true;

        /// <summary>手動解開的人：到這個 tick 之前不再自動扣這個錨點。</summary>
        private Dictionary<Pawn, int> cooldownUntil = new Dictionary<Pawn, int>();

        private List<Pawn> tmpCooldownPawns;

        private List<int> tmpCooldownTicks;

        public readonly RopeCoverage coverage = new RopeCoverage();

        /// <summary>解開的冷卻時間（ticks）。</summary>
        public const int UntieCooldownTicks = 2500;

        /// <summary>兩個扣環相對錨點中心的位置（畫面座標，左右各一）。</summary>
        public static readonly Vector3[] ClaspOffsets = { new Vector3(-0.22f, 0f, 0.18f), new Vector3(0.22f, 0f, 0.18f) };

        public Pawn SlotPawn(int i) => slots[i];

        public int TetheredCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] != null)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        public bool HasFreeSlot => FreeSlot() >= 0;

        public int FreeSlot()
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null)
                {
                    return i;
                }
            }
            return -1;
        }

        public int SlotOf(Pawn pawn)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == pawn)
                {
                    return i;
                }
            }
            return -1;
        }

        public bool OnCooldown(Pawn pawn)
        {
            return cooldownUntil.TryGetValue(pawn, out int until) && Find.TickManager.TicksGame < until;
        }

        public void StartCooldown(Pawn pawn)
        {
            cooldownUntil[pawn] = Find.TickManager.TicksGame + UntieCooldownTicks;
        }

        /// <summary>扣環 <paramref name="slot"/> 在畫面上的位置。</summary>
        public Vector3 ClaspPos(int slot)
        {
            return DrawPos + ClaspOffsets[slot];
        }

        // ── 由 MapComponent_RopeTethers 呼叫 ──

        internal void SetSlot(int slot, Pawn pawn)
        {
            slots[slot] = pawn;
            lastCell[slot] = IntVec3.Invalid;
            lastKey[slot] = int.MinValue;
        }

        /// <summary>位置與遮罩都沒變：不用重驗（§7.5）。</summary>
        internal bool Unchanged(int slot, IntVec3 cell)
        {
            return lastCell[slot] == cell && lastKey[slot] == coverage.Key;
        }

        internal void MarkValidated(int slot, IntVec3 cell)
        {
            lastCell[slot] = cell;
            lastKey[slot] = coverage.Key;
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            coverage.Invalidate();
            MapComponent_RopeTethers.For(map)?.Notify_AnchorSpawned(this);
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map oldMap = Map;
            // DMSE 搬船體時用 WillReplace 先移走、到新地圖再生成：只是安靜地解開，到了下一次掃描再扣（§2.5）。
            // 其他情況（被打壞、拆除、搬成可搬運物品）繩子斷開，那些人立刻做一次落海判定。
            MapComponent_RopeTethers.For(oldMap)?.Notify_AnchorRemoved(this, checkNow: mode != DestroyMode.WillReplace);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Pawn slot0 = slots[0], slot1 = slots[1];
            Scribe_References.Look(ref slot0, "tetheredLeft");
            Scribe_References.Look(ref slot1, "tetheredRight");
            Scribe_Values.Look(ref autoTether, "autoTether", true);
            Scribe_Collections.Look(ref cooldownUntil, "untieCooldown", LookMode.Reference, LookMode.Value, ref tmpCooldownPawns, ref tmpCooldownTicks);
            if (Scribe.mode == LoadSaveMode.LoadingVars || Scribe.mode == LoadSaveMode.ResolvingCrossRefs || Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                slots[0] = slot0;
                slots[1] = slot1;
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (cooldownUntil == null)
                {
                    cooldownUntil = new Dictionary<Pawn, int>();
                }
                cooldownUntil.RemoveAll(kv => kv.Key == null);
            }
        }

        public override string GetInspectString()
        {
            StringBuilder sb = new StringBuilder(base.GetInspectString());
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
            MapComponent_RopeTethers comp = Spawned ? MapComponent_RopeTethers.For(Map) : null;
            if (comp == null || comp.Hazard == HazardKind.None)
            {
                sb.Append("DepthRim.Rope.Standby".Translate());
            }
            else
            {
                List<string> names = new List<string>();
                for (int i = 0; i < slots.Length; i++)
                {
                    if (slots[i] != null)
                    {
                        names.Add(slots[i].LabelShortCap);
                    }
                }
                sb.Append("DepthRim.Rope.Tethered".Translate(TetheredCount, RopeTetherMath.MaxTethers, names.Count > 0 ? names.ToCommaList() : "-"));
            }
            if (!autoTether)
            {
                sb.AppendLine();
                sb.Append("DepthRim.Rope.AutoOff".Translate());
            }
            return sb.ToString();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            if (Faction != Faction.OfPlayer)
            {
                yield break;
            }
            Command_Action untie = new Command_Action
            {
                defaultLabel = "DepthRim.Rope.UntieAll".Translate(),
                defaultDesc = "DepthRim.Rope.UntieAllDesc".Translate(),
                icon = RopeAssets.UntieIcon,
                action = () =>
                {
                    MapComponent_RopeTethers comp = MapComponent_RopeTethers.For(Map);
                    for (int i = 0; i < slots.Length; i++)
                    {
                        Pawn p = slots[i];
                        if (p != null)
                        {
                            StartCooldown(p);
                            comp?.Untie(p);
                        }
                    }
                },
            };
            if (TetheredCount == 0)
            {
                untie.Disable("DepthRim.Rope.NobodyTethered".Translate());
            }
            yield return untie;
            yield return new Command_Toggle
            {
                defaultLabel = "DepthRim.Rope.AutoTether".Translate(),
                defaultDesc = "DepthRim.Rope.AutoTetherDesc".Translate(),
                icon = def.uiIcon,
                isActive = () => autoTether,
                toggleAction = () => autoTether = !autoTether,
            };
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            GenDraw.DrawRadiusRing(Position, RopeTetherMath.RopeLength);
        }
    }

    /// <summary>選了 Pawn、右鍵錨點：「扣在這個錨點」（§5）。滿了就擠掉離錨點最遠的那個人。</summary>
    public class FloatMenuOptionProvider_RopeAnchor : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;

        protected override bool Undrafted => true;

        protected override bool Multiselect => false;

        protected override bool RequiresManipulation => false;

        protected override bool MechanoidCanDo => true;

        protected override FloatMenuOption GetSingleOptionFor(Thing clickedThing, FloatMenuContext context)
        {
            if (!(clickedThing is Building_RopeAnchor anchor) || !anchor.Spawned)
            {
                return null;
            }
            Pawn pawn = context.FirstSelectedPawn;
            MapComponent_RopeTethers comp = MapComponent_RopeTethers.For(anchor.Map);
            if (pawn == null || comp == null || pawn.Map != anchor.Map || !RopeTether.CanEverTether(pawn))
            {
                return null;
            }
            string label = "DepthRim.Rope.TieHere".Translate(pawn.LabelShort);
            if (comp.AnchorOf(pawn) == anchor)
            {
                return new FloatMenuOption(label + ": " + "DepthRim.Rope.AlreadyHere".Translate(), null);
            }
            if (comp.Hazard == HazardKind.None)
            {
                return new FloatMenuOption(label + ": " + "DepthRim.Rope.NoHazard".Translate(), null);
            }
            string why = comp.CannotTieReason(pawn, anchor);
            if (why != null)
            {
                return new FloatMenuOption(label + ": " + why, null);
            }
            return new FloatMenuOption(label, () => comp.ForceTie(pawn, anchor));
        }
    }

    [StaticConstructorOnStartup]
    public static class RopeAssets
    {
        public static readonly Texture2D UntieIcon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel", false) ?? BaseContent.BadTex;

        /// <summary>繩子的顏色：深灰色（純色，§4.2）。</summary>
        public static readonly Color RopeColor = new Color(0.22f, 0.22f, 0.24f, 1f);

        /// <summary>繩子的寬度（格）。</summary>
        public const float RopeWidth = 0.12f;

        public static readonly Material RopeMat = SolidColorMaterials.NewSolidColorMaterial(Color.white, ShaderDatabase.Transparent);

        public static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
    }
}
