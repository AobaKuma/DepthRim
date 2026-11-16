using System;
using System.Collections.Generic;
using DMSE;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 浮到已經有的地圖上時選停船位置（比照 DMSE 的 <see cref="Designator_LandTransferShip"/>）：船體輪廓跟著滑鼠，
    /// 白色可以停、紅色受阻。每一格都要能降落（DMSE 的規則）而且符合 <see cref="terrainOk"/>（例如只能浮在開放海面上）。
    /// 取消選位時潛艇留在原處。
    /// </summary>
    public class Designator_PlaceSubmarine : Designator
    {
        private static readonly Color ValidStripes = ColorLibrary.White.WithAlpha(0.5f);

        private static readonly Color InvalidStripes = ColorLibrary.Red.WithAlpha(0.5f);

        private readonly WorldObject_Submarine sub;

        private readonly Map map;

        private readonly List<IntVec3> cells;

        private readonly Predicate<IntVec3> terrainOk;

        private readonly Action<IntVec3> onPlace;

        private readonly IntVec3 center;

        private readonly List<IntVec3> tmpValid = new List<IntVec3>();

        private readonly List<IntVec3> tmpInvalid = new List<IntVec3>();

        private AcceptanceReport lastReport = true;

        private float selectedAt;

        private bool placed;

        public Designator_PlaceSubmarine(WorldObject_Submarine sub, Map map, List<IntVec3> cells, Predicate<IntVec3> terrainOk, Action<IntVec3> onPlace)
        {
            this.sub = sub;
            this.map = map;
            this.cells = cells;
            this.terrainOk = terrainOk;
            this.onPlace = onPlace;
            center = cells.Count > 0 ? CellRect.FromCellList(cells).CenterCell : IntVec3.Zero;
            defaultLabel = "DepthRim.Nav.PlaceLabel".Translate();
            defaultDesc = "DepthRim.Nav.PlaceDesc".Translate();
            useMouseIcon = false;
        }

        public Map TargetMap => map;

        public override bool AlwaysDoGuiControls => true;

        public override void Selected()
        {
            base.Selected();
            selectedAt = Time.realtimeSinceStartup;
        }

        public override void Deselected()
        {
            base.Deselected();
            if (!placed && !sub.Destroyed)
            {
                sub.landingTarget = PlanetTile.Invalid;
            }
        }

        private IntVec3 OffsetFor(IntVec3 mouseCell) => mouseCell - center;

        public AcceptanceReport CanPlaceAt(IntVec3 offset, List<IntVec3> valid = null, List<IntVec3> invalid = null)
        {
            AcceptanceReport result = true;
            foreach (IntVec3 c in cells)
            {
                IntVec3 d = c + offset;
                AcceptanceReport r = TransferLandingUtility.IsValidLandingCell(d, map);
                if (r.Accepted && !terrainOk(d))
                {
                    r = "DepthRim.Nav.PlaceNotOpenSea".Translate();
                }
                if (r.Accepted)
                {
                    valid?.Add(d);
                    continue;
                }
                result = r;
                invalid?.Add(d);
                if (valid == null && invalid == null)
                {
                    return r;
                }
            }
            return result;
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 loc)
        {
            // 剛選上的瞬間不接受點擊，避免觸發選位的那次點擊被當成位置
            if (Find.CurrentMap != map || sub.Destroyed || !sub.HasMap || Time.realtimeSinceStartup - selectedAt <= 0.3f)
            {
                return false;
            }
            return CanPlaceAt(OffsetFor(loc));
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            placed = true;
            Find.DesignatorManager.Deselect();
            onPlace(OffsetFor(c));
        }

        public override void SelectedUpdate()
        {
            if (sub.Destroyed || !sub.HasMap)
            {
                Find.DesignatorManager.Deselect();
                return;
            }
            if (Find.CurrentMap != map)
            {
                return;
            }
            tmpValid.Clear();
            tmpInvalid.Clear();
            lastReport = CanPlaceAt(OffsetFor(UI.MouseCell()), tmpValid, tmpInvalid);
            GenDraw.DrawFieldEdges(tmpValid, ColorLibrary.White);
            GenDraw.DrawFieldEdges(tmpInvalid, ColorLibrary.Red);
            GenDraw.DrawDiagonalStripes(tmpValid, ValidStripes);
            GenDraw.DrawDiagonalStripes(tmpInvalid, InvalidStripes);
        }

        public override void DrawMouseAttachments()
        {
            if (!lastReport.Accepted)
            {
                GenUI.DrawMouseAttachment(null, lastReport.Reason, 0f, default(Vector2), null, ColorLibrary.RedReadable, drawTextBackground: true, new Color(0f, 0f, 0f, 0.5f));
            }
        }
    }

    /// <summary>
    /// 延後選上 <see cref="Designator_PlaceSubmarine"/>：切換地圖、開關視窗都會取消目前的設計器，
    /// 等地圖切換完成、沒有長時間事件後兩個畫面再選上（同 DMSE 的 GameComponent_TransferLanding）。不存檔。
    /// </summary>
    public class GameComponent_SubmarinePlacement : GameComponent
    {
        private static Designator_PlaceSubmarine pending;

        private static int stableFrames;

        public GameComponent_SubmarinePlacement(Game game)
        {
            pending = null;
        }

        public static void Request(Designator_PlaceSubmarine designator)
        {
            pending = designator;
            stableFrames = 0;
        }

        public override void GameComponentUpdate()
        {
            if (pending == null)
            {
                return;
            }
            Map map = pending.TargetMap;
            if (map == null || !Find.Maps.Contains(map))
            {
                pending = null;
                return;
            }
            if (LongEventHandler.AnyEventNowOrWaiting)
            {
                stableFrames = 0;
                return;
            }
            if (Find.CurrentMap != map || WorldRendererUtility.WorldSelected)
            {
                Current.Game.CurrentMap = map;
                CameraJumper.TryJump(map.Center, map, CameraJumper.MovementMode.Cut);
                stableFrames = 0;
                return;
            }
            if (++stableFrames < 2)
            {
                return;
            }
            Find.DesignatorManager.Select(pending);
            pending = null;
        }
    }
}
