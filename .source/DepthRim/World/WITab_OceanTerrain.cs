using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 海洋格的世界檢視分頁：原版的地形分頁只給地表格（<c>Tile.OnSurface</c>），海洋層選到格子時只剩「星球」分頁。
    /// 這裡顯示生態區與說明、海床深度、水深層級、能不能下錨，大陸下方的陸塊另外說明。
    /// </summary>
    public class WITab_OceanTerrain : WITab
    {
        private static readonly Vector2 WinSize = new Vector2(400f, 320f);

        private Vector2 scrollPosition;

        private float lastDrawnHeight;

        public WITab_OceanTerrain()
        {
            size = WinSize;
            labelKey = "TabTerrain";
            tutorTag = "Terrain";
        }

        public override bool IsVisible => SelPlanetTile.Valid && OceanRouting.IsOcean(SelPlanetTile);

        protected override void FillTab()
        {
            Rect outRect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(10f);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(lastDrawnHeight, outRect.height));
            Widgets.BeginScrollView(outRect, ref scrollPosition, view);
            Tile tile = SelTile;
            PlanetTile planetTile = SelPlanetTile;
            BiomeDef biome = tile.PrimaryBiome;
            Text.Font = GameFont.Medium;
            Widgets.Label(view, biome.LabelCap);
            Text.Font = GameFont.Small;
            Rect body = view;
            body.yMin += 35f;
            body.height = 99999f;
            Listing_Standard listing = new Listing_Standard { verticalSpacing = 0f };
            listing.Begin(body);
            listing.Label(biome.description);
            listing.Gap(8f);
            listing.GapLine();
            if (OceanWorldView.IsLandmass(biome))
            {
                listing.Label("DepthRim.OceanTab.Landmass".Translate());
            }
            else
            {
                DepthTier tier = DepthTierUtility.MaxTierAt(planetTile);
                listing.LabelDouble("DepthRim.OceanTab.SeafloorDepth".Translate(), "DepthRim.OceanTab.Meters".Translate(Mathf.RoundToInt(-tile.elevation)));
                listing.LabelDouble("DepthRim.OceanTab.DepthTier".Translate(), tier.Label().CapitalizeFirst());
                listing.LabelDouble("DepthRim.OceanTab.Anchor".Translate(),
                    (DiveMath.Anchorable(tier) ? "DepthRim.OceanTab.AnchorYes" : "DepthRim.OceanTab.AnchorNo").Translate());
                if (tile.hilliness != Hilliness.Undefined && tile.hilliness != Hilliness.Flat)
                {
                    listing.LabelDouble("Terrain".Translate(), tile.hilliness.GetLabelCap());
                }
            }
            if (Prefs.DevMode)
            {
                listing.LabelDouble("Debug world tile ID", planetTile.ToString());
            }
            lastDrawnHeight = body.y + listing.CurHeight;
            listing.End();
            Widgets.EndScrollView();
        }
    }
}
