using RimWorld;
using Verse;

namespace DepthRim
{
    public class CompProperties_FloodPump : CompProperties
    {
        /// <summary>每秒從所在艙室抽走的水量（以格數計：抽 1 格 = 讓 1 格水位下降 100%）。</summary>
        public float cellsPerSecond = 2f;

        public CompProperties_FloodPump()
        {
            compClass = typeof(CompFloodPump);
        }
    }

    /// <summary>
    /// 排水泵（設計稿 §4.3）：抽乾所在的密閉艙室。在開放水域裡沒有效果（抽不完整片海）。
    /// 需要通電；有開關時要打開。實際抽水由 <see cref="MapComponent_Flooding"/> 每次更新統一結算。
    /// </summary>
    public class CompFloodPump : ThingComp
    {
        private CompPowerTrader power;

        private CompFlickable flickable;

        public CompProperties_FloodPump Props => (CompProperties_FloodPump)props;

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

        /// <summary>被抽水的艙室：泵本身所在的房間（牆掛式則取交互格的房間）。</summary>
        public Room DrainedRoom
        {
            get
            {
                if (!parent.Spawned)
                {
                    return null;
                }
                Room room = parent.Position.GetRoom(parent.Map);
                if (room == null && parent.def.hasInteractionCell)
                {
                    room = parent.InteractionCell.GetRoom(parent.Map);
                }
                return room;
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            power = parent.GetComp<CompPowerTrader>();
            flickable = parent.GetComp<CompFlickable>();
            parent.Map.Flooding()?.Register(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            map.Flooding()?.Deregister(this);
        }

        public override string CompInspectStringExtra()
        {
            MapComponent_Flooding flooding = parent.Map?.Flooding();
            if (flooding == null || !flooding.Active)
            {
                return null;
            }
            Room room = DrainedRoom;
            if (room == null)
            {
                return null;
            }
            if (flooding.IsOpenWater(room))
            {
                return "DepthRim.Pump.OpenWater".Translate();
            }
            return "DepthRim.Pump.RoomLevel".Translate(flooding.LevelOf(room).ToStringPercent());
        }
    }
}
