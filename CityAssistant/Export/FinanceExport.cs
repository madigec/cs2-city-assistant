using System;
using System.Collections.Generic;
using Game.City;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;

namespace CityAssistant.Export
{
    /// <summary>
    /// Layer 2, Finance domain: every income and expense line, loan, tax rates, fees and service budgets.
    /// </summary>
    public static class FinanceExport
    {
        private static readonly string[] EducationLevels =
            { "uneducated", "poorlyEducated", "educated", "wellEducated", "highlyEducated" };

        public static JsonObject Build(GameData g, Unity.Entities.World world, string trigger)
        {
            var o = g.Header("finance", trigger);

            o.Add("summary", GameData.Safe("summary", () =>
            {
                g.TryGetCityComponent<PlayerMoney>(out var money);
                return new JsonObject
                {
                    { "cash", money.money },
                    { "unlimitedMoney", money.m_Unlimited },
                    { "totalIncome", g.Budget.GetTotalIncome() },
                    { "totalTaxIncome", g.Budget.GetTotalTaxIncome() },
                    { "totalExpenses", g.TotalExpenses() },
                    { "balance", g.Budget.GetBalance() },
                    { "moneyDelta", g.Budget.GetMoneyDelta() },
                };
            }));

            o.Add("income", GameData.Safe("income", () =>
            {
                var lines = new JsonObject();
                foreach (IncomeSource source in Enum.GetValues(typeof(IncomeSource)))
                    if (source != IncomeSource.Count)
                        lines.Add(source.ToString(), g.Budget.GetIncome(source));
                return lines;
            }));

            o.Add("expenses", GameData.Safe("expenses", () =>
            {
                var lines = new JsonObject();
                foreach (ExpenseSource source in Enum.GetValues(typeof(ExpenseSource)))
                    if (source != ExpenseSource.Count)
                        lines.Add(source.ToString(), g.Budget.GetExpense(source));
                return lines;
            }));

            o.Add("loan", GameData.Safe("loan", () =>
            {
                var loan = g.Loans.CurrentLoan;
                return new JsonObject
                {
                    { "amount", loan.m_Amount },
                    { "dailyInterestRate", loan.m_DailyInterestRate },
                    { "dailyPayment", loan.m_DailyPayment },
                    { "creditworthiness", g.Loans.Creditworthiness },
                };
            }));

            o.Add("taxes", GameData.Safe("taxes", () =>
            {
                // GetTaxRateRange() is NOT the allowed range (it gave 10-10 while taxes were adjustable;
                // probably the spread of sub-rates). The allowed limits come from TaxParameterData.
                var limits = g.Tax.GetTaxParameterData();
                var zones = new JsonObject
                {
                    { "Residential", g.Tax.GetTaxRate(TaxAreaType.Residential) },
                    { "Commercial", g.Tax.GetTaxRate(TaxAreaType.Commercial) },
                    { "Industrial", g.Tax.GetTaxRate(TaxAreaType.Industrial) },
                    { "Office", g.Tax.GetTaxRate(TaxAreaType.Office) },
                };
                var allowed = new JsonObject
                {
                    { "total", new[] { limits.m_TotalTaxLimits.x, limits.m_TotalTaxLimits.y } },
                    { "residential", new[] { limits.m_ResidentialTaxLimits.x, limits.m_ResidentialTaxLimits.y } },
                    { "commercial", new[] { limits.m_CommercialTaxLimits.x, limits.m_CommercialTaxLimits.y } },
                    { "industrial", new[] { limits.m_IndustrialTaxLimits.x, limits.m_IndustrialTaxLimits.y } },
                    { "office", new[] { limits.m_OfficeTaxLimits.x, limits.m_OfficeTaxLimits.y } },
                    { "residentialEducation", new[] { limits.m_JobLevelTaxLimits.x, limits.m_JobLevelTaxLimits.y } },
                    { "perResource", new[] { limits.m_ResourceTaxLimits.x, limits.m_ResourceTaxLimits.y } },
                };

                var residentialByEducation = new JsonObject();
                for (int level = 0; level < EducationLevels.Length; level++)
                    residentialByEducation.Add(EducationLevels[level], g.Tax.GetResidentialTaxRate(level));

                return new JsonObject
                {
                    { "mainRate", g.Tax.TaxRate },
                    { "byZone", zones },
                    { "allowedLimitsMinMax", allowed },
                    { "residentialByEducation", residentialByEducation },
                    // The Taxation menu's product rows: the rate each taxed product pays (zone rate + its offset).
                    { "byResource", new JsonObject
                        {
                            { "Commercial", Systems.CityActions.ResourceRates(g, world, TaxAreaType.Commercial) },
                            { "Industrial", Systems.CityActions.ResourceRates(g, world, TaxAreaType.Industrial) },
                            { "Office", Systems.CityActions.ResourceRates(g, world, TaxAreaType.Office) },
                        }
                    },
                };
            }));

            o.Add("serviceFees", GameData.Safe("serviceFees", () =>
            {
                var fees = new JsonObject();
                var city = g.CityEntity;
                if (g.EntityManager.HasBuffer<ServiceFee>(city))
                {
                    var buffer = g.EntityManager.GetBuffer<ServiceFee>(city, true);
                    for (int i = 0; i < buffer.Length; i++)
                        fees.Add(buffer[i].m_Resource.ToString(), buffer[i].m_Fee);
                }
                return fees;
            }));

            // City-wide policies as the Policies panel lists them (district and building policies are not here).
            o.Add("cityPolicies", GameData.Safe("cityPolicies", () => Systems.CityActions.CityPoliciesJson(g, world)));

            o.Add("serviceBudgets", GameData.Safe("serviceBudgets", () =>
            {
                var services = new List<JsonObject>();
                using (var entities = g.ServicePrefabs.ToEntityArray(Allocator.Temp))
                {
                    foreach (var prefab in entities)
                    {
                        var data = g.EntityManager.GetComponentData<ServiceData>(prefab);
                        g.Budget.GetEstimatedServiceBudget(prefab, out var upkeep);
                        services.Add(new JsonObject
                        {
                            { "service", data.m_Service },
                            { "name", g.Prefabs.GetPrefabName(prefab) },
                            { "budgetAdjustable", data.m_BudgetAdjustable },
                            { "budgetPercent", g.Budget.GetServiceBudget(prefab) },
                            { "estimatedUpkeep", upkeep },
                        });
                    }
                }
                services.Sort((a, b) => string.CompareOrdinal(a[0].Value.ToString(), b[0].Value.ToString()));
                return services;
            }));

            o.Add("taxByProduct", GameData.Safe("taxByProduct", () => EconomyDetailsExport.TaxByProduct(g)));
            o.Add("serviceDetails", GameData.Safe("serviceDetails", () => EconomyDetailsExport.ServiceDetails(g, world)));
            o.Add("wagesAndBenefits", GameData.Safe("wagesAndBenefits", () => EconomyDetailsExport.Wages(g)));
            o.Add("policyEffects", GameData.Safe("policyEffects", () => PoliciesExport.Build(g, world)));
            o.Add("serviceSales", GameData.Safe("serviceSales", () => LeftoverExports.ServiceSales(g, world)));
            return o;
        }
    }
}
