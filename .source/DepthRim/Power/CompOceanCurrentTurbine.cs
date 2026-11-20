using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 洋流發電機的設定（3×3）：仿原版 <c>CompProperties_Power</c>＋<c>CompPowerPlantWind</c>，
    /// 只是把「風」換成「洋流」，並且要求整台機器浸在水裡。
    /// </summary>
    public class CompProperties_OceanCurrentTurbine : CompProperties_Power
    {
        /// <summary>要完全浸在水裡才算運轉：建築佔的每一格水位都要至少這麼高（0–1）。</summary>
        public float minWaterLevel = 0.5f;

        /// <summary>水流通道往建築前後各伸幾格（比照原版風力發電機的風道）。</summary>
        public int flowReach = OceanCurrentMath.DefaultFlowReach;

        public CompProperties_OceanCurrentTurbine()
        {
            compClass = typeof(CompOceanCurrentTurbine);
        }
    }

    /// <summary>
    /// 洋流發電機：比照原版風力發電機（<c>RimWorld.CompPowerPlantWind</c>）——
    /// 每 <see cref="UpdateInterval"/> ticks 重算一次輸出、通道被擋住會折減、輸出上限 1.5 倍（這裡 1.4 倍）。
    ///
    /// <para>差別：</para>
    /// <list type="bullet">
    /// <item>洋流強度看水深層級、天氣（只有表層受影響）與潮汐漲落（<see cref="OceanCurrentMath"/>）；</item>
    /// <item>建築佔的每一格都要浸在水裡（水位 ≥ <see cref="CompProperties_OceanCurrentTurbine.minWaterLevel"/>），
    /// 放在抽乾的艙室或陸地上完全不發電；</item>
    /// <item>水流通道（建築前後各 <see cref="CompProperties_OceanCurrentTurbine.flowReach"/> 格）被牆、石頭、乾掉的格子擋住就折減，
    /// 和原版風力發電機的「前方要有空地」同一個概念。</item>
    /// </list>
    /// </summary>
    [StaticConstructorOnStartup]
    public class CompOceanCurrentTurbine : CompPowerPlant
    {
        /// <summary>重算間隔（ticks）。比照原版風力發電機的 <c>updateWeatherEveryXTicks</c>。</summary>
        public const int UpdateInterval = 250;

        private int ticksSinceUpdate;

        /// <summary>這次重算的輸出（W，正 = 發電）；<c>null</c> 之前先算一次。</summary>
        private float cachedOutput;

        private float strength;

        private int blockedCells;

        private int flowCells;

        private bool submerged;

        private bool underwaterMap;

        private static readonly Material BarFilledMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.32f, 0.6f, 0.7f));

        private static readonly Material BarUnfilledMat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.15f, 0.15f, 0.15f));

        public CompProperties_OceanCurrentTurbine CurrentProps => (CompProperties_OceanCurrentTurbine)props;

        /// <summary>額定輸出（W）：滿流、通道全通時的發電量。</summary>
        public float RatedPower => Mathf.Max(0f, 0f - base.Props.PowerConsumption);

        /// <summary>這次重算的洋流強度（0–<see cref="OceanCurrentMath.MaxStrength"/>）。</summary>
        public float CurrentStrength => strength;

        /// <summary>這次重算時被擋住的水流通道格數。</summary>
        public int BlockedCells => blockedCells;

        /// <summary>這次重算時納入檢查的水流通道格數。</summary>
        public int FlowCells => flowCells;

        /// <summary>整台機器是否浸在水裡（每次重算更新）。</summary>
        public bool Submerged => submerged;

        /// <summary>這張地圖是不是水下地圖（每次重算更新）。</summary>
        public bool OnUnderwaterMap => underwaterMap;

        protected override float DesiredPowerOutput => cachedOutput;

        /// <summary>輸出佔額定輸出（乘上洋流上限）的比例，給發電量條用。</summary>
        public float PowerPercent => RatedPower <= 0f ? 0f : Mathf.Clamp01(PowerOutput / (RatedPower * OceanCurrentMath.MaxStrength));

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            // 一生成／讀檔就重算，不要等第一個 250 ticks
            ticksSinceUpdate = UpdateInterval;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref ticksSinceUpdate, "ticksSinceUpdate", 0);
            Scribe_Values.Look(ref cachedOutput, "cachedOutput", 0f);
        }

        public override void CompTick()
        {
            base.CompTick();
            ticksSinceUpdate++;
            if (ticksSinceUpdate >= UpdateInterval)
            {
                ticksSinceUpdate = 0;
                Recalculate();
            }
        }

        /// <summary>重算洋流強度、浸水狀態與通道阻塞。地圖的水位每 250 ticks 才更新一次，這裡跟著同一個節奏。</summary>
        public void Recalculate()
        {
            Map map = parent.Map;
            MapComponent_Flooding flooding = map?.Flooding();
            underwaterMap = map != null && FloodingUtility.IsWaterMap(map) && flooding != null && flooding.Active;
            submerged = underwaterMap && IsFullySubmerged(map, flooding, parent.OccupiedRect(), CurrentProps.minWaterLevel);
            blockedCells = 0;
            flowCells = 0;
            strength = 0f;
            cachedOutput = 0f;
            if (!submerged)
            {
                return;
            }
            strength = OceanCurrentMath.Strength(FloodingUtility.DepthTierOf(map), map.windManager.WindSpeed,
                Find.TickManager.TicksGame, OceanCurrentMath.Phase(parent.thingIDNumber));
            foreach (IntVec3 c in OceanCurrentMath.FlowCells(parent.Position, parent.Rotation, parent.def.size, CurrentProps.flowReach))
            {
                if (!c.InBounds(map))
                {
                    continue;
                }
                flowCells++;
                if (CellBlocksFlow(c, map, flooding, CurrentProps.minWaterLevel))
                {
                    blockedCells++;
                }
            }
            cachedOutput = OceanCurrentMath.Output(base.Props.PowerConsumption, strength, blockedCells);
        }

        /// <summary>建築佔的每一格都要浸在水裡。</summary>
        public static bool IsFullySubmerged(Map map, MapComponent_Flooding flooding, CellRect rect, float minWaterLevel)
        {
            if (map == null || flooding == null || !flooding.Active)
            {
                return false;
            }
            foreach (IntVec3 c in rect)
            {
                if (!c.InBounds(map) || flooding.LevelAt(c) < minWaterLevel)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>這一格會不會擋住水流：沒有水（抽乾的艙室、露出的地面）或是有 blockWind 的東西（牆、石頭、其他發電機）。</summary>
        public static bool CellBlocksFlow(IntVec3 c, Map map, MapComponent_Flooding flooding, float minWaterLevel)
        {
            if (flooding == null || flooding.LevelAt(c) < minWaterLevel)
            {
                return true;
            }
            List<Thing> things = map.thingGrid.ThingsListAt(c);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (!t.Destroyed && t.def.blockWind)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>通道裡被擋住的格數（放置預覽與測試用）。</summary>
        public static int CountBlocked(Map map, IEnumerable<IntVec3> cells, float minWaterLevel)
        {
            MapComponent_Flooding flooding = map?.Flooding();
            int blocked = 0;
            foreach (IntVec3 c in cells)
            {
                if (c.InBounds(map) && CellBlocksFlow(c, map, flooding, minWaterLevel))
                {
                    blocked++;
                }
            }
            return blocked;
        }

        public override string CompInspectStringExtra()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(base.CompInspectStringExtra());
            if (!underwaterMap)
            {
                AppendLine(sb);
                sb.Append("DepthRim.CurrentTurbine.NotUnderwater".Translate().Colorize(ColoredText.WarningColor));
                return sb.ToString();
            }
            if (!submerged)
            {
                AppendLine(sb);
                sb.Append("DepthRim.CurrentTurbine.NotSubmerged".Translate().Colorize(ColoredText.WarningColor));
                return sb.ToString();
            }
            AppendLine(sb);
            sb.Append("DepthRim.CurrentTurbine.Current".Translate(strength.ToStringPercent()));
            if (blockedCells > 0)
            {
                AppendLine(sb);
                int loss = Mathf.RoundToInt((1f - OceanCurrentMath.FlowFactor(blockedCells)) * 100f);
                sb.Append("DepthRim.CurrentTurbine.Blocked".Translate(blockedCells, loss).Colorize(ColoredText.WarningColor));
            }
            return sb.ToString();
        }

        private static void AppendLine(StringBuilder sb)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
        }

        /// <summary>發電量條（比照原版風力發電機）：藍綠色，虛線在發電時才看得出來。</summary>
        public override void PostDraw()
        {
            base.PostDraw();
            Vector2 barSize = new Vector2(parent.def.size.z - 0.95f, 0.14f);
            GenDraw.FillableBarRequest request = new GenDraw.FillableBarRequest
            {
                center = parent.DrawPos + Vector3.up * 0.1f,
                size = barSize,
                fillPercent = PowerPercent,
                filledMat = BarFilledMat,
                unfilledMat = BarUnfilledMat,
                margin = 0.15f,
            };
            Rot4 rotation = parent.Rotation;
            rotation.Rotate(RotationDirection.Clockwise);
            request.rotation = rotation;
            GenDraw.DrawFillableBar(request);
        }
    }
}
