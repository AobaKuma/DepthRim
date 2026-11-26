using System;
using System.Collections.Generic;
using System.Linq;
using DMSE;
using Fortified;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>水面推進器的推力（M2 的浮航推力檢查讀它；沒有 DMSE 的 CompTransferEngine，不算進水下航行）。</summary>
    public class CompProperties_SurfaceThruster : CompProperties
    {
        public float thrust = 3f;

        public CompProperties_SurfaceThruster()
        {
            compClass = typeof(CompSurfaceThruster);
        }
    }

    public class CompSurfaceThruster : ThingComp
    {
        public CompProperties_SurfaceThruster Props => (CompProperties_SurfaceThruster)props;
    }

    /// <summary>
    /// 海岸造船 M1 的「下水」（Docs/海岸船舶與潛水鐘_機制設計.md §3.1）：在原版海岸地圖的灣裡造好的船
    /// （龍骨網路圍起來的船體平台），從艦橋下令開出港，整具船體搬到殖民地旁邊那一格海面的海面地點
    /// （<see cref="WorldObject_SeaSurfaceSite"/>，沒有就建立）。灣裡那幾格由 <see cref="Patch_SeaSurfaceVoid"/> 還原成原本的水。
    /// <para>只做「離港到隔壁的海」：在海上移動是 M2（SurfaceVoyage），開回港口靠岸是 M3，演出是 M4。</para>
    /// </summary>
    public static class CoastalLaunch
    {
        /// <summary>測試用：略過演出與鏡頭（現在沒有演出，只影響鏡頭切換）。</summary>
        public static bool quiet;

        /// <summary>這張地圖上可以下水：原版地表地圖（海岸殖民地）。</summary>
        public static bool IsShipyardMap(Map map) => SeaLevels.IsVanillaSurface(map);

        /// <summary>
        /// 殖民地旁邊能出港的海面格：相鄰、是海、沒有冰（<see cref="IceConditions.Info.OpenSea"/>），
        /// 上面沒有東西或只有海面地點（已經有的海面地點優先，否則挑第一個）。沒有時 Invalid。
        /// </summary>
        public static PlanetTile FindLaunchTile(PlanetTile home, int preferredTileId = -1)
        {
            if (!home.Valid)
            {
                return PlanetTile.Invalid;
            }
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(home, neighbors);
            // 靠岸時記下的那一格海（船從哪裡來就從哪裡出去，設計稿 §4.1）優先
            if (preferredTileId >= 0)
            {
                PlanetTile preferred = neighbors.FirstOrDefault(n => n.tileId == preferredTileId);
                if (preferred.Valid && IceConditions.At(preferred).OpenSea && Find.WorldObjects.MapParentAt(preferred) is null or WorldObject_SeaSurfaceSite)
                {
                    return preferred;
                }
            }
            PlanetTile first = PlanetTile.Invalid;
            foreach (PlanetTile n in neighbors)
            {
                if (!IceConditions.At(n).OpenSea)
                {
                    continue;
                }
                MapParent parent = Find.WorldObjects.MapParentAt(n);
                if (parent is WorldObject_SeaSurfaceSite)
                {
                    return n;
                }
                if (parent == null && !Find.WorldObjects.AnyWorldObjectAt(n) && !first.Valid)
                {
                    first = n;
                }
            }
            return first;
        }

        /// <summary>下水不能做的原因（可以時 null）；同時給船體網路與出港的海面格。</summary>
        public static string BlockReason(Thing helm, out PileNetwork net, out PlanetTile target)
        {
            net = null;
            target = PlanetTile.Invalid;
            Map map = helm?.Map;
            if (!IsShipyardMap(map))
            {
                return "DepthRim.Launch.NotInPort".Translate();
            }
            net = MapComponent_PileNetwork.For(map)?.NetworkContaining(helm);
            if (net == null)
            {
                return "DepthRim.Surface.NotOnHull".Translate();
            }
            net.RecalculateFootprint();
            if (net.Footprint.Count == 0)
            {
                return "DepthRim.Surface.NotOnHull".Translate();
            }
            CompPowerTrader power = helm.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                return "DepthRim.Launch.NoPower".Translate();
            }
            HashSet<IntVec3> footprint = net.Footprint;
            Building_Gangway gangway = map.listerThings.AllThings.OfType<Building_Gangway>()
                .FirstOrDefault(g => footprint.Contains(g.Position) && g.State != GangwayState.Retracted);
            if (gangway != null)
            {
                return "DepthRim.Launch.GangwayOut".Translate();
            }
            string name = helm.TryGetComp<CompTransferConsole>()?.ShipName;
            string bellLock = DiveBellLock.Reason(map, footprint);
            if (bellLock != null)
            {
                return bellLock;
            }
            target = FindLaunchTile(map.Tile, map.GetComponent<MapComponent_Berths>()?.For(name)?.seaTileId ?? -1);
            if (!target.Valid)
            {
                return "DepthRim.Launch.NoSea".Translate();
            }
            return null;
        }

        public static Command LaunchGizmo(Thing helm)
        {
            Command_Action cmd = new Command_Action
            {
                defaultLabel = "DepthRim.Launch.Label".Translate(),
                defaultDesc = "DepthRim.Launch.Desc".Translate(),
                icon = SubmarineIcons.RoutePlanning,
                action = () => Launch(helm),
            };
            string reason = BlockReason(helm, out _, out _);
            if (reason != null)
            {
                cmd.Disable(reason);
            }
            return cmd;
        }

        /// <summary>
        /// 下水：建立或重用隔壁的海面地點，在開放海面上找得下船體的位置（<see cref="SubmarineSurfacing.TryFindSpot"/>），
        /// 在長時間事件裡 <see cref="ShipSeparationUtility.RelocateHull"/>。回傳是否開始（失敗時已發訊息）。
        /// </summary>
        public static bool Launch(Thing helm)
        {
            string reason = BlockReason(helm, out PileNetwork net, out PlanetTile target);
            if (reason != null)
            {
                Messages.Message(reason, helm, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            Map origin = helm.Map;
            HashSet<IntVec3> cells = new HashSet<IntVec3>(net.Footprint);
            List<IntVec3> cellList = cells.ToList();
            MapParent site = Find.WorldObjects.MapParentAt(target);
            bool created = false;
            if (site == null)
            {
                site = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_SeaSurfaceSite);
                site.Tile = target;
                site.SetFaction(Faction.OfPlayer);
                Find.WorldObjects.Add(site);
                created = true;
            }
            Map sea = site.HasMap ? site.Map
                : GetOrGenerateMapUtility.GetOrGenerateMap(site.Tile, TransferLandingUtility.RequiredMapSize(site, CellRect.FromCellList(cellList)), site.def);
            if (!SubmarineSurfacing.TryFindSpot(sea, cellList, c => SeaSurface.IsOpenSea(c, sea), out IntVec3 offset))
            {
                if (created && !sea.mapPawns.AnyPawnBlockingMapRemoval && !sea.listerBuildings.allBuildingsColonist.Any())
                {
                    site.Destroy();
                }
                Messages.Message("DepthRim.Surface.NoRoom".Translate(site.LabelCap), helm, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            string label = site.LabelCap;
            LongEventHandler.QueueLongEvent(delegate
            {
                ShipSeparationUtility.RelocateHull(origin, sea, cells, offset);
                HashSet<IntVec3> hull = new HashSet<IntVec3>(cells.Select(c => c + offset));
                MapComponent_PileNetwork.For(origin)?.MarkDirty();
                MapComponent_PileNetwork.For(sea)?.MarkDirty();
                IntVec3 center = CellRect.FromCellList(hull).CenterCell;
                SubmarineSurfacing.Splash(sea, hull, 12, 1.5f);
                DepthRimDefOf.DepthRim_Breach?.PlayOneShot(new TargetInfo(center, sea));
                Find.LetterStack.ReceiveLetter("DepthRim.Launch.LetterLabel".Translate(), "DepthRim.Launch.LetterText".Translate(label),
                    LetterDefOf.PositiveEvent, new TargetInfo(center, sea));
                Action follow = () =>
                {
                    if (!quiet && Find.Maps.Contains(sea))
                    {
                        Current.Game.CurrentMap = sea;
                        CameraJumper.TryJump(center, sea);
                    }
                    // 從停泊點出港、上面沒人也沒建築：照奧德賽重力船離開的做法放棄那張地圖
                    LongEventHandler.ExecuteWhenFinished(() =>
                    {
                        if (Find.Maps.Contains(origin))
                        {
                            Docking.AbandonIfUnused(origin);
                        }
                    });
                };
                // 離港演出（M4）：船體已經搬到海上，殖民地上只剩影像——從泊位退出、轉向、沿路線開出地圖邊
                if (quiet || Docking.SkipShow())
                {
                    follow();
                    return;
                }
                LongEventHandler.ExecuteWhenFinished(() => Find.WindowStack.Add(Docking.MakeShow(origin, cells, follow, sea, hull, departing: true, shortVersion: false)));
            }, "DepthRim.Launch.Launching", doAsynchronously: false, null);
            return true;
        }
    }
}
