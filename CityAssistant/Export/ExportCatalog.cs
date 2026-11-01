using System;
using System.Collections.Generic;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Every file the mod produces, in export order. The snapshot export and the live endpoint both use this
    /// list, so a live answer is always built exactly like the file of the same name.
    /// </summary>
    public static class ExportCatalog
    {
        public static readonly string[] Names =
        {
            "overview", "finance", "services", "utilities", "growth", "economy",
            "traffic", "transport", "environment", "wellbeing", "districts", "problems", "disasters", "statistics", "progression",
        };

        /// <summary>Builds one export by name (one of <see cref="Names"/>), or returns null for an unknown name.</summary>
        public static JsonObject Build(string name, GameData g, World world, string trigger)
        {
            switch (name)
            {
                case "overview": return OverviewExport.Build(g, trigger);
                case "finance": return FinanceExport.Build(g, world, trigger);
                case "services": return ServicesExport.Build(g, world, trigger);
                case "utilities": return UtilitiesExport.Build(g, world, trigger);
                case "growth": return DomainExports.Growth(g, world, trigger);
                case "economy": return DomainExports.Economy(g, world, trigger);
                case "traffic": return DomainExports.Traffic(g, world, trigger);
                case "transport": return DomainExports.Transport(g, world, trigger);
                case "environment": return DomainExports.Environment(g, world, trigger);
                case "wellbeing": return DomainExports.Wellbeing(g, world, trigger);
                case "districts": return DistrictsExport.Build(g, world, trigger);
                case "problems": return ProblemsExport.Build(g, trigger);
                case "disasters": return Wave3Exports.Disasters(g, world, trigger);
                case "statistics": return Wave3Exports.History(g, trigger);
                case "progression": return ProgressionExport.Build(g, world, trigger);
                default: return null;
            }
        }
    }
}
