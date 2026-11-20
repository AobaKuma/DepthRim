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
