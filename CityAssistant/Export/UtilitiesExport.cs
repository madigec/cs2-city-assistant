using Game.UI.InGame;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Layer 2, Utilities domain: what the Electricity and Water info view panels show,
    /// including imports and exports with neighbouring cities.
    /// Values are the panel's raw numbers; units are checked against the panels before the skill relies on them.
    /// The utility buildings themselves are listed in services.json under "buildings".
    /// </summary>
    public static class UtilitiesExport
    {
        public static JsonObject Build(GameData g, World world, string trigger)
        {
            var o = g.Header("utilities", trigger);

            o.Add("electricity", GameData.Safe("electricity", () => InfoviewReader.Read<ElectricityInfoviewUISystem>(world)));
            o.Add("water", GameData.Safe("water", () => InfoviewReader.Read<WaterInfoviewUISystem>(world)));
            return o;
        }
    }
}
