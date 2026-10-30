using System;
using System.IO;
using Colossal.PSI.Environment;

namespace CityAssistant
{
    /// <summary>
    /// All file output goes through here, so exports (and later the city journal)
    /// share one folder layout and one error-handling path.
    ///
    /// ModsData\CityAssistant\cities\&lt;city&gt;\overview.json                 latest export
    ///                                     \finance.json
    ///                                     \history\2025-06\06h_overview.json  4 snapshots per game month
    ///                                     \history\abandoned\...              played but not saved
    /// </summary>
    public static class ModFiles
    {
        // Community convention: <user data>\ModsData\<ModName>\
        // e.g. %USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\ModsData\CityAssistant
        public static string DataFolder => Path.Combine(EnvPath.kUserDataPath, "ModsData", nameof(CityAssistant));

        public static string CityFolder(string cityName) => Path.Combine(DataFolder, "cities", SafeFileName(cityName));

        /// <summary>
        /// Writes the latest copy, and for snapshot exports also the history copy
        /// (history\YYYY-MM\HHh_kind.json). Exports taken right after loading are not history:
        /// the game is still re-counting citizens then.
        /// </summary>
        public static void WriteCityExport(string cityName, string kind, string gameMonth, string snapshot, string json, bool toHistory)
        {
            var folder = CityFolder(cityName);
            WriteAtomic(Path.Combine(folder, kind + ".json"), json);
            if (toHistory)
                WriteAtomic(Path.Combine(folder, "history", gameMonth, $"{snapshot}_{kind}.json"), json);
        }

        /// <summary>
        /// After loading a save, history snapshots later than the save's own time belong to a run that was
        /// played but not saved (or to a newer save). Move them to history\abandoned\&lt;time&gt;\ so they are
        /// not mistaken for this save's history.
        /// </summary>
        public static void ArchiveLaterHistory(string cityName, string gameMonth, int snapshotHour)
        {
            try
            {
                var history = Path.Combine(CityFolder(cityName), "history");
                if (!Directory.Exists(history))
                    return;

                string target = null;
                void MoveToAbandoned(string source, string relativePath)
                {
                    if (target == null)
                        target = Path.Combine(history, "abandoned", DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
                    var destination = Path.Combine(target, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Move(source, destination);
                    Mod.log.Info($"Moved {relativePath} to {target} (later than the loaded save: {gameMonth} {snapshotHour:D2}h)");
                }

                // Current layout: history\YYYY-MM\HHh_kind.json. Month names sort correctly as text.
                foreach (var monthFolder in Directory.GetDirectories(history))
                {
                    var month = Path.GetFileName(monthFolder);
                    if (!IsMonthName(month))
                        continue; // e.g. "abandoned"

                    var comparison = string.CompareOrdinal(month, gameMonth);
                    if (comparison < 0)
                        continue;

                    foreach (var file in Directory.GetFiles(monthFolder, "*.json"))
                    {
                        var name = Path.GetFileName(file);
                        // In the loaded month, snapshots up to the current slot happened before the save point.
                        if (comparison == 0 && SnapshotHourOf(name) <= snapshotHour)
                            continue;
                        MoveToAbandoned(file, Path.Combine(month, name));
                    }

                    if (Directory.GetFileSystemEntries(monthFolder).Length == 0)
                        Directory.Delete(monthFolder);
                }

                // Older layout (before snapshots): history\YYYY-MM_kind.json, one per month.
                foreach (var file in Directory.GetFiles(history, "*.json"))
                {
                    var name = Path.GetFileName(file);
                    if (name.Length >= 8 && IsMonthName(name.Substring(0, 7)) && string.CompareOrdinal(name.Substring(0, 7), gameMonth) > 0)
                        MoveToAbandoned(file, name);
                }
            }
            catch (Exception e)
            {
                Mod.log.Error($"Could not archive later history for {cityName}: {e}");
            }
        }

        private static bool IsMonthName(string s) =>
            s.Length == 7 && char.IsDigit(s[0]) && char.IsDigit(s[3]) && s[4] == '-' && char.IsDigit(s[5]) && char.IsDigit(s[6]);

        /// <summary>"06h_overview.json" -> 6; anything unexpected sorts last so it gets archived.</summary>
        private static int SnapshotHourOf(string fileName) =>
            fileName.Length >= 3 && int.TryParse(fileName.Substring(0, 2), out var hour) ? hour : int.MaxValue;

        /// <summary>
        /// Writes to a temporary file first and then swaps it in, so a reader never sees a half-written file.
        /// </summary>
        private static void WriteAtomic(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp";
                File.WriteAllText(temp, text);
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
                Mod.log.Info($"Wrote {path}");
            }
            catch (Exception e)
            {
                Mod.log.Error($"Could not write {path}: {e}");
            }
        }

        private static string SafeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
