using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Verse;

// 預製潛艇（FFF 結構，Submarine_Prefabs.xml）：不啟動遊戲，只讀 XML 檢查佈局。
// 真的生成、通電、出航條件在驗收測試 S6。
static partial class Test
{
    /// <summary>原版建築的尺寸（離線讀不到原版 XML）。</summary>
    static readonly Dictionary<string, IntVec2> VanillaSizes = new Dictionary<string, IntVec2>
    {
        { "PowerConduit", new IntVec2(1, 1) },
        { "Battery", new IntVec2(1, 2) },
        { "Bed", new IntVec2(1, 2) },
        { "Heater", new IntVec2(1, 1) },
        { "StandingLamp", new IntVec2(1, 1) },
    };

    /// <summary>原版的電力 transmitter（同一格不能有兩個）。</summary>
    static readonly HashSet<string> PrefabTransmitters = new HashSet<string> { "PowerConduit", "Battery" };

    struct PrefabThing
    {
        public string def;
        public IntVec3 pos;
        public Rot4 rot;
        public CellRect rect;
    }

    static IntVec3 ParseCell(string s)
    {
        int[] v = s.Trim().Trim('(', ')').Split(',').Select(p => int.Parse(p.Trim())).ToArray();
        return new IntVec3(v[0], v[1], v[2]);
    }

    static IntVec2 ParseSize(string s)
    {
        int[] v = s.Trim().Trim('(', ')').Split(',').Select(p => int.Parse(p.Trim())).ToArray();
        return new IntVec2(v[0], v[1]);
    }

    static Rot4 ParseRot(string s)
    {
        switch (s)
        {
            case null: return Rot4.North;
            case "East": return Rot4.East;
            case "South": return Rot4.South;
            case "West": return Rot4.West;
            default: return Rot4.North;
        }
    }

    /// <summary>
    /// 比照原版 <c>GenAdj.OccupiedRect</c>／<c>AdjustForRotation</c>（參考方向北）：橫向時寬高互換，
    /// 偶數邊依方向往負方向（東：z−1；南：x−1、z−1；西：x−1）偏一格。
    /// </summary>
    static CellRect PrefabRect(IntVec3 center, Rot4 rot, IntVec2 size)
    {
        if (size.x == 1 && size.z == 1)
        {
            return CellRect.SingleCell(center);
        }
        if (rot.IsHorizontal)
        {
            size = new IntVec2(size.z, size.x);
        }
        (int dx, int dz) = rot.AsInt switch { 1 => (0, -1), 2 => (-1, -1), 3 => (-1, 0), _ => (0, 0) };
        if (size.x % 2 == 0) { center.x += dx; }
        if (size.z % 2 == 0) { center.z += dz; }
        return new CellRect(center.x - (size.x - 1) / 2, center.z - (size.z - 1) / 2, size.x, size.z);
    }

