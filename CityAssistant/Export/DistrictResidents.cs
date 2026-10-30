using Game.Areas;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game;
using Game.UI.InGame;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using GameResources = Game.Economy.Resources;

namespace CityAssistant.Export
{
    /// <summary>
    /// The district panel's "residents" numbers (households, residents, pets, wealth, ages, education),
    /// calculated the way the game's ResidentsSection does (game version 1.6.1f1), but for all districts in
    /// one Burst job spread over all CPU cores instead of household by household on the main thread: in
    /// Mega City (240,000 households) the game's way froze the game for about 1.4 seconds per export.
    /// Each worker thread adds into its own rows, which are summed at the end; the totals are plain int
    /// sums, so the order does not change the result (not even when a sum wraps around).
    ///
    /// Being a copy, it can drift from the game's own code after a game update. DistrictsExport.BuildCheck
    /// runs both side by side and lists every difference; run it after each game update.
    /// Rules copied from the game, including its quirks (int sums that wrap, integer division), are marked.
    /// It is a game system only because the fast component lookups can only be created by a system; it is
    /// never scheduled, DistrictsExport calls Compute when it exports.
    /// </summary>
    public partial class DistrictResidents : GameSystemBase
    {
        // One row of ints per district.
        private const int kVisible = 0, kResidents = 1, kPets = 2, kHouseholds = 3, kMaxHouseholds = 4,
            kWealthSum = 5, kIncome = 6, kRent = 7, kUpkeep = 8, kResourceCost = 9, kFees = 10,
            kAge = 11, kEducation = 15, kFields = 20;

        private EntityQuery m_Buildings;
        private EntityQuery m_Parameters;

        protected override void OnCreate()
        {
            base.OnCreate();
            // The same buildings the game's ResidentsSection looks at for a district.
            m_Buildings = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<ResidentialProperty>(),
                    ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Renter>(),
                    ComponentType.ReadOnly<CurrentDistrict>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            m_Parameters = GetEntityQuery(ComponentType.ReadOnly<CitizenHappinessParameterData>());
        }

        protected override void OnUpdate()
        {
        }

        /// <summary>Residents numbers per district, in the same order as <paramref name="districts"/>; null where the panel would hide the section.</summary>
        public object[] Compute(NativeArray<Entity> districts, Entity city)
        {
            var em = EntityManager;

            // Fees are the same for every household; read them once (the game reads them per household).
            var hasFees = em.HasBuffer<ServiceFee>(city);
            float electricityFee = 0f, waterFee = 0f, garbageFee = 0f;
            if (hasFees)
            {
                var fees = em.GetBuffer<ServiceFee>(city, true);
                electricityFee = ServiceFeeSystem.GetFee(PlayerResource.Electricity, fees);
                waterFee = ServiceFeeSystem.GetFee(PlayerResource.Water, fees);
                garbageFee = ServiceFeeSystem.GetFee(PlayerResource.Garbage, fees);
            }

            // One block of rows per worker thread, so threads never add into the same number.
            var rowsPerThread = districts.Length * kFields;
            var perThread = new NativeArray<int>(JobsUtility.ThreadIndexCount * rowsPerThread, Allocator.TempJob);
            var totals = new NativeArray<int>(rowsPerThread, Allocator.TempJob);
            try
            {
                // Simulation jobs may still be writing these components; wait for them, like the game's panel does.
                em.CompleteAllTrackedJobs();
                new CountJob
                {
                    Entities = GetEntityTypeHandle(),
                    CurrentDistricts = GetComponentTypeHandle<CurrentDistrict>(true),
                    PrefabRefHandle = GetComponentTypeHandle<PrefabRef>(true),
                    Parks = GetComponentLookup<Game.Buildings.Park>(true),
                    AbandonedBuildings = GetComponentLookup<Abandoned>(true),
                    PropertyData = GetComponentLookup<BuildingPropertyData>(true),
                    HealthProblems = GetComponentLookup<HealthProblem>(true),
                    TravelPurposes = GetComponentLookup<TravelPurpose>(true),
                    Households = GetComponentLookup<Household>(true),
                    Citizens = GetComponentLookup<Citizen>(true),
                    PropertyRenters = GetComponentLookup<PropertyRenter>(true),
                    ElectricityConsumers = GetComponentLookup<ElectricityConsumer>(true),
                    WaterConsumers = GetComponentLookup<WaterConsumer>(true),
                    PrefabRefs = GetComponentLookup<PrefabRef>(true),
                    Consumption = GetComponentLookup<ConsumptionData>(true),
                    Renters = GetBufferLookup<Renter>(true),
                    HouseholdCitizens = GetBufferLookup<HouseholdCitizen>(true),
                    HouseholdAnimals = GetBufferLookup<HouseholdAnimal>(true),
                    HouseholdResources = GetBufferLookup<GameResources>(true),
                    Districts = districts,
                    HasFees = hasFees,
                    ElectricityFee = electricityFee,
                    WaterFee = waterFee,
                    GarbageFee = garbageFee,
                    RowsPerThread = rowsPerThread,
                    Totals = perThread,
                }.ScheduleParallel(m_Buildings, default(JobHandle)).Complete();

                // unchecked: int sums wrap around exactly as the game's single sum does.
                for (int i = 0; i < perThread.Length; i++)
                    totals[i % rowsPerThread] = unchecked(totals[i % rowsPerThread] + perThread[i]);

                var parameters = m_Parameters.GetSingleton<CitizenHappinessParameterData>();
                var result = new object[districts.Length];
                for (int d = 0; d < districts.Length; d++)
                    result[d] = ToJson(totals, d * kFields, parameters);
                return result;
            }
            finally
            {
                totals.Dispose();
                perThread.Dispose();
            }
        }

