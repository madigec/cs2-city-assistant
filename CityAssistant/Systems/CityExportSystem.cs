using System;
using CityAssistant.Export;
using Colossal.Serialization.Entities;
using Game;
using Game.Simulation;

namespace CityAssistant.Systems
{
    /// <summary>
    /// Decides when to export: once after a city finishes loading, then at every history snapshot
    /// (00:00, 06:00, 12:00 and 18:00 in-game, i.e. four times per in-game month).
    /// Runs in the simulation phase, so nothing happens while the game is paused or in the menu.
    /// </summary>
    public partial class CityExportSystem : GameSystemBase
    {
        // After loading, wait this many updates (x 256 frames, about a second of unpaused play)
        // before the first export, so the game's statistics systems have run at least once.
        private const int kLoadDelayUpdates = 4;

        private GameData m_Data;

        /// <summary>The game data this system exports from; shared with the live endpoint so both know when the city was loaded.</summary>
        public GameData Data
        {
            get
            {
                if (m_Data == null)
                    m_Data = new GameData(World);
                return m_Data;
            }
        }
        private bool m_CityJustLoaded;
        private int m_LoadDelay;
        private long m_LastSnapshotKey = -1;

        // Check the clock every 256 simulation frames (about 1.4 in-game minutes); a snapshot slot is 6 hours.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            Mod.log.Info($"Game loading complete: purpose={purpose}, mode={mode}");

            if (mode == GameMode.Game)
                Mod.log.Info($"Simulation frame at load: {World.GetOrCreateSystemManaged<SimulationSystem>().frameIndex}");

            m_CityJustLoaded = mode == GameMode.Game;
            m_LoadDelay = kLoadDelayUpdates;
            m_LastSnapshotKey = -1;
        }

        protected override void OnUpdate()
        {
            if (m_Data == null)
                m_Data = new GameData(World);

            if (m_CityJustLoaded)
            {
                if (m_LoadDelay-- > 0)
                    return;

                // The slot we loaded into is "taken" by the load export; the next slot gets a history snapshot.
                m_CityJustLoaded = false;
                m_Data.LoadedAtMonths = m_Data.TotalMonths;
                m_LastSnapshotKey = m_Data.SnapshotKey;
                Export("load");
                return;
            }

            // Only move forward: at the turn of the year the year and the date may update a frame apart,
            // which could briefly look like going back in time.
            var key = m_Data.SnapshotKey;
            if (key <= m_LastSnapshotKey)
                return;

            m_LastSnapshotKey = key;
            Export("snapshot");
        }

        private void Export(string trigger)
        {
            try
            {
                var city = m_Data.CityName;
                var month = m_Data.GameMonthKey;
                var snapshot = m_Data.SnapshotName;
                Mod.log.Info($"Exporting '{city}' for {month} {snapshot} (clock {m_Data.GameClock}, trigger: {trigger}, simulation frame {m_Data.Simulation.frameIndex}, speed {m_Data.Simulation.selectedSpeed})");

                // Exports right after loading are not history: the game is still re-counting citizens then.
                var isSnapshot = trigger == "snapshot";
                if (!isSnapshot)
                    ModFiles.ArchiveLaterHistory(city, month, m_Data.SnapshotHour);

                foreach (var name in ExportCatalog.Names)
                    ModFiles.WriteCityExport(city, name, month, snapshot, ExportCatalog.Build(name, m_Data, World, trigger).ToString(), isSnapshot);

                // The map layer once per game day (00:00), into history too, so maps can be compared between
                // months (the network carries traffic and coverage, so it is history as well).
                if (isSnapshot && m_Data.SnapshotHour == 0)
                {
                    foreach (var name in MapExport.Names)
                        ModFiles.WriteCityExport(city, name, month, snapshot, MapExport.Build(name, m_Data, World, trigger).ToString(), true);
                }
            }
            catch (Exception e)
            {
                Mod.log.Error($"Export failed: {e}");
            }
        }
    }
}
