using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using UnityEngine;
using Verse;
using RimWorld.Planet;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 一張地圖上的安全繩：每 <see cref="ScanInterval"/> ticks 掃描一次，把範圍內、在艙外的我方 Pawn 扣到錨點上，
    /// 走回艙內、離開範圍、風險結束時解開；維護 Pawn → 錨點的反向索引；每幀畫繩子。
    ///
    /// <para>效能（設計稿 §7）：錨點清單用原版的 <c>listerThings.ThingsOfDef</c>（不自己登記）；沒有風險就提早結束；
    /// 候選只看我方與囚犯的清單、不掃全圖；範圍與牆的判定查每個錨點預先算好的格子遮罩（<see cref="RopeCoverage"/>）；
    /// 已扣的人位置與遮罩都沒變就不重驗；掃描過程不配置記憶體。</para>
    /// </summary>
    public class MapComponent_RopeTethers : MapComponent
    {
        public const int ScanInterval = 60;

        /// <summary>扣著繩的人在航行中加上的狀態（移動、操作變差）：每次掃描補滿，解開後約一小時退掉。</summary>
        public const float CurrentHediffSeverity = 1f;

        private readonly Dictionary<Pawn, Building_RopeAnchor> index = new Dictionary<Pawn, Building_RopeAnchor>();

        private HazardKind hazard;

        private bool indexBuilt;

        // ── 重複使用的暫存清單（§7.6）──
        private readonly List<Building_RopeAnchor> openAnchors = new List<Building_RopeAnchor>();

        private readonly List<Candidate> candidates = new List<Candidate>();

        private readonly List<Pawn> untethered = new List<Pawn>();

        private readonly List<Pawn> pendingCheck = new List<Pawn>();

        private readonly List<Pawn> tmpPawns = new List<Pawn>();

        private readonly Vector3[] points = new Vector3[RopeTetherMath.Segments + 1];

        // ── 給測試看的計數 ──
        public int Scans { get; private set; }

        public int EarlyOuts { get; private set; }

        public long ScanStopwatchTicks { get; private set; }

        private readonly Stopwatch watch = new Stopwatch();

        private struct Candidate
        {
            public Pawn pawn;
            public TetherRank rank;
            public int distSq;
            public Building_RopeAnchor anchor;
        }

        public MapComponent_RopeTethers(Map map) : base(map)
        {
        }

        public static MapComponent_RopeTethers For(Map map)
        {
            return map?.GetComponent<MapComponent_RopeTethers>();
        }

        /// <summary>上一次掃描時的風險。</summary>
        public HazardKind Hazard => hazard;

        /// <summary>上一次掃描時：有風險、在艙外、我方、沒扣繩的人（給警報直接讀，§7.7）。</summary>
        public List<Pawn> UntetheredOutside => untethered;

        public int TetheredCount => index.Count;

        public Building_RopeAnchor AnchorOf(Pawn pawn)
        {
            return pawn != null && index.TryGetValue(pawn, out Building_RopeAnchor a) ? a : null;
        }

        public override void MapComponentTick()
        {
            if (pendingCheck.Count > 0)
            {
                CheckPending();
            }
            if ((Find.TickManager.TicksGame + map.uniqueID) % ScanInterval == 0)
            {
                Scan();
            }
        }

        // ── 掃描 ──

        /// <summary>立刻掃描一次（落海檢查前也會先呼叫，保證剛走出艙的人先扣上）。</summary>
        public void Scan()
        {
            watch.Restart();
            Scans++;
            if (!indexBuilt)
            {
                RebuildIndex();
            }
            HazardKind now = OverboardHazard.HazardOf(map);
            hazard = now;
            untethered.Clear();
            if (now == HazardKind.None)
            {
                // 第一個提早結束：沒有風險不扣繩，剛從有風險變成沒風險時把繩子全部解開
                if (index.Count > 0)
                {
                    UntieAll();
                }
                EarlyOuts++;
                Stop();
                return;
            }

            List<Thing> anchors = map.listerThings.ThingsOfDef(DepthRimDefOf.DR_RopeAnchor);
            openAnchors.Clear();
            CellRect bounds = CellRect.Empty;
            for (int i = 0; i < anchors.Count; i++)
            {
                if (!(anchors[i] is Building_RopeAnchor anchor) || !anchor.Spawned)
                {
                    continue;
                }
                anchor.coverage.EnsureFresh(map, anchor.Position);
                ValidateSlots(anchor);
                if (anchor.autoTether && anchor.HasFreeSlot)
                {
                    openAnchors.Add(anchor);
                    CellRect r = CellRect.CenteredOn(anchor.Position, RopeTetherMath.Reach);
                    bounds = bounds.Area == 0 ? r : CellRect.FromLimits(Mathf.Min(bounds.minX, r.minX), Mathf.Min(bounds.minZ, r.minZ), Mathf.Max(bounds.maxX, r.maxX), Mathf.Max(bounds.maxZ, r.maxZ));
                }
            }

            candidates.Clear();
            Consider(map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer), bounds);
            Consider(map.mapPawns.PrisonersOfColonySpawned, bounds);
            if (candidates.Count > 1)
            {
                candidates.Sort((a, b) => RopeTetherMath.Compare(a.rank, a.distSq, b.rank, b.distSq));
            }
            for (int i = 0; i < candidates.Count; i++)
            {
                Candidate c = candidates[i];
                Building_RopeAnchor anchor = c.anchor.HasFreeSlot ? c.anchor : BestAnchorFor(c.pawn, out _);
                if (anchor == null)
                {
                    untethered.Add(c.pawn);
                    continue;
                }
                Tie(c.pawn, anchor, anchor.FreeSlot());
            }

            if (now == HazardKind.Voyage && index.Count > 0)
            {
                RefreshCurrentHediff();
            }
            Stop();
        }

        private void Stop()
        {
            watch.Stop();
            ScanStopwatchTicks += watch.ElapsedTicks;
        }

        /// <summary>候選 Pawn 的判斷照便宜到貴的順序（§7.2）。</summary>
        private void Consider(IReadOnlyList<Pawn> pawns, CellRect bounds)
        {
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (index.ContainsKey(p) || p.Dead || !p.Spawned || !VoyageOverboard.CanBeSwept(p))
                {
                    continue;
                }
                if (!VoyageOverboard.IsOutside(p))
                {
                    continue;
                }
                // 倒地的人不能自己扣繩；所有錨點的外框外面不用逐一查遮罩
                if (p.Downed || openAnchors.Count == 0 || !bounds.Contains(p.Position))
                {
                    untethered.Add(p);
                    continue;
                }
                Building_RopeAnchor best = BestAnchorFor(p, out int distSq);
                if (best == null)
                {
                    untethered.Add(p);
                    continue;
                }
                candidates.Add(new Candidate { pawn = p, rank = RankOf(p), distSq = distSq, anchor = best });
            }
        }

        /// <summary>有空位、允許自動扣繩、不在冷卻中、遮罩涵蓋這一格的錨點裡最近的一個。</summary>
        private Building_RopeAnchor BestAnchorFor(Pawn p, out int bestDist)
        {
            Building_RopeAnchor best = null;
            bestDist = int.MaxValue;
            IntVec3 cell = p.Position;
            for (int i = 0; i < openAnchors.Count; i++)
            {
                Building_RopeAnchor a = openAnchors[i];
                if (!a.HasFreeSlot || !a.coverage.Covers(cell) || a.OnCooldown(p))
                {
                    continue;
                }
                int d = (cell - a.Position).LengthHorizontalSquared;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = a;
                }
            }
            return best;
        }

        public static TetherRank RankOf(Pawn p)
        {
            if (p.IsPrisonerOfColony)
            {
                return TetherRank.Prisoner;
            }
            if (p.RaceProps.Humanlike)
            {
                return TetherRank.Colonist;
            }
            if (p.RaceProps.IsMechanoid)
            {
                return TetherRank.Mech;
            }
            return TetherRank.Animal;
        }

        /// <summary>已扣的繩子還有效嗎（§7.5）：位置與遮罩都沒變就不重驗。</summary>
        private void ValidateSlots(Building_RopeAnchor anchor)
        {
            for (int s = 0; s < RopeTetherMath.MaxTethers; s++)
            {
                Pawn p = anchor.SlotPawn(s);
                if (p == null)
                {
                    continue;
                }
                if (p.Dead || !p.Spawned || p.Map != map)
                {
                    Untie(p, anchor, s);
                    continue;
                }
                IntVec3 cell = p.Position;
                if (anchor.Unchanged(s, cell))
                {
                    continue;
                }
                if (!anchor.coverage.Covers(cell) || !VoyageOverboard.IsOutside(p))
                {
                    Untie(p, anchor, s);
                    continue;
                }
                anchor.MarkValidated(s, cell);
            }
        }

        private void RefreshCurrentHediff()
        {
            HediffDef def = DepthRimDefOf.DepthRim_TetheredInCurrent;
            foreach (KeyValuePair<Pawn, Building_RopeAnchor> kv in index)
            {
                Pawn p = kv.Key;
                if (p.health == null)
                {
                    continue;
                }
                Hediff h = p.health.hediffSet.GetFirstHediffOfDef(def);
                if (h == null)
                {
                    h = HediffMaker.MakeHediff(def, p);
                    h.Severity = CurrentHediffSeverity;
                    p.health.AddHediff(h);
                }
                else
                {
                    h.Severity = CurrentHediffSeverity;
                }
            }
        }

        // ── 扣、解 ──

        private void Tie(Pawn pawn, Building_RopeAnchor anchor, int slot)
        {
            anchor.SetSlot(slot, pawn);
            anchor.MarkValidated(slot, pawn.Position);
            index[pawn] = anchor;
            // 回饋只有扣繩聲和畫出來的繩子，不冒文字
            if (pawn.Spawned && pawn.Map == Find.CurrentMap)
            {
                RopeSounds.Clip?.PlayOneShot(new TargetInfo(pawn.Position, map));
            }
        }

        private void Untie(Pawn pawn, Building_RopeAnchor anchor, int slot)
        {
            anchor.SetSlot(slot, null);
            if (pawn != null)
            {
                index.Remove(pawn);
            }
        }

        /// <summary>解開這個人的繩子（任何錨點）。</summary>
        public void Untie(Pawn pawn)
        {
            if (pawn == null || !index.TryGetValue(pawn, out Building_RopeAnchor anchor))
            {
                return;
            }
            int slot = anchor.SlotOf(pawn);
            if (slot >= 0)
            {
                anchor.SetSlot(slot, null);
            }
            index.Remove(pawn);
        }

        private void UntieAll()
        {
            tmpPawns.Clear();
            tmpPawns.AddRange(index.Keys);
            for (int i = 0; i < tmpPawns.Count; i++)
            {
                Untie(tmpPawns[i]);
            }
            tmpPawns.Clear();
            index.Clear();
        }

        /// <summary>手動扣到指定的錨點（右鍵選單）：滿了就擠掉離錨點最遠的那個人。</summary>
        public void ForceTie(Pawn pawn, Building_RopeAnchor anchor)
        {
            Untie(pawn);
            int slot = anchor.FreeSlot();
            if (slot < 0)
            {
                int farthest = -1, farDist = -1;
                for (int s = 0; s < RopeTetherMath.MaxTethers; s++)
                {
                    Pawn other = anchor.SlotPawn(s);
                    int d = other == null ? 0 : (other.Position - anchor.Position).LengthHorizontalSquared;
                    if (d > farDist)
                    {
                        farDist = d;
                        farthest = s;
                    }
                }
                Pawn evicted = anchor.SlotPawn(farthest);
                Untie(evicted, anchor, farthest);
                anchor.StartCooldown(evicted);
                slot = farthest;
            }
            Tie(pawn, anchor, slot);
            // 被擠掉的人立刻找別的錨點
            Scan();
        }

        /// <summary>右鍵選單：不能扣在這個錨點的原因（可以時回傳 null）。</summary>
        public string CannotTieReason(Pawn pawn, Building_RopeAnchor anchor)
        {
            if (!pawn.Spawned || pawn.Map != map)
            {
                return "DepthRim.Rope.OutOfReach".Translate();
            }
            if (!VoyageOverboard.IsOutside(pawn))
            {
                return "DepthRim.Rope.NotOutside".Translate();
            }
            anchor.coverage.EnsureFresh(map, anchor.Position);
            if (!anchor.coverage.Covers(pawn.Position))
            {
                return "DepthRim.Rope.OutOfReach".Translate();
            }
            return null;
        }

        // ── 錨點生成、移除 ──

        public void Notify_AnchorSpawned(Building_RopeAnchor anchor)
        {
            // 讀檔後錨點自己存著扣環上的人：重建索引時一併收進來
            indexBuilt = false;
        }

        public void Notify_AnchorRemoved(Building_RopeAnchor anchor, bool checkNow)
        {
            for (int s = 0; s < RopeTetherMath.MaxTethers; s++)
            {
                Pawn p = anchor.SlotPawn(s);
                if (p == null)
                {
                    continue;
                }
                Untie(p, anchor, s);
                if (checkNow && !pendingCheck.Contains(p))
                {
                    pendingCheck.Add(p);
                }
            }
        }

        /// <summary>繩子斷開的人：下一個 tick 做一次落海判定（不在建築 DeSpawn 的途中把 Pawn 移出地圖）。</summary>
        private void CheckPending()
        {
            tmpPawns.Clear();
            tmpPawns.AddRange(pendingCheck);
            pendingCheck.Clear();
            Scan();
            switch (hazard)
            {
                case HazardKind.Voyage:
                    VoyageOverboard.SweepPawns(map, tmpPawns, (WorldObject_Submarine)map.Parent);
                    break;
                case HazardKind.RoughSea:
                    map.GetComponent<MapComponent_RoughSeaOverboard>()?.Roll(tmpPawns, forceHit: true);
                    break;
            }
            tmpPawns.Clear();
        }

        private void RebuildIndex()
        {
            index.Clear();
            List<Thing> anchors = map.listerThings.ThingsOfDef(DepthRimDefOf.DR_RopeAnchor);
            for (int i = 0; i < anchors.Count; i++)
            {
                if (!(anchors[i] is Building_RopeAnchor a))
                {
                    continue;
                }
                for (int s = 0; s < RopeTetherMath.MaxTethers; s++)
                {
                    Pawn p = a.SlotPawn(s);
                    if (p == null)
                    {
                        continue;
                    }
                    if (p.Dead || !p.Spawned || p.Map != map || index.ContainsKey(p))
                    {
                        a.SetSlot(s, null);
                        continue;
                    }
                    index[p] = a;
                }
            }
            indexBuilt = true;
        }

        // ── 畫繩子（§4、§7.8）──

        public override void MapComponentUpdate()
        {
            if (index.Count == 0 || map != Find.CurrentMap || WorldRendererUtility.WorldRendered)
            {
                return;
            }
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
            bool voyage = hazard == HazardKind.Voyage;
            bool sea = hazard == HazardKind.RoughSea;
            float[] dark = voyage ? SectionLayer_VoyageDarkness.FieldFor(map) : null;
            Vector3 drift = Vector3.zero;
            if (voyage)
            {
                Vector2 heading = VoyageMotion.Heading;
                drift = new Vector3(-heading.x, 0f, -heading.y) * 0.2f;
            }
            else if (sea)
            {
                float factor = OverboardHazard.SeaFactor(map);
                Vector2 o = SeaSway.Offset(Time.time, SeaSway.BaseAmplitude * factor);
                drift = new Vector3(o.x, 0f, o.y) * 0.15f;
            }
            foreach (KeyValuePair<Pawn, Building_RopeAnchor> kv in index)
            {
                Pawn p = kv.Key;
                Building_RopeAnchor a = kv.Value;
                if (!p.Spawned || p.Map != map || !a.Spawned)
                {
                    continue;
                }
                if (!view.Contains(a.Position) && !view.Contains(p.Position))
                {
                    continue;
                }
                int slot = a.SlotOf(p);
                if (slot < 0)
                {
                    continue;
                }
                bool selected = Find.Selector.IsSelected(p) || Find.Selector.IsSelected(a);
                float light = 1f;
                if (dark != null)
                {
                    IntVec3 mid = new IntVec3((p.Position.x + a.Position.x) / 2, 0, (p.Position.z + a.Position.z) / 2);
                    int i = mid.z * map.Size.x + mid.x;
                    if (i >= 0 && i < dark.Length)
                    {
                        light = 1f - VoyageView.FadeAlpha(dark[i]);
                    }
                }
                DrawRope(a.ClaspPos(slot), p.DrawPos + new Vector3(0f, 0f, -0.1f), drift, selected, light);
            }
        }

        private void DrawRope(Vector3 from, Vector3 to, Vector3 drift, bool selected, float light)
        {
            float y = AltitudeLayer.PawnState.AltitudeFor();
            from.y = y;
            to.y = y;
            float distance = new Vector2(to.x - from.x, to.z - from.z).magnitude;
            float sag = RopeTetherMath.Sag(distance);
            for (int i = 0; i <= RopeTetherMath.Segments; i++)
            {
                points[i] = RopeTetherMath.Point(from, to, i / (float)RopeTetherMath.Segments, sag, drift);
            }
            Color color = RopeAssets.RopeColor * (selected ? 1.3f : 1f) * light;
            color.a = 1f;
            RopeAssets.Block.SetColor(ShaderPropertyIDs.Color, color);
            for (int i = 0; i < RopeTetherMath.Segments; i++)
            {
                Vector3 a = points[i], b = points[i + 1];
                Vector3 d = b - a;
                float len = new Vector2(d.x, d.z).magnitude;
                if (len < 0.001f)
                {
                    continue;
                }
                Vector3 mid = (a + b) * 0.5f;
                Quaternion rot = Quaternion.AngleAxis(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, Vector3.up);
                // 段與段稍微重疊，轉角處不會有縫
                Matrix4x4 m = Matrix4x4.TRS(mid, rot, new Vector3(RopeAssets.RopeWidth, 1f, len + RopeAssets.RopeWidth * 0.5f));
                Graphics.DrawMesh(MeshPool.plane10, m, RopeAssets.RopeMat, 0, null, 0, RopeAssets.Block);
            }
        }

        /// <summary>給測試：目前所有扣著繩的人。</summary>
        public IEnumerable<Pawn> TetheredPawns => index.Keys;
    }

    /// <summary>安全繩的靜態查詢（§7.4）：都走地圖元件的反向索引。</summary>
    public static class RopeTether
    {
        public static Building_RopeAnchor AnchorOf(Pawn pawn)
        {
            return pawn != null && pawn.Spawned ? MapComponent_RopeTethers.For(pawn.Map)?.AnchorOf(pawn) : null;
        }

        public static bool IsTethered(Pawn pawn)
        {
            return AnchorOf(pawn) != null;
        }

        /// <summary>受保護：自己扣著繩，或被扣著繩的人扛著（§2.4）。</summary>
        public static bool IsProtected(Pawn pawn)
        {
            if (pawn == null)
            {
                return false;
            }
            if (IsTethered(pawn))
            {
                return true;
            }
            return pawn.ParentHolder is Pawn_CarryTracker carrier && IsTethered(carrier.pawn);
        }

        /// <summary>這個 Pawn 會用安全繩：我方（殖民者、囚犯、奴隸、動物、機械體），而且不是水生的。</summary>
        public static bool CanEverTether(Pawn pawn)
        {
            return pawn != null && VoyageOverboard.IsOurs(pawn) && VoyageOverboard.CanBeSwept(pawn);
        }
    }

    [StaticConstructorOnStartup]
    public static class RopeSounds
    {
        /// <summary>扣繩的金屬聲：借原版（Anomaly 的鎖鏈聲，沒有時用金屬落地聲）。</summary>
        public static readonly SoundDef Clip = DefDatabase<SoundDef>.GetNamedSilentFail("EntityChainLow") ?? DefDatabase<SoundDef>.GetNamedSilentFail("Metal_Drop");
    }
}
