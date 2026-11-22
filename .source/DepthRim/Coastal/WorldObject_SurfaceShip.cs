using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 浮在水面上的船（設計稿 §4）。是 <see cref="WorldObject_SeaSurfaceSite"/> 的子類別：地圖就是一張海面地圖，
    /// 所以海面、天氣、乾舷、下潛、地圖去留規則都照舊。
    /// <para>航行中（<see cref="underWay"/>）沿 <see cref="route"/> 逐格前進：每 <see cref="CheckInterval"/> ticks 依目前推力相對於出航推力的比例
    /// 推進度（<see cref="SurfaceVoyageMath.Advance"/>），推力為零就漂著不動。進下一格前重新檢查那一格還能不能走（結冰了就停在原地）。
    /// 到了終點：那一格沒有別的地點就停在那裡（變成普通的海面地點）；有海面地點就把船體搬進那張地圖、這個物件跟著空地圖一起移除。</para>
    /// </summary>
    public class WorldObject_SurfaceShip : WorldObject_SeaSurfaceSite
    {
        public const int CheckInterval = 60;

        public string shipName;

        public List<PlanetTile> route = new List<PlanetTile>();

        /// <summary>目前所在的格在 <see cref="route"/> 裡的位置。</summary>
        public int routeIndex;

        /// <summary>往下一格的進度（0..1）。</summary>
        public float progress;

        public bool underWay;

        /// <summary>出航時點的海岸格（tileId；抵達後靠那一格，沒有時 -1）。</summary>
        public int coastTarget = -1;

        /// <summary>出航時的水面推力（推力比例的分母）。</summary>
        public float plannedThrust;

        /// <summary>出航時算好的每格 ticks（推力全開時）。</summary>
        public int ticksPerTile = int.MaxValue;

        /// <summary>最近一次檢查時推力是零（漂著不動）。</summary>
        public bool stalled;

        /// <summary>最近一次檢查時的推力比例。</summary>
        public float thrustFraction = 1f;

        private int sinceCheck;

        public override string Label => shipName.NullOrEmpty() ? base.Label : shipName;

        public PlanetTile Destination => route.Count > 0 ? route[route.Count - 1] : Tile;

        public PlanetTile NextTile => underWay && routeIndex + 1 < route.Count ? route[routeIndex + 1] : PlanetTile.Invalid;

        public int TilesLeft => underWay ? Mathf.Max(0, route.Count - 1 - routeIndex) : 0;

        /// <summary>航跡用的推進程度（0..1）：航行中且有推力時是推力比例，漂流或停船時 0。</summary>
        public float WakeSpeed => underWay && !stalled ? Mathf.Clamp01(thrustFraction) : 0f;

        /// <summary>
        /// 地圖上的航向（單位向量，x = 東，z = 北）：船上水面推進器的推力方向（<see cref="SeaWake.ThrustHeading(Map)"/>）；
        /// 推進器互相抵消時才用世界地圖上這一格往下一格的方位。沒在航行時 null。
        /// </summary>
        public Vector2? MapHeading
        {
            get
            {
                PlanetTile next = NextTile;
                if (!next.Valid)
                {
                    return null;
                }
                return (HasMap ? SeaWake.ThrustHeading(Map) : null) ?? SeaWake.HeadingVector(Find.WorldGrid.GetHeadingFromTo(Tile, next));
            }
        }

        /// <summary>出航：沿 <paramref name="path"/>（第一格是目前所在格）出發。</summary>
        public void BeginVoyage(List<PlanetTile> path, float thrust, int ticksPerTileAtFull)
        {
            route = new List<PlanetTile>(path);
            routeIndex = 0;
            progress = 0f;
            plannedThrust = thrust;
            ticksPerTile = ticksPerTileAtFull;
            stalled = false;
            thrustFraction = 1f;
            sinceCheck = 0;
            underWay = route.Count > 1;
            SurfaceVoyage.Notify_UnderWayChanged(this);
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (Destroyed || !underWay || !HasMap)
            {
                return;
            }
            sinceCheck += delta;
            if (sinceCheck < CheckInterval)
            {
                return;
            }
            AdvanceTicks(sinceCheck);
            sinceCheck = 0;
        }

        /// <summary>依目前推力推進 <paramref name="ticks"/> ticks（驗收測試也直接呼叫，不必真的等好幾個小時）。</summary>
        public void AdvanceTicks(int ticks)
        {
            if (!underWay || !HasMap)
            {
                return;
            }
            float thrust = SurfaceVoyage.CurrentThrust(Map);
            thrustFraction = VoyageMath.ThrustFraction(thrust, plannedThrust);
            if (plannedThrust > 0f && thrust <= 0f)
            {
                thrustFraction = 0f;
            }
            stalled = thrustFraction <= 0f;
            int steps = SurfaceVoyageMath.Advance(ref progress, ticks, thrustFraction, ticksPerTile);
            for (int i = 0; i < steps && underWay && !Destroyed; i++)
            {
                StepToNext();
            }
        }

        private void StepToNext()
        {
            PlanetTile next = NextTile;
            if (!next.Valid)
            {
                SurfaceVoyage.Arrive(this);
                return;
            }
            if (!SurfaceRouting.Navigable(next))
            {
                // 前方結冰（或其他原因走不了）：停在現在這一格
                Messages.Message("DepthRim.Sail.BlockedAhead".Translate(Label), this, MessageTypeDefOf.NegativeEvent);
                SurfaceVoyage.Arrive(this);
                return;
            }
            Tile = next;
            routeIndex++;
            SurfaceVoyage.Notify_UnderWayChanged(this); // 航向可能換了：航跡的船體外框重算
            if (routeIndex >= route.Count - 1)
            {
                SurfaceVoyage.Arrive(this);
            }
        }

        /// <summary>停船（到達或就地停船）：不再前進。</summary>
        public void EndVoyage()
        {
            underWay = false;
            stalled = false;
            progress = 0f;
            route.Clear();
            routeIndex = 0;
            SurfaceVoyage.Notify_UnderWayChanged(this);
        }

        public override Vector3 DrawPos
        {
            get
            {
                PlanetTile next = NextTile;
                if (!next.Valid)
                {
                    return base.DrawPos;
                }
                Vector3 a = Find.WorldGrid.GetTileCenter(Tile);
                Vector3 b = Find.WorldGrid.GetTileCenter(next);
                return Vector3.Slerp(a, b, Mathf.Clamp01(progress));
            }
        }

        /// <summary>圖示跟著航向轉（船體在地圖上的朝向固定，只有世界地圖上的圖示轉；設計稿 §4.3）。</summary>
        public override float ExpandingIconRotation
        {
            get
            {
                PlanetTile next = NextTile;
                if (!next.Valid)
                {
                    return base.ExpandingIconRotation;
                }
                return Find.WorldGrid.GetHeadingFromTo(DrawPos, Find.WorldGrid.GetTileCenter(next));
            }
        }

        public override string GetInspectString()
        {
            string s = base.GetInspectString();
            if (underWay)
            {
                string line = stalled
                    ? "DepthRim.Sail.InspectStalled".Translate(TilesLeft).ToString()
                    : "DepthRim.Sail.InspectUnderWay".Translate(TilesLeft, thrustFraction.ToStringPercent()).ToString();
                s = s.NullOrEmpty() ? line : s + "\n" + line;
            }
            return s;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos())
            {
                yield return g;
            }
            if (Faction != Faction.OfPlayer)
            {
                yield break;
            }
            // 世界地圖上選著船也能下航行指令（2026-11-21：停船後原本只剩一般的按鈕）
            Thing helm = HasMap ? SurfaceVoyage.HelmOn(Map, shipName) : null;
            if (helm != null)
            {
                yield return SubmarineNavigation.NavigateGizmo(helm);
            }
            if (underWay)
            {
                yield return SurfaceVoyage.StopGizmo(this);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref shipName, "shipName");
            // 航線存成地表圖層的 tileId（PlanetTile 清單不一定能直接存）
            List<int> ids = Scribe.mode == LoadSaveMode.Saving ? route.ConvertAll(t => t.tileId) : null;
            Scribe_Collections.Look(ref ids, "routeTileIds", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                route = new List<PlanetTile>();
                if (ids != null)
                {
                    PlanetLayer surface = Find.WorldGrid.FirstLayerOfDef(PlanetLayerDefOf.Surface);
                    foreach (int id in ids)
                    {
                        route.Add(new PlanetTile(id, surface));
                    }
                }
            }
            Scribe_Values.Look(ref routeIndex, "routeIndex");
            Scribe_Values.Look(ref progress, "progress");
            Scribe_Values.Look(ref underWay, "underWay");
            Scribe_Values.Look(ref plannedThrust, "plannedThrust");
            Scribe_Values.Look(ref ticksPerTile, "ticksPerTile", int.MaxValue);
            Scribe_Values.Look(ref stalled, "stalled");
            Scribe_Values.Look(ref thrustFraction, "thrustFraction", 1f);
            Scribe_Values.Look(ref sinceCheck, "sinceCheck");
            Scribe_Values.Look(ref coastTarget, "coastTarget", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                route ??= new List<PlanetTile>();
                if (underWay && route.Count < 2)
                {
                    underWay = false;
                }
            }
        }
    }
}