        /// <summary>Same field names and shape as the game's ResidentsSection writes for a district.</summary>
        private static JsonObject ToJson(NativeArray<int> t, int row, CitizenHappinessParameterData parameters)
        {
            if (t[row + kVisible] == 0) // summed over threads: 0 only if no thread counted a building
                return null;
            var households = t[row + kHouseholds];
            // Game quirk kept: average wealth = int sum (wraps around on overflow) / household count, integer division.
            var averageWealth = t[row + kWealthSum] / math.select(households, 1, households == 0);
            int ages = 0, educations = 0;
            var ageValues = new int[4];
            var educationValues = new int[5];
            for (int i = 0; i < 4; i++) { ageValues[i] = t[row + kAge + i]; ages += ageValues[i]; }
            for (int i = 0; i < 5; i++) { educationValues[i] = t[row + kEducation + i]; educations += educationValues[i]; }
            return new JsonObject
            {
                { "householdCount", households },
                { "maxHouseholds", t[row + kMaxHouseholds] },
                { "residentCount", t[row + kResidents] },
                { "petCount", t[row + kPets] },
                { "wealthKey", CitizenUIUtils.GetHouseholdWealthKey(averageWealth, parameters).ToString() },
                { "wealthData", new JsonObject
                    {
                        { "income", t[row + kIncome] },
                        { "rent", t[row + kRent] },
                        { "upkeep", t[row + kUpkeep] },
                        { "resourceCost", (uint)t[row + kResourceCost] },
                        { "fees", t[row + kFees] },
                    } },
                { "ageData", new JsonObject { { "values", ageValues }, { "total", ages } } },
                { "educationData", new JsonObject { { "values", educationValues }, { "total", educations } } },
            };
        }

        [BurstCompile]
        private struct CountJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle Entities;
            [ReadOnly] public ComponentTypeHandle<CurrentDistrict> CurrentDistricts;
            [ReadOnly] public ComponentTypeHandle<PrefabRef> PrefabRefHandle;
            [ReadOnly] public ComponentLookup<Game.Buildings.Park> Parks;
            [ReadOnly] public ComponentLookup<Abandoned> AbandonedBuildings;
            [ReadOnly] public ComponentLookup<BuildingPropertyData> PropertyData;
            [ReadOnly] public ComponentLookup<HealthProblem> HealthProblems;
            [ReadOnly] public ComponentLookup<TravelPurpose> TravelPurposes;
            [ReadOnly] public ComponentLookup<Household> Households;
            [ReadOnly] public ComponentLookup<Citizen> Citizens;
            [ReadOnly] public ComponentLookup<PropertyRenter> PropertyRenters;
            [ReadOnly] public ComponentLookup<ElectricityConsumer> ElectricityConsumers;
            [ReadOnly] public ComponentLookup<WaterConsumer> WaterConsumers;
            [ReadOnly] public ComponentLookup<PrefabRef> PrefabRefs;
            [ReadOnly] public ComponentLookup<ConsumptionData> Consumption;
            [ReadOnly] public BufferLookup<Renter> Renters;
            [ReadOnly] public BufferLookup<HouseholdCitizen> HouseholdCitizens;
            [ReadOnly] public BufferLookup<HouseholdAnimal> HouseholdAnimals;
            [ReadOnly] public BufferLookup<GameResources> HouseholdResources;
            [ReadOnly] public NativeArray<Entity> Districts;
            public bool HasFees;
            public float ElectricityFee, WaterFee, GarbageFee;
            public int RowsPerThread;
            [NativeDisableParallelForRestriction] public NativeArray<int> Totals;
#pragma warning disable 0649 // set by the job system to the worker thread's number
            [NativeSetThreadIndex] private int m_ThreadIndex;
#pragma warning restore 0649

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(Entities);
                var districts = chunk.GetNativeArray(ref CurrentDistricts);
                var prefabs = chunk.GetNativeArray(ref PrefabRefHandle);
                for (int i = 0; i < entities.Length; i++)
                {
                    var d = Districts.IndexOf(districts[i].m_District);
                    if (d >= 0)
                        CountBuilding(entities[i], prefabs[i].m_Prefab, m_ThreadIndex * RowsPerThread + d * kFields);
                }
            }

