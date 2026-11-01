using System;
using Game.Areas;
using Game.City;
using Game.Buildings;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// One place that holds references to the game systems we read from.
    /// Exporters ask this class for data; they never look up systems themselves.
    /// </summary>
    public class GameData
    {
        public readonly EntityManager EntityManager;
        public readonly CitySystem City;
        public readonly CityConfigurationSystem Config;
        public readonly CityServiceBudgetSystem Budget;
        public readonly CityStatisticsSystem Statistics;
        public readonly TaxSystem Tax;
        public readonly TimeSystem Time;
        public readonly LoanSystem Loans;
        public readonly MilestoneSystem Milestones;
        public readonly PrefabSystem Prefabs;
        public readonly ResidentialDemandSystem ResidentialDemand;
        public readonly CommercialDemandSystem CommercialDemand;
        public readonly IndustrialDemandSystem IndustrialDemand;
        public readonly ElectricityStatisticsSystem Electricity;
        public readonly WaterStatisticsSystem Water;
        public readonly CountHouseholdDataSystem Households;
        public readonly SimulationSystem Simulation;

        /// <summary>All city service prefabs (Education, Healthcare, ...), used for budgets.</summary>
        public readonly EntityQuery ServicePrefabs;
        /// <summary>The districts the player has drawn.</summary>
        public readonly EntityQuery Districts;
        /// <summary>Road segments the player built (sub-roads inside buildings, which have an Owner, are left out).</summary>
        public readonly EntityQuery Roads;
        /// <summary>Power lines and roads with cables: map edges that have a node in the electricity flow graph.</summary>
        public readonly EntityQuery PowerLines;
        /// <summary>Water and sewage pipes (and roads with pipes): map edges in the water flow graph.</summary>
        public readonly EntityQuery Pipes;
        /// <summary>Every building with a position (for landmarks).</summary>
        public readonly EntityQuery Buildings;
        /// <summary>Residential buildings.</summary>
        public readonly EntityQuery Homes;
        /// <summary>Zoning blocks along roads (Game.Zones.Block with its cells).</summary>
        public readonly EntityQuery ZoneBlocks;
        /// <summary>The map tiles (owned ones lack the Native tag).</summary>
        public readonly EntityQuery MapTiles;
        /// <summary>Warning icons (notifications) over buildings, roads and vehicles.</summary>
        public readonly EntityQuery Icons;
        /// <summary>Companies renting a building (shops, industry, offices, warehouses, extractors).</summary>
        public readonly EntityQuery Companies;
        /// <summary>Households on their way out of the city, with the game's reason.</summary>
        public readonly EntityQuery MovingAwayHouseholds;
        /// <summary>Cars and trucks on the move (not parked), with their owner.</summary>
        public readonly EntityQuery MovingCars;
        /// <summary>Accident and crime sites.</summary>
        public readonly EntityQuery AccidentSites;

        public GameData(World world)
        {
            EntityManager = world.EntityManager;
            City = world.GetOrCreateSystemManaged<CitySystem>();
            Config = world.GetOrCreateSystemManaged<CityConfigurationSystem>();
            Budget = world.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            Statistics = world.GetOrCreateSystemManaged<CityStatisticsSystem>();
            Tax = world.GetOrCreateSystemManaged<TaxSystem>();
            Time = world.GetOrCreateSystemManaged<TimeSystem>();
            Loans = world.GetOrCreateSystemManaged<LoanSystem>();
            Milestones = world.GetOrCreateSystemManaged<MilestoneSystem>();
            Prefabs = world.GetOrCreateSystemManaged<PrefabSystem>();
            ResidentialDemand = world.GetOrCreateSystemManaged<ResidentialDemandSystem>();
            CommercialDemand = world.GetOrCreateSystemManaged<CommercialDemandSystem>();
            IndustrialDemand = world.GetOrCreateSystemManaged<IndustrialDemandSystem>();
            Electricity = world.GetOrCreateSystemManaged<ElectricityStatisticsSystem>();
            Water = world.GetOrCreateSystemManaged<WaterStatisticsSystem>();
            Households = world.GetOrCreateSystemManaged<CountHouseholdDataSystem>();
            Simulation = world.GetOrCreateSystemManaged<SimulationSystem>();

            ServicePrefabs = EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ServiceData>(),
                ComponentType.ReadOnly<PrefabData>());
            Districts = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<District>(), ComponentType.ReadOnly<Area>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            Roads = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<Road>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            PowerLines = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Simulation.ElectricityNodeConnection>() },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            Pipes = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Edge>(), ComponentType.ReadOnly<Curve>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Simulation.WaterPipeNodeConnection>() },
                None = new[] { ComponentType.ReadOnly<Owner>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            Buildings = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            Homes = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<ResidentialProperty>(), ComponentType.ReadOnly<Game.Objects.Transform>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            ZoneBlocks = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Zones.Block>(), ComponentType.ReadOnly<Game.Zones.Cell>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            MapTiles = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<MapTile>(), ComponentType.ReadOnly<Area>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            Companies = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Companies.CompanyData>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<PropertyRenter>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            MovingAwayHouseholds = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Citizens.Household>(), ComponentType.ReadOnly<Game.Agents.MovingAway>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            MovingCars = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Vehicles.Car>(), ComponentType.ReadOnly<Owner>() },
                None = new[] { ComponentType.ReadOnly<Game.Vehicles.ParkedCar>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            AccidentSites = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Events.AccidentSite>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            Icons = EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Notifications.Icon>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        public Entity CityEntity => City.City;

        public string CityName
        {
            get
            {
                var name = Config.cityName;
                return string.IsNullOrWhiteSpace(name) ? "Unnamed City" : name.Trim();
            }
        }

        // The calendar the player sees advances one month per in-game day (262144 ticks).
        // TimeSystem.GetCurrentDateTime() counts those days as days of January, so it is NOT the calendar;
        // the month is derived from the time of year instead. Test on Pedoburg: GetCurrentDateTime gave
        // 2025-01-06 while the game showed June 2025. Confirmed: normalizedDate 0.4785 = June, 0.5 = July.
        // Because one day is one month, the time of year carries both the month and the time of day:
        // normalizedDate 0.4785 -> 5.742 months -> June, 0.742 of the day = 17:48 (matched the game's clock).
        // Month and hour are both derived from this one value so they can never disagree at midnight.
        public int GameYear => Time.year;

        private double MonthsIntoYear => Time.normalizedDate * 12.0;

        public int GameMonth => Math.Min(11, (int)Math.Floor(MonthsIntoYear)) + 1;

        /// <summary>Time of day as a fraction 0..1 (equals the fraction of the month that has passed).</summary>
        public double DayFraction
        {
            get
            {
                var m = MonthsIntoYear;
                return Math.Max(0.0, Math.Min(0.999999, m - Math.Floor(m)));
            }
        }

        /// <summary>History snapshots per month: every 6 in-game hours, at 00:00, 06:00, 12:00 and 18:00.</summary>
        public const int SnapshotsPerMonth = 4;

        public int SnapshotIndex => (int)(DayFraction * SnapshotsPerMonth);

        public int SnapshotHour => SnapshotIndex * (24 / SnapshotsPerMonth);

        /// <summary>"06h": the snapshot slot the current time falls into.</summary>
        public string SnapshotName => $"{SnapshotHour:D2}h";

        /// <summary>Increases with every snapshot slot; used to detect when a new slot starts.</summary>
        public long SnapshotKey => ((long)GameYear * 12 + (GameMonth - 1)) * SnapshotsPerMonth + SnapshotIndex;

        /// <summary>In-game clock, "17:48".</summary>
        public string GameClock => TimeSpan.FromMinutes(Math.Floor(DayFraction * 24 * 60)).ToString(@"hh\:mm");

        /// <summary>"2025-06": used in file names and to detect month changes.</summary>
        public string GameMonthKey => $"{GameYear:D4}-{GameMonth:D2}";

        /// <summary>Continuous game time in months; one month = 24 game hours.</summary>
        public double TotalMonths => GameYear * 12.0 + MonthsIntoYear;

        /// <summary>Set by the export system when a save finishes loading.</summary>
        public double? LoadedAtMonths;

        // Right after loading, the game re-counts citizens and companies. A snapshot 12 game minutes after
        // loading (Pedoburg, 2025-06 18h) was still far off; one 6 hours later was settled. 3 h is a guess.
        public const double kWarmUpHours = 3.0;

        public double? GameHoursSinceLoad => LoadedAtMonths.HasValue ? (TotalMonths - LoadedAtMonths.Value) * 24.0 : (double?)null;

        public int Stat(StatisticType type, int parameter = 0) => Statistics.GetStatisticValue(type, parameter);

        /// <summary>
        /// Sum of all expense lines, as a positive number. The game's own GetTotalExpenses() is negative
        /// while each GetExpense() line is positive; we export expenses as positive everywhere.
        /// </summary>
        public int TotalExpenses()
        {
            var total = 0;
            foreach (ExpenseSource source in Enum.GetValues(typeof(ExpenseSource)))
                if (source != ExpenseSource.Count)
                    total += Budget.GetExpense(source);
            return total;
        }

        public bool TryGetCityComponent<T>(out T value) where T : unmanaged, IComponentData
        {
            var city = CityEntity;
            if (city != Entity.Null && EntityManager.HasComponent<T>(city))
            {
                value = EntityManager.GetComponentData<T>(city);
                return true;
            }
            value = default;
            return false;
        }

        /// <summary>
        /// Runs one export section. If the game throws (e.g. after a patch renamed something),
        /// the section becomes {"error": ...} and the rest of the file still exports.
        /// </summary>
        public static object Safe(string section, Func<object> read)
        {
            try
            {
                return read();
            }
            catch (Exception e)
            {
                Mod.log.Warn($"Export section '{section}' failed: {e}");
                return new JsonObject { { "error", e.GetType().Name + ": " + e.Message } };
            }
        }

        public JsonObject Header(string kind, string trigger)
        {
            return new JsonObject
            {
                { "schema", $"cityassistant.{kind}/1" },
                { "city", CityName },
                { "gameMonth", GameMonthKey },
                { "snapshot", SnapshotName },
                { "gameClock", GameClock },
                { "trigger", trigger },
                { "gameHoursSinceLoad", GameHoursSinceLoad.HasValue ? (object)Math.Round(GameHoursSinceLoad.Value, 1) : null },
                { "warmingUp", trigger == "load" || (GameHoursSinceLoad ?? 0) < kWarmUpHours },
                { "exportedAt", DateTime.Now },
                { "modVersion", typeof(Mod).Assembly.GetName().Version.ToString() },
                { "conventions", "Expenses are positive numbers; balance = totalIncome - totalExpenses." },
            };
        }
    }
}
