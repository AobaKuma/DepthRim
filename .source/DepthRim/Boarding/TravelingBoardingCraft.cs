using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>
    /// 玩家突擊艇在世界地圖上的航跡：沿海路前進，抵達後在目的地的地圖降落（沒有地圖就開一個海洋地點）。
    /// 降落點與敵方的突擊艇一樣：優先切入對方的密閉艙室，否則降在海床上。
    /// 目的地是遭遇型地標時不降落：暫停並跳出對話（<see cref="LandmarkEncounter"/>），處理完帶著收穫開回出發地。
    /// </summary>
    public class WorldObject_TravelingBoardingCraft : WorldObject, IThingHolder
    {
        public const int TicksPerTile = 250;

        private ThingOwner<Building_BoardingCraft> craftHolder;

        public PlanetTile from = PlanetTile.Invalid;

        public PlanetTile to = PlanetTile.Invalid;

        public int departTick;

        public int arriveTick;

        /// <summary>出發時目的地上的據點（例如會移動的海盜潛艇）：抵達時它走了就追過去。回程時是出發地。</summary>
        public MapParent targetParent;

        /// <summary>出發地（地圖的父物件）：處理完遭遇型地標後開回這裡。</summary>
        public MapParent home;

        /// <summary>正在回程：抵達時降落在 <see cref="home"/> 的地圖。</summary>
        public bool returning;

        /// <summary>
        /// 停在遭遇型地標旁、對話還沒處理完。對話會暫停遊戲，所以只要遊戲還在跑、這裡還是 true，
        /// 就表示對話被關掉了（例如讀檔）：重新打開。
        /// </summary>
        public bool encounterPending;

        public WorldObject_TravelingBoardingCraft()
        {
            craftHolder = new ThingOwner<Building_BoardingCraft>(this);
        }

        public Building_BoardingCraft Craft => craftHolder.InnerListForReading.FirstOrDefault();

        public ThingOwner GetDirectlyHeldThings() => craftHolder;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public void Setup(Building_BoardingCraft craft, PlanetTile origin, PlanetTile destination, MapParent homeParent = null)
        {
            craftHolder.TryAdd(craft);
            from = origin;
            to = destination;
            Tile = origin;
            home = homeParent;
            SetFaction(craft.Faction);
            targetParent = Find.WorldObjects.MapParentAt(destination);
            SetCourse(origin, destination);
        }

        private void SetCourse(PlanetTile origin, PlanetTile destination)
        {
            from = origin;
            to = destination;
            departTick = Find.TickManager.TicksGame;
            int distance = Mathf.Max(1, OceanRouting.Distance(origin, destination));
            arriveTick = departTick + distance * TicksPerTile;
        }

        public float Progress => arriveTick <= departTick ? 1f
            : Mathf.Clamp01((float)(Find.TickManager.TicksGame - departTick) / (arriveTick - departTick));

        public override Vector3 DrawPos => encounterPending ? Find.WorldGrid.GetTileCenter(to)
            : Vector3.Slerp(Find.WorldGrid.GetTileCenter(from), Find.WorldGrid.GetTileCenter(to), Progress);

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (encounterPending)
            {
                // 同一個畫格可能跑好幾個 tick，對話打開後還沒暫停：已經有對話開著就不再開
                if (Find.WindowStack.WindowOfType<Dialog_NodeTree>() == null)
                {
                    OpenEncounter();
                }
                return;
            }
            if (Find.TickManager.TicksGame < arriveTick)
            {
                return;
            }
            if (targetParent != null && !targetParent.Destroyed && targetParent.Tile != to && OceanRouting.Navigable(targetParent.Tile))
            {
                Tile = to;
                SetCourse(to, targetParent.Tile);
                return;
            }
            Arrive();
        }

        public void Arrive()
        {
            Building_BoardingCraft craft = Craft;
            if (craft == null)
            {
                Destroy();
                return;
            }
            Tile = to;
            if (!returning && OceanLandmarkUtility.EncounterAt(to) != null)
            {
                encounterPending = true;
                OpenEncounter();
                return;
            }
            if (returning && home != null && !home.Destroyed && home.HasMap)
            {
                LandOn(craft, home.Map, home);
                return;
            }
            MapParent parent = Find.WorldObjects.MapParentAt(to);
            Map map = parent?.Map;
            if (map == null)
            {
                if (parent == null)
                {
                    parent = (MapParent)WorldObjectMaker.MakeWorldObject(DepthRimDefOf.DepthRim_OceanSite);
                    parent.Tile = to;
                    parent.SetFaction(Faction.OfPlayer);
                    Find.WorldObjects.Add(parent);
                }
                IntVec3 size = parent is WorldObject_PirateSubmarine pirate ? pirate.Ext.mapSize
                    : parent is WorldObject_LandmarkSite site ? site.MapSize
                    : Find.World.info.initialMapSize;
                map = GetOrGenerateMapUtility.GetOrGenerateMap(to, size, parent.def);
            }
            LandOn(craft, map, parent);
        }

        private void LandOn(Building_BoardingCraft craft, Map map, MapParent parent)
        {
            craftHolder.Remove(craft);
            craft.destinationTile = PlanetTile.Invalid;
            Faction target = parent.Faction != null && parent.Faction != craft.Faction ? parent.Faction : null;
            if (!BoardingUtility.TryFindLandingCell(map, target ?? craft.Faction, out IntVec3 cell, out _))
            {
                cell = CellFinder.RandomCell(map);
            }
            SkyfallerMaker.SpawnSkyfaller(BoardingDefOf.DepthRim_BoardingCraftIncoming, craft, cell, map);
            Messages.Message("DepthRim.Boarding.Arrived".Translate(parent.LabelCap), new LookTargets(cell, map), MessageTypeDefOf.PositiveEvent);
            Destroy();
        }

        /// <summary>遭遇型地標：暫停並跳出對話；結束後收穫留在艇上，艇開回出發地。</summary>
        private void OpenEncounter()
        {
            Building_BoardingCraft craft = Craft;
            WorldObject_OceanLandmark landmark = OceanLandmarkUtility.EncounterAt(to);
            if (craft == null || landmark == null)
            {
                encounterPending = false;
                StartReturn();
                return;
            }
            List<Pawn> crew = craft.Aboard.ToList();
            Map homeMap = home != null && !home.Destroyed ? home.Map : null;
            CameraJumper.TryJump(CameraJumper.GetWorldTarget(this));
            LandmarkEncounter.Open(landmark, crew, homeMap, LoadCargo, FinishEncounter);
        }

        /// <summary>對話結束（不論選了什麼）：開回出發地。</summary>
        public void FinishEncounter()
        {
            encounterPending = false;
            StartReturn();
        }

        public void LoadCargo(List<Thing> cargo)
        {
            Building_BoardingCraft craft = Craft;
            if (craft == null)
            {
                return;
            }
            ThingOwner aboard = ((IThingHolder)craft).GetDirectlyHeldThings();
            foreach (Thing t in cargo)
            {
                aboard.TryAdd(t, canMergeWithExistingStacks: true);
            }
        }

        /// <summary>開回出發地（它是會移動的潛艇時追著它）；出發地沒了就就地開一個海洋地點降落。</summary>
        public void StartReturn()
        {
            returning = true;
            if (home == null || home.Destroyed || !home.HasMap)
            {
                Arrive();
                return;
            }
            PlanetTile homeTile = WorldComponent_OceanLandmarks.OceanCenter(home);
            if (!homeTile.Valid)
            {
                homeTile = from;
            }
            targetParent = OceanRouting.IsOcean(home.Tile) ? home : null;
            Tile = to;
            SetCourse(to, homeTile);
            Craft?.BurnFuelFor(OceanRouting.Distance(from, to));
            Messages.Message("DepthRim.Landmark.Returning".Translate(home.LabelCap), this, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref craftHolder, "craft", this);
            Scribe_Values.Look(ref from, "from", PlanetTile.Invalid);
            Scribe_Values.Look(ref to, "to", PlanetTile.Invalid);
            Scribe_Values.Look(ref departTick, "departTick", 0);
            Scribe_Values.Look(ref arriveTick, "arriveTick", 0);
            Scribe_References.Look(ref targetParent, "targetParent");
            Scribe_References.Look(ref home, "home");
            Scribe_Values.Look(ref returning, "returning", false);
            Scribe_Values.Look(ref encounterPending, "encounterPending", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                craftHolder ??= new ThingOwner<Building_BoardingCraft>(this);
            }
        }
    }
}
