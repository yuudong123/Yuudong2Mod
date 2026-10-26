using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Yuudong2Mod
{
    // Turn It On and Off 포팅. 작업대·TV 같은 전력 건물은 쓰는 동안만 정격 전력을 쓰고, 쉬는 동안에는 1W만 쓴다.
    // HugsLib 대신 GameComponent로 매 틱 갱신하며, 원본 기본 설정값을 쓴다.
    internal static class PowerOnDemandDefs
    {
        private const float IdlePowerUsage = 1f;

        private static readonly string[] WhitelistedDefNames =
        {
            "TubeTelevision", "FlatscreenTelevision", "MegascreenTelevision", "HiTechResearchBench",
            "MultiAnalyzer", "VitalsMonitor", "LightBall", "Loudspeaker", "WastepackAtomizer"
        };

        private static readonly string[] BlacklistedDefNames = { "MechGestator", "LargeMechGestator" };

        private static readonly string[] ReservableDefNames = { "LongRangeMineralScanner", "GroundPenetratingScanner", "DeepDrill" };

        public static readonly Dictionary<ThingDef, float> IdlePower = new Dictionary<ThingDef, float>();
        public static readonly Dictionary<ThingDef, float> ActivePower = new Dictionary<ThingDef, float>();
        public static readonly List<ThingDef> ReservableDefs = new List<ThingDef>();

        public static ThingDef MedicalBed;
        public static ThingDef HiTechResearchBench;
        public static ThingDef Loudspeaker;
        public static ThingDef LightBall;
        public static ThingDef WastepackAtomizer;

        public static void Initialize()
        {
            MedicalBed = DefDatabase<ThingDef>.GetNamedSilentFail("HospitalBed");
            HiTechResearchBench = DefDatabase<ThingDef>.GetNamedSilentFail("HiTechResearchBench");
            Loudspeaker = DefDatabase<ThingDef>.GetNamedSilentFail("Loudspeaker");
            LightBall = DefDatabase<ThingDef>.GetNamedSilentFail("LightBall");
            WastepackAtomizer = DefDatabase<ThingDef>.GetNamedSilentFail("WastepackAtomizer");

            HashSet<string> whitelist = new HashSet<string>(WhitelistedDefNames);
            HashSet<string> blacklist = new HashSet<string>(BlacklistedDefNames);
            foreach (string defName in ReservableDefNames)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (def != null)
                {
                    ReservableDefs.Add(def);
                    whitelist.Add(defName);
                }
            }

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                CompProperties_Power power = def.GetCompProperties<CompProperties_Power>();
                if (power == null || !typeof(CompPowerTrader).IsAssignableFrom(power.compClass))
                {
                    continue;
                }

                bool included = whitelist.Contains(def.defName) || typeof(Building_WorkTable).IsAssignableFrom(def.thingClass);
                if (included && !blacklist.Contains(def.defName))
                {
                    IdlePower[def] = -IdlePowerUsage;
                    ActivePower[def] = -power.PowerConsumption;
                }
            }
        }
    }

    public class PowerOnDemandComponent : GameComponent
    {
        private const int RescanPeriod = 2000;

        private static PowerOnDemandComponent instance;

        private readonly Dictionary<Map, MapData> mapData = new Dictionary<Map, MapData>();
        // 폰의 간격 틱(시청 등)은 최대 15틱마다 한 번 돈다. 그보다 조금 길게 사용 중으로 친다.
        private const int UsedGraceTicks = 20;

        private readonly Dictionary<Building, int> lastUsedTick = new Dictionary<Building, int>();

        private class MapData
        {
            public int buildingCount = -1;
            public int ticksToRescan;
            public readonly HashSet<Building> controlled = new HashSet<Building>();
            public readonly List<Building_Bed> medicalBeds = new List<Building_Bed>();
            public readonly List<Building> researchBenches = new List<Building>();
            public readonly List<Building> reservables = new List<Building>();
            public readonly List<Building> loudspeakers = new List<Building>();
            public readonly List<Building> lightBalls = new List<Building>();
            public readonly List<Building> atomizers = new List<Building>();

            public void Clear()
            {
                controlled.Clear();
                medicalBeds.Clear();
                researchBenches.Clear();
                reservables.Clear();
                loudspeakers.Clear();
                lightBalls.Clear();
                atomizers.Clear();
            }
        }

        public PowerOnDemandComponent(Game game)
        {
            instance = this;
        }

        public static void MarkUsed(Building building)
        {
            if (building != null && instance != null)
            {
                instance.lastUsedTick[building] = Find.TickManager.TicksGame;
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            instance = this;
            mapData.Clear();
            lastUsedTick.Clear();
        }

        public override void GameComponentTick()
        {
            if (!Yuudong2Mod.AppliedFeatures.Contains(Features.PowerOnDemand))
            {
                return;
            }

            int now = Find.TickManager.TicksGame;
            if (now % 2500 == 0)
            {
                lastUsedTick.RemoveAll(kv => now - kv.Value > UsedGraceTicks || kv.Key.Destroyed);
            }

            List<Map> maps = Find.Maps;
            if (mapData.Count != maps.Count)
            {
                mapData.Clear();
            }

            foreach (Map map in maps)
            {
                if (!mapData.TryGetValue(map, out MapData data))
                {
                    data = new MapData();
                    mapData[map] = data;
                }

                int buildingCount = map.listerBuildings.allBuildingsColonist.Count;
                if (data.buildingCount != buildingCount || data.ticksToRescan <= 0)
                {
                    data.buildingCount = buildingCount;
                    data.ticksToRescan = RescanPeriod;
                    Scan(map, data);
                }

                data.ticksToRescan--;
                MarkActiveBuildings(data);

                foreach (Building building in data.controlled)
                {
                    CompPowerTrader power = building.TryGetComp<CompPowerTrader>();
                    if (power == null)
                    {
                        continue;
                    }

                    power.PowerOutput = lastUsedTick.TryGetValue(building, out int used) && now - used <= UsedGraceTicks
                        ? PowerOnDemandDefs.ActivePower[building.def]
                        : PowerOnDemandDefs.IdlePower[building.def];
                }
            }
        }

        // 이번 틱 판정 결과는 다음 틱 전력에 반영된다.
        private void MarkActiveBuildings(MapData data)
        {
            foreach (Building_Bed bed in data.medicalBeds)
            {
                if (bed.AnyOccupants)
                {
                    MarkFacilities(bed);
                }
            }

            foreach (Building bench in data.researchBenches)
            {
                if (bench.Map.reservationManager.IsReservedByAnyoneOf(bench, bench.Faction))
                {
                    MarkUsed(bench);
                    MarkFacilities(bench);
                }
            }

            foreach (Building building in data.reservables)
            {
                if (building.Map.reservationManager.IsReservedByAnyoneOf(building, building.Faction))
                {
                    MarkUsed(building);
                }
            }

            foreach (Building speaker in data.loudspeakers)
            {
                CompLoudspeaker comp = speaker.TryGetComp<CompLoudspeaker>();
                if (comp != null && comp.Active)
                {
                    MarkUsed(speaker);
                }
            }

            foreach (Building lightBall in data.lightBalls)
            {
                if (lightBall.TryGetComp<CompLightball>() != null && lightBall.IsRitualTarget())
                {
                    MarkUsed(lightBall);
                }
            }

            foreach (Building atomizer in data.atomizers)
            {
                CompAtomizer comp = atomizer.TryGetComp<CompAtomizer>();
                if (comp != null && comp.FillPercent != 0f)
                {
                    MarkUsed(atomizer);
                }
            }
        }

        private void MarkFacilities(Building building)
        {
            CompAffectedByFacilities facilities = building.TryGetComp<CompAffectedByFacilities>();
            if (facilities == null)
            {
                return;
            }

            foreach (Thing facility in facilities.LinkedFacilitiesListForReading)
            {
                if (facility is Building facilityBuilding)
                {
                    MarkUsed(facilityBuilding);
                }
            }
        }

        private static void Scan(Map map, MapData data)
        {
            data.Clear();
            ListerBuildings lister = map.listerBuildings;
            foreach (ThingDef def in PowerOnDemandDefs.ActivePower.Keys)
            {
                data.controlled.UnionWith(lister.AllBuildingsColonistOfDef(def));
            }

            if (PowerOnDemandDefs.MedicalBed != null)
            {
                foreach (Building building in lister.AllBuildingsColonistOfDef(PowerOnDemandDefs.MedicalBed))
                {
                    if (building is Building_Bed bed)
                    {
                        data.medicalBeds.Add(bed);
                    }
                }
            }

            AddAll(lister, PowerOnDemandDefs.HiTechResearchBench, data.researchBenches);
            AddAll(lister, PowerOnDemandDefs.Loudspeaker, data.loudspeakers);
            AddAll(lister, PowerOnDemandDefs.LightBall, data.lightBalls);
            AddAll(lister, PowerOnDemandDefs.WastepackAtomizer, data.atomizers);
            foreach (ThingDef def in PowerOnDemandDefs.ReservableDefs)
            {
                data.reservables.AddRange(lister.AllBuildingsColonistOfDef(def));
            }
        }

        private static void AddAll(ListerBuildings lister, ThingDef def, List<Building> target)
        {
            if (def != null)
            {
                target.AddRange(lister.AllBuildingsColonistOfDef(def));
            }
        }
    }

    [HarmonyPatchCategory(Features.PowerOnDemand)]
    [HarmonyPatch(typeof(Building_WorkTable), nameof(Building_WorkTable.UsedThisTick))]
    internal static class Building_WorkTable_UsedThisTick_Patch
    {
        private static void Prefix(Building_WorkTable __instance)
        {
            PowerOnDemandComponent.MarkUsed(__instance);
        }
    }

    [HarmonyPatchCategory(Features.PowerOnDemand)]
    [HarmonyPatch(typeof(JobDriver_WatchBuilding), "WatchTickAction")]
    internal static class JobDriver_WatchBuilding_WatchTickAction_Patch
    {
        private static void Prefix(JobDriver_WatchBuilding __instance)
        {
            PowerOnDemandComponent.MarkUsed(__instance.job.targetA.Thing as Building);
        }
    }
}
