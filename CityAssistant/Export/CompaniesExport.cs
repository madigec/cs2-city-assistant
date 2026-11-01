using System;
using System.Collections.Generic;
using Game.Agents;
using Game.Buildings;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Economy addition (Phase 6 hole 5): company health per product. Every company that rents a building,
    /// grouped by what it makes and its kind (commercial, industrial, office, warehouse, extractor), with the
    /// company panel's profit words, staffing, companies on their way out, the product's tax rate and the
    /// game's chance that such a company leaves.
    /// </summary>
    public static class CompaniesExport
    {
        // CompanyUIUtils.GetProfitabilityKey on profit = profitability x 2 - 255 (ProfitabilitySection.OnProcess).
        private static readonly string[] kProfitWords = { "Bankrupt", "LosingMoney", "BreakingEven", "GettingBy", "Profitable" };

        private static int ProfitWord(byte profitability)
        {
            int profit = profitability * 2 - 255;
            if (profit > 128) return 4;
            if (profit > 32) return 3;
            if (profit > -64) return 2;
            if (profit > -182) return 1;
            return 0;
        }

        private class Group
        {
            public string Product, Kind;
            public int Companies, Workers, MaxWorkers, MovingAway, ChanceSamples, MissingUneducatedIcon, MissingEducatedIcon;
            public long ProfitabilitySum, StatProfitSum;
            public int StatSamples;
            public double ChanceSum, MonthlySum;
            public readonly int[] Words = new int[5];
            public int TaxRate = int.MinValue;
        }

        public static JsonObject Build(GameData g)
        {
            var em = g.EntityManager;
            var groups = new Dictionary<string, Group>();
            var economyQuery = em.CreateEntityQuery(ComponentType.ReadOnly<EconomyParameterData>());
            bool hasEconomy = economyQuery.CalculateEntityCount() == 1;
            var economy = hasEconomy ? economyQuery.GetSingleton<EconomyParameterData>() : default;
            using (var entities = g.Companies.ToEntityArray(Allocator.Temp))
            using (var prefabs = g.Companies.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            using (var renters = g.Companies.ToComponentDataArray<PropertyRenter>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    var prefab = prefabs[i].m_Prefab;
                    var property = renters[i].m_Property;
                    if (property == Entity.Null || !em.HasComponent<IndustrialProcessData>(prefab))
                        continue;
                    var process = em.GetComponentData<IndustrialProcessData>(prefab);
                    var product = process.m_Output.m_Resource;
                    bool commercial = em.HasComponent<ServiceAvailable>(e);
                    bool office = em.HasComponent<OfficeProperty>(property);
                    string kind = em.HasComponent<Game.Companies.StorageCompany>(e) ? "warehouse"
                        : em.HasComponent<Game.Companies.ExtractorCompany>(e) ? "extractor"
                        : commercial ? "commercial" : office ? "office" : "industrial";
                    string key = kind + ":" + product;
                    if (!groups.TryGetValue(key, out var grp))
                        groups.Add(key, grp = new Group { Product = product.ToString(), Kind = kind });
                    grp.Companies++;
                    if (em.HasComponent<Profitability>(e))
                    {
                        byte p = em.GetComponentData<Profitability>(e).m_Profitability;
                        grp.ProfitabilitySum += p;
                        grp.Words[ProfitWord(p)]++;
                    }
                    // The Companies info view's gauge: CompanyUtils.GetCompanyProfitability of the company's profit
                    // statistic, 0-255 with 127 = breaking even (CompanyInfoviewUISystem).
                    if (hasEconomy && em.HasComponent<CompanyStatisticData>(e))
                    {
                        grp.StatProfitSum += Game.Simulation.CompanyUtils.GetCompanyProfitability(em.GetComponentData<CompanyStatisticData>(e).m_Profit, economy);
                        grp.StatSamples++;
                    }
                    if (em.HasComponent<MovingAway>(e))
                        grp.MovingAway++;
                    WorkProvider work = default;
                    bool hasWork = em.HasComponent<WorkProvider>(e);
                    if (hasWork)
                    {
                        work = em.GetComponentData<WorkProvider>(e);
                        grp.MaxWorkers += work.m_MaxWorkers;
                        if (em.HasBuffer<Employee>(e))
                            grp.Workers += em.GetBuffer<Employee>(e, true).Length;
                        if (work.m_UneducatedNotificationEntity != Entity.Null) grp.MissingUneducatedIcon++;
                        if (work.m_EducatedNotificationEntity != Entity.Null) grp.MissingEducatedIcon++;
                    }
                    if (kind == "warehouse")
                        continue;
                    int tax = commercial ? g.Tax.GetCommercialTaxRate(product)
                        : office ? g.Tax.GetOfficeTaxRate(product) : g.Tax.GetIndustrialTaxRate(product);
                    grp.TaxRate = tax;
                    // CompanyMoveAwaySystem rolls CompanyUtils.GetCompanyMoveAwayChance 16 times per game day for
                    // processing companies (not extractors): (tax - 10) x 5 / 2 in whole numbers, +5 with the
                    // "not enough uneducated workers" icon, +20 with the educated one.
                    if (kind == "extractor" || !em.HasComponent<Game.Companies.ProcessingCompany>(e) || !hasWork)
                        continue;
                    int chance = (tax - 10) * 5 / 2;
                    if (work.m_UneducatedNotificationEntity != Entity.Null) chance += 5;
                    if (work.m_EducatedNotificationEntity != Entity.Null) chance += 20;
                    double perCheck = Math.Max(0, Math.Min(100, chance)) / 100.0;
                    grp.ChanceSum += perCheck;
                    grp.MonthlySum += 1 - Math.Pow(1 - perCheck, ChecksPerDay);
                    grp.ChanceSamples++;
                }
            }

            var list = new List<Group>(groups.Values);
            list.Sort((a, b) => b.Companies != a.Companies ? b.Companies.CompareTo(a.Companies) : string.CompareOrdinal(a.Kind + a.Product, b.Kind + b.Product));
            var rows = new List<object>();
            int total = 0, leaving = 0;
            foreach (var grp in list)
            {
                total += grp.Companies;
                leaving += grp.MovingAway;
                var words = new JsonObject();
                for (int w = 4; w >= 0; w--)
                    if (grp.Words[w] > 0)
                        words.Add(kProfitWords[w], grp.Words[w]);
                rows.Add(new JsonObject
                {
                    { "product", grp.Product },
                    { "kind", grp.Kind },
                    { "companies", grp.Companies },
                    { "profitPercent", grp.StatSamples > 0 ? (object)Math.Round(100.0 * grp.StatProfitSum / grp.StatSamples / 255, 1) : null },
                    { "panelWords", words },
                    { "workers", grp.Workers },
                    { "maxWorkers", grp.MaxWorkers },
                    { "withLackOfLaborIcon", grp.MissingUneducatedIcon },
                    { "movingAway", grp.MovingAway },
                    { "taxRate", grp.TaxRate == int.MinValue ? null : (object)grp.TaxRate },
                    { "leaveChancePerCheckPercent", grp.ChanceSamples > 0 ? (object)Math.Round(100 * grp.ChanceSum / grp.ChanceSamples, 1) : null },
                    { "leaveChancePerGameDayPercent", grp.ChanceSamples > 0 ? (object)Math.Round(100 * grp.MonthlySum / grp.ChanceSamples, 1) : null },
                });
            }
            return new JsonObject
            {
                { "companies", total },
                { "movingAway", leaving },
                { "byProduct", rows },
                { "note", "profitPercent = the Companies info view's profitability scale (50 % = breaking even, above = making money), averaged per product. panelWords = what the company info panel says per company (Profitable, Getting by, Breaking even, Losing money, Bankrupt); it reflects only the change in the company's worth since the last check, so it swings a lot. Leave chance: the game rolls it 16 times per game day (one day = one month on the calendar) for shops, industry and offices, not extractors or warehouses; it is 0 at a product tax of 10 % or less unless worker-shortage icons add to it. Companies also leave after staying below the bankruptcy limit for a quarter of a day." },
            };
        }

        /// <summary>CompanyMoveAwaySystem.kUpdatesPerDay: each company is checked this often per game day.</summary>
        private const int ChecksPerDay = 16;
    }
}
