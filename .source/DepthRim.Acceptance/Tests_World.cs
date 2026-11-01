using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>冰洋圖層與升降井（設計稿 §4、DMSE_對接研究 §4）。</summary>
    public static class Tests_World
    {
        public static IEnumerable<Wait> L1_OceanLayer(TestContext ctx)
        {
            PlanetLayer ocean = DepthRimFixture.OceanLayer;
            ctx.Require(ocean != null, "ocean layer registered");
            PlanetLayer surface = Find.WorldGrid.Surface;
            ctx.Check(ocean.TilesCount > 0 && ocean.Tiles.Count == ocean.TilesCount, $"ocean layer has generated tiles ({ocean.Tiles.Count}/{ocean.TilesCount})");
            ctx.Check(ocean.Tiles.All(t => t.PrimaryBiome?.HasModExtension<WaterMapExtension>() == true), "every ocean tile uses an ocean (water map) biome");
            var byBiome = ocean.Tiles.GroupBy(t => t.PrimaryBiome.defName).OrderByDescending(g => g.Count()).ToList();
            ctx.Note("ocean biomes: " + string.Join(", ", byBiome.Select(g => $"{g.Key} {g.Count()}")));
            ctx.Check(byBiome.Count >= 5, $"the ocean has varied biomes ({byBiome.Count} of 7)");
            ctx.Check(ocean.Tiles.All(t => t.hilliness != Hilliness.Undefined), "ocean tiles have hilliness for rock generation");
            // 依正上方地表決定：冰原／海冰下方都是極地冰蓋（抽樣）
            int iceChecked = 0, iceOk = 0;
            for (int i = 0; i < surface.TilesCount && iceChecked < 40; i += 97)
            {
                Tile st = surface[i];
                if (st.PrimaryBiome != BiomeDefOf.IceSheet && st.PrimaryBiome != BiomeDefOf.SeaIce) { continue; }
                iceChecked++;
                PlanetTile under = ocean.GetClosestTile_NewTemp(new PlanetTile(i, surface));
                if (under.Valid && under.Tile.PrimaryBiome.defName == "DepthRim_PolarIceCap") { iceOk++; }
            }
            ctx.Note($"ice-covered surface samples: {iceOk}/{iceChecked} have polar ice cap below");
            ctx.Check(iceChecked == 0 || iceOk >= iceChecked * 0.8f, "ice sheet and sea ice lie over the polar ice cap");
            ctx.Check(surface.zoomInToLayer == ocean, "zooming in on the surface goes to the ice ocean");
            ctx.Check(ocean.zoomOutToLayer == surface, "zooming out of the ice ocean goes to the surface");
            ctx.Check(!surface.HasConnectionFromTo(ocean) && !ocean.HasConnectionFromTo(surface), "no gravship/shuttle connection between surface and ocean");
            ctx.Check(Find.WorldGrid.PlanetLayers.Values.Count(l => l.Def == DepthRimDefOf.DepthRim_Ocean) == 1, "exactly one ice ocean layer");
            ctx.Check(!DMSE.TransferEnvironment.AllowsTransfer(DepthRimDefOf.DepthRim_Ocean), "DMSE transfer flight is not enabled on the ocean yet (submarine prototype pending)");
            int seabed = 0;
            for (int i = 0; i < ocean.TilesCount; i += 7)
            {
                if (DepthTierUtility.MaxTierAt(new PlanetTile(i, ocean)) == DepthTier.Seabed) { seabed++; }
            }
            ctx.Note($"sampled seabed-tier tiles: {seabed} of {ocean.TilesCount / 7 + 1}");
            yield break;
        }

        public static IEnumerable<Wait> E1_IceShaft(TestContext ctx)
        {
            Map ocean = DepthRimFixture.EnsureOcean();
            ctx.Require(ocean != null, "ice shaft generated a map");
            yield return new WaitFrames(5);
            Building_IceShaft shaft = DepthRimFixture.Shaft;
            ctx.Check(ocean.Tile.LayerDef == DepthRimDefOf.DepthRim_Ocean, "map is on the ice ocean layer");
            ctx.Check(ocean.Tile == DepthRimFixture.OceanLayer.GetClosestTile_NewTemp(DepthRimFixture.Home.Tile), "map is under the surface colony");
            ctx.Check(ocean.Parent.def == DepthRimDefOf.DepthRim_OceanSite, "map parent is an ocean site");
            bool iconOk;
            try { _ = ocean.Parent.ExpandingIconColor; iconOk = true; }
            catch (System.Exception) { iconOk = false; }
            ctx.Check(iconOk, "the ocean site's world-map icon colour can be read (no NullReference while drawing)");
            ctx.Check(ocean.Parent.Faction == Faction.OfPlayer, "ice ocean site belongs to the player");
            ctx.Check(ocean.Biome.defName == "DepthRim_PolarIceCap", "map uses the ice ocean biome");
            ctx.Check(ocean.GetComponent<MapComponent_Flooding>().Active, "flooding is active on the ocean map");
            ctx.Require(shaft.exit != null && shaft.exit.Spawned && shaft.exit.Map == ocean, "landing spawned on the ocean map");
            ctx.Check(shaft.exit.def == DepthRimFixture.IceShaftBottom, "landing uses DepthRim_IceShaftBottom");
            ctx.Check(shaft.exit.entrance == shaft, "landing is linked back to the shaft");
            // PocketMapExit.GetOtherMap() 就是 entrance.Map，所以這條等於「出口的入口井還在地表那張圖上」
            Map other = shaft.exit.GetOtherMap();
            ctx.Check(other == DepthRimFixture.Home,
                $"landing leads back to the surface ({other?.uniqueID.ToString() ?? "null"} vs home {DepthRimFixture.Home.uniqueID}, shaft spawned={shaft.Spawned})");
            bool clear = GenRadial.RadialCellsAround(shaft.exit.Position, 2.9f, true)
                .Where(c => c.InBounds(ocean))
                .All(c => !(c.GetEdifice(ocean)?.def.mineable ?? false));
            ctx.Check(clear, "rock cleared around the landing");
            ctx.Check(shaft.GetOtherMap() == ocean, "entering again reuses the same map");
            int rocks = ocean.listerThings.AllThings.Count(t => t.def.mineable);
            ctx.Check(rocks > 100, $"ocean map has rock ({rocks} mineable)");
            Current.Game.CurrentMap = ocean;
            CameraJumper.TryJump(shaft.exit);
            yield return new WaitFrames(10);
            ctx.Screenshot("ocean_landing");
        }
    }
}
