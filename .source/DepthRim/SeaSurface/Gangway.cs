using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>舷梯的設定（掛在 ThingDef 上）。</summary>
    public class GangwayExtension : DefModExtension
    {
        /// <summary>最多跨過幾格開放海面。</summary>
        public int maxReach = 6;

        /// <summary>伸出或收回一格要幾 tick。</summary>
        public int ticksPerCell = 30;

        /// <summary>伸出時鋪在海面格上的地基（<see cref="DepthRimDefOf.DepthRim_GangwayDeck"/>）。</summary>
        public TerrainDef deckTerrain;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors())
            {
                yield return e;
            }
            if (deckTerrain == null || !deckTerrain.isFoundation)
            {
                yield return "deckTerrain must be a foundation terrain";
            }
            if (maxReach < 1 || ticksPerCell < 1)
            {
                yield return "maxReach and ticksPerCell must be positive";
            }
        }
    }

    /// <summary>舷梯跨距：純函式，可單元測試。</summary>
    public static class GangwayMath
    {
        /// <summary>
        /// 從舷梯（<paramref name="origin"/>）往 <paramref name="step"/> 方向數：先是連續的空隙格（開放海面），
        /// 接著第一格必須是對岸（能站的甲板）。空隙至少一格、最多 <paramref name="maxReach"/> 格。
        /// 成功時 <paramref name="span"/> 是由近到遠的空隙格、<paramref name="landing"/> 是對岸那一格。
        /// </summary>
        public static bool TryFindSpan(IntVec3 origin, IntVec3 step, int maxReach, Func<IntVec3, bool> isGap, Func<IntVec3, bool> isLanding, List<IntVec3> span, out IntVec3 landing)
        {
            span.Clear();
            landing = IntVec3.Invalid;
            IntVec3 c = origin;
            for (int i = 0; i <= maxReach; i++)
            {
                c += step;
                if (isGap(c))
                {
                    if (i == maxReach)
                    {
                        break;
                    }
                    span.Add(c);
                    continue;
                }
                if (span.Count > 0 && isLanding(c))
                {
                    landing = c;
                    return true;
                }
                break;
            }
            span.Clear();
            return false;
        }
    }

    public enum GangwayState
    {
        Retracted,
        Extending,
        Extended,
        Retracting,
    }

    /// <summary>
    /// 可收放的舷梯（水面層）：蓋在浮船甲板的邊上、朝向開放海面。伸出時往前逐格鋪臨時甲板（地基 <see cref="GangwayExtension.deckTerrain"/>），
    /// 碰到對岸的甲板（另一艘浮船、上浮的潛艇、橋）就接上，兩邊可以直接走過去；收回時從遠端逐格拆掉。
    /// <list type="bullet">
    /// <item>對岸不見了（船開走、平台被拆）或臨時甲板被拆：立刻全部收回，上面的人落海後爬回最近的甲板（<see cref="SeaDrop"/>）。</item>
    /// <item>手動收回時有人站在舷梯上就等他離開；物品移到旁邊。</item>
    /// <item>舷梯本身被拆、搬走或隨船體被 DMSE 帶走（DeSpawn）時，留在原地圖的臨時甲板立刻拆掉。</item>
    /// </list>
    /// 臨時甲板的底層仍是開放海面（<see cref="TerrainGrid.BaseTerrainAt"/>），所以龍骨樁不會把它算進船體。
    /// </summary>
    public class Building_Gangway : Building
    {
        /// <summary>伸出後每隔幾 tick 檢查一次對岸還在不在。</summary>
        public const int CheckInterval = 60;

        private GangwayState state;

        /// <summary>這次伸出要鋪的格子（由近到遠）。</summary>
        private List<IntVec3> span = new List<IntVec3>();

        /// <summary>已經鋪好幾格（span 的前幾格）。</summary>
        private int laid;

        private int nextStepTick;

        private static readonly List<IntVec3> tmpSpan = new List<IntVec3>();

        private static readonly List<Thing> tmpThings = new List<Thing>();

        public GangwayState State => state;

        public int LaidCells => laid;

        public IReadOnlyList<IntVec3> Span => span;

        public GangwayExtension Ext => def.GetModExtension<GangwayExtension>();

        public IntVec3 Landing => span.Count > 0 ? span[span.Count - 1] + Rotation.FacingCell : IntVec3.Invalid;

        public bool Connected => state == GangwayState.Extended;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref state, "state", GangwayState.Retracted);
            Scribe_Collections.Look(ref span, "span", LookMode.Value);
            Scribe_Values.Look(ref laid, "laid", 0);
            Scribe_Values.Look(ref nextStepTick, "nextStepTick", 0);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && span == null)
            {
                span = new List<IntVec3>();
            }
        }

        /// <summary>這一格能當空隙（要鋪臨時甲板的開放海面）：沒有建築（例如支柱腳）。</summary>
        public static bool IsGap(IntVec3 c, Map map)
        {
            return c.InBounds(map) && SeaSurface.IsOpenSea(c, map) && c.GetEdifice(map) == null;
        }

        /// <summary>這一格能當對岸：不是開放海面、能走（浮船、潛艇甲板、橋、另一座舷梯的臨時甲板）。</summary>
        public static bool IsLanding(IntVec3 c, Map map)
        {
            return c.InBounds(map) && !SeaSurface.IsOpenSea(c, map) && c.Walkable(map);
        }

        /// <summary>從 <paramref name="pos"/> 朝 <paramref name="rot"/> 看，現在能不能接到對岸。</summary>
        public static bool TryFindSpan(IntVec3 pos, Rot4 rot, Map map, int maxReach, List<IntVec3> outSpan, out IntVec3 landing)
        {
            return GangwayMath.TryFindSpan(pos, rot.FacingCell, maxReach, c => IsGap(c, map), c => IsLanding(c, map), outSpan, out landing);
        }

        public bool CanExtend(out string reason)
        {
            reason = null;
            if (state == GangwayState.Extended || state == GangwayState.Extending)
            {
                reason = "DepthRim.Gangway.AlreadyExtended".Translate();
                return false;
            }
            if (!TryFindSpan(Position, Rotation, Map, Ext.maxReach, tmpSpan, out _))
            {
                reason = "DepthRim.Gangway.NoLanding".Translate(Ext.maxReach);
                return false;
            }
            return true;
        }

        /// <summary>開始伸出（從收回中轉回伸出時，已經鋪好的格子保留）。</summary>
        public bool Extend()
        {
            if (state == GangwayState.Retracting && laid > 0 && StillValid())
            {
                state = GangwayState.Extending;
                nextStepTick = Find.TickManager.TicksGame + Ext.ticksPerCell;
                return true;
            }
            if (!CanExtend(out string reason))
            {
                Messages.Message(reason, this, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            RemoveAll(Map);
            TryFindSpan(Position, Rotation, Map, Ext.maxReach, span, out _);
            state = GangwayState.Extending;
            nextStepTick = Find.TickManager.TicksGame;
            return true;
        }

        public void Retract()
        {
            if (state == GangwayState.Retracted)
            {
                return;
            }
            state = GangwayState.Retracting;
            nextStepTick = Find.TickManager.TicksGame + Ext.ticksPerCell;
        }

        protected override void Tick()
        {
            base.Tick();
            if (!Spawned || state == GangwayState.Retracted)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if (state == GangwayState.Extended)
            {
                if (this.IsHashIntervalTick(CheckInterval) && !StillValid())
                {
                    CollapseAll();
                }
                return;
            }
            if (now < nextStepTick)
            {
                return;
            }
            nextStepTick = now + Ext.ticksPerCell;
            if (state == GangwayState.Extending)
            {
                StepExtend();
            }
            else
            {
                StepRetract();
            }
        }

        /// <summary>已鋪的格子都還是自己的臨時甲板，而且（伸出中以外）對岸還在。</summary>
        private bool StillValid()
        {
            TerrainDef deck = Ext.deckTerrain;
            for (int i = 0; i < laid; i++)
            {
                if (Map.terrainGrid.FoundationAt(span[i]) != deck)
                {
                    return false;
                }
            }
            return span.Count > 0 && IsLanding(Landing, Map);
        }

        private void StepExtend()
        {
            if (laid >= span.Count)
            {
                state = GangwayState.Extended;
                return;
            }
            IntVec3 c = span[laid];
            // 鋪到一半對岸走了，或前面的格子被佔了：收回已鋪的部分
            if (!IsGap(c, Map) || !IsLanding(Landing, Map))
            {
                Messages.Message("DepthRim.Gangway.Interrupted".Translate(Label), this, MessageTypeDefOf.NegativeEvent, historical: false);
                Retract();
                return;
            }
            Map.terrainGrid.SetFoundation(c, Ext.deckTerrain);
            laid++;
            SoundDefOf.Building_Complete.PlayOneShot(new TargetInfo(c, Map));
            if (laid >= span.Count)
            {
                state = GangwayState.Extended;
                Messages.Message("DepthRim.Gangway.Connected".Translate(Label), this, MessageTypeDefOf.PositiveEvent, historical: false);
            }
        }

        private void StepRetract()
        {
            if (laid <= 0)
            {
                Finish();
                return;
            }
            IntVec3 c = span[laid - 1];
            if (Map.terrainGrid.FoundationAt(c) != Ext.deckTerrain)
            {
                // 被別的方式拆掉了：當作已收
                laid--;
                return;
            }
            if (c.GetFirstPawn(Map) != null)
            {
                // 等人走開
                nextStepTick = Find.TickManager.TicksGame + 30;
                return;
            }
            MoveItemsOff(c);
            Map.terrainGrid.RemoveFoundation(c, doLeavings: false);
            laid--;
            if (laid <= 0)
            {
                Finish();
            }
        }

        private void Finish()
        {
            laid = 0;
            span.Clear();
            state = GangwayState.Retracted;
        }

        /// <summary>物品挪到旁邊能站的格子（不能放回舷梯上）。</summary>
        private void MoveItemsOff(IntVec3 c)
        {
            tmpThings.Clear();
            tmpThings.AddRange(c.GetThingList(Map));
            HashSet<IntVec3> onSpan = new HashSet<IntVec3>(span);
            foreach (Thing t in tmpThings)
            {
                if (t.def.category != ThingCategory.Item || t.Destroyed)
                {
                    continue;
                }
                t.DeSpawn();
                if (!GenPlace.TryPlaceThing(t, Position, Map, ThingPlaceMode.Near, null, x => !onSpan.Contains(x) && !SeaSurface.IsOpenSea(x, Map)))
                {
                    t.Destroy();
                }
            }
            tmpThings.Clear();
        }

        /// <summary>對岸不見了：所有臨時甲板一次拆掉，上面的人落海（<see cref="Patch_FoundationRemovedOverSea"/>）。</summary>
        public void CollapseAll()
        {
            if (laid > 0)
            {
                Messages.Message("DepthRim.Gangway.Collapsed".Translate(Label), new LookTargets(new TargetInfo(Position, Map)), MessageTypeDefOf.NegativeEvent);
            }
            RemoveAll(Map);
            Finish();
        }

        private void RemoveAll(Map map)
        {
            if (map == null)
            {
                return;
            }
            TerrainDef deck = Ext.deckTerrain;
            // 整段一起掉：落海的人不能爬到同一段還沒拆到的步道上（否則逐格被救好幾次，最後才回到甲板）
            for (int i = 0; i < laid; i++)
            {
                SeaDrop.falling.Add(span[i]);
            }
            try
            {
                for (int i = laid - 1; i >= 0; i--)
                {
                    IntVec3 c = span[i];
                    if (c.InBounds(map) && map.terrainGrid.FoundationAt(c) == deck)
                    {
                        map.terrainGrid.RemoveFoundation(c, doLeavings: false);
                    }
                }
            }
            finally
            {
                SeaDrop.falling.Clear();
            }
            laid = 0;
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = Map;
            base.DeSpawn(mode);
            RemoveAll(map);
            Finish();
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
            if (state == GangwayState.Retracted || state == GangwayState.Retracting)
            {
                Command_Action extend = new Command_Action
                {
                    defaultLabel = "DepthRim.Gangway.Extend".Translate(),
                    defaultDesc = "DepthRim.Gangway.ExtendDesc".Translate(Ext.maxReach),
                    icon = TexCommand.Install,
                    action = () => Extend(),
                };
                if (state == GangwayState.Retracted && !CanExtend(out string reason))
                {
                    extend.Disable(reason);
                }
                yield return extend;
            }
            else
            {
                yield return new Command_Action
                {
                    defaultLabel = "DepthRim.Gangway.Retract".Translate(),
                    defaultDesc = "DepthRim.Gangway.RetractDesc".Translate(),
                    icon = TexCommand.ClearPrioritizedWork,
                    action = Retract,
                };
            }
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            string line = ("DepthRim.Gangway.State." + state).Translate(laid, span.Count);
            if (state == GangwayState.Retracting && laid > 0 && span[laid - 1].GetFirstPawn(Map) is Pawn p)
            {
                line += "\n" + "DepthRim.Gangway.WaitingFor".Translate(p.LabelShort, p);
            }
            return s.NullOrEmpty() ? line : s + "\n" + line;
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            if (state == GangwayState.Retracted)
            {
                if (TryFindSpan(Position, Rotation, Map, Ext.maxReach, tmpSpan, out IntVec3 landing))
                {
                    GenDraw.DrawFieldEdges(tmpSpan, Color.white);
                    GenDraw.DrawFieldEdges(new List<IntVec3> { landing }, Color.green);
                }
            }
            else if (span.Count > 0)
            {
                GenDraw.DrawFieldEdges(span, Color.white);
            }
        }
    }

    /// <summary>
    /// 舷梯只能蓋在水面層的甲板上、正前方是開放海面；放置時畫出現在能接到的跨距（白）與對岸（綠），接不到時畫出最遠範圍（紅）。
    /// 接不到也可以蓋（船可能晚點才來）。
    /// </summary>
    public class PlaceWorker_Gangway : PlaceWorker
    {
        private static readonly List<IntVec3> tmp = new List<IntVec3>();

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            if (!SeaLevels.IsSeaLevel(map))
            {
                return "DepthRim.Gangway.SeaLevelOnly".Translate();
            }
            if (SeaSurface.IsOpenSea(loc, map))
            {
                return "DepthRim.Gangway.MustBeOnDeck".Translate();
            }
            if (!Building_Gangway.IsGap(loc + rot.FacingCell, map))
            {
                return "DepthRim.Gangway.MustFaceSea".Translate();
            }
            return true;
        }

        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            Map map = Find.CurrentMap;
            if (map == null || !center.InBounds(map))
            {
                return;
            }
            int reach = def.GetModExtension<GangwayExtension>()?.maxReach ?? 6;
            if (Building_Gangway.TryFindSpan(center, rot, map, reach, tmp, out IntVec3 landing))
            {
                GenDraw.DrawFieldEdges(tmp, Color.white);
                GenDraw.DrawFieldEdges(new List<IntVec3> { landing }, Color.green);
                return;
            }
            tmp.Clear();
            for (int i = 1; i <= reach; i++)
            {
                IntVec3 c = center + rot.FacingCell * i;
                if (c.InBounds(map))
                {
                    tmp.Add(c);
                }
            }
            GenDraw.DrawFieldEdges(tmp, Color.red);
        }
    }

    /// <summary>海面地圖上有人或物品掉進海裡：物品沉下去，人爬回最近的甲板（受點傷），附近沒有甲板就失蹤。</summary>
    public static class SeaDrop
    {
        /// <summary>爬回甲板的搜尋半徑（格）。</summary>
        public const float ClimbRadius = 12f;

        /// <summary>給驗收測試：累計爬回甲板、失蹤的人數。</summary>
        public static int climbedBack;

        public static int lost;

        /// <summary>同一批正在拆掉的格子：爬回甲板時不選這些格子（<see cref="Building_Gangway"/> 整段掉落時填入）。</summary>
        public static readonly HashSet<IntVec3> falling = new HashSet<IntVec3>();

        private static readonly List<Thing> tmp = new List<Thing>();

        /// <summary>這一格變成開放海面了：處理上面的東西。</summary>
        public static void FellIntoSea(IntVec3 c, Map map)
        {
            tmp.Clear();
            tmp.AddRange(c.GetThingList(map));
            foreach (Thing t in tmp)
            {
                if (t is Pawn pawn && pawn.Spawned && !pawn.Destroyed)
                {
                    Rescue(pawn);
                }
            }
            tmp.Clear();
            Patch_AbyssSwallowsItems.SwallowAt(c, map);
        }

        public static void Rescue(Pawn pawn)
        {
            Map map = pawn.Map;
            IntVec3 from = pawn.Position;
            IntVec3 safe = IntVec3.Invalid;
            int n = GenRadial.NumCellsInRadius(ClimbRadius);
            for (int i = 1; i < n; i++)
            {
                IntVec3 c = from + GenRadial.RadialPattern[i];
                if (c.InBounds(map) && !falling.Contains(c) && !SeaSurface.IsOpenSea(c, map) && c.Standable(map))
                {
                    safe = c;
                    break;
                }
            }
            map.GetComponent<MapComponent_PlatformSupport>()?.Splash(from);
            if (!safe.IsValid)
            {
                lost++;
                Messages.Message("DepthRim.Gangway.PawnLost".Translate(pawn.LabelShort, pawn), new LookTargets(new TargetInfo(from, map)), MessageTypeDefOf.PawnDeath);
                pawn.Kill(null);
                if (pawn.Corpse != null && !pawn.Corpse.Destroyed)
                {
                    pawn.Corpse.Destroy();
                }
                return;
            }
            climbedBack++;
            pawn.pather?.StopDead();
            pawn.Position = safe;
            pawn.Notify_Teleported(true, true);
            pawn.TakeDamage(new DamageInfo(DamageDefOf.Blunt, Rand.Range(3f, 8f)));
            Messages.Message("DepthRim.Gangway.PawnFell".Translate(pawn.LabelShort, pawn), new LookTargets(pawn), MessageTypeDefOf.NegativeHealthEvent);
        }
    }

    /// <summary>
    /// 地基（原版的橋、重型橋、舷梯的臨時甲板）被拆掉或炸毀後露出開放海面：上面的人落海、物品沉下去。
    /// 原版只處理建築（<c>destroyBuildingsOnDestroyed</c>）；開放海面不可通行，不處理的話人會卡在海上。
    /// </summary>
    [HarmonyPatch(typeof(TerrainGrid), nameof(TerrainGrid.RemoveFoundation))]
    public static class Patch_FoundationRemovedOverSea
    {
        [HarmonyPostfix]
        public static void Postfix(IntVec3 c, Map ___map)
        {
            if (___map == null || !SeaLevels.IsSeaLevel(___map) || !SeaSurface.IsOpenSea(c, ___map))
            {
                return;
            }
            SeaDrop.FellIntoSea(c, ___map);
        }
    }
}