    static void ContentSubmarinePrefab()
    {
        var all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        XElement prefab = all.SingleOrDefault(e => e.Name.LocalName == "Fortified.Structures.FFF_StructureDef" && (string)e.Element("defName") == DepthRim.SubmarinePrefab.DefaultDefName);
        if (!Check(prefab != null, "the prefab submarine is an FFF structure def")) { return; }

        // DepthRim 的 ThingDef：尺寸（含抽象父類別）
        Dictionary<string, XElement> byName = all.Where(e => e.Attribute("Name") != null).GroupBy(e => (string)e.Attribute("Name")).ToDictionary(g => g.Key, g => g.First());
        Dictionary<string, XElement> things = all.Where(e => e.Name.LocalName == "ThingDef" && e.Element("defName") != null).ToDictionary(e => (string)e.Element("defName"));
        IntVec2? SizeOf(string def)
        {
            if (VanillaSizes.TryGetValue(def, out IntVec2 v)) { return v; }
            if (!things.TryGetValue(def, out XElement d)) { return null; }
            for (XElement cur = d; cur != null; cur = cur.Attribute("ParentName") != null && byName.TryGetValue((string)cur.Attribute("ParentName"), out XElement p) ? p : null)
            {
                if (cur.Element("size") != null) { return ParseSize((string)cur.Element("size")); }
            }
            return new IntVec2(1, 1);
        }

        IntVec2 size = ParseSize((string)prefab.Element("size"));
        CellRect hull = new CellRect(0, 0, size.x, size.z);
        Check((string)prefab.Element("disableSuggestedRoof") == "True", "the prefab roofs itself (no suggested roof)");

        // ── 展開元素 ──
        List<PrefabThing> placed = new List<PrefabThing>();
        HashSet<IntVec3> platform = new HashSet<IntVec3>();
        HashSet<IntVec3> roof = new HashSet<IntVec3>();
        foreach (XElement li in prefab.Element("elements").Elements("li"))
        {
            string cls = (string)li.Attribute("Class");
            Check(typeof(Fortified.Structures.FFF_Element).Assembly.GetType(cls) != null, $"prefab element type {cls} exists");
            string def = (string)li.Element("def");
            IntVec3 pos = li.Element("pos") != null ? ParseCell((string)li.Element("pos")) : IntVec3.Zero;
            IntVec2 rectSize = li.Element("size") != null ? ParseSize((string)li.Element("size")) : new IntVec2(1, 1);
            Rot4 rot = ParseRot((string)li.Element("rot"));
            CellRect area = new CellRect(pos.x, pos.z, rectSize.x, rectSize.z);
            switch (cls.Substring(cls.LastIndexOf('.') + 1))
            {
                case "FFF_Element_TerrainRect":
                    Check(def == "DepthRim_HullPlatform", "the prefab floor is hull platform");
                    foreach (IntVec3 c in area) { platform.Add(c); }
                    break;
                case "FFF_Element_RoofRect":
                    foreach (IntVec3 c in area) { roof.Add(c); }
                    break;
                case "FFF_Element_Thing":
                case "FFF_Element_ThingRect":
                case "FFF_Element_ThingScatter":
                    IntVec2? s = SizeOf(def);
                    if (!Check(s != null, $"prefab thing {def} is a known ThingDef")) { break; }
                    IEnumerable<IntVec3> spots = cls.EndsWith("ThingScatter") ? li.Element("posList").Elements("li").Select(e => ParseCell(e.Value))
                        : cls.EndsWith("ThingRect") ? area.Cells : new[] { pos };
                    foreach (IntVec3 spot in spots)
                    {
                        placed.Add(new PrefabThing { def = def, pos = spot, rot = rot, rect = PrefabRect(spot, rot, s.Value) });
                    }
                    break;
                default:
                    Check(false, $"prefab element {cls} is handled by the layout test");
                    break;
            }
        }
        Check(platform.SetEquals(hull.Cells), "the whole footprint is hull platform");
        Check(roof.SetEquals(hull.Cells), "the whole footprint is roofed");
        Check(placed.All(t => t.rect.Cells.All(hull.Contains)), "every building lies inside the hull");

        // ── 重疊 ──
        Dictionary<IntVec3, string> solid = new Dictionary<IntVec3, string>();
        Dictionary<IntVec3, string> transmit = new Dictionary<IntVec3, string>();
        foreach (PrefabThing t in placed)
        {
            foreach (IntVec3 c in t.rect)
            {
                if (t.def != "PowerConduit")
                {
                    Check(!solid.ContainsKey(c), $"{t.def} at {c} does not overlap {(solid.TryGetValue(c, out string o) ? o : "")}");
                    solid[c] = t.def;
                }
                if (PrefabTransmitters.Contains(t.def))
                {
                    Check(!transmit.ContainsKey(c), $"no two power transmitters share {c}");
                    transmit[c] = t.def;
                }
            }
        }

        bool IsWall(IntVec3 c) => solid.TryGetValue(c, out string d) && d == "DepthRim_PressureHullWall";
        bool IsDoor(IntVec3 c) => solid.TryGetValue(c, out string d) && d == "DepthRim_WatertightDoor";

        // ── 外殼完整：邊緣只有牆或門，外門只有一道（氣閘） ──
        Check(hull.EdgeCells.All(c => IsWall(c) || IsDoor(c)), "the hull edge is all pressure wall or watertight door");
        Check(hull.EdgeCells.Count(IsDoor) == 1, "exactly one outer door (the airlock)");

        // ── 艙室：牆與門以外的格子分成幾個連通塊 ──
        HashSet<IntVec3> open = new HashSet<IntVec3>(hull.Cells.Where(c => !IsWall(c) && !IsDoor(c)));
        List<HashSet<IntVec3>> rooms = new List<HashSet<IntVec3>>();
        foreach (IntVec3 start in open)
        {
            if (rooms.Any(r => r.Contains(start))) { continue; }
            HashSet<IntVec3> room = new HashSet<IntVec3> { start };
            Queue<IntVec3> q = new Queue<IntVec3>(room);
            while (q.Count > 0)
            {
                IntVec3 c = q.Dequeue();
                foreach (IntVec3 n in GenAdj.CardinalDirections.Select(d => c + d))
                {
                    if (open.Contains(n) && room.Add(n)) { q.Enqueue(n); }
                }
            }
            rooms.Add(room);
        }
        Check(rooms.Count == 4, $"four sealed rooms: stern, middle, bow and the airlock ({rooms.Count})");
        HashSet<IntVec3> RoomOf(IntVec3 c) => rooms.FirstOrDefault(r => r.Contains(c));
        HashSet<IntVec3> airlock = rooms.OrderBy(r => r.Count).First();
        Check(airlock.Count == 2, $"the airlock is two cells ({airlock.Count})");
        foreach (IntVec3 door in placed.Where(t => t.def == "DepthRim_WatertightDoor").Select(t => t.pos))
        {
            int sides = GenAdj.CardinalDirections.Select(d => door + d).Count(n => open.Contains(n) && (!solid.ContainsKey(n) || solid[n] == "DepthRim_BilgePump"));
            Check(sides >= (hull.EdgeCells.Contains(door) ? 1 : 2), $"the door at {door} has walkable cells on its sides");
        }
        foreach (HashSet<IntVec3> room in rooms)
        {
            Check(placed.Any(t => t.def == "DepthRim_BilgePump" && room.Contains(t.pos)), $"the room around {room.First()} ({room.Count} cells) has a bilge pump");
            Check(room.All(c => !solid.ContainsKey(c) || solid[c] != "DepthRim_PressureHullWall"), "rooms are separated by walls only");
        }
        PrefabThing pressurizer = placed.Single(t => t.def == "DepthRim_Pressurizer");
        Check(RoomOf(pressurizer.pos).Count <= 80, "the pressuriser's room is within its 80-cell limit");

        // ── 走得通：從氣閘出發，經過門可以走到每個艙室的每一格空地 ──
        bool Walkable(IntVec3 c) => hull.Contains(c) && (!solid.ContainsKey(c) || IsDoor(c) || solid[c] == "DepthRim_BilgePump");
        HashSet<IntVec3> reached = new HashSet<IntVec3>();
        Queue<IntVec3> walk = new Queue<IntVec3>();
        walk.Enqueue(airlock.First());
        reached.Add(airlock.First());
        while (walk.Count > 0)
        {
            IntVec3 c = walk.Dequeue();
            foreach (IntVec3 n in GenAdj.CardinalDirections.Select(d => c + d))
            {
                if (Walkable(n) && reached.Add(n)) { walk.Enqueue(n); }
            }
        }
        List<IntVec3> stranded = open.Where(c => !solid.ContainsKey(c) && !reached.Contains(c)).ToList();
        Check(stranded.Count == 0, $"every free cell is reachable from the airlock ({string.Join(" ", stranded)})");

        // ── 艦橋：互動格是空地 ──
        PrefabThing helm = placed.Single(t => t.def == "DepthRim_Helm");
        IntVec3 interaction = helm.pos + new IntVec3(0, 0, -1);
        Check(open.Contains(interaction) && !solid.ContainsKey(interaction) && reached.Contains(interaction), "the helm's interaction cell is free and reachable");

        // ── 龍骨：整塊平台都在覆蓋半徑內（不必鋪大樑） ──
        PrefabThing keel = placed.Single(t => t.def == "DepthRim_Keel");
        XElement pile = things["DepthRim_Keel"].Element("comps").Elements("li").Single(e => (string)e.Attribute("Class") == "DMSE.CompProperties_StructuralPile");
        float radius = float.Parse((string)pile.Element("coverageRadius"));
        float farthest = hull.Cells.Max(c => (c - keel.pos).LengthHorizontal);
        Check(farthest <= radius, $"the keel frame covers the whole hull ({farthest:0.0} <= {radius})");
        Check(placed.Count(t => t.def == "DepthRim_Helm") == 1 && placed.Count(t => t.def.Contains("Thruster")) >= 1, "one helm and at least one thruster");

        // ── 電：電池接到電線主幹；每個用電建築都在連接距離（6 格）內 ──
        HashSet<IntVec3> net = new HashSet<IntVec3>();
        IntVec3 seed = transmit.First(kv => kv.Value == "PowerConduit").Key;
        Queue<IntVec3> grow = new Queue<IntVec3>();
        net.Add(seed);
        grow.Enqueue(seed);
        while (grow.Count > 0)
        {
            IntVec3 c = grow.Dequeue();
            foreach (IntVec3 n in GenAdj.CardinalDirections.Select(d => c + d))
            {
                if (transmit.ContainsKey(n) && net.Add(n)) { grow.Enqueue(n); }
            }
        }
        Check(transmit.Keys.All(net.Contains), "every battery and conduit is one connected power net");
        string[] consumers = { "DepthRim_Thruster", "DepthRim_Helm", "DepthRim_PassiveSonar", "DepthRim_Pressurizer", "DepthRim_BilgePump", "Heater", "StandingLamp" };
        foreach (PrefabThing t in placed.Where(t => consumers.Contains(t.def)))
        {
            float d = t.rect.Cells.Min(c => net.Min(n => (c - n).LengthHorizontal));
            Check(d <= 6f, $"{t.def} at {t.pos} is within power connection range ({d:0.0})");
        }
        Check(placed.Count(t => t.def == "Battery") >= 2, "at least two batteries");
    }
}