            /// <summary>Copy of ResidentsSection.TryCountHouseholds, plus the per-household totals of its OnProcess.</summary>
            private void CountBuilding(Entity building, Entity prefab, int row)
            {
                var abandoned = AbandonedBuildings.HasComponent(building);
                var hasRenters = Renters.TryGetBuffer(building, out var renters) && renters.Length > 0;
                var park = Parks.HasComponent(building);
                var residential = PropertyData.TryGetComponent(prefab, out var property) && property.m_ResidentialProperties > 0 && !abandoned;
                if (!residential && !((park || abandoned) && hasRenters))
                    return;

                Totals[row + kVisible] = 1;
                Totals[row + kMaxHouseholds] += property.m_ResidentialProperties;
                for (int r = 0; r < renters.Length; r++)
                {
                    var household = renters[r].m_Renter;
                    if (!Households.HasComponent(household) || !HouseholdCitizens.TryGetBuffer(household, out var members))
                        continue;

                    Totals[row + kHouseholds]++;
                    for (int c = 0; c < members.Length; c++)
                    {
                        var citizen = members[c].m_Citizen;
                        if (CitizenUtils.IsCorpsePickedByHearse(citizen, ref HealthProblems, ref TravelPurposes))
                            continue;
                        Totals[row + kResidents]++;
                        // Ages and education only count citizens that still exist (as in GetAgeData / GetEducationData).
                        if (Citizens.TryGetComponent(citizen, out var data))
                        {
                            Totals[row + kAge + (int)data.GetAge()]++;
                            var level = data.GetEducationLevel();
                            if (level >= 0 && level <= 4)
                                Totals[row + kEducation + level]++;
                        }
                    }
                    if (HouseholdAnimals.TryGetBuffer(household, out var pets))
                        Totals[row + kPets] += pets.Length;

                    var householdData = Households[household];
                    // CitizenUIUtils.GetAverageHouseholdWealth: households without a resources buffer add nothing.
                    if (HouseholdResources.TryGetBuffer(household, out var money))
                        Totals[row + kWealthSum] += EconomyUtils.GetHouseholdTotalWealth(householdData, money);
                    AddEconomy(householdData, household, row);
                }
            }

            /// <summary>Copy of ResidentsSection.GetHouseholdEconomyData.</summary>
            private void AddEconomy(Household household, Entity entity, int row)
            {
                int rent = 0, fees = 0;
                if (PropertyRenters.TryGetComponent(entity, out var renter))
                {
                    rent = renter.m_Rent;
                    var sharing = 1;
                    if (Renters.TryGetBuffer(renter.m_Property, out var others))
                        sharing = math.max(others.Length, 1);
                    if (HasFees)
                    {
                        if (ElectricityConsumers.TryGetComponent(renter.m_Property, out var electricity))
                            fees += (int)((float)electricity.m_FulfilledConsumption * ElectricityFee);
                        if (WaterConsumers.TryGetComponent(renter.m_Property, out var water))
                        {
                            fees += (int)((float)water.m_FulfilledFresh * WaterFee);
                            fees += (int)((float)water.m_FulfilledSewage * WaterFee);
                        }
                        if (PrefabRefs.TryGetComponent(renter.m_Property, out var propertyPrefab) && Consumption.TryGetComponent(propertyPrefab.m_Prefab, out var consumption))
                            fees += (int)(consumption.m_GarbageAccumulation * GarbageFee);
                    }
                    fees /= sharing;
                }
                Totals[row + kIncome] += household.m_Income;
                Totals[row + kRent] += rent;
                Totals[row + kUpkeep] += math.abs(household.m_MoneySpendOnBuildingLevelingLastDay);
                Totals[row + kResourceCost] = (int)((uint)Totals[row + kResourceCost] + household.m_ShoppedValuePerDay);
                Totals[row + kFees] += fees;
            }
        }
    }
}
