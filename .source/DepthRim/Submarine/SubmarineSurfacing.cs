using System;
using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 上浮與下潛（設計稿 Docs/潛艇破冰出水_機制設計.md）。
    /// <list type="bullet">
    /// <item><b>常規上浮</b>：潛艇停在淺海、正上方是沒有冰的海：船體搬到地表圖層那一格的海面地圖（<see cref="WorldObject_SeaSurfaceSite"/>），浮在開放海面上。</item>
    /// <item><b>破冰上浮</b>：正上方是海冰：船上要有破冰結構。懸停一小時後緊急吹除主壓載，用 <see cref="IceBreachMath.Evaluate"/> 判定；
    /// 成功時船體搬到冰面地圖（<see cref="WorldObject_IceBreachSite"/>），四周照 <see cref="IceCrackPattern"/> 開裂；
    /// 部分開裂或頂不動時破冰結構受損，六小時後才能再試（部分開裂後一天內冰的抗彎厚度打 7 折）。</item>
    /// <item><b>下潛</b>：在海面或冰面地圖上用艦橋下潛：船體分離成正下方海洋格的潛艇（淺海、停著），用離港過場沉進水裡。
    /// 冰面上船體四周的冰比 0.4 m 厚時船被凍住，要用破冰結構撞開。</item>
    /// </list>
    /// 船體搬移沿用 DMSE 的 <see cref="ShipSeparationUtility.RelocateHull"/>（不分圖層：海洋層的船體地圖 ↔ 地表圖層的地圖）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class SubmarineSurfacing
    {
        /// <summary>破冰前懸停上升的時間。</summary>
        public const int HoverTicks = GenDate.TicksPerHour;

        /// <summary>每次頂冰後重新注水、充氣的時間。</summary>
        public const int RetryTicks = GenDate.TicksPerHour * 6;

        /// <summary>部分開裂後多久內再頂，冰的抗彎厚度打折。</summary>
        public const int CrackedTicks = GenDate.TicksPerDay;

        /// <summary>破冰結構撞開凍住的冰時每座受的傷。</summary>
        public const float BreakFreeDamage = 60f;

        /// <summary>測試用：不播出水與下潛演出。</summary>
        public static bool skipCutscenes;

        public static Texture2D IceIcon => DefDatabase<ThingDef>.GetNamedSilentFail("DepthRim_IceBreakerFrame")?.uiIcon ?? WorldObject_Submarine.DiveIcon;

        public static PlanetTile SurfaceAbove(WorldObject_Submarine sub) => OceanWorldView.TileAbove(sub.Tile);

        public static int HullCellCount(Map shipMap) => shipMap == null ? 0 : TransferLandingUtility.HullCells(shipMap).Count;

        // ==================================================================
        //  條件
        // ==================================================================

        /// <summary>上浮共同的深度條件：停著、巡航深度與目標都是淺海、沒有在懸停破冰。</summary>
        public static string DepthBlock(WorldObject_Submarine sub)
        {
            if (!sub.HasMap)
            {
                return "DepthRim.Surface.NoHull".Translate();
            }
            if (sub.InFlight)
            {
                return "DepthRim.Surface.InFlight".Translate();
            }
            if (sub.iceBreachAtTick >= 0)
            {
                return "DepthRim.IceBreach.Hovering".Translate();
            }
            if (sub.cruiseTier != DepthTier.Shallow || sub.targetTier != DepthTier.Shallow)
            {
                return "DepthRim.Surface.NotShallow".Translate(DepthTier.Shallow.Label());
            }
            return null;
        }

        /// <summary>正上方不是海（陸地或冰棚）時的原因。</summary>
        private static string SurfaceKindBlock(IceConditions.Info ice)
        {
            if (ice.kind == IceKind.Shelf)
            {
                return "DepthRim.Surface.Shelf".Translate();
            }
            if (ice.land)
            {
                return "DepthRim.Surface.Land".Translate();
            }
            return null;
        }

        /// <summary>常規上浮不能做的原因（可以時 null）。</summary>
        public static string SurfaceBlockReason(WorldObject_Submarine sub, out IceConditions.Info ice)
        {
            ice = default;
            string block = DepthBlock(sub);
            if (block != null)
            {
                return block;
            }
            PlanetTile above = SurfaceAbove(sub);
            ice = IceConditions.At(above);
            block = SurfaceKindBlock(ice);
            if (block != null)
            {
                return block;
            }
            if (ice.Frozen)
            {
                return "DepthRim.Surface.Iced".Translate(ice.thickness.ToString("0.0"));
            }
            MapParent parent = Find.WorldObjects.MapParentAt(above);
            if (parent != null && !(parent is WorldObject_SeaSurfaceSite))
            {
                return "DepthRim.Surface.Occupied".Translate(parent.LabelCap);
            }
            return null;
        }

        /// <summary>破冰上浮不能做的原因（可以時 null）；同時給冰況與預估結果。</summary>
        public static string IceBreachBlockReason(WorldObject_Submarine sub, out IceConditions.Info ice, out IceBreachOutcome estimate)
        {
            ice = default;
            estimate = default;
            string depth = DepthBlock(sub);
            if (depth != null)
            {
                return depth;
            }
            List<CompIceBreakerFrame> frames = CompIceBreakerFrame.On(sub.Map);
            if (frames.Count == 0)
            {
                return "DepthRim.IceBreach.NoFrame".Translate();
            }
            int now = Find.TickManager.TicksGame;
            if (now < sub.nextIceBreachTick)
            {
                return "DepthRim.IceBreach.Cooldown".Translate((sub.nextIceBreachTick - now).ToStringTicksToPeriod());
            }
            PlanetTile above = SurfaceAbove(sub);
            ice = IceConditions.At(above);
            string kind = SurfaceKindBlock(ice);
            if (kind != null)
            {
                return kind;
            }
            if (!ice.Frozen)
            {
                return "DepthRim.IceBreach.NoIce".Translate();
            }
            MapParent parent = Find.WorldObjects.MapParentAt(above);
            if (parent != null && !(parent is WorldObject_IceBreachSite))
            {
                return "DepthRim.Surface.Occupied".Translate(parent.LabelCap);
            }
            estimate = Estimate(sub, ice, frames);
            if (estimate.result == IceBreachResult.TooThick)
            {
                return "DepthRim.IceBreach.TooThick".Translate(ice.thickness.ToString("0.0"), CompIceBreakerFrame.BestLimit(frames).ToString("0.0"));
            }
            return null;
        }

        public static IceBreachOutcome Estimate(WorldObject_Submarine sub, IceConditions.Info ice, List<CompIceBreakerFrame> frames)
        {
            bool cracked = Find.TickManager.TicksGame < sub.crackedUntilTick;
            return IceBreachMath.Evaluate(ice.thickness, ice.airTemperature, HullCellCount(sub.Map), cracked, CompIceBreakerFrame.BestLimit(frames), frames.Count);
        }

        // ==================================================================
        //  常規上浮
        // ==================================================================

        public static void Surface(WorldObject_Submarine sub)
        {
            string reason = SurfaceBlockReason(sub, out _);
            if (reason != null)
            {
                Messages.Message(reason, sub, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            PlanetTile above = SurfaceAbove(sub);
            MapParent site = Find.WorldObjects.MapParentAt(above);
            if (site == null)
            {
                site = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_SeaSurfaceSite);
                site.Tile = above;
                site.SetFaction(Faction.OfPlayer);
                Find.WorldObjects.Add(site);
            }
            bool existing = site.HasMap;
            Map map = SiteMap(sub, site);
            List<IntVec3> cells = TransferLandingUtility.LandingCells(sub.Map);
            if (!TryFindSpot(map, cells, c => SeaSurface.IsOpenSea(c, map), out IntVec3 offset))
            {
                sub.landingTarget = PlanetTile.Invalid;
                Messages.Message("DepthRim.Surface.NoRoom".Translate(site.LabelCap), sub, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            if (existing && !skipCutscenes)
            {
                // 已經有的海面地圖：讓玩家選停船位置（只能浮在開放海面上）
                GameComponent_SubmarinePlacement.Request(new Designator_PlaceSubmarine(sub, map, cells, c => SeaSurface.IsOpenSea(c, map), chosen =>
                {
                    // 選位期間情況可能變了（例如又開始換深度）
                    string late = SurfaceBlockReason(sub, out _);
                    if (late != null)
                    {
                        sub.landingTarget = PlanetTile.Invalid;
                        Messages.Message(late, sub, MessageTypeDefOf.RejectInput, historical: false);
                        return;
                    }
                    SurfaceAt(sub, map, chosen);
                }));
                return;
            }
            SurfaceAt(sub, map, offset);
        }

        /// <summary>常規上浮的出水演出與搬移（位置已選好）。</summary>
        private static void SurfaceAt(WorldObject_Submarine sub, Map map, IntVec3 offset)
        {
            string label = sub.Label;
            SurfacingShow show = SurfacingShow.OpenSea(map, DestinationHull(sub, offset));
            PlayThenMove(sub, map, offset, show, hull =>
            {
                Find.LetterStack.ReceiveLetter("DepthRim.Surface.LetterLabel".Translate(), "DepthRim.Surface.LetterText".Translate(label),
                    LetterDefOf.PositiveEvent, new TargetInfo(CellRect.FromCellList(hull).CenterCell, map));
            });
        }

        /// <summary>船體搬到 <paramref name="offset"/> 之後會佔的格子（目的地座標）。</summary>
        private static HashSet<IntVec3> DestinationHull(WorldObject_Submarine sub, IntVec3 offset)
        {
            return new HashSet<IntVec3>(TransferLandingUtility.HullCells(sub.Map).Select(c => c + offset));
        }

        /// <summary>現在有沒有在播出水演出（測試用）。</summary>
        public static bool CutscenePlaying => Find.WindowStack.Windows.Any(w => w is SubmarineSurfacingCutscene);

        /// <summary>
        /// 播出水演出（<see cref="SubmarineSurfacingCutscene"/>），結束時才把船體搬上來；冰面的地形在演出中照出現時間套上，結束時補齊。
        /// 演出被略過（<see cref="skipCutscenes"/>、原版設定關掉過場、沒有 Odyssey）時直接搬，並補上水花與震動。
        /// </summary>
        private static void PlayThenMove(WorldObject_Submarine sub, Map map, IntVec3 offset, SurfacingShow show, Action<HashSet<IntVec3>> after)
        {
            bool played = false;
            Action move = delegate
            {
                show.ApplyAll();
                MoveInto(sub, map, offset, hull =>
                {
                    if (!played)
                    {
                        Splash(map, hull, 12, show.violent ? 2.5f : 1.5f);
                        DepthRimDefOf.DepthRim_Breach?.PlayOneShot(new TargetInfo(CellRect.FromCellList(hull).CenterCell, map));
                        if (Find.CurrentMap == map)
                        {
                            Find.CameraDriver.shaker.DoShake(show.ice ? (show.violent ? 4f : 2.5f) : 1f);
                        }
                    }
                    SubmarineSurfacingCutscene.LeaveTraces(show);
                    after?.Invoke(hull);
                });
            };
            if (skipCutscenes || !Prefs.GravshipCutscenes || !ModsConfig.OdysseyActive)
            {
                move();
                return;
            }
            played = true;
            HashSet<IntVec3> shipCells = new HashSet<IntVec3>(TransferLandingUtility.HullCells(sub.Map));
            Vector3 center = CellRect.FromCellList(shipCells).CenterVector3 + offset.ToVector3();
            Find.WindowStack.Add(new SubmarineSurfacingCutscene(map, center, move, sub.Map, shipCells, show));
        }

        /// <summary>地點的地圖（沒有就生成，尺寸至少容得下船體）。生成前先把潛艇的降落目標設成這一格，DMSE 才不會把空地圖當成沒用的移除。</summary>
        private static Map SiteMap(WorldObject_Submarine sub, MapParent site)
        {
            sub.landingTarget = site.Tile;
            if (site.HasMap)
            {
                return site.Map;
            }
            IntVec3 size = TransferLandingUtility.RequiredMapSize(site, CellRect.FromCellList(TransferLandingUtility.LandingCells(sub.Map)));
            return GetOrGenerateMapUtility.GetOrGenerateMap(site.Tile, size, site.def);
        }

        /// <summary>
        /// 找船體放得下的位移：從地圖中央往外找，船體每一格都要能降落（DMSE 的規則：不在邊緣、沒有屋頂、沒有擋路的東西）
        /// 而且 <paramref name="terrainOk"/>（例如海面地圖只能浮在開放海面上，不能壓到平台）。
        /// </summary>
        public static bool TryFindSpot(Map map, List<IntVec3> cells, Predicate<IntVec3> terrainOk, out IntVec3 offset)
        {
            offset = IntVec3.Zero;
            if (map == null || cells.Count == 0)
            {
                return false;
            }
            IntVec3 preferred = TransferLandingUtility.CenteredOffset(map, cells);
            bool Fits(IntVec3 o)
            {
                foreach (IntVec3 c in cells)
                {
                    IntVec3 d = c + o;
                    if (!d.InBounds(map) || !terrainOk(d) || !TransferLandingUtility.IsValidLandingCell(d, map).Accepted)
                    {
                        return false;
                    }
                }
                return true;
            }
            int radial = Mathf.Min(GenRadial.RadialPattern.Length, 400);
            for (int i = 0; i < radial; i++)
            {
                IntVec3 o = preferred + GenRadial.RadialPattern[i];
                if (Fits(o))
                {
                    offset = o;
                    return true;
                }
            }
            CellRect bounds = CellRect.FromCellList(cells);
            List<IntVec3> coarse = new List<IntVec3>();
            for (int x = -bounds.minX; x + bounds.maxX < map.Size.x; x += 3)
            {
                for (int z = -bounds.minZ; z + bounds.maxZ < map.Size.z; z += 3)
                {
                    coarse.Add(new IntVec3(x, 0, z));
                }
            }
            foreach (IntVec3 o in coarse.OrderBy(o => (o - preferred).LengthHorizontalSquared))
            {
                if (Fits(o))
                {
                    offset = o;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 把潛艇的船體搬進 <paramref name="map"/>（座標加 <paramref name="offset"/>），潛艇世界物件移除（和 DMSE 的降落一樣），
        /// 再呼叫 <paramref name="after"/>（參數是搬過去之後的船體格）。在長時間事件裡做，結束後鏡頭切過去。
        /// </summary>
        public static void MoveInto(WorldObject_Submarine sub, Map map, IntVec3 offset, Action<HashSet<IntVec3>> after)
        {
            Map shipMap = sub.Map;
            HashSet<IntVec3> cells = new HashSet<IntVec3>(TransferLandingUtility.HullCells(shipMap));
            LongEventHandler.QueueLongEvent(delegate
            {
                ShipSeparationUtility.RelocateHull(shipMap, map, cells, offset);
                HashSet<IntVec3> hull = new HashSet<IntVec3>(cells.Select(c => c + offset));
                IntVec3 center = CellRect.FromCellList(hull).CenterCell;
                foreach (Pawn p in shipMap.mapPawns.AllPawnsSpawned.ToList())
                {
                    // 不在船體格上的 pawn（例如在船外游泳的潛水員）放到船上
                    if (!CellFinder.TryFindRandomCellNear(center, map, 30, x => x.Standable(map) && hull.Contains(x), out IntVec3 spot)
                        && !CellFinder.TryFindRandomCellNear(center, map, 30, x => x.Standable(map), out spot))
                    {
                        spot = center;
                    }
                    p.DeSpawn();
                    GenSpawn.Spawn(p, spot, map);
                }
                TransferGraceUtility.CarryOver(shipMap, map);
                sub.landingTarget = PlanetTile.Invalid;
                sub.Destroy();
                Current.Game.CurrentMap = map;
                CameraJumper.TryJump(center, map);
                after?.Invoke(hull);
            }, "DepthRim.Surface.Surfacing", doAsynchronously: false, null);
        }

        // ==================================================================
        //  破冰上浮
        // ==================================================================

        public static void BeginIceBreach(WorldObject_Submarine sub)
        {
            string reason = IceBreachBlockReason(sub, out _, out _);
            if (reason != null)
            {
                Messages.Message(reason, sub, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            sub.iceBreachAtTick = Find.TickManager.TicksGame + HoverTicks;
            Messages.Message("DepthRim.IceBreach.Begin".Translate(sub.Label), sub, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        public static void CancelIceBreach(WorldObject_Submarine sub, string why)
        {
            if (sub.iceBreachAtTick < 0)
            {
                return;
            }
            sub.iceBreachAtTick = -1;
            Messages.Message("DepthRim.IceBreach.Cancelled".Translate(sub.Label, why), sub, MessageTypeDefOf.RejectInput, historical: false);
        }

        /// <summary>懸停結束：緊急吹除主壓載、判定。成功時船體出水；失敗時破冰結構受損，記下冷卻與「頂裂過」。</summary>
        public static IceBreachOutcome ResolveIceBreach(WorldObject_Submarine sub)
        {
            sub.iceBreachAtTick = -1;
            string reason = IceBreachBlockReason(sub, out IceConditions.Info ice, out IceBreachOutcome outcome);
            if (reason != null)
            {
                Messages.Message("DepthRim.IceBreach.Cancelled".Translate(sub.Label, reason), sub, MessageTypeDefOf.RejectInput, historical: false);
                outcome.result = IceBreachResult.TooThick;
                return outcome;
            }
            List<CompIceBreakerFrame> frames = CompIceBreakerFrame.On(sub.Map);
            List<IntVec3> contacts = frames.Select(f => f.parent.Position).ToList();
            int now = Find.TickManager.TicksGame;
            sub.nextIceBreachTick = now + RetryTicks;
            CompIceBreakerFrame.Damage(frames, outcome.damagePerFrame);
            string h = ice.thickness.ToString("0.00");
            string r = outcome.ratio.ToString("0.00");
            switch (outcome.result)
            {
                case IceBreachResult.Stuck:
                    Find.LetterStack.ReceiveLetter("DepthRim.IceBreach.StuckLabel".Translate(), "DepthRim.IceBreach.StuckText".Translate(sub.Label, h, r),
                        LetterDefOf.NegativeEvent, sub);
                    break;
                case IceBreachResult.Partial:
                    sub.crackedUntilTick = now + CrackedTicks;
                    Find.LetterStack.ReceiveLetter("DepthRim.IceBreach.PartialLabel".Translate(), "DepthRim.IceBreach.PartialText".Translate(sub.Label, h, r),
                        LetterDefOf.NeutralEvent, sub);
                    break;
                case IceBreachResult.Surfaced:
                case IceBreachResult.Violent:
                    BreakThrough(sub, ice, outcome, contacts);
                    break;
            }
            return outcome;
        }

        private static void BreakThrough(WorldObject_Submarine sub, IceConditions.Info ice, IceBreachOutcome outcome, List<IntVec3> contacts)
        {
            PlanetTile above = SurfaceAbove(sub);
            MapParent site = Find.WorldObjects.MapParentAt(above);
            if (site == null)
            {
                WorldObject_IceBreachSite created = (WorldObject_IceBreachSite)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_IceBreachSite);
                created.Tile = above;
                created.SetFaction(Faction.OfPlayer);
                created.iceThickness = ice.thickness;
                created.iceKind = ice.kind;
                Find.WorldObjects.Add(created);
                site = created;
            }
            Map map = SiteMap(sub, site);
            List<IntVec3> cells = TransferLandingUtility.LandingCells(sub.Map);
            if (!TryFindSpot(map, cells, c => true, out IntVec3 offset))
            {
                sub.landingTarget = PlanetTile.Invalid;
                Messages.Message("DepthRim.Surface.NoRoom".Translate(site.LabelCap), sub, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            string label = sub.Label;
            int seed = above.tileId ^ Find.TickManager.TicksGame;
            SurfacingShow show = SurfacingShow.IceBreach(map, DestinationHull(sub, offset), contacts.Select(c => c + offset).ToList(), ice.thickness, outcome.ratio, seed);
            PlayThenMove(sub, map, offset, show, hull =>
            {
                LeaveIceChunks(map, hull, seed);
                Find.LetterStack.ReceiveLetter("DepthRim.IceBreach.SurfacedLabel".Translate(),
                    "DepthRim.IceBreach.SurfacedText".Translate(label, ice.thickness.ToString("0.00"), outcome.ratio.ToString("0.00"))
                    + (outcome.result == IceBreachResult.Violent ? "\n\n" + "DepthRim.IceBreach.Violent".Translate() : (TaggedString)""),
                    LetterDefOf.PositiveEvent, new TargetInfo(CellRect.FromCellList(hull).CenterCell, map));
            });
        }

        /// <summary>
        /// 冰面開裂（設計稿 §5）：一次套上整個圖樣（測試與除錯用；遊戲裡由出水演出照出現時間一格一格套上，見 <see cref="SurfacingShow"/>）。
        /// </summary>
        public static IceCrackPattern.Result ApplyBreach(Map map, HashSet<IntVec3> hull, List<IntVec3> contacts, float thickness, float ratio, int seed)
        {
            SurfacingShow show = SurfacingShow.IceBreach(map, hull, contacts, thickness, ratio, seed);
            show.ApplyAll();
            LeaveIceChunks(map, hull, seed);
            return show.pattern;
        }

        /// <summary>出水後甲板（露天的船體格）上留下的碎冰。</summary>
        public static void LeaveIceChunks(Map map, ICollection<IntVec3> hull, int seed)
        {
            Rand.PushState(seed);
            foreach (IntVec3 c in hull)
            {
                if (!c.Roofed(map) && c.Standable(map) && Rand.Chance(0.3f))
                {
                    FilthMaker.TryMakeFilth(c, map, DepthRimDefOf.DepthRim_Filth_IceChunks);
                }
            }
            Rand.PopState();
        }

        /// <summary>船體周圍的開放水濺起大水花（最多 <paramref name="max"/> 個，每 8 格一個）。</summary>
        public static int Splash(Map map, ICollection<IntVec3> hull, int max, float scale)
        {
            EffecterDef def = DefDatabase<EffecterDef>.GetNamedSilentFail("PawnEmergeFromWaterLarge");
            if (def == null)
            {
                return 0;
            }
            List<IntVec3> ring = MapComponent_IceSheet.RingAround(map, hull)
                .Where(c => c.GetTerrain(map) == DepthRimDefOf.DepthRim_BreachWater || SeaSurface.IsOpenSea(c, map)).ToList();
            int step = Mathf.Max(1, Mathf.Max(8, ring.Count / Mathf.Max(1, max)));
            int count = 0;
            for (int i = 0; i < ring.Count && count < max; i += step)
            {
                Effecter e = def.Spawn(ring[i], map, scale);
                map.effecterMaintainer.AddEffecterToMaintain(e, ring[i], 60);
                count++;
            }
            return count;
        }

        // ==================================================================
        //  下潛（從海面或冰面）
        // ==================================================================

        public static bool IsSurfacedMap(Map map)
        {
            return map?.Parent is WorldObject_SeaSurfaceSite || map?.Parent is WorldObject_IceBreachSite;
        }

        /// <summary>下潛後潛艇停的海洋格：正下方；那裡已經有東西時找旁邊能航行、沒有東西的格子。</summary>
        public static PlanetTile FindDiveTile(PlanetTile surface)
        {
            PlanetTile below = OceanWorldView.TileBelow(surface);
            if (!below.Valid || !OceanRouting.Navigable(below))
            {
                return PlanetTile.Invalid;
            }
            if (!Find.WorldObjects.AnyWorldObjectAt(below))
            {
                return below;
            }
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(below, neighbors);
            return neighbors.FirstOrDefault(n => OceanRouting.Navigable(n) && !Find.WorldObjects.AnyWorldObjectAt(n));
        }

        public static string DiveBlockReason(Thing helm, out PileNetwork net, out PlanetTile below)
        {
            net = null;
            below = PlanetTile.Invalid;
            Map map = helm?.Map;
            if (!IsSurfacedMap(map))
            {
                return "DepthRim.Surface.NotSurfaced".Translate();
            }
            net = MapComponent_PileNetwork.For(map)?.NetworkContaining(helm);
            if (net == null)
            {
                return "DepthRim.Surface.NotOnHull".Translate();
            }
            net.RecalculateFootprint();
            below = FindDiveTile(map.Tile);
            if (!below.Valid)
            {
                return "DepthRim.Surface.NoSeaBelow".Translate();
            }
            MapComponent_IceSheet sheet = MapComponent_IceSheet.For(map);
            if (sheet != null && sheet.Active && sheet.FrozenIn(net.Footprint.ToList()))
            {
                return "DepthRim.IceBreach.FrozenIn".Translate(sheet.ThickestAround(net.Footprint.ToList()).ToString("0.00"), IceBreachMath.FrozenInThickness.ToString("0.0"));
            }
            return null;
        }

        /// <summary>從海面或冰面下潛：船體分離成正下方海洋格的潛艇（從淺海往 endTier 下潛、停著），播下潛演出。回傳新的潛艇（排進長時間事件，事件跑完才有地圖）。</summary>
        public static void Dive(Thing helm, DepthTier endTier = DepthTier.Shallow)
        {
            string reason = DiveBlockReason(helm, out PileNetwork net, out PlanetTile below);
            if (reason != null)
            {
                Messages.Message(reason, helm, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            Map origin = helm.Map;
            HashSet<IntVec3> cells = new HashSet<IntVec3>(net.Footprint);
            string name = helm.TryGetComp<CompTransferConsole>()?.TakeShipNameForLaunch();
            LongEventHandler.QueueLongEvent(delegate
            {
                PlanetLayerDef ocean = DepthRimDefOf.DepthRim_Ocean;
                WorldObject_Submarine sub = (WorldObject_Submarine)WorldObjectMaker.MakeWorldObject(TransferEnvironment.ShipWorldObject(ocean));
                sub.Tile = below;
                sub.SetFaction(Faction.OfPlayer);
                if (!name.NullOrEmpty())
                {
                    sub.Name = name;
                }
                sub.origin = below;
                sub.destination = below;
                sub.phase = TransferPhase.Arrived;
                sub.InitDepth(DepthTier.Shallow);
                Find.WorldObjects.Add(sub);
                if (endTier != DepthTier.Shallow)
                {
                    sub.SetTargetTier(endTier);
                }
                Map shipMap = MapGenerator.GenerateMap(origin.Size, sub, TransferEnvironment.ShipMapGenerator(ocean));
                // 和出港一樣把船體放在航行地圖正中央（海洋層的 centerHullOnShipMap）
                IntVec3 shipOffset = ShipSeparationUtility.CenteringOffset(cells, shipMap.Size);
                ShipSeparationUtility.RelocateHull(origin, shipMap, cells, shipOffset);
                HashSet<IntVec3> shipCells = new HashSet<IntVec3>(cells.Select(c => c + shipOffset));
                MapComponent_PileNetwork.For(shipMap)?.MarkDirty();
                Current.Game.CurrentMap = shipMap;
                string label = sub.Label;
                Action finish = delegate
                {
                    Current.Game.CurrentMap = shipMap;
                    Messages.Message("DepthRim.Surface.Dived".Translate(label), sub, MessageTypeDefOf.PositiveEvent);
                };
                if (skipCutscenes || !Prefs.GravshipCutscenes || !ModsConfig.OdysseyActive)
                {
                    finish();
                    return;
                }
                LongEventHandler.ExecuteWhenFinished(delegate
                {
                    // 下潛演出：船體在原地沉下去（不是離港那樣滑走），見 SubmarineDiveCutscene
                    Find.WindowStack.Add(new SubmarineDiveCutscene(origin, CellRect.FromCellList(cells).CenterVector3, finish, shipMap, shipCells, cells));
                });
            }, "DepthRim.Surface.Diving", doAsynchronously: false, null);
        }

        /// <summary>被凍住時用破冰結構把船體四周撞開（每座受 <see cref="BreakFreeDamage"/> 平分的傷）。</summary>
        public static string BreakFreeBlockReason(Thing helm, out PileNetwork net, out List<CompIceBreakerFrame> frames)
        {
            frames = new List<CompIceBreakerFrame>();
            net = null;
            Map map = helm?.Map;
            MapComponent_IceSheet sheet = MapComponent_IceSheet.For(map);
            if (!(map?.Parent is WorldObject_IceBreachSite) || sheet == null)
            {
                return "DepthRim.Surface.NotSurfaced".Translate();
            }
            net = MapComponent_PileNetwork.For(map)?.NetworkContaining(helm);
            if (net == null)
            {
                return "DepthRim.Surface.NotOnHull".Translate();
            }
            HashSet<IntVec3> footprint = new HashSet<IntVec3>(net.Footprint);
            frames = CompIceBreakerFrame.On(map).Where(f => footprint.Contains(f.parent.Position)).ToList();
            if (frames.Count == 0)
            {
                return "DepthRim.IceBreach.NoFrame".Translate();
            }
            if (!sheet.FrozenIn(footprint))
            {
                return "DepthRim.IceBreach.NotFrozen".Translate();
            }
            return null;
        }

        public static void BreakFree(Thing helm)
        {
            string reason = BreakFreeBlockReason(helm, out PileNetwork net, out List<CompIceBreakerFrame> frames);
            if (reason != null)
            {
                Messages.Message(reason, helm, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            HashSet<IntVec3> footprint = new HashSet<IntVec3>(net.Footprint);
            MapComponent_IceSheet.For(helm.Map).OpenRing(footprint);
            CompIceBreakerFrame.Damage(frames, BreakFreeDamage / frames.Count);
            Splash(helm.Map, footprint, 6, 1f);
            Messages.Message("DepthRim.IceBreach.BrokeFree".Translate(), helm, MessageTypeDefOf.PositiveEvent);
        }

        // ==================================================================
        //  量測冰層
        // ==================================================================

        public static string MeasureReport(WorldObject_Submarine sub)
        {
            IceConditions.Info ice = IceConditions.At(SurfaceAbove(sub));
            List<CompIceBreakerFrame> frames = CompIceBreakerFrame.On(sub.Map);
            string kind = ("DepthRim.IceKind." + ice.kind).Translate();
            if (ice.land && ice.kind != IceKind.Shelf)
            {
                return "DepthRim.Surface.Land".Translate();
            }
            if (!ice.Frozen)
            {
                return "DepthRim.IceBreach.MeasureOpen".Translate(kind);
            }
            IceBreachOutcome o = Estimate(sub, ice, frames);
            string lead = ice.lead ? "DepthRim.IceBreach.MeasureLead".Translate().ToString() : "";
            string result = frames.Count == 0 ? "DepthRim.IceBreach.NoFrame".Translate().ToString() : ("DepthRim.IceBreach.Result." + o.result).Translate().ToString();
            return "DepthRim.IceBreach.MeasureText".Translate(kind, ice.thickness.ToString("0.00"), lead, ice.airTemperature.ToString("0"),
                IceBreachMath.FlexuralStrength(ice.airTemperature).ToString("0.00"), HullCellCount(sub.Map), o.ratio.ToString("0.00"), result);
        }

        // ==================================================================
        //  指令
        // ==================================================================

        /// <summary>潛艇（海洋層）上航行指令以外的指令：取消破冰懸停、量測冰層。上浮與破冰上浮在航行指令的選單裡。</summary>
        public static IEnumerable<Gizmo> SubmarineGizmos(WorldObject_Submarine sub)
        {
            if (!sub.HasMap)
            {
                yield break;
            }
            if (sub.iceBreachAtTick >= 0)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DepthRim.IceBreach.CancelLabel".Translate(),
                    defaultDesc = "DepthRim.IceBreach.CancelDesc".Translate((sub.iceBreachAtTick - Find.TickManager.TicksGame).ToStringTicksToPeriod()),
                    icon = IceIcon,
                    action = () => CancelIceBreach(sub, "DepthRim.IceBreach.CancelledByPlayer".Translate()),
                };
            }

            Command_Action measure = new Command_Action
            {
                defaultLabel = "DepthRim.IceBreach.MeasureLabel".Translate(),
                defaultDesc = "DepthRim.IceBreach.MeasureDesc".Translate(),
                icon = IceIcon,
                action = () => Find.WindowStack.Add(new Dialog_MessageBox(MeasureReport(sub))),
            };
            string depth = DepthBlock(sub);
            if (depth != null && sub.iceBreachAtTick < 0)
            {
                measure.Disable(depth);
            }
            yield return measure;
        }

        /// <summary>海面或冰面地圖上艦橋航行指令以外的指令：撞開凍冰。下潛在航行指令的選單裡。</summary>
        public static IEnumerable<Gizmo> SurfacedGizmos(Thing helm)
        {
            if (helm.Map?.Parent is WorldObject_IceBreachSite)
            {
                Command_Action free = new Command_Action
                {
                    defaultLabel = "DepthRim.IceBreach.BreakFreeLabel".Translate(),
                    defaultDesc = "DepthRim.IceBreach.BreakFreeDesc".Translate(BreakFreeDamage.ToString("0")),
                    icon = IceIcon,
                    action = () => BreakFree(helm),
                };
                string freeBlock = BreakFreeBlockReason(helm, out _, out _);
                if (freeBlock != null)
                {
                    free.Disable(freeBlock);
                }
                yield return free;
            }
        }
    }
}
