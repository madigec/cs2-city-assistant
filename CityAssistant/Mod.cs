using CityAssistant.Systems;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace CityAssistant
{
    /// <summary>
    /// Entry point. The game creates this class when the mod is enabled and calls
    /// OnLoad once at startup, OnDispose once at shutdown.
    /// </summary>
    public class Mod : IMod
    {
        // Logger name "CityAssistant" -> the game writes Logs\CityAssistant.log
        public static ILog log = LogManager.GetLogger(nameof(CityAssistant)).SetShowsErrorsInUI(false);

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info($"{nameof(OnLoad)}: CityAssistant starting, exports go to {ModFiles.DataFolder}");

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            // GameSimulation only runs while a city is open and unpaused.
            updateSystem.UpdateAt<CityExportSystem>(SystemUpdatePhase.GameSimulation);
            // The UI phase also runs while paused, so live questions get answers then too.
            updateSystem.UpdateAt<LiveEndpointSystem>(SystemUpdatePhase.UIUpdate);
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
        }
    }
}
