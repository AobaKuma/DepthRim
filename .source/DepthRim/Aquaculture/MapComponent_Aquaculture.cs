using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace DepthRim
{
    /// <summary>某一座箱網這一刻的環境（每 250 tick 由 <see cref="MapComponent_Aquaculture.EnvironmentFor"/> 算）。</summary>
    public struct CageEnvironment
    {
        public CageVenue venue;
        public float waterTemp;
        public float oxygenExtra;
        public float growthFactor;
        public bool bloom;
        public int neighbors;
        public int aerators;
        public OceanCurrentKind current;
    }

    /// <summary>
    /// 箱網的地圖層資訊（§12）：場地判定、水溫、天氣與事件修正、曝氣機與鄰居、藻華／病害／掠食者／洋流的觸發、水中魚影。
    /// 不逐一推進箱網（那是 <see cref="CompNetCage"/> 自己的 CompTickRare），避免重複計算。
    /// </summary>
    public class MapComponent_Aquaculture : MapComponent
    {
        public const int Version = 1;

        public const float NeighborGap = 8f;

        private int version = Version;

        private readonly List<Building_NetCage> cages = new List<Building_NetCage>();

        private readonly List<Building> aerators = new List<Building>();

        private int warmTicks;

        private int lastPredatorTick = -9999999;

        private int lastSpreadMessageTick = -9999999;

        private readonly Queue<int> fleckTicks = new Queue<int>();

        public static bool debugNoEvents;

        public MapComponent_Aquaculture(Map map) : base(map)
        {
        }

        public static MapComponent_Aquaculture For(Map map)
        {
            return map?.GetComponent<MapComponent_Aquaculture>();
        }

        public static bool Enabled => DepthRimModSettings.Current.enableNetCages;

        public IReadOnlyList<Building_NetCage> Cages => cages;

        public IReadOnlyList<Building> Aerators => aerators;

        public int WarmTicks => warmTicks;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref version, "version", Version);
            Scribe_Values.Look(ref warmTicks, "warmTicks");
            Scribe_Values.Look(ref lastPredatorTick, "lastPredatorTick", -9999999);
        }

        public void Register(Building_NetCage cage)
        {
            if (!cages.Contains(cage)) { cages.Add(cage); }
        }

        public void Deregister(Building_NetCage cage)
        {
            cages.Remove(cage);
        }

        public void RegisterAerator(Building b)
        {
            if (!aerators.Contains(b)) { aerators.Add(b); }
        }

        public void DeregisterAerator(Building b)
        {
            aerators.Remove(b);
        }

        // ── 場地與水深 ──

        public CageVenue Venue => SeaLevels.IsSeaLevel(map) ? CageVenue.OpenSea : CageVenue.Bay;

        public static bool IsSurfaceWater(TerrainDef t)
        {
            return FishHabitat.IsSurfaceWater(t);
        }

        /// <summary>外海：正下方海洋格的種類與水深。拿不到（陸塊、沒有海洋層）回傳 false，退回表面模型。</summary>
        public bool TryOceanBelow(out OceanTemperature.Kind kind, out float depth)
        {
            kind = OceanTemperature.Kind.OpenWater;
            depth = 0f;
            if (Venue != CageVenue.OpenSea)
            {
                return false;
            }
            PlanetTile below = OceanWorldView.TileBelow(map.Tile);
            return OceanTemperature.TryGetWater(below, out kind, out depth, out _);
        }

        /// <summary>某塊範圍的水深（m）：灣內淺水 10、深水 50；外海用正下方海洋格；拿不到海洋格時 50。</summary>
        public float WaterDepthAt(CellRect rect)
        {
            if (Venue == CageVenue.OpenSea)
            {
                return TryOceanBelow(out _, out float d) ? d : NetCageMath.BayDeepDepth;
            }
            float depth = NetCageMath.BayDeepDepth;
            foreach (IntVec3 c in rect)
            {
                if (c.InBounds(map) && map.terrainGrid.TerrainAt(c) != TerrainDefOf.WaterOceanDeep)
                {
                    depth = NetCageMath.BayShallowDepth;
                }
            }
            return depth;
        }

        public float AirAnnual => map.Tile.Valid ? map.Tile.Tile.temperature : 10f;

        /// <summary>網袋中點的水溫（不含洋流）。</summary>
        public float WaterTemperatureAt(float netDepth)
        {
            float outdoor = map.mapTemperature.OutdoorTemp;
            if (TryOceanBelow(out OceanTemperature.Kind kind, out _))
            {
                return NetCageMath.OpenSeaWaterTemperature(netDepth, AirAnnual, outdoor, kind);
            }
            return NetCageMath.BayWaterTemperature(AirAnnual, outdoor);
        }

        // ── 天氣與事件 ──

        public bool BayRoughWeather
        {
            get
            {
                WeatherDef w = map.weatherManager?.curWeather;
                if (w == null) { return false; }
                if (Venue == CageVenue.OpenSea && w.HasModExtension<SeaStateExtension>())
                {
                    return false; // 海況天氣由海況處理
                }
                return NetCageMath.BayRoughWeather(w.windSpeedFactor, w.rainRate);
            }
        }

        public bool SeaStorm => Venue == CageVenue.OpenSea && SeaState.Of(map).buildingHitMtbHours > 0f;

        public bool Bloom => map.gameConditionManager.ConditionIsActive(DepthRimDefOf.DepthRim_AlgalBloom);

        public OceanCurrentKind Current
        {
            get
            {
                if (Venue != CageVenue.OpenSea) { return OceanCurrentKind.None; }
                if (map.gameConditionManager.ConditionIsActive(DepthRimDefOf.DepthRim_WarmCurrent)) { return OceanCurrentKind.Warm; }
                if (map.gameConditionManager.ConditionIsActive(DepthRimDefOf.DepthRim_ColdCurrent)) { return OceanCurrentKind.Cold; }
                return OceanCurrentKind.None;
            }
        }

        public ClimateEffect VanillaClimate
        {
            get
            {
                GameConditionManager g = map.gameConditionManager;
                return ClimateMath.Vanilla(
                    g.ConditionIsActive(GameConditionDefOf.HeatWave),
                    g.ConditionIsActive(GameConditionDefOf.ColdSnap),
                    g.ConditionIsActive(GameConditionDefOf.VolcanicWinter),
                    g.ConditionIsActive(GameConditionDefOf.ToxicFallout));
            }
        }

        public int NeighborsOf(Building_NetCage cage, float gap)
        {
            int n = 0;
            for (int i = 0; i < cages.Count; i++)
            {
                if (cages[i] != cage && cages[i].Spawned && RectGap(cage.OccupiedRect(), cages[i].OccupiedRect()) <= gap)
                {
                    n++;
                }
            }
            return n;
        }

        /// <summary>病害用：中心距離 12 格內的其他箱網。</summary>
        public void CentersWithin(Building_NetCage cage, float radius, List<Building_NetCage> result)
        {
            result.Clear();
            for (int i = 0; i < cages.Count; i++)
            {
                if (cages[i] != cage && cages[i].Spawned && (cages[i].TrueCenter() - cage.TrueCenter()).MagnitudeHorizontal() <= radius)
                {
                    result.Add(cages[i]);
                }
            }
        }

        /// <summary>兩個矩形之間的格距（相鄰 = 0，中間隔一格 = 1）。</summary>
        public static float RectGap(CellRect a, CellRect b)
        {
            int dx = Mathf.Max(0, Mathf.Max(a.minX - b.maxX, b.minX - a.maxX) - 1);
            int dz = Mathf.Max(0, Mathf.Max(a.minZ - b.maxZ, b.minZ - a.maxZ) - 1);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public const float AeratorRadius = 4.9f;

        /// <summary>有電的曝氣機，半徑 4.9 格內碰到箱網任一格就算（每座最多計 2 台）。</summary>
        public int AeratorsFor(Building_NetCage cage)
        {
            int n = 0;
            CellRect rect = cage.OccupiedRect();
            for (int i = 0; i < aerators.Count; i++)
            {
                Building a = aerators[i];
                if (!a.Spawned) { continue; }
                CompPowerTrader power = a.GetComp<CompPowerTrader>();
                if (power != null && !power.PowerOn) { continue; }
                IntVec3 closest = rect.ClosestCellTo(a.Position);
                if (closest.DistanceTo(a.Position) <= AeratorRadius)
                {
                    n++;
                }
            }
            return Mathf.Min(n, NetCageMath.MaxAerators);
        }

        public CageEnvironment EnvironmentFor(Building_NetCage cage, float netDepth, FloatRange speciesTemp)
        {
            CageEnvironment env = new CageEnvironment
            {
                venue = Venue,
                current = Current,
                bloom = Bloom,
                neighbors = NeighborsOf(cage, NeighborGap),
                aerators = AeratorsFor(cage),
                growthFactor = 1f
            };
            env.waterTemp = WaterTemperatureAt(netDepth) + ClimateMath.CurrentOffset(env.current, netDepth, env.venue);
            ClimateEffect climate = VanillaClimate;
            climate.Add(ClimateMath.Current(env.current, env.venue));
            env.oxygenExtra = climate.oxygen;
            env.growthFactor *= climate.growth * ClimateMath.CurrentGrowth(env.current, env.venue, speciesTemp.min, speciesTemp.max);
            if (BayRoughWeather)
            {
                env.oxygenExtra -= NetCageMath.BayWeatherPenalty;
                env.growthFactor *= NetCageMath.BayStormGrowth;
            }
            if (SeaStorm)
            {
                env.growthFactor *= cage.Props.stormGrowthFactor;
            }
            return env;
        }

        // ── 事件 ──

        public const float BloomMtbDaysBay = 20f;
        public const float BloomMtbDaysSea = 25f;
        public const float PredatorMtbDays = 10f;
        public const int PredatorMinDays = 5;
        public const float CurrentMtbDays = 20f;

        public int StockedCages()
        {
            int n = 0;
            for (int i = 0; i < cages.Count; i++)
            {
                if (cages[i].Spawned && cages[i].Cage.HasFish) { n++; }
            }
            return n;
        }

        public bool AnyFed()
        {
            for (int i = 0; i < cages.Count; i++)
            {
                if (cages[i].Spawned && cages[i].Cage.HasFish && cages[i].Cage.TicksSinceFed < 3 * GenDate.TicksPerDay) { return true; }
            }
            return false;
        }

        public bool BloomConditionsMet()
        {
            if (StockedCages() < 2 || warmTicks < 5 * GenDate.TicksPerDay)
            {
                return false;
            }
            if (Venue == CageVenue.Bay)
            {
                return AnyFed();
            }
            return map.windManager.WindSpeed < 0.7f;
        }

        public override void MapComponentTick()
        {
            if (cages.Count == 0 || !Enabled)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            if ((now + map.uniqueID * 7) % 250 == 0)
            {
                Tick250();
            }
            if ((now + map.uniqueID * 13) % FishShadowField.Interval == 0)
            {
                SpawnFishShadows();
            }
            if ((now + map.uniqueID * 17) % GenDate.TicksPerDay == 0)
            {
                DailyDisease();
            }
        }

        private void Tick250()
        {
            warmTicks = map.mapTemperature.OutdoorTemp > 18f ? warmTicks + 250 : 0;
            if (debugNoEvents || StockedCages() == 0)
            {
                return;
            }
            ClimateEffect climate = VanillaClimate;
            climate.Add(ClimateMath.Current(Current, Venue));
            if (!Bloom && BloomConditionsMet())
            {
                float mtb = (Venue == CageVenue.Bay ? BloomMtbDaysBay : BloomMtbDaysSea) * climate.bloomMtbFactor;
                if (Rand.MTBEventOccurs(mtb, GenDate.TicksPerDay, 250))
                {
                    TryFire(DepthRimDefOf.DepthRim_AlgalBloomIncident);
                }
            }
            int now = Find.TickManager.TicksGame;
            if (now - lastPredatorTick >= PredatorMinDays * GenDate.TicksPerDay)
            {
                float mtb = PredatorMtbDays / (Venue == CageVenue.Bay ? 0.5f : 0.6f);
                if (Rand.MTBEventOccurs(mtb, GenDate.TicksPerDay, 250) && TryFire(DepthRimDefOf.DepthRim_CagePredator))
                {
                    lastPredatorTick = now;
                }
            }
            if (Venue == CageVenue.OpenSea && Current == OceanCurrentKind.None && Rand.MTBEventOccurs(CurrentMtbDays, GenDate.TicksPerDay, 250))
            {
                TryFire(Rand.Bool ? DepthRimDefOf.DepthRim_WarmCurrentIncident : DepthRimDefOf.DepthRim_ColdCurrentIncident);
            }
        }

        public bool TryFire(IncidentDef def)
        {
            if (def == null) { return false; }
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, map);
            parms.forced = true;
            return def.Worker.CanFireNow(parms) && def.Worker.TryExecute(parms);
        }

        // ── 病害：每天一次 ──

        private readonly List<Building_NetCage> tmpNear = new List<Building_NetCage>();

        public void DailyDisease()
        {
            int now = Find.TickManager.TicksGame;
            int spreadTo = 0;
            Building_NetCage first = null;
            for (int i = 0; i < cages.Count; i++)
            {
                Building_NetCage cage = cages[i];
                if (!cage.Spawned || !cage.Cage.HasFish) { continue; }
                CentersWithin(cage, DiseaseSpread.NeighborRadius, tmpNear);
                cage.Cage.DiseaseDay(tmpNear.Count);
            }
            for (int i = 0; i < cages.Count; i++)
            {
                Building_NetCage cage = cages[i];
                CompNetCage comp = cage.Cage;
                if (!cage.Spawned || comp.DiseaseSeverity <= 0f || now - comp.LastSpreadCheck < DiseaseSpread.SpreadIntervalDays * GenDate.TicksPerDay)
                {
                    continue;
                }
                comp.LastSpreadCheck = now;
                CentersWithin(cage, DiseaseSpread.NeighborRadius, tmpNear);
                for (int j = 0; j < tmpNear.Count; j++)
                {
                    CompNetCage other = tmpNear[j].Cage;
                    if (!other.HasFish || other.DiseaseSeverity > 0f) { continue; }
                    float chance = DiseaseSpread.SpreadChance(comp.DiseaseSeverity, tmpNear.Count, Venue, other.Species.diseaseResistance);
                    if (DiseaseSpread.debugAlwaysInfect || Rand.Chance(chance))
                    {
                        other.Infect();
                        spreadTo++;
                        first = first ?? tmpNear[j];
                    }
                }
            }
            if (spreadTo > 0 && now - lastSpreadMessageTick >= GenDate.TicksPerDay)
            {
                lastSpreadMessageTick = now;
                Messages.Message("DepthRim.NetCage.DiseaseSpread".Translate(spreadTo), first, MessageTypeDefOf.NegativeEvent);
            }
        }

        // ── 水中魚影 ──

        public int FishShadowsSpawned { get; private set; }

        public float FishDensity()
        {
            bool bloom = Bloom;
            float sum = 0f;
            for (int i = 0; i < cages.Count; i++)
            {
                if (cages[i].Spawned && cages[i].Cage.HasFish)
                {
                    sum += FishShadowField.PerCage(true, cages[i].Cage.FeedStore > 0f, bloom);
                }
            }
            return FishShadowField.Clamp(sum);
        }

        public void SpawnFishShadows()
        {
            if (!ModsConfig.OdysseyActive || map != Find.CurrentMap)
            {
                return;
            }
            int now = Find.TickManager.TicksGame;
            while (fleckTicks.Count > 0 && now - fleckTicks.Peek() > 600)
            {
                fleckTicks.Dequeue();
            }
            float density = FishDensity();
            int n = FishShadowField.FlecksToSpawn(density, fleckTicks.Count);
            if (n <= 0) { return; }
            List<Building_NetCage> stocked = new List<Building_NetCage>();
            for (int i = 0; i < cages.Count; i++)
            {
                if (cages[i].Spawned && cages[i].Cage.HasFish) { stocked.Add(cages[i]); }
            }
            if (stocked.Count == 0) { return; }
            for (int i = 0; i < n; i++)
            {
                Building_NetCage cage = stocked.RandomElement();
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    IntVec3 c = cage.Position + GenRadial.RadialPattern[Rand.Range(0, GenRadial.NumCellsInRadius(FishShadowField.Radius))];
                    if (!ShadowCellOk(c)) { continue; }
                    FleckDef def = Rand.Bool ? FleckDefOf.FishShadow : FleckDefOf.FishShadowReverse;
                    FleckCreationData data = FleckMaker.GetDataStatic(c.ToVector3Shifted(), map, def, 3f * Rand.Range(0.4f, 1.1f));
                    data.rotation = Rand.Range(0f, 180f);
                    data.rotationRate = Rand.Sign * 75f * Rand.Range(0.75f, 1.25f);
                    map.flecks.CreateFleck(data);
                    fleckTicks.Enqueue(now);
                    FishShadowsSpawned++;
                    break;
                }
            }
        }

        public bool ShadowCellOk(IntVec3 c)
        {
            if (!c.InBounds(map) || !c.ShouldSpawnMotesAt(map)) { return false; }
            for (int i = 0; i < 9; i++)
            {
                IntVec3 n = c + GenAdj.AdjacentCellsAndInside[i];
                if (!n.InBounds(map)) { return false; }
                TerrainDef t = map.terrainGrid.TerrainAt(n);
                if (t == null || !(t == DepthRimDefOf.DepthRim_OpenSea || t.IsWater)) { return false; }
            }
            return true;
        }
    }
}
