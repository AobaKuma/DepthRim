using System;
using System.Collections.Generic;
using System.Linq;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 壓載的純計算（Docs/海岸船舶與潛水鐘_機制設計.md §2.4，M6 第一版：用船體格數，不算重量）。
    /// </summary>
    public static class BallastMath
    {
        /// <summary>每座壓載水艙撐得住幾格船體。</summary>
        public const int CellsPerTank = 200;

        /// <summary>壓載不夠時換一層最多慢幾倍（沒有壓載時也是這個倍數）。</summary>
        public const float MaxSlowdown = 4f;

        /// <summary>這麼多座壓載撐得住幾格船體。</summary>
        public static int Capacity(int tanks) => Math.Max(0, tanks) * CellsPerTank;

        /// <summary>換一層的時間倍數：船體格數 ÷ 撐得住的格數，最少 1、最多 <see cref="MaxSlowdown"/>。</summary>
        public static float Slowdown(int hullCells, int tanks)
        {
            int cap = Capacity(tanks);
            if (cap <= 0)
            {
                return MaxSlowdown;
            }
            float ratio = (float)hullCells / cap;
            return ratio < 1f ? 1f : (ratio > MaxSlowdown ? MaxSlowdown : ratio);
        }

        /// <summary>換一層要幾 tick。</summary>
        public static int TicksPerTier(int hullCells, int tanks)
        {
            return (int)Math.Round(DiveMath.TicksPerTier * Slowdown(hullCells, tanks));
        }

        /// <summary>壓載不夠撐整艘船（會變慢）。</summary>
        public static bool Short(int hullCells, int tanks) => hullCells > Capacity(tanks);
    }

    /// <summary>船上的壓載水艙（建築本身只是標記，沒有自己的行為）。</summary>
    public static class Ballast
    {
        public static ThingDef TankDef => DepthRimDefOf.DepthRim_BallastTank;

        /// <summary>
        /// 這具船體的範圍：潛艇（海洋層）的船體地圖上是整張地圖的船體格，其他地圖（海面、船塢）是艦橋所在的龍骨網路。
        /// <paramref name="anchor"/> 為 null 時用整張地圖。
        /// </summary>
        public static ICollection<IntVec3> HullCells(Map map, Thing anchor = null)
        {
            if (map == null)
            {
                return Array.Empty<IntVec3>();
            }
            if (anchor != null && !(map.Parent is WorldObject_Submarine))
            {
                PileNetwork net = MapComponent_PileNetwork.For(map)?.NetworkContaining(anchor);
                if (net != null)
                {
                    net.RecalculateFootprint();
                    return new HashSet<IntVec3>(net.Footprint);
                }
            }
            return new HashSet<IntVec3>(TransferLandingUtility.HullCells(map));
        }

        /// <summary>範圍內的壓載水艙座數（我方、蓋好的）。</summary>
        public static int TanksIn(Map map, ICollection<IntVec3> cells)
        {
            if (map == null || TankDef == null || cells == null)
            {
                return 0;
            }
            int n = 0;
            foreach (Thing t in map.listerThings.ThingsOfDef(TankDef))
            {
                if (t.Spawned && t.Faction == Faction.OfPlayer && cells.Contains(t.Position))
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>艦橋所在船體的壓載座數與船體格數。</summary>
        public static int TanksFor(Thing helm, out int hullCells)
        {
            ICollection<IntVec3> cells = HullCells(helm?.Map, helm);
            hullCells = cells.Count;
            return TanksIn(helm?.Map, cells);
        }

        /// <summary>潛艇（船體地圖）的壓載座數與船體格數。</summary>
        public static int TanksFor(Map shipMap, out int hullCells)
        {
            ICollection<IntVec3> cells = HullCells(shipMap);
            hullCells = cells.Count;
            return TanksIn(shipMap, cells);
        }

        /// <summary>這麼大的船體要幾座才能全速換深度。</summary>
        public static int Needed(int hullCells) => (hullCells + BallastMath.CellsPerTank - 1) / BallastMath.CellsPerTank;

        /// <summary>
        /// 開局用的保險（<see cref="ScenPart_SubmarineStart"/>）：船體上的壓載不夠全速時，在艙內靠牆的空地補上（1×2 朝北），回傳補了幾座。
        /// 開局選的結構（包括玩家自己的設計）沒有壓載也能下潛。挑的格子：兩格都在船體上、有屋頂、能站、上面沒有東西、
        /// 不是任何建築的互動格、旁邊沒有門、至少一格貼著牆；每座之間隔 4 格以上。
        /// </summary>
        public static int FillHull(Map map, ICollection<IntVec3> hull, Faction faction)
        {
            if (map == null || TankDef == null || hull == null || hull.Count == 0)
            {
                return 0;
            }
            int missing = Needed(hull.Count) - TanksIn(map, hull);
            if (missing <= 0)
            {
                return 0;
            }
            HashSet<IntVec3> interaction = new HashSet<IntVec3>();
            foreach (IntVec3 c in hull)
            {
                foreach (Thing t in c.GetThingList(map))
                {
                    if (t.def.hasInteractionCell)
                    {
                        interaction.Add(t.InteractionCell);
                    }
                }
            }
            bool Free(IntVec3 c)
            {
                if (!hull.Contains(c) || !c.Roofed(map) || !c.Standable(map) || c.GetThingList(map).Count > 0 || interaction.Contains(c))
                {
                    return false;
                }
                foreach (IntVec3 n in GenAdj.CardinalDirections)
                {
                    if ((c + n).InBounds(map) && (c + n).GetEdifice(map) is Building_Door)
                    {
                        return false;
                    }
                }
                return true;
            }
            bool AgainstWall(IntVec3 c)
            {
                foreach (IntVec3 n in GenAdj.CardinalDirections)
                {
                    if ((c + n).InBounds(map) && (c + n).GetEdifice(map)?.def.building?.isWall == true)
                    {
                        return true;
                    }
                }
                return false;
            }
            List<IntVec3> placed = new List<IntVec3>();
            foreach (IntVec3 c in hull.OrderBy(c => c.z).ThenBy(c => c.x))
            {
                if (placed.Count >= missing)
                {
                    break;
                }
                IntVec3 up = c + IntVec3.North;
                if (!Free(c) || !Free(up) || !(AgainstWall(c) || AgainstWall(up)) || placed.Any(p => p.DistanceTo(c) < 4f))
                {
                    continue;
                }
                Thing tank = ThingMaker.MakeThing(TankDef);
                tank.SetFaction(faction);
                GenSpawn.Spawn(tank, c, map, Rot4.North);
                placed.Add(c);
            }
            if (placed.Count < missing)
            {
                Log.Warning($"[DepthRim] start submarine: only found room for {placed.Count} of {missing} missing ballast tanks.");
            }
            return placed.Count;
        }

        /// <summary>檢視面板的一行：幾座、撐幾格、船體幾格，不夠時說會慢幾倍。</summary>
        public static string Describe(int tanks, int hullCells)
        {
            if (tanks <= 0)
            {
                return "DepthRim.Ballast.InspectNone".Translate();
            }
            string line = "DepthRim.Ballast.Inspect".Translate(tanks, BallastMath.Capacity(tanks), hullCells);
            if (BallastMath.Short(hullCells, tanks))
            {
                line += " " + "DepthRim.Ballast.InspectShort".Translate(BallastMath.Slowdown(hullCells, tanks).ToString("0.0"));
            }
            return line;
        }
    }

    /// <summary>下潛前清空甲板（§2.2c）：船殼範圍裡不在密閉艙室內的格子上不能有我方的人。</summary>
    public static class DeckClearing
    {
        /// <summary>這格是甲板（沒有屋頂，或所在房間用室外溫度）。</summary>
        public static bool IsOpenDeck(IntVec3 c, Map map)
        {
            if (!c.Roofed(map))
            {
                return true;
            }
            Room room = c.GetRoom(map);
            return room == null || room.UsesOutdoorTemperature;
        }

        /// <summary>站在甲板上的我方人員（殖民者、奴隸、囚犯、動物）。</summary>
        public static List<Pawn> PawnsOnDeck(Map map, ICollection<IntVec3> cells)
        {
            List<Pawn> result = new List<Pawn>();
            if (map == null || cells == null)
            {
                return result;
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (p.Dead || !cells.Contains(p.Position))
                {
                    continue;
                }
                bool ours = p.Faction == Faction.OfPlayer || p.HostFaction == Faction.OfPlayer;
                if (ours && IsOpenDeck(p.Position, map))
                {
                    result.Add(p);
                }
            }
            return result;
        }

        /// <summary>不能下潛的原因（甲板上有人時），可以時 null。</summary>
        public static string Block(Map map, ICollection<IntVec3> cells)
        {
            List<Pawn> pawns = PawnsOnDeck(map, cells);
            if (pawns.Count == 0)
            {
                return null;
            }
            string names = string.Join(", ", pawns.Take(4).Select(p => p.LabelShortCap));
            if (pawns.Count > 4)
            {
                names += ", …";
            }
            return "DepthRim.Dive.DeckOccupied".Translate(names);
        }
    }

    /// <summary>
    /// 下潛前的耐壓警告（§2.2）：目標比船殼能耐深時跳確認視窗，按「仍然下潛」才執行。
    /// </summary>
    public static class HullWarning
    {
        /// <summary>超出幾級（不超出時 0）。</summary>
        public static int Excess(DepthTier target, DepthTier rating) => target > rating ? target - rating : 0;

        /// <summary>最弱的那一面牆在目標深度每天大約損失多少 HP（艙內一大氣壓、外面是海水的負載）。</summary>
        public static float DamagePerDay(DepthTier target, DepthTier rating)
        {
            float perUpdate = WallPressureTopology.DamagePerUpdate(WallPressureTopology.Capacity(target), WallPressureTopology.Capacity(rating));
            return perUpdate * (GenDate.TicksPerDay / 250f);
        }

        public static string Text(DepthTier target, DepthTier rating, Building weakest)
        {
            string piece = weakest != null ? weakest.LabelCap.ToString() : "-";
            return "DepthRim.HullWarn.Text".Translate(target.Label(), rating.Label(), piece, Excess(target, rating), DamagePerDay(target, rating).ToString("0"));
        }

        /// <summary>
        /// 從 <paramref name="current"/> 往 <paramref name="target"/>：比船殼能耐深（而且比現在深）就先確認，否則直接執行。
        /// </summary>
        public static void ConfirmThen(Map hullMap, DepthTier current, DepthTier target, Action proceed)
        {
            Building weakest = null;
            DepthTier rating = hullMap != null ? HullPressure.RatingFor(hullMap, out weakest) : HullPressure.Unlimited;
            if (target <= current || target <= rating)
            {
                proceed();
                return;
            }
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(Text(target, rating, weakest), proceed, destructive: true,
                title: "DepthRim.HullWarn.Title".Translate()));
        }
    }

    /// <summary>船殼超載的警示：潛艇所在的層級比它的船殼能耐深（§2.2）。</summary>
    public class Alert_HullOverloaded : Alert_Critical
    {
        private readonly List<GlobalTargetInfo> targets = new List<GlobalTargetInfo>();

        public Alert_HullOverloaded()
        {
            defaultLabel = "DepthRim.HullWarn.AlertLabel".Translate();
        }

        private List<GlobalTargetInfo> Overloaded()
        {
            targets.Clear();
            foreach (WorldObject_Submarine sub in Find.WorldObjects.AllWorldObjects.OfType<WorldObject_Submarine>())
            {
                if (sub.Faction == Faction.OfPlayer && sub.HullOverloaded)
                {
                    targets.Add(sub);
                }
            }
            return targets;
        }

        public override TaggedString GetExplanation()
        {
            List<string> lines = new List<string>();
            foreach (GlobalTargetInfo t in Overloaded())
            {
                if (t.WorldObject is WorldObject_Submarine sub)
                {
                    DepthTier rating = sub.HullRating(out Building weakest);
                    lines.Add("  - " + sub.LabelCap + ": " + HullWarning.Text(sub.CurrentDepthTier, rating, weakest));
                }
            }
            return "DepthRim.HullWarn.AlertDesc".Translate() + "\n\n" + string.Join("\n\n", lines);
        }

        public override AlertReport GetReport()
        {
            return AlertReport.CulpritsAre(Overloaded());
        }
    }
}
