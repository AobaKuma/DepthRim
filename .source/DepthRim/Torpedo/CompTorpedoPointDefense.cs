using DMSE;
using Fortified;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace DepthRim
{
    /// <summary>
    /// 近防砲（超空泡機砲，DMSE CIWS 的換皮，設計稿 §11 M5 5.7）：點防禦式的世界層武器。
    /// 不用火控、不佔通道：只要這張地圖的陣營聽得到魚雷，魚雷快到時每次掃描都可能開火，瞬間結算命中。
    /// 只打以這張地圖為目的地的魚雷。
    /// </summary>
    public class CompProperties_TorpedoPointDefense : CompProperties
    {
        /// <summary>每輪射擊擊毀魚雷的機率。</summary>
        public float hitChance = 0.35f;

        /// <summary>魚雷剩下多少 tick 抵達時才開火（射程很短：只打最後一段）。</summary>
        public int engageEtaTicks = 600;

        /// <summary>兩輪射擊之間的冷卻。</summary>
        public int cooldownTicks = 240;

        /// <summary>每輪消耗的彈藥（CompRefuelable）；沒有 CompRefuelable 時不消耗。</summary>
        public float fuelPerBurst = 10f;

        public SoundDef fireSound;

        public CompProperties_TorpedoPointDefense()
        {
            compClass = typeof(CompTorpedoPointDefense);
        }
    }

    public class CompTorpedoPointDefense : ThingComp, IAirEffector
    {
        public int cooldownUntil;

        public CompProperties_TorpedoPointDefense Props => (CompProperties_TorpedoPointDefense)props;

        private CompPowerTrader Power => parent.GetComp<CompPowerTrader>();

        private CompRefuelable Ammo => parent.GetComp<CompRefuelable>();

        private CompBreakdownable Breakdown => parent.GetComp<CompBreakdownable>();

        public Thing Source => parent;

        public bool UsesFireControl => false;

        public int ReadyTick => cooldownUntil;

        public bool Operational => parent.Spawned
            && (Power == null || Power.PowerOn)
            && (Breakdown == null || !Breakdown.BrokenDown);

        public bool HasAmmo => Ammo == null || Ammo.Fuel >= Props.fuelPerBurst;

        public bool Armed => Operational && HasAmmo;

        public bool CanFire(int now) => Armed && now >= cooldownUntil;

        public bool CanEngage(AirEngagement engagement)
        {
            if (engagement == null || !parent.Spawned || !TorpedoUtility.IsTorpedoTrack(engagement.TargetObject))
            {
                return false;
            }
            return engagement.Track != null && engagement.Track.Destination == parent.Map.Tile
                && engagement.EtaTicks <= Props.engageEtaTicks;
        }

        public AirEffectorShot PrepareShot(AirEngagement engagement) => new AirEffectorShot
        {
            AngularSpeed = 0f,
            BaseHitChance = Props.hitChance,
        };

        public bool TryFire(AirEngagement engagement, int now, Vector3 meetingPoint)
        {
            if (!CanFire(now))
            {
                return false;
            }
            Ammo?.ConsumeFuel(Props.fuelPerBurst);
            cooldownUntil = now + Props.cooldownTicks;
            Props.fireSound?.PlayOneShot(new TargetInfo(parent.Position, parent.Map));
            FleckMaker.Static(parent.TrueCenter(), parent.Map, FleckDefOf.ShotFlash, 6f);
            return true;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            parent.Map.GetComponent<MapComponent_AirEffectors>()?.Register(this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            map?.GetComponent<MapComponent_AirEffectors>()?.Deregister(this);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref cooldownUntil, "cooldownUntil", 0);
        }

        public override string CompInspectStringExtra()
        {
            int now = Find.TickManager.TicksGame;
            if (!Operational)
            {
                return null;
            }
            if (!HasAmmo)
            {
                return "DepthRim.PointDefense.NoAmmo".Translate();
            }
            return now < cooldownUntil
                ? "DepthRim.PointDefense.Cycling".Translate((cooldownUntil - now).ToStringTicksToPeriod())
                : "DepthRim.PointDefense.Ready".Translate();
        }
    }
}
