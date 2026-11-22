using System;
using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 水面航行 M2（Docs/海岸船舶與潛水鐘_機制設計.md §4）：不掛 DMSE 的轉移飛行（那綁在海洋層的 <c>TransferEnvironmentExtension</c>），
    /// 自己寫的輕量版：沿用 <see cref="OceanRouting.Bfs"/>（地表海路見 <see cref="SurfaceRouting"/>）與 <see cref="ShipSeparationUtility.RelocateHull"/>，
    /// 跳過點火、升空、降落。
    /// <list type="bullet">
    /// <item>出航：艦橋所在龍骨網路的船體搬進一張新的 <see cref="WorldObject_SurfaceShip"/> 地圖（同座標），沿海路逐格走。
    /// 不在船體上的人（在海裡游的）留在原地點。原地點沒人沒建築就照海面地點的規則自己移除。</item>
    /// <item>出航前檢查（一次列出第一個沒就緒的項目）：艦橋有電、船上有能動的水面推進器、龍骨網路上只有一座艦橋（兩艘船的龍骨不能連在一起）。
    /// 船上伸出的舷梯會自動收起，收完才離港（<see cref="GameComponent_SurfaceSail"/>）。潛水鐘（M5）做好後也在這裡擋。</item>
    /// <item>推力與耗電：水面推進器在航行中才全速耗電（<see cref="CompThrusterDraw"/>），電池沒電推進器就停、船就漂著不動。
    /// 水下推進器浮在水面時不出力。</item>
    /// <item>到達：見 <see cref="WorldObject_SurfaceShip"/>。</item>
    /// </list>
    /// </summary>
    public static class SurfaceVoyage
    {
        /// <summary>出航用的船體資料（艦橋所在的龍骨網路）。</summary>
        public sealed class Plan
        {
            public PileNetwork net;
            public HashSet<IntVec3> footprint;
            public float thrust;
            public float watts;
            public float storedWd;
            public float hoursPerTile;
            public int range;
        }

        // ── 船上的東西 ──

        private static IEnumerable<Building> SurfaceThrusters(Map map, ICollection<IntVec3> footprint)
        {
            return map.listerBuildings.allBuildingsColonist.Where(b => b.TryGetComp<CompSurfaceThruster>() != null && (footprint == null || footprint.Contains(b.Position)));
        }

        /// <summary>這座水面推進器現在出不出力：有電（開關打開）、沒故障。</summary>
        public static bool Operational(Building thruster)
        {
            CompPowerTrader power = thruster.TryGetComp<CompPowerTrader>();
            CompBreakdownable breakdown = thruster.TryGetComp<CompBreakdownable>();
            return (power == null || power.PowerOn) && (breakdown == null || !breakdown.BrokenDown);
        }

        /// <summary>現在的水面推力（只算能運作的水面推進器；<paramref name="footprint"/> = null 時整張地圖）。</summary>
        public static float CurrentThrust(Map map, ICollection<IntVec3> footprint = null)
        {
            if (map == null)
            {
                return 0f;
            }
            float thrust = 0f;
            foreach (Building b in SurfaceThrusters(map, footprint))
            {
                if (Operational(b))
                {
                    thrust += b.GetComp<CompSurfaceThruster>().Props.thrust;
                }
            }
            return thrust;
        }

        /// <summary>
        /// 艦橋所在的船體：龍骨網路、推力、全速耗電、電池存量、每格時間與航程。不在龍骨網路上時 null。
        /// 推力只算「沒故障」的水面推進器（電的有無由出航前檢查另外擋：出港前推進器在待機，有接電就算）。
        /// </summary>
        public static Plan PlanFor(Thing helm)
        {
            Map map = helm?.Map;
            PileNetwork net = map == null ? null : MapComponent_PileNetwork.For(map)?.NetworkContaining(helm);
            if (net == null)
            {
                return null;
            }
            net.RecalculateFootprint();
            Plan plan = new Plan { net = net, footprint = new HashSet<IntVec3>(net.Footprint) };
            foreach (Building b in SurfaceThrusters(map, plan.footprint))
            {
                CompBreakdownable breakdown = b.TryGetComp<CompBreakdownable>();
                CompPowerTrader power = b.TryGetComp<CompPowerTrader>();
                if ((breakdown != null && breakdown.BrokenDown) || (power != null && !power.PowerOn))
                {
                    continue;
                }
                plan.thrust += b.GetComp<CompSurfaceThruster>().Props.thrust;
                plan.watts += power?.Props.PowerConsumption ?? 0f;
            }
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (plan.footprint.Contains(b.Position) && b.TryGetComp<CompPowerBattery>() is CompPowerBattery battery)
                {
                    plan.storedWd += battery.StoredEnergy;
                }
            }
            plan.hoursPerTile = SurfaceVoyageMath.HoursPerTile(plan.thrust, plan.footprint.Count);
            plan.range = SurfaceVoyageMath.RangeTiles(plan.storedWd, plan.watts, plan.hoursPerTile);
            return plan;
        }

        public static WorldObject_SurfaceShip ShipOf(Thing helm) => helm?.Map?.Parent as WorldObject_SurfaceShip;

        /// <summary>地圖上的艦橋（有下潛控制與轉移控制台的建築；船地圖上只有一座）。沒有時 null。</summary>
        /// <summary>
        /// 船地圖上的艦橋。母船載子船時會有好幾座：<paramref name="shipName"/> 給了就優先挑名字對得上的（出航時用的那座），
        /// 否則第一座。
        /// </summary>
        public static Building HelmOn(Map map, string shipName = null)
        {
            if (map == null)
            {
                return null;
            }
            Building first = null;
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (b.TryGetComp<CompDiveControl>() == null || !(b.TryGetComp<CompTransferConsole>() is CompTransferConsole console))
                {
                    continue;
                }
                if (shipName.NullOrEmpty() || console.ShipName == shipName)
                {
                    return b;
                }
                first ??= b;
            }
            return first;
        }

        /// <summary>
        /// 船名：玩家在艦橋取的名字優先；沒有就給一個水面船的預設名並存回艦橋（DMSE 的預設名在地表圖層會叫「轉移艦」）。
        /// 航行、靠岸、泊位紀錄都用這個名字。
        /// </summary>
        public static string ShipNameFor(Thing helm)
        {
            CompTransferConsole console = helm?.TryGetComp<CompTransferConsole>();
            string name = console?.ShipName;
            if (name.NullOrEmpty())
            {
                name = "DepthRim.Sail.DefaultName".Translate(GameComponent_SurfaceSail.Instance?.NextShipNumber() ?? 1);
                console?.SetShipName(name);
            }
            return name;
        }

        /// <summary>
        /// 目的地：點到能靠的海岸格（<see cref="Docking.IsCoastTarget"/>：殖民地、停泊點或沒有東西的海岸陸地）時，
        /// 改成它旁邊海路最近的一格海（開到外海，抵達後選泊位）。其他格照原樣。
        /// </summary>
        public static PlanetTile ResolveDestination(PlanetTile origin, PlanetTile tile)
        {
            if (!tile.Valid || !Docking.IsCoastTarget(tile))
            {
                return tile;
            }
            List<PlanetTile> neighbors = new List<PlanetTile>();
            Find.WorldGrid.GetTileNeighbors(tile, neighbors);
            PlanetTile best = tile;
            int bestDist = OceanRouting.Unreachable;
            foreach (PlanetTile n in neighbors)
            {
                if (n == origin)
                {
                    return n;
                }
                if (!SurfaceRouting.Navigable(n))
                {
                    continue;
                }
                MapParent other = Find.WorldObjects.MapParentAt(n);
                if (other != null && !(other is WorldObject_SeaSurfaceSite))
                {
                    continue;
                }
                int d = SurfaceRouting.Distance(origin, n);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = n;
                }
            }
            return best;
        }

        /// <summary>
        /// 點的是能靠的海岸格、而且船就停在它旁邊的海格上：回傳那一格（直接靠岸，不用再航行）。其他情況 Invalid。
        /// </summary>
        public static PlanetTile AdjacentDockTarget(PlanetTile origin, PlanetTile tile)
        {
            if (!origin.Valid || !tile.Valid || tile == origin || tile.Layer != origin.Layer || !Find.WorldGrid.IsNeighbor(origin, tile))
            {
                return PlanetTile.Invalid;
            }
            return Docking.IsCoastTarget(tile) ? tile : PlanetTile.Invalid;
        }

        /// <summary>直接靠到旁邊海岸格不能做的原因（可以時 null）：已有地圖就照那張地圖檢查，否則只檢查船。</summary>
        public static string CoastDockBlockReason(Thing helm, PlanetTile coast)
        {
            Map map = Docking.CoastMap(coast);
            return map != null ? Docking.BlockReason(helm, out _, out _, map) : Docking.ShipBlockReason(helm, out _);
        }

        /// <summary>海岸格的名字（殖民地或停泊點用它的名字，沒有地圖時是「海岸」）。</summary>
        public static string CoastLabel(PlanetTile coast)
        {
            Map map = Docking.CoastMap(coast);
            return map != null ? map.Parent.LabelCap.ToString() : "DepthRim.Dock.Coast".Translate().ToString();
        }

        // ── 出航前檢查 ──

        /// <summary>從這座艦橋出航不能做的原因（可以時 null）。</summary>
        public static string BlockReason(Thing helm, out Plan plan)
        {
            plan = null;
            Map map = helm?.Map;
            if (map == null || !SeaLevels.IsSeaLevel(map))
            {
                return "DepthRim.Sail.NotAtSea".Translate();
            }
            if (ShipOf(helm) is WorldObject_SurfaceShip ship && ship.underWay)
            {
                return "DepthRim.Sail.AlreadyUnderWay".Translate();
            }
            plan = PlanFor(helm);
            if (plan == null || plan.footprint.Count == 0)
            {
                return "DepthRim.Surface.NotOnHull".Translate();
            }
            CompPowerTrader power = helm.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                return "DepthRim.Launch.NoPower".Translate();
            }
            // 2026-11-21：同一個龍骨網路上可以有多座艦橋（母船載子船），整個網路一起走，不再擋
            // 潛水鐘沒收回不能走（§7.7）
            string bellLock = DiveBellLock.Reason(map, plan.footprint);
            if (bellLock != null)
            {
                return bellLock;
            }
            if (plan.thrust <= 0f)
            {
                return "DepthRim.Sail.NoThruster".Translate();
            }
            if (plan.range < 1)
            {
                return "DepthRim.Sail.NoCharge".Translate();
            }
            return null;
        }

        /// <summary>這一格能不能當目的地（可以時 null）。</summary>
        public static string DestinationBlockReason(PlanetTile origin, PlanetTile tile, int range)
        {
            if (!tile.Valid || tile.LayerDef != PlanetLayerDefOf.Surface)
            {
                return "DepthRim.Sail.NotSurface".Translate();
            }
            if (tile == origin)
            {
                return "DepthRim.Sail.Here".Translate();
            }
            if (!SurfaceRouting.Navigable(tile))
            {
                return "DepthRim.Sail.NotOpenSea".Translate();
            }
            MapParent parent = Find.WorldObjects.MapParentAt(tile);
            if (parent != null && !(parent is WorldObject_SeaSurfaceSite))
            {
                return "DepthRim.Surface.Occupied".Translate(parent.LabelCap);
            }
            if (parent is WorldObject_SurfaceShip other && other.underWay)
            {
                return "DepthRim.Surface.Occupied".Translate(other.LabelCap);
            }
            int distance = SurfaceRouting.Distance(origin, tile);
            if (distance >= OceanRouting.Unreachable)
            {
                return "DepthRim.Route.Unreachable".Translate();
            }
            if (distance > range)
            {
                return "DepthRim.Sail.OutOfRange".Translate(distance, range);
            }
            return null;
        }

        // ── 指令 ──

        public static FloatMenuOption SailOption(Thing helm)
        {
            string block = BlockReason(helm, out _);
            FloatMenuOption option = new FloatMenuOption("DepthRim.Sail.Option".Translate(), () => BeginPicking(helm), SubmarineIcons.RoutePlanning, Color.white);
            if (block != null)
            {
                option.Label = "DepthRim.Sail.Option".Translate() + " (" + block + ")";
                option.Disabled = true;
            }
            return option;
        }

        public static FloatMenuOption StopOption(WorldObject_SurfaceShip ship)
        {
            return new FloatMenuOption("DepthRim.Sail.Stop".Translate(), () => StopHere(ship), SubmarineIcons.Anchoring, Color.white);
        }

        public static Command StopGizmo(WorldObject_SurfaceShip ship)
        {
            return new Command_Action
            {
                defaultLabel = "DepthRim.Sail.Stop".Translate(),
                defaultDesc = "DepthRim.Sail.StopDesc".Translate(),
                icon = SubmarineIcons.Anchoring,
                action = () => StopHere(ship),
            };
        }

        /// <summary>在世界地圖上選目的地（只能選航程內、海路到得了的海面格）。</summary>
        public static void BeginPicking(Thing helm)
        {
            string block = BlockReason(helm, out Plan plan);
            if (block != null)
            {
                Messages.Message(block, helm, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            PlanetTile origin = helm.Map.Tile;
            int range = Mathf.Min(plan.range, SubmarineEnergyWorker.MaxRangeTiles);
            CameraJumper.TryJump(CameraJumper.GetWorldTarget(helm), CameraJumper.MovementMode.Pan);
            Find.WorldSelector.ClearSelection();
            Find.WorldTargeter.BeginTargeting(target =>
            {
                // 2026-11-22：船已經在殖民地旁邊的海格（剛下水、取消靠岸後）時，點殖民地直接開始選泊位，
                // 不再解析成「目前這一格」被當成「已經在這裡」擋掉
                PlanetTile dockAt = AdjacentDockTarget(origin, target.Tile);
                if (dockAt.Valid)
                {
                    string dockBlock = CoastDockBlockReason(helm, dockAt);
                    if (dockBlock != null)
                    {
                        Messages.Message(dockBlock, MessageTypeDefOf.RejectInput, historical: false);
                        return false;
                    }
                    Docking.BeginAtCoast(helm, dockAt);
                    return true;
                }
                PlanetTile dest = ResolveDestination(origin, target.Tile);
                string reason = DestinationBlockReason(origin, dest, range);
                if (reason != null)
                {
                    Messages.Message(reason, MessageTypeDefOf.RejectInput, historical: false);
                    return false;
                }
                Depart(helm, dest, dest != target.Tile ? target.Tile : PlanetTile.Invalid);
                return true;
            }, canTargetTiles: true, mouseAttachment: SubmarineIcons.RoutePlanning, closeWorldTabWhenFinished: true,
            onUpdate: () =>
            {
                if (helm.Spawned && range > 0)
                {
                    GenDraw.DrawWorldRadiusRing(origin, range);
                }
            },
            extraLabelGetter: target =>
            {
                PlanetTile dockAt = AdjacentDockTarget(origin, target.Tile);
                if (dockAt.Valid)
                {
                    return CoastDockBlockReason(helm, dockAt) ?? "DepthRim.Dock.Option".Translate(CoastLabel(dockAt)).ToString();
                }
                PlanetTile dest = ResolveDestination(origin, target.Tile);
                return DestinationBlockReason(origin, dest, range)
                    ?? "DepthRim.Sail.Eta".Translate(SurfaceRouting.Distance(origin, dest),
                        Mathf.RoundToInt(SurfaceRouting.Distance(origin, dest) * plan.hoursPerTile * GenDate.TicksPerHour).ToStringTicksToPeriod()).ToString();
            },
            canSelectTarget: target => AdjacentDockTarget(origin, target.Tile) is PlanetTile dockAt && dockAt.Valid
                ? CoastDockBlockReason(helm, dockAt) == null
                : DestinationBlockReason(origin, ResolveDestination(origin, target.Tile), range) == null,
            originForClosest: origin, showCancelButton: true);
        }

        /// <summary>
        /// 出航到 <paramref name="destination"/>：船上有伸出的舷梯就先收起來、收完才走（回傳 false，排進 <see cref="GameComponent_SurfaceSail"/>）；
        /// 否則立刻出航（回傳 true）。不能出航時發訊息、回傳 false。
        /// </summary>
        public static bool Depart(Thing helm, PlanetTile destination, PlanetTile? coast = null)
        {
            string block = BlockReason(helm, out Plan plan);
            if (block == null)
            {
                int range = Mathf.Min(plan.range, SubmarineEnergyWorker.MaxRangeTiles);
                block = DestinationBlockReason(helm.Map.Tile, destination, range);
            }
            if (block != null)
            {
                Messages.Message(block, helm, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            List<Building_Gangway> gangways = GangwaysOut(helm.Map, plan.footprint);
            if (gangways.Count > 0)
            {
                foreach (Building_Gangway g in gangways)
                {
                    if (g.State != GangwayState.Retracting)
                    {
                        g.Retract();
                    }
                }
                GameComponent_SurfaceSail.Instance?.Queue(helm, destination, plan.footprint, coast);
                Messages.Message("DepthRim.Sail.RetractingGangway".Translate(), helm, MessageTypeDefOf.NeutralEvent, historical: false);
                return false;
            }
            DepartNow(helm, destination, plan, coast);
            return true;
        }

        public static List<Building_Gangway> GangwaysOut(Map map, ICollection<IntVec3> footprint)
        {
            return map.listerThings.AllThings.OfType<Building_Gangway>().Where(g => footprint.Contains(g.Position) && g.State != GangwayState.Retracted).ToList();
        }

        /// <summary>出航（檢查都過了）：船體搬進新的船地圖，開始沿海路走。</summary>
        public static void DepartNow(Thing helm, PlanetTile destination, Plan plan, PlanetTile? coast = null)
        {
            Map origin = helm.Map;
            PlanetTile from = origin.Tile;
            List<PlanetTile> path = SurfaceRouting.Path(from, destination);
            if (path == null || path.Count < 2)
            {
                Messages.Message("DepthRim.Route.Unreachable".Translate(), helm, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            HashSet<IntVec3> cells = new HashSet<IntVec3>(plan.footprint);
            string name = ShipNameFor(helm);
            float thrust = plan.thrust;
            int ticksPerTile = SurfaceVoyageMath.TicksPerTile(plan.hoursPerTile);
            bool viewing = Find.CurrentMap == origin;
            LongEventHandler.QueueLongEvent(delegate
            {
                WorldObject_SurfaceShip ship = (WorldObject_SurfaceShip)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_SurfaceShip);
                ship.Tile = from;
                ship.SetFaction(Faction.OfPlayer);
                ship.shipName = name;
                ship.coastTarget = coast is PlanetTile c && c.Valid ? c.tileId : -1;
                Find.WorldObjects.Add(ship);
                Map shipMap = MapGenerator.GenerateMap(origin.Size, ship, ship.def.mapGenerator);
                ShipSeparationUtility.RelocateHull(origin, shipMap, cells, IntVec3.Zero);
                MapComponent_PileNetwork.For(origin)?.MarkDirty();
                MapComponent_PileNetwork.For(shipMap)?.MarkDirty();
                ship.BeginVoyage(path, thrust, ticksPerTile);
                Messages.Message("DepthRim.Sail.Departed".Translate(ship.Label, path.Count - 1), ship, MessageTypeDefOf.PositiveEvent);
                Action follow = () =>
                {
                    if (viewing && Find.Maps.Contains(shipMap))
                    {
                        Current.Game.CurrentMap = shipMap;
                        CameraJumper.TryJump(CellRect.FromCellList(cells).CenterCell, shipMap);
                    }
                };
                // 2026-11-21：出航的離港演出（同出港 M4）：船體已經搬到船地圖，原本的地圖上只剩影像——開出地圖邊。
                // 只在玩家正在看這張地圖時播
                if (!viewing || Docking.SkipShow())
                {
                    follow();
                    return;
                }
                LongEventHandler.ExecuteWhenFinished(() => Find.WindowStack.Add(Docking.MakeShow(origin, cells, follow, shipMap, cells, departing: true, shortVersion: false)));
            }, "DepthRim.Sail.Departing", doAsynchronously: false, null);
        }

        // ── 停船與到達 ──

        public static void StopHere(WorldObject_SurfaceShip ship)
        {
            if (ship == null || ship.Destroyed || !ship.underWay)
            {
                return;
            }
            Arrive(ship);
        }

        /// <summary>
        /// 停在現在這一格：那一格另外有海面地點（有地圖）就把船體搬進去、這艘船的空地圖一起移除；
        /// 沒有就直接停著（這艘船本身就是一個海面地點）。
        /// </summary>
        public static void Arrive(WorldObject_SurfaceShip ship)
        {
            ship.EndVoyage();
            PlanetTile tile = ship.Tile;
            WorldObject_SeaSurfaceSite other = Find.WorldObjects.AllWorldObjects.OfType<WorldObject_SeaSurfaceSite>()
                .FirstOrDefault(s => s != ship && s.Tile == tile && s.HasMap && !(s is WorldObject_SurfaceShip os && os.underWay));
            Thing helm = ship.HasMap ? HelmOn(ship.Map, ship.shipName) : null;
            // 2026-11-21：抵達時讓玩家選停靠位置（同靠岸的設計器：位置＋朝向、進場路線），選好才播進場演出；
            // 取消就停在這一格（船自己的地圖，船體在中央）。測試用 SubmarineSurfacing.autoPlace 照舊直接停進去。
            bool choose = helm != null && !SubmarineSurfacing.autoPlace;
            if (other != null && choose && Docking.BlockReason(helm, out _, out _, other.Map) == null)
            {
                Messages.Message("DepthRim.Sail.ArrivedChooseBerth".Translate(ship.Label, other.LabelCap), ship, MessageTypeDefOf.PositiveEvent);
                Docking.Begin(helm, other.Map);
                return;
            }
            if (other == null || !ship.HasMap)
            {
                // 2026-11-22：出航時點的海岸格（殖民地、停泊點或沒有東西的海岸）優先；沒記下就找旁邊的殖民地
                PlanetTile coast = ship.coastTarget >= 0 ? new PlanetTile(ship.coastTarget, tile.Layer) : PlanetTile.Invalid;
                ship.coastTarget = -1;
                if (!coast.Valid || !Find.WorldGrid.IsNeighbor(tile, coast) || !Docking.IsCoastTarget(coast))
                {
                    Map near = Docking.AdjacentColony(tile);
                    coast = near != null ? near.Tile : PlanetTile.Invalid;
                }
                if (coast.Valid)
                {
                    Messages.Message("DepthRim.Dock.ArrivedOffColony".Translate(ship.Label, CoastLabel(coast)), ship, MessageTypeDefOf.PositiveEvent);
                    if (choose && CoastDockBlockReason(helm, coast) == null)
                    {
                        Docking.BeginAtCoast(helm, coast);
                    }
                }
                else
                {
                    Messages.Message("DepthRim.Sail.Arrived".Translate(ship.Label), ship, MessageTypeDefOf.PositiveEvent);
                }
                return;
            }
            Map shipMap = ship.Map;
            Map dest = other.Map;
            List<IntVec3> cells = shipMap.AllCells.Where(c => MapComponent_SeabedMemory.IsPlatform(shipMap.terrainGrid.TerrainAt(c))).ToList();
            if (!SubmarineSurfacing.TryFindSpot(dest, cells, c => SeaSurface.IsOpenSea(c, dest), out IntVec3 offset))
            {
                Messages.Message("DepthRim.Sail.ArrivedNoRoom".Translate(ship.Label, other.LabelCap), ship, MessageTypeDefOf.NeutralEvent);
                return;
            }
            string label = ship.Label;
            bool viewing = Find.CurrentMap == shipMap;
            LongEventHandler.QueueLongEvent(delegate
            {
                HashSet<IntVec3> hull = new HashSet<IntVec3>(cells);
                ShipSeparationUtility.RelocateHull(shipMap, dest, hull, offset);
                HashSet<IntVec3> moved = new HashSet<IntVec3>(cells.Select(c => c + offset));
                IntVec3 center = CellRect.FromCellList(moved).CenterCell;
                foreach (Pawn p in shipMap.mapPawns.AllPawnsSpawned.ToList())
                {
                    if (!CellFinder.TryFindRandomCellNear(center, dest, 30, x => x.Standable(dest) && moved.Contains(x), out IntVec3 spot))
                    {
                        spot = center;
                    }
                    p.DeSpawn();
                    GenSpawn.Spawn(p, spot, dest);
                }
                MapComponent_PileNetwork.For(dest)?.MarkDirty();
                if (viewing)
                {
                    Current.Game.CurrentMap = dest;
                    CameraJumper.TryJump(center, dest);
                }
                Current.Game.DeinitAndRemoveMap(shipMap, false);
                if (!ship.Destroyed)
                {
                    ship.Destroy();
                }
                Messages.Message("DepthRim.Sail.ArrivedAt".Translate(label, other.LabelCap), new TargetInfo(center, dest), MessageTypeDefOf.PositiveEvent);
            }, "DepthRim.Sail.Mooring", doAsynchronously: false, null);
        }

        /// <summary>
        /// 航行狀態改變：航跡（<see cref="SeaWake"/>）的船體外形要重算。航跡是著色器參數，每幀由
        /// <see cref="MapComponent_SeaSurface"/> 設定，不用重建網格（剛生成的地圖還沒有區塊，WholeMapChanged 會丟例外）。
        /// </summary>
        public static void Notify_UnderWayChanged(WorldObject_SurfaceShip ship)
        {
            if (ship.HasMap)
            {
                ship.Map.GetComponent<MapComponent_SeaSurface>()?.Notify_HullMoved();
            }
        }
    }

    /// <summary>等舷梯收完才出航的排程（存檔會保留）。</summary>
    public class GameComponent_SurfaceSail : GameComponent
    {
        public const int CheckInterval = 60;

        /// <summary>舷梯收不完（有人一直站在上面）就放棄出航。</summary>
        public const int GiveUpTicks = 5000;

        private List<Thing> helms = new List<Thing>();
        private List<int> destinations = new List<int>();
        private List<int> queuedTicks = new List<int>();
        /// <summary>出航時點的海岸格（抵達後靠那一格；沒有時 -1）。</summary>
        private List<int> coasts = new List<int>();

        private int shipCounter;

        /// <summary>下一艘沒取名的水面船的編號（1 起算，存檔保留）。</summary>
        public int NextShipNumber() => ++shipCounter;

        public static GameComponent_SurfaceSail Instance => Current.Game?.GetComponent<GameComponent_SurfaceSail>();

        public GameComponent_SurfaceSail(Game game)
        {
        }

        public bool Pending(Thing helm) => helms.Contains(helm);

        /// <summary>靠岸時建立的停泊點（殖民地的世界物件 ID）：船離開、沒人留下時放棄，取消靠岸時撤掉。</summary>
        private List<int> landingSites = new List<int>();

        public void MarkLandingSite(WorldObject site)
        {
            if (site != null && !landingSites.Contains(site.ID))
            {
                landingSites.Add(site.ID);
            }
        }

        public void UnmarkLandingSite(WorldObject site)
        {
            if (site != null)
            {
                landingSites.Remove(site.ID);
            }
        }

        public bool IsLandingSite(WorldObject site) => site != null && landingSites.Contains(site.ID);

        /// <summary>排進出航；同一艘船（<paramref name="footprint"/> 上的其他艦橋，母船載子船時會有好幾座）先前排的取消，只留最新的這一筆。</summary>
        public void Queue(Thing helm, PlanetTile destination, ICollection<IntVec3> footprint = null, PlanetTile? coast = null)
        {
            for (int i = helms.Count - 1; i >= 0; i--)
            {
                Thing other = helms[i];
                if (other == helm || (footprint != null && other != null && other.Map == helm.Map && footprint.Contains(other.Position)))
                {
                    Remove(i);
                }
            }
            helms.Add(helm);
            destinations.Add(destination.tileId);
            queuedTicks.Add(Find.TickManager.TicksGame);
            coasts.Add(coast is PlanetTile c && c.Valid ? c.tileId : -1);
        }

        public override void GameComponentTick()
        {
            if (helms.Count == 0 || Find.TickManager.TicksGame % CheckInterval != 0)
            {
                return;
            }
            for (int i = helms.Count - 1; i >= 0; i--)
            {
                Thing helm = helms[i];
                if (helm == null || helm.Destroyed || !helm.Spawned)
                {
                    Remove(i);
                    continue;
                }
                string block = SurfaceVoyage.BlockReason(helm, out SurfaceVoyage.Plan plan);
                if (block != null)
                {
                    Messages.Message(block, helm, MessageTypeDefOf.RejectInput, historical: false);
                    Remove(i);
                    continue;
                }
                if (SurfaceVoyage.GangwaysOut(helm.Map, plan.footprint).Count == 0)
                {
                    PlanetTile dest = new PlanetTile(destinations[i], helm.Map.Tile.Layer);
                    PlanetTile? coast = coasts[i] >= 0 ? new PlanetTile(coasts[i], helm.Map.Tile.Layer) : (PlanetTile?)null;
                    Remove(i);
                    SurfaceVoyage.Depart(helm, dest, coast);
                    continue;
                }
                if (Find.TickManager.TicksGame - queuedTicks[i] > GiveUpTicks)
                {
                    Messages.Message("DepthRim.Sail.GangwayStuck".Translate(), helm, MessageTypeDefOf.RejectInput, historical: false);
                    Remove(i);
                }
            }
        }

        private void Remove(int i)
        {
            helms.RemoveAt(i);
            destinations.RemoveAt(i);
            queuedTicks.RemoveAt(i);
            coasts.RemoveAt(i);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref helms, "helms", LookMode.Reference);
            Scribe_Collections.Look(ref destinations, "destinations", LookMode.Value);
            Scribe_Collections.Look(ref queuedTicks, "queuedTicks", LookMode.Value);
            Scribe_Collections.Look(ref coasts, "coasts", LookMode.Value);
            Scribe_Collections.Look(ref landingSites, "landingSites", LookMode.Value);
            Scribe_Values.Look(ref shipCounter, "shipCounter");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                helms ??= new List<Thing>();
                destinations ??= new List<int>();
                queuedTicks ??= new List<int>();
                coasts ??= new List<int>();
                landingSites ??= new List<int>();
                while (coasts.Count < helms.Count)
                {
                    coasts.Add(-1);
                }
                int n = Mathf.Min(helms.Count, Mathf.Min(destinations.Count, queuedTicks.Count));
                for (int i = helms.Count - 1; i >= 0; i--)
                {
                    if (i >= n || helms[i] == null)
                    {
                        helms.RemoveAt(i);
                        if (i < destinations.Count) { destinations.RemoveAt(i); }
                        if (i < queuedTicks.Count) { queuedTicks.RemoveAt(i); }
                        if (i < coasts.Count) { coasts.RemoveAt(i); }
                    }
                }
            }
        }
    }
}
