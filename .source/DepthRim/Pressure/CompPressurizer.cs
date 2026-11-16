using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DepthRim
{
    public class CompProperties_Pressurizer : CompProperties
    {
        /// <summary>運作時艙內壓力每小時（2500 ticks）往目標移動幾級。</summary>
        public float levelsPerHour = 1f;

        /// <summary>一台加壓機能控制的艙室大小上限（格）。</summary>
        public int maxRoomCells = 80;

        public CompProperties_Pressurizer()
        {
            compClass = typeof(CompPressurizer);
        }
    }

    /// <summary>
    /// 加壓機：把所在的密閉艙室推到指定的壓力等級，讓裡面的人照那個壓力適應。
    ///
    /// <para>艙壓本身存在 <see cref="MapComponent_PressureChambers"/>（每格），加壓機只是其中一個改變它的東西：
    /// 停機時艙壓維持不變（艙室會記住原有壓力），不會自己洩掉。</para>
    ///
    /// <para>艙室要算「加壓艙」必須：密閉（有屋頂、不碰地圖邊緣）、不超過 <see cref="CompProperties_Pressurizer.maxRoomCells"/> 格、
    /// 地板全部是耐壓甲板或船體平台、所有的門都是水密門（見 <see cref="PressureChamberUtility.Problem"/>）。
    /// 艙壁撐不撐得住壓差由牆體水壓系統（<see cref="MapComponent_WallPressure"/>）統一判定。</para>
    /// </summary>
    public class CompPressurizer : ThingComp
    {
        public const int UpdateInterval = MapComponent_PressureChambers.UpdateInterval;

        private int targetLevel;

        private string problem;

        private CompPowerTrader power;

        private CompFlickable flickable;

        public CompProperties_Pressurizer Props => (CompProperties_Pressurizer)props;

        /// <summary>玩家設定的目標壓力等級（0–3）。</summary>
        public int TargetLevel
        {
            get => targetLevel;
            set => targetLevel = Mathf.Clamp(value, 0, PressureAdaptationMath.MaxLevel);
        }

        public MapComponent_PressureChambers Chambers => parent.Spawned ? parent.Map.GetComponent<MapComponent_PressureChambers>() : null;

        /// <summary>所在艙室目前的艙壓（級）。</summary>
        public float CurrentLevel => Chambers?.AirOf(Room) ?? 0f;

        /// <summary>最近一次結算時艙室是否符合加壓艙的條件。</summary>
        public bool ChamberSealed => parent.Spawned && problem == null;

        /// <summary>艙室不符合條件的原因（翻譯鍵）；符合時為 null。</summary>
        public string Problem => problem;

        /// <summary>同一間艙室已經由另一台加壓機控制。</summary>
        public bool Secondary
        {
            get
            {
                CompPressurizer controller = Chambers?.ControllerOf(Room);
                return controller != null && controller != this;
            }
        }

        /// <summary>這間艙室邊界上撐不住壓差、正在受損的牆與門（由牆體水壓系統判定）。</summary>
        public int OverloadedCount
        {
            get
            {
                Room room = Room;
                MapComponent_WallPressure walls = parent.Spawned ? parent.Map.GetComponent<MapComponent_WallPressure>() : null;
                return room == null || walls == null ? 0 : walls.OverloadedAround(room);
            }
        }

        public bool Working
        {
            get
            {
                if (!parent.Spawned)
                {
                    return false;
                }
                if (power != null && !power.PowerOn)
                {
                    return false;
                }
                return flickable == null || flickable.SwitchIsOn;
            }
        }

        public Room Room => parent.Spawned ? parent.GetRoom() : null;

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            power = parent.GetComp<CompPowerTrader>();
            flickable = parent.GetComp<CompFlickable>();
            MapComponent_PressureChambers chambers = parent.Map.GetComponent<MapComponent_PressureChambers>();
            chambers?.Register(this);
            if (!respawningAfterLoad)
            {
                targetLevel = Mathf.RoundToInt(chambers?.AirOf(Room) ?? 0f);
            }
            RefreshProblem();
            RefreshPowerDraw();
        }

        /// <summary>這台是艙室的控制者，而且艙壓還沒到目標；到了目標、艙室不合格或是副機時只吃待機電力。</summary>
        public bool HasWork
        {
            get
            {
                if (!ChamberSealed || Secondary)
                {
                    return false;
                }
                float tolerance = 0.5f * PressureAdaptationMath.MaxLevel / 255f;
                return Mathf.Abs(CurrentLevel - targetLevel) > tolerance;
            }
        }

        public void RefreshPowerDraw()
        {
            IdlePowerUtility.SetBusy(power, HasWork);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            map.GetComponent<MapComponent_PressureChambers>()?.Deregister(this);
            problem = PressureChamberUtility.NotSealed;
        }

        public void RefreshProblem()
        {
            problem = parent.Spawned ? PressureChamberUtility.Problem(Room, parent.Map, Props.maxRoomCells) : PressureChamberUtility.NotSealed;
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            RefreshProblem();
            RefreshPowerDraw();
        }

        /// <summary>立刻結算這張地圖的艙壓 <paramref name="ticks"/>（驗收測試用；平常由地圖元件每 250 ticks 結算）。</summary>
        public void UpdateChamber(int ticks)
        {
            Chambers?.UpdateNow(ticks);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }
            if (parent.Faction != Faction.OfPlayer)
            {
                yield break;
            }
            yield return new Command_Action
            {
                defaultLabel = "DepthRim.Pressurizer.Lower".Translate(),
                defaultDesc = "DepthRim.Pressurizer.LowerDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/TempLower"),
                action = () => { TargetLevel = targetLevel - 1; RefreshPowerDraw(); },
                Disabled = targetLevel <= 0
            };
            yield return new Command_Action
            {
                defaultLabel = "DepthRim.Pressurizer.Raise".Translate(),
                defaultDesc = "DepthRim.Pressurizer.RaiseDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/TempRaise"),
                action = () => { TargetLevel = targetLevel + 1; RefreshPowerDraw(); },
                Disabled = targetLevel >= PressureAdaptationMath.MaxLevel
            };
        }

        public override string CompInspectStringExtra()
        {
            string target = "DepthRim.Pressurizer.Target".Translate(targetLevel, PressureAdaptationUtility.LevelLabel(targetLevel));
            string line = target + "\n" + "DepthRim.Pressurizer.Current".Translate(CurrentLevel.ToString("0.0"));
            if (problem != null)
            {
                return line + "\n" + problem.Translate(Props.maxRoomCells);
            }
            if (Secondary)
            {
                line += "\n" + "DepthRim.Pressurizer.Secondary".Translate();
            }
            int overloaded = OverloadedCount;
            if (overloaded > 0)
            {
                line += "\n" + "DepthRim.Pressurizer.Overloaded".Translate(overloaded);
            }
            return line;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref targetLevel, "targetLevel", 0);
        }
    }

    public static class PressureChamberUtility
    {
        public const string NotSealed = "DepthRim.Pressurizer.NotSealed";

        public const string TooLarge = "DepthRim.Pressurizer.TooLarge";

        public const string NeedsDeck = "DepthRim.Pressurizer.NeedsDeck";

        public const string NeedsWatertightDoors = "DepthRim.Pressurizer.NeedsWatertightDoors";

        /// <summary>
        /// 這間房間不能當加壓艙（撐不住壓力）的原因（翻譯鍵）；可以時回傳 null。
        /// 條件：密閉（有屋頂、不碰地圖邊緣、不是門口）、格數不超過上限、地板全是耐壓甲板或船體平台、周圍的門都是水密門。
        /// </summary>
        public static string Problem(Room room, Map map, int maxCells)
        {
            if (room == null || room.IsDoorway || room.TouchesMapEdge || room.OpenRoofCount > 0)
            {
                return NotSealed;
            }
            if (room.CellCount > maxCells)
            {
                return TooLarge;
            }
            TerrainAffordanceDef deck = DepthRimDefOf.DepthRim_HullPlatform;
            foreach (IntVec3 c in room.Cells)
            {
                TerrainDef terrain = map.terrainGrid.TerrainAt(c);
                if (terrain?.affordances == null || !terrain.affordances.Contains(deck))
                {
                    return NeedsDeck;
                }
            }
            foreach (IntVec3 c in room.BorderCellsCardinal)
            {
                if (c.InBounds(map) && c.GetEdifice(map) is Building_Door door && !(door is Building_WatertightDoor))
                {
                    return NeedsWatertightDoors;
                }
            }
            return null;
        }
    }
}
