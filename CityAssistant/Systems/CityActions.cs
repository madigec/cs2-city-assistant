using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using CityAssistant.Export;
using Game.City;
using Game.Economy;
using Game.Policies;
using Game.Prefabs;
using Game.Simulation;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Systems
{
    /// <summary>
    /// Phase 5, co-pilot: the short list of numeric settings Claude may change, each one only after the player
    /// approved it in chat. Every action calls the same public game function the game's own menu calls
    /// (decompiled 1.6.1f1: TaxationUISystem, ServiceBudgetUISystem, LoanUISystem), and is refused outside the
    /// limits that menu allows. Nothing else can be changed: no building, zoning, roads, district or building policies.
    ///
    /// action=tax.main         to=&lt;percent&gt;                       TaxSystem.TaxRate
    /// action=tax.zone         target=Residential|Commercial|Industrial|Office  TaxSystem.SetTaxRate
    /// action=tax.education    target=uneducated|poorlyEducated|educated|wellEducated|highlyEducated
    ///                                                           TaxSystem.SetResidentialTaxRate
    /// action=budget           target=&lt;service prefab name, as finance.json serviceBudgets.name&gt;  50-150 %
    ///                                                           CityServiceBudgetSystem.SetServiceBudget
    /// action=fee              target=&lt;PlayerResource, as finance.json serviceFees&gt;  0..max
    ///                                                           ServiceFeeSystem.SetFee
    /// action=loan             to=&lt;total loan amount&gt;           LoanSystem.ChangeLoan (via RequestLoanOffer)
    /// action=tax.resource     target=Commercial|Industrial|Office:&lt;Resource&gt;, e.g. Commercial:Food
    ///                                                           TaxSystem.Set{Commercial,Industrial,Office}TaxRate
    /// action=policy           target=&lt;city policy prefab name&gt;  to=1 on / 0 off  PoliciesUISystem.SetCityPolicy
    /// action=policy.slider    target=&lt;city policy prefab name&gt;  to=&lt;slider value&gt; (policy must be on)
    ///
    /// dryRun=1 only checks and reports the current value and limits. A real change also needs from=&lt;current
    /// value&gt;: if the game's value moved since the proposal, the change is refused (stale proposal).
    /// </summary>
    public static class CityActions
    {
        // ServiceBudget slider range in the game's UI (Content/Game/UI/index.js: min:50, max:150).
        private const int kBudgetMin = 50, kBudgetMax = 150;

        private static readonly string[] EducationLevels =
            { "uneducated", "poorlyEducated", "educated", "wellEducated", "highlyEducated" };

        public static readonly string[] Actions = { "tax.main", "tax.zone", "tax.education", "tax.resource", "budget", "fee", "loan", "policy", "policy.slider" };

        private class Setting
        {
            public double From;
            public double Min, Max;
            public string Unit;
            public bool Fractions;
            public bool Delayed; // the game applies it on its next update (loan queue, policy event)
            public TaxAreaType TaxArea;
            public Action<double> Apply;
            public Func<double> Read;
        }

        public static JsonObject Run(GameData g, World world, NameValueCollection q, out int status)
        {
            var action = q["action"] ?? "";
            var target = q["target"] ?? "";
            var dryRun = q["dryRun"] == "1";
            var result = new JsonObject
            {
                { "schema", "cityassistant.action/1" },
                { "city", g.CityName },
                { "gameMonth", g.GameMonthKey },
                { "gameClock", g.GameClock },
                { "action", action },
                { "target", target },
                { "dryRun", dryRun },
            };

            Setting s;
            try
            {
                s = Find(g, world, action, target);
            }
            catch (ArgumentException e)
            {
                status = 400;
                return result.Add("error", e.Message);
            }

            result.Add("unit", s.Unit);
            result.Add("current", s.From);
            result.Add("allowedMinMax", new[] { s.Min, s.Max });
            var taxesBefore = action.StartsWith("tax.") ? TaxSnapshot(g, world, s.TaxArea) : null;

            if (dryRun && q["to"] == null)
            {
                status = 200;
                return result;
            }
            if (!TryNumber(q["to"], out var to))
            {
                status = 400;
                return result.Add("error", "Give the new value as to=<number>.");
            }
            result.Add("to", to);
            if (to < s.Min || to > s.Max)
            {
                status = 400;
                return result.Add("error", $"{to} is outside what the game allows ({s.Min} to {s.Max}).");
            }
            if (!s.Fractions && Math.Abs(to - Math.Round(to)) > 1e-9)
            {
                status = 400;
                return result.Add("error", "Use a whole number.");
            }
            if (dryRun)
            {
                status = 200;
                return result.Add("ok", true);
            }

            if (!TryNumber(q["from"], out var from))
            {
                status = 400;
                return result.Add("error", "A change needs from=<the value the player approved changing from>.");
            }
            if (Math.Abs(from - s.From) > 1e-3)
            {
                status = 409;
                return result.Add("error", $"The game's value is {s.From} now, not {from}: the proposal is out of date. Propose again.");
            }

            s.Apply(to);
            var after = s.Read();
            result.Add("from", s.From);
            result.Add("applied", true);
            result.Add("valueAfter", after);
            if (Math.Abs(after - to) > 1e-3)
                result.Add("note", s.Delayed
                    ? "The game applies this change on its next update; check again in a moment."
                    : "The game stored a different value than asked (its own limits); valueAfter is what it holds now.");
            if (taxesBefore != null)
            {
                result.Add("taxesBefore", taxesBefore);
                result.Add("taxesAfter", TaxSnapshot(g, world, s.TaxArea));
            }
            Log(g, result);
            status = 200;
            return result;
        }

        private static Setting Find(GameData g, World world, string action, string target)
        {
            var limits = g.Tax.GetTaxParameterData();
            switch (action)
            {
                case "tax.main":
                    return new Setting
                    {
                        From = g.Tax.TaxRate, Min = limits.m_TotalTaxLimits.x, Max = limits.m_TotalTaxLimits.y, Unit = "percent",
                        Apply = v => { g.Tax.Readers.Complete(); g.Tax.TaxRate = (int)Math.Round(v); },
                        Read = () => g.Tax.TaxRate,
                    };

                case "tax.zone":
                {
                    if (!Enum.TryParse<TaxAreaType>(target, out var area) || area == TaxAreaType.None || !Enum.IsDefined(typeof(TaxAreaType), area))
                        throw new ArgumentException("target must be Residential, Commercial, Industrial or Office.");
                    var range = area == TaxAreaType.Residential ? limits.m_ResidentialTaxLimits
                        : area == TaxAreaType.Commercial ? limits.m_CommercialTaxLimits
                        : area == TaxAreaType.Industrial ? limits.m_IndustrialTaxLimits
                        : limits.m_OfficeTaxLimits;
                    return new Setting
                    {
                        From = g.Tax.GetTaxRate(area), Min = range.x, Max = range.y, Unit = "percent", TaxArea = area,
                        Apply = v => { g.Tax.Readers.Complete(); g.Tax.SetTaxRate(area, (int)Math.Round(v)); },
                        Read = () => g.Tax.GetTaxRate(area),
                    };
                }

                case "tax.education":
                {
                    var level = Array.IndexOf(EducationLevels, target);
                    if (level < 0)
                        throw new ArgumentException("target must be one of: " + string.Join(", ", EducationLevels) + ".");
                    return new Setting
                    {
                        From = g.Tax.GetResidentialTaxRate(level), Min = limits.m_JobLevelTaxLimits.x, Max = limits.m_JobLevelTaxLimits.y, Unit = "percent",
                        Apply = v => { g.Tax.Readers.Complete(); g.Tax.SetResidentialTaxRate(level, (int)Math.Round(v)); },
                        Read = () => g.Tax.GetResidentialTaxRate(level),
                    };
                }

                case "budget":
                {
                    var prefab = FindService(g, target, out var adjustable);
                    if (prefab == Entity.Null)
                        throw new ArgumentException($"No service named '{target}'. Use a name from finance.json serviceBudgets.");
                    if (!adjustable)
                        throw new ArgumentException($"The game does not let the player change the {target} budget.");
                    return new Setting
                    {
                        From = g.Budget.GetServiceBudget(prefab), Min = kBudgetMin, Max = kBudgetMax, Unit = "percent of normal budget",
                        Apply = v => g.Budget.SetServiceBudget(prefab, (int)Math.Round(v)),
                        Read = () => g.Budget.GetServiceBudget(prefab),
                    };
                }

                case "fee":
                {
                    if (!Enum.TryParse<PlayerResource>(target, out var resource) || resource == PlayerResource.Parking)
                        throw new ArgumentException("target must be a fee name from finance.json serviceFees (parking fees are set per road, not here).");
                    var city = g.CityEntity;
                    if (!g.EntityManager.HasBuffer<ServiceFee>(city))
                        throw new ArgumentException("This city has no service fees.");
                    if (!ServiceFeeSystem.TryGetFee(resource, g.EntityManager.GetBuffer<ServiceFee>(city, true), out var fee))
                        throw new ArgumentException($"The city has no {target} fee.");
                    var parameters = Singleton<ServiceFeeParameterData>(world).GetFeeParameters(resource);
                    if (!parameters.m_Adjustable)
                        throw new ArgumentException($"The game does not let the player change the {target} fee.");
                    return new Setting
                    {
                        From = Math.Round(fee, 4), Min = 0, Max = parameters.m_Max, Unit = "money per unit", Fractions = true,
                        Apply = v => ServiceFeeSystem.SetFee(resource, g.EntityManager.GetBuffer<ServiceFee>(city), (float)v),
                        Read = () => ServiceFeeSystem.TryGetFee(resource, g.EntityManager.GetBuffer<ServiceFee>(city, true), out var now) ? Math.Round(now, 4) : double.NaN,
                    };
                }

                case "loan":
                {
                    // LoanSystem.ClampLoanAmount: you can only pay back what you have in cash, borrow up to creditworthiness.
                    var current = g.Loans.CurrentLoan.m_Amount;
                    var money = g.EntityManager.GetComponentData<PlayerMoney>(g.CityEntity).money;
                    return new Setting
                    {
                        From = current, Min = Math.Max(0, current - Math.Max(0, money)), Max = g.Loans.Creditworthiness, Unit = "money (total loan)", Delayed = true,
                        Apply = v => g.Loans.ChangeLoan(g.Loans.RequestLoanOffer((int)Math.Round(v)).m_Amount),
                        Read = () => g.Loans.CurrentLoan.m_Amount,
                    };
                }

                case "tax.resource":
                {
                    var parts = target.Split(':');
                    if (parts.Length != 2 || !Enum.TryParse<TaxAreaType>(parts[0], out var area) || area == TaxAreaType.None || area == TaxAreaType.Residential)
                        throw new ArgumentException("target must be Commercial:<product>, Industrial:<product> or Office:<product> (finance.json taxes.byResource).");
                    if (!Enum.TryParse<Resource>(parts[1], out var resource) || !TaxedIn(world, resource, area))
                        throw new ArgumentException($"'{parts[1]}' is not a {area} product the game taxes. See finance.json taxes.byResource.{area}.");
                    return new Setting
                    {
                        From = ResourceRate(g, area, resource), Min = limits.m_ResourceTaxLimits.x, Max = limits.m_ResourceTaxLimits.y, Unit = "percent", TaxArea = area,
                        Apply = v =>
                        {
                            g.Tax.Readers.Complete();
                            var rate = (int)Math.Round(v);
                            if (area == TaxAreaType.Commercial) g.Tax.SetCommercialTaxRate(resource, rate);
                            else if (area == TaxAreaType.Industrial) g.Tax.SetIndustrialTaxRate(resource, rate);
                            else g.Tax.SetOfficeTaxRate(resource, rate);
                        },
                        Read = () => ResourceRate(g, area, resource),
                    };
                }

                case "policy":
                case "policy.slider":
                {
                    var p = CityPolicies(g, world).Find(x => string.Equals(x.Name, target, StringComparison.OrdinalIgnoreCase));
                    if (p == null)
                        throw new ArgumentException($"No city policy named '{target}'. See finance.json cityPolicies.");
                    if (p.Locked)
                        throw new ArgumentException($"{p.Title} is still locked (needs milestone {p.Milestone}).");
                    var ui = world.GetExistingSystemManaged<PoliciesUISystem>();
                    var entity = p.Entity;
                    Func<CityPolicy> now = () => CityPolicies(g, world).Find(x => x.Entity == entity);
                    if (action == "policy")
                        return new Setting
                        {
                            From = p.Active ? 1 : 0, Min = 0, Max = 1, Unit = "on/off (1 = on)", Delayed = true,
                            Apply = v => ui.SetCityPolicy(entity, v > 0.5, p.Value),
                            Read = () => now().Active ? 1 : 0,
                        };
                    if (!p.Slider)
                        throw new ArgumentException($"{p.Title} has no slider; use action=policy to turn it on or off.");
                    if (!p.Active)
                        throw new ArgumentException($"{p.Title} is off; turn it on first (action=policy, to=1), as its own approved change.");
                    return new Setting
                    {
                        From = Math.Round(p.Value, 4), Min = p.SliderMin, Max = p.SliderMax,
                        Unit = "policy slider" + (p.SliderStep > 0 ? $" (step {p.SliderStep})" : ""), Fractions = true, Delayed = true,
                        Apply = v => ui.SetCityPolicy(entity, true, (float)v),
                        Read = () => Math.Round(now().Value, 4),
                    };
                }

                default:
                    throw new ArgumentException($"Unknown action '{action}'. Allowed: {string.Join(", ", Actions)}.");
            }
        }

        private static Entity FindService(GameData g, string name, out bool adjustable)
        {
            adjustable = false;
            using (var entities = g.ServicePrefabs.ToEntityArray(Allocator.Temp))
            {
                foreach (var prefab in entities)
                {
                    if (!string.Equals(g.Prefabs.GetPrefabName(prefab), name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    adjustable = g.EntityManager.GetComponentData<ServiceData>(prefab).m_BudgetAdjustable;
                    return prefab;
                }
            }
            return Entity.Null;
        }

        // ---- per-product taxes ----

        private static bool TaxedIn(World world, Resource resource, TaxAreaType area)
        {
            if (resource == Resource.NoResource)
                return false;
            var prefabs = world.GetExistingSystemManaged<ResourceSystem>().GetPrefabs();
            var entity = prefabs[resource];
            var em = world.EntityManager;
            return entity != Entity.Null && em.HasComponent<TaxableResourceData>(entity) && em.GetComponentData<TaxableResourceData>(entity).Contains(area);
        }

        private static int ResourceRate(GameData g, TaxAreaType area, Resource resource) =>
            area == TaxAreaType.Commercial ? g.Tax.GetCommercialTaxRate(resource)
            : area == TaxAreaType.Industrial ? g.Tax.GetIndustrialTaxRate(resource)
            : g.Tax.GetOfficeTaxRate(resource);

        /// <summary>Every product the game taxes in this zone type with its rate (the Taxation menu's sub-rows).</summary>
        public static JsonObject ResourceRates(GameData g, World world, TaxAreaType area)
        {
            var o = new JsonObject();
            var iterator = ResourceIterator.GetIterator();
            while (iterator.Next())
                if (TaxedIn(world, iterator.resource, area))
                    o.Add(iterator.resource.ToString(), ResourceRate(g, area, iterator.resource));
            return o;
        }

        // ---- city policies ----

        public class CityPolicy
        {
            public Entity Entity;
            public string Name, Title;
            public bool Active, Locked, Slider;
            public int Milestone;
            public float Value, SliderMin, SliderMax, SliderStep, SliderDefault;
        }

        /// <summary>The city policies the game's Policies panel lists (PoliciesUISystem.FindAndSortPolicies for the city).</summary>
        public static List<CityPolicy> CityPolicies(GameData g, World world)
        {
            var em = world.EntityManager;
            var list = new List<CityPolicy>();
            var active = em.GetBuffer<Policy>(g.CityEntity, true);
            var desc = new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PolicyData>() },
                Any = new[] { ComponentType.ReadOnly<CityOptionData>(), ComponentType.ReadOnly<CityModifierData>() },
            };
            using (var query = em.CreateEntityQuery(desc))
            using (var entities = query.ToEntityArray(Allocator.Temp))
            {
                var dictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
                foreach (var entity in entities)
                {
                    var prefab = g.Prefabs.GetPrefab<PolicyPrefab>(entity);
                    if (prefab == null || prefab.m_Visibility == PolicyVisibility.HideFromPolicyList)
                        continue;
                    var slider = em.HasComponent<PolicySliderData>(entity);
                    var sliderData = slider ? em.GetComponentData<PolicySliderData>(entity) : default;
                    var p = new CityPolicy
                    {
                        Entity = entity, Name = prefab.name, Slider = slider,
                        Locked = em.HasComponent<Locked>(entity) && em.IsComponentEnabled<Locked>(entity),
                        Value = slider ? sliderData.m_Default : 0f,
                        SliderMin = sliderData.m_Range.min, SliderMax = sliderData.m_Range.max, SliderStep = sliderData.m_Step, SliderDefault = sliderData.m_Default,
                    };
                    for (int i = 0; i < active.Length; i++)
                    {
                        if (active[i].m_Policy != entity)
                            continue;
                        p.Active = (active[i].m_Flags & PolicyFlags.Active) != 0;
                        p.Value = active[i].m_Adjustment;
                        break;
                    }
                    p.Milestone = p.Locked ? ProgressionUtils.GetRequiredMilestone(em, entity) : 0;
                    p.Title = dictionary != null && dictionary.TryGetValue($"Policy.TITLE[{prefab.name}]", out var title) ? title : prefab.name;
                    list.Add(p);
                }
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        public static List<JsonObject> CityPoliciesJson(GameData g, World world)
        {
            var result = new List<JsonObject>();
            foreach (var p in CityPolicies(g, world))
            {
                var o = new JsonObject { { "name", p.Name }, { "title", p.Title }, { "active", p.Active }, { "locked", p.Locked } };
                if (p.Locked)
                    o.Add("unlocksAtMilestone", p.Milestone);
                if (p.Slider)
                    o.Add("slider", new JsonObject { { "value", p.Value }, { "min", p.SliderMin }, { "max", p.SliderMax }, { "step", p.SliderStep }, { "default", p.SliderDefault } });
                result.Add(o);
            }
            return result;
        }

        private static T Singleton<T>(World world) where T : unmanaged, IComponentData
        {
            using (var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<T>()))
                return query.GetSingleton<T>();
        }

        private static JsonObject TaxSnapshot(GameData g, World world, TaxAreaType resourceArea)
        {
            var o = new JsonObject { { "mainRate", g.Tax.TaxRate } };
            foreach (TaxAreaType area in new[] { TaxAreaType.Residential, TaxAreaType.Commercial, TaxAreaType.Industrial, TaxAreaType.Office })
                o.Add(area.ToString(), g.Tax.GetTaxRate(area));
            for (int level = 0; level < EducationLevels.Length; level++)
                o.Add("residential." + EducationLevels[level], g.Tax.GetResidentialTaxRate(level));
            // Product rates of the zone type being changed: the game shifts them (and the zone rate) to stay in its limits.
            foreach (TaxAreaType area in new[] { TaxAreaType.Commercial, TaxAreaType.Industrial, TaxAreaType.Office })
                if (resourceArea == area)
                    foreach (var kv in ResourceRates(g, world, area))
                        o.Add(area + ":" + kv.Key, kv.Value);
            return o;
        }

        private static bool TryNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);

        /// <summary>Every applied change: one line in cities\&lt;city&gt;\actions.jsonl and the mod log.</summary>
        private static void Log(GameData g, JsonObject result)
        {
            try
            {
                var entry = new JsonObject { { "appliedAt", DateTime.Now } };
                entry.AddRange(result);
                var line = Regex.Replace(entry.ToString(), @"\r?\n\s*", "") + Environment.NewLine;
                var folder = ModFiles.CityFolder(g.CityName);
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "actions.jsonl"), line);
            }
            catch (Exception e)
            {
                Mod.log.Warn($"Could not write actions.jsonl: {e.Message}");
            }
            Mod.log.Info($"Co-pilot change applied: {result.Get("action")} {result.Get("target")} {result.Get("from")} -> {result.Get("to")} (now {result.Get("valueAfter")})");
        }
    }
}
