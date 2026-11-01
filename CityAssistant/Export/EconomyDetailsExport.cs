using System;
using System.Collections.Generic;
using Game.City;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Phase 6 holes 6, 14 and 17: the Production tab per resource, tax and taxable income per product, the
    /// Services tab's budget efficiency and fee effects, wages and benefits, commuters and outside connections.
    /// </summary>
    public static class EconomyDetailsExport
    {
        /// <summary>
        /// economy.json "production": per resource what the Production tab shows (CityProductionStatisticSystem,
        /// CountCityStoredResourceSystem, CityProductionCapacityCalculationSystem; ProductionUISystem reads the same).
        /// </summary>
        public static JsonObject Production(World world)
        {
            var stats = world.GetOrCreateSystemManaged<CityProductionStatisticSystem>();
            var storedSystem = world.GetOrCreateSystemManaged<CountCityStoredResourceSystem>();
            var capacitySystem = world.GetOrCreateSystemManaged<CityProductionCapacityCalculationSystem>();
            var consumptionProduction = stats.GetConsumptionProductions();
            var usages = stats.GetCityResourceUsages();
            var stored = storedSystem.GetCityStoredResources();
            var list = new List<object>();
            var iterator = ResourceIterator.GetIterator();
            while (iterator.Next())
            {
                var r = iterator.resource;
                int i = EconomyUtils.GetResourceIndex(r);
                if (i < 0 || i >= consumptionProduction.Length)
                    continue;
                var cp = consumptionProduction[i];
                var u = i < usages.Length ? usages[i] : default;
                int storedAmount = i < stored.Length ? stored[i] : 0;
                int capacity = capacitySystem.GetProductionCapacity(r);
                if (cp.x == 0 && cp.y == 0 && storedAmount == 0 && capacity == 0 && u.m_ImportExport == 0 && u.m_Citizens == 0)
                    continue;
                var byConsumer = new JsonObject();
                for (var c = CityProductionStatisticSystem.CityResourceUsage.Consumer.ServiceUpkeep; c < CityProductionStatisticSystem.CityResourceUsage.Consumer.Count; c++)
                {
                    int v = u[c];
                    if (v != 0)
                        byConsumer.Add(c.ToString(), v);
                }
                list.Add(new JsonObject
                {
                    { "resource", r.ToString() },
                    { "production", cp.y },
                    { "consumption", cp.x },
                    { "surplus", cp.y - cp.x },
                    { "productionCapacity", capacity },
                    { "stored", storedAmount },
                    { "usage", byConsumer },
                });
            }
            return new JsonObject
            {
                { "resources", list },
                { "note", "The Production tab's figures (game units, per month). consumption = used by companies making other goods (the production chain); usage = the tab's consumers: Citizens, Commercial / Retail, Industrial, Office, ServiceUpkeep (city buildings), Heating, LevelUp (building materials) and ImportExport (trade with outside connections; sign not yet checked in game)." },
            };
        }

        /// <summary>finance.json "taxByProduct": rate, taxable income and the tax it brings, per product (TaxSystem.GetEstimated*TaxIncome).</summary>
        public static JsonObject TaxByProduct(GameData g)
        {
            var o = new JsonObject();
            void Area(string name, StatisticType taxable, Func<Resource, int> rate, TaxAreaType area)
            {
                var list = new List<object>();
                var iterator = ResourceIterator.GetIterator();
                long total = 0;
                while (iterator.Next())
                {
                    var r = iterator.resource;
                    int income = g.Statistics.GetStatisticValue(taxable, EconomyUtils.GetResourceIndex(r));
                    if (income == 0)
                        continue;
                    int rt = rate(r);
                    long tax = (long)rt * income / 100;
                    total += tax;
                    list.Add(new JsonObject { { "product", r.ToString() }, { "taxRate", rt }, { "taxableIncome", income }, { "tax", tax } });
                }
                list.Sort((a, b) => ((long)((JsonObject)b)[3].Value).CompareTo((long)((JsonObject)a)[3].Value));
                o.Add(name, new JsonObject { { "total", total }, { "products", list } });
            }
            Area("commercial", StatisticType.CommercialTaxableIncome, r => g.Tax.GetCommercialTaxRate(r), TaxAreaType.Commercial);
            Area("industrial", StatisticType.IndustrialTaxableIncome, r => g.Tax.GetIndustrialTaxRate(r), TaxAreaType.Industrial);
            Area("office", StatisticType.OfficeTaxableIncome, r => g.Tax.GetOfficeTaxRate(r), TaxAreaType.Office);
            var residential = new List<object>();
            for (int level = 0; level < 5; level++)
            {
                int income = g.Statistics.GetStatisticValue(StatisticType.ResidentialTaxableIncome, level);
                int rt = g.Tax.GetResidentialTaxRate(level);
                residential.Add(new JsonObject { { "educationLevel", level }, { "taxRate", rt }, { "taxableIncome", income }, { "tax", (long)rt * income / 100 } });
            }
            o.Add("residential", residential);
            o.Add("note", "tax = rate x taxable income / 100, the same formula as the income lines (per month). Shows which products carry the tax income.");
            return o;
        }

        /// <summary>finance.json "serviceDetails": the Services tab per service (ServiceBudgetUISystem.WriteServiceDetails).</summary>
        public static object ServiceDetails(GameData g, World world)
        {
            var list = new List<object>();
            using (var services = g.ServicePrefabs.ToEntityArray(Allocator.Temp))
            {
                foreach (var s in services)
                {
                    var details = GameData.Safe(g.Prefabs.GetPrefabName(s), () => InfoviewReader.CallWriter<Game.UI.InGame.ServiceBudgetUISystem>(world, "WriteServiceDetails", s));
                    if (details != null)
                        list.Add(details);
                }
            }
            return list;
        }

        /// <summary>finance.json "wagesAndBenefits": the game's economy settings for what citizens are paid.</summary>
        public static JsonObject Wages(GameData g)
        {
            var q = g.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            var p = q.GetSingleton<EconomyParameterData>();
            return new JsonObject
            {
                { "wageByJobLevel", new[] { p.m_Wage0, p.m_Wage1, p.m_Wage2, p.m_Wage3, p.m_Wage4 } },
                { "commuterWageMultiplier", p.m_CommuterWageMultiplier },
                { "unemploymentBenefit", p.m_UnemploymentBenefit },
                { "pension", p.m_Pension },
                { "familyAllowance", p.m_FamilyAllowance },
                { "cityServiceWageAdjustment", p.m_CityServiceWageAdjustment },
                { "companyBankruptcyLimit", p.m_CompanyBankruptcyLimit },
                { "note", "The game's economy settings (EconomyParameterData), in its own money units; job levels 0-4 = uneducated .. highly educated. Paid out by the simulation (PayWageSystem), not visible in a panel." },
            };
        }

        /// <summary>economy.json "commuters" and "outsideConnections".</summary>
        public static JsonObject Commuters(GameData g)
        {
            var em = g.EntityManager;
            var connections = new SortedDictionary<string, int>();
            var q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Game.Objects.OutsideConnection>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
            using (var prefabs = q.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            {
                foreach (var p in prefabs)
                {
                    string type = em.HasComponent<OutsideConnectionData>(p.m_Prefab) ? em.GetComponentData<OutsideConnectionData>(p.m_Prefab).m_Type.ToString() : g.Prefabs.GetPrefabName(p.m_Prefab);
                    connections.TryGetValue(type, out var n);
                    connections[type] = n + 1;
                }
            }
            var c = new JsonObject();
            foreach (var kv in connections)
                c.Add(kv.Key, kv.Value);
            return new JsonObject
            {
                { "commuterHouseholds", g.Households.CommuterHouseholdCount },
                { "outsideConnectionsByType", c },
                { "note", "Commuter households live outside and work in the city; the game adds them when jobs needing more education than local job-seekers have stay free (CommuterSpawnSystem). Their wages leave the city." },
            };
        }
    }
}
