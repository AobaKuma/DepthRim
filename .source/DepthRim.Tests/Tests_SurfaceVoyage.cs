using System.Linq;
using System.Xml.Linq;
using DepthRim;

// 水面航行（海岸造船 M2）：每格時間、推進、航程，與世界物件 Def。
static partial class Test
{
    static void SurfaceVoyageMathTests()
    {
        Check(SurfaceVoyageMath.HoursPerTile(3f, 81) == SurfaceVoyageMath.BaseHoursPerTile, "a 9x9 ship with one surface thruster sails at the base speed");
        Check(SurfaceVoyageMath.HoursPerTile(3f, 300) == SurfaceVoyageMath.BaseHoursPerTile * 2f, "twice the hull per thrust takes twice as long");
        Check(float.IsPositiveInfinity(SurfaceVoyageMath.HoursPerTile(0f, 81)), "no thrust never gets anywhere");
        Check(SurfaceVoyageMath.BaseHoursPerTile > 4f, "the surface is slower than a submarine (DMSE coast 4 h per tile)");
        Check(SurfaceVoyageMath.TicksPerTile(6f) == 6 * 2500, "6 hours per tile is 15000 ticks");
        Check(SurfaceVoyageMath.TicksPerTile(float.PositiveInfinity) == int.MaxValue, "infinite time per tile never advances");

        // 地圖上的航向 = 推進器的推力方向（朝向，噴口在背面）
        UnityEngine.Vector2? north = SeaWake.ThrustHeading(new[] { Verse.Rot4.North, Verse.Rot4.North });
        Check(north.HasValue && System.Math.Abs(north.Value.x) < 1e-4f && System.Math.Abs(north.Value.y - 1f) < 1e-4f, "north-facing thrusters push the ship north");
        UnityEngine.Vector2? east = SeaWake.ThrustHeading(new[] { Verse.Rot4.East });
        Check(east.HasValue && System.Math.Abs(east.Value.x - 1f) < 1e-4f, "an east-facing thruster pushes east");
        UnityEngine.Vector2? mixed = SeaWake.ThrustHeading(new[] { Verse.Rot4.North, Verse.Rot4.East });
        Check(mixed.HasValue && System.Math.Abs(mixed.Value.x - mixed.Value.y) < 1e-4f && System.Math.Abs(mixed.Value.magnitude - 1f) < 1e-4f, "mixed thrusters add up to a unit diagonal");
        Check(!SeaWake.ThrustHeading(new[] { Verse.Rot4.North, Verse.Rot4.South }).HasValue, "opposed thrusters cancel: no heading (falls back to the route)");
        Check(!SeaWake.ThrustHeading(new Verse.Rot4[0]).HasValue, "no thrusters: no heading");

        // 船艏輪廓：10×5 的船身往東，前面接一個尖船艏（x=10 三格、x=11 一格）
        var hullCells = new System.Collections.Generic.List<Verse.IntVec3>();
        for (int x = 0; x < 10; x++) { for (int z = 0; z < 5; z++) { hullCells.Add(new Verse.IntVec3(x, 0, z)); } }
        for (int z = 1; z <= 3; z++) { hullCells.Add(new Verse.IntVec3(10, 0, z)); }
        hullCells.Add(new Verse.IntVec3(11, 0, 2));
        SeaWake.Hull pointed = SeaWake.HullAlong(hullCells, new UnityEngine.Vector2(1f, 0f));
        Check(pointed.valid && System.Math.Abs(pointed.halfWidth - 2.5f) < 1e-4f && System.Math.Abs(pointed.length - 12f) < 1e-4f, "the hull box is 12 long and 2.5 half-wide");
        Check(System.Math.Abs(pointed.bowProfile[0] - 0.5f) < 1e-4f, "the bow tip row is one cell wide (half-width 0.5)");
        Check(System.Math.Abs(pointed.bowProfile[1] - 1.5f) < 1e-4f, "the next row is three cells wide");
        Check(System.Math.Abs(pointed.bowProfile[2] - 2.5f) < 1e-4f && System.Math.Abs(pointed.bowProfile[7] - 2.5f) < 1e-4f, "behind the bow the profile is the full width");
        Check(System.Math.Abs(pointed.BowA.x - 0.5f) < 1e-4f && System.Math.Abs(pointed.BowB.w - 2.5f) < 1e-4f, "the profile goes to the shader as two vectors");

        float progress = 0f;
        Check(SurfaceVoyageMath.Advance(ref progress, 7500, 1f, 15000) == 0 && System.Math.Abs(progress - 0.5f) < 1e-4f, "half the time at full thrust is half a tile");
        Check(SurfaceVoyageMath.Advance(ref progress, 7500, 1f, 15000) == 1 && progress < 1e-4f, "the rest of the time reaches the next tile");
        progress = 0f;
        Check(SurfaceVoyageMath.Advance(ref progress, 15000, 0.5f, 15000) == 0 && System.Math.Abs(progress - 0.5f) < 1e-4f, "half thrust moves half as far");
        Check(SurfaceVoyageMath.Advance(ref progress, 15000, 0f, 15000) == 0 && System.Math.Abs(progress - 0.5f) < 1e-4f, "no thrust: the ship drifts in place");
        progress = 0f;
        Check(SurfaceVoyageMath.Advance(ref progress, 45000, 1f, 15000) == 3, "a long step can cross several tiles");

        Check(SurfaceVoyageMath.RangeTiles(600f, 150f, 6f) == 16, $"600 Wd runs one 150 W thruster for 16 tiles ({SurfaceVoyageMath.RangeTiles(600f, 150f, 6f)})");
        Check(SurfaceVoyageMath.RangeTiles(5f, 150f, 6f) == 0, "a nearly flat battery cannot go a tile");
    }

    static void ContentSurfaceShip()
    {
        System.Collections.Generic.List<XElement> all = DefFiles.SelectMany(f => XDocument.Load(f).Root.Elements()).ToList();
        XElement ship = all.Single(e => e.Name.LocalName == "WorldObjectDef" && (string)e.Element("defName") == "DepthRim_SurfaceShip");
        Check((string)ship.Element("worldObjectClass") == "DepthRim.WorldObject_SurfaceShip", "the surface ship uses WorldObject_SurfaceShip");
        Check((string)ship.Element("mapGenerator") == "DepthRim_SeaSurface", "its map is a sea-surface map");
        Check((string)ship.Element("expandingIcon") == "true", "it has an expanding icon (rotated with the heading)");
        Check(TextureExists((string)ship.Element("expandingIconTexture")), "the ship icon texture exists");
        Check(typeof(WorldObject_SeaSurfaceSite).IsAssignableFrom(typeof(WorldObject_SurfaceShip)), "a ship is a sea-surface site (diving, weather and waterline work on it)");
    }
}
