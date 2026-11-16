using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim.Acceptance
{
    /// <summary>換深度時光線與水色淡入淡出，不是一下子跳過去。</summary>
    public static class Tests_TierFade
    {
        public static IEnumerable<Wait> V20_TierFade(TestContext ctx)
        {
            Map map = DepthRimFixture.EnsureOcean();
            MapComponent_Flooding flooding = DepthRimFixture.Flooding;
            ctx.Require(flooding != null && flooding.Active, "the ocean test map has water");
            Current.Game.CurrentMap = map;
            yield return new WaitFrames(5);
            PlanetTile tile = map.Tile;
            BiomeDef original = tile.Tile.PrimaryBiome;
            DepthTier before = flooding.DepthTier;
            float capBefore = OceanDaylight.CapOn(map);
            try
            {
                tile.Tile.PrimaryBiome = DefDatabase<BiomeDef>.GetNamed("DepthRim_AbyssalTrench");
                DepthTier after = flooding.DepthTier;
                ctx.Note($"{before} (cap {capBefore:F2}) -> {after} (cap {OceanDaylight.CapFor(after):F2})");
                ctx.Require(after != before, "the test map changed depth");
                yield return new WaitFrames(3);
                ctx.Check(flooding.FadingTier && flooding.FadeFrom == before && flooding.FadeTo == after, "changing depth starts a fade");
                ctx.Check(flooding.MeshTier == before, "the water keeps the old material while it fades");
                ctx.Check(Mathf.Abs(OceanDaylight.CapOn(map) - capBefore) < 0.05f, $"right after the change the light has barely moved ({OceanDaylight.CapOn(map):F2})");

                yield return new WaitTicks(MapComponent_Flooding.TierFadeTicks / 2);
                float mid = OceanDaylight.CapOn(map);
                float capAfter = OceanDaylight.CapFor(after);
                ctx.Check(flooding.FadingTier, "halfway it is still fading");
                ctx.Check(capBefore == capAfter || (mid - capBefore) * (mid - capAfter) < 0f, $"halfway the daylight cap is between the two depths ({capBefore:F2} > {mid:F2} > {capAfter:F2})");
                ctx.Check(WaterTierMaterials.BlendingIndex == (int)before && WaterTierMaterials.BlendProgress > 0.2f && WaterTierMaterials.BlendProgress < 0.8f,
                    $"the old material is part-way to the new look ({WaterTierMaterials.BlendProgress:F2})");
                Color midColor = WaterTierMaterials.Outer(before).color;
                Color fromColor = WaterVisualTierDef.For(before).waterColor, toColor = WaterVisualTierDef.For(after).waterColor;
                ctx.Check(fromColor == toColor || ((Vector4)(midColor - fromColor)).magnitude > 0.001f && ((Vector4)(midColor - toColor)).magnitude > 0.001f, $"the water colour is in between ({midColor})");

                yield return new WaitTicks(MapComponent_Flooding.TierFadeTicks / 2 + 30);
                ctx.Check(!flooding.FadingTier && flooding.MeshTier == after, "after the fade the water uses the new depth's material");
                ctx.Check(Mathf.Abs(OceanDaylight.CapOn(map) - capAfter) < 0.001f, "and the light is the new depth's");
                ctx.Check(WaterTierMaterials.BlendingIndex == -1 && WaterTierMaterials.Outer(before).color == fromColor, "the borrowed material is restored");
            }
            finally
            {
                tile.Tile.PrimaryBiome = original;
            }
            yield return new WaitTicks(MapComponent_Flooding.TierFadeTicks + 30);
            ctx.Check(!flooding.FadingTier && flooding.MeshTier == before, "back to the original depth");
        }
    }
}
