using System;
using System.Collections.Generic;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Events;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;
using Transform = Game.Objects.Transform;

namespace CityAssistant.Export
{
    /// <summary>
    /// Phase 6 wave 3: weather (hole 18), tourism details (15), disasters and emergencies (9), citizens' daily
    /// life (19), Chirper (21) and the game's own statistic history (20).
    /// </summary>
    public static class Wave3Exports
    {
        private static EntityQuery Query(EntityManager em, params ComponentType[] all)
        {
            return em.CreateEntityQuery(new EntityQueryDesc
            {
                All = all,
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Deleted>() },
            });
        }

        // ---------- weather (environment.json "climate") ----------

        public static JsonObject Climate(World world)
        {
            var o = InfoviewReader.Read<ClimateUISystem>(world);
            var c = world.GetOrCreateSystemManaged<ClimateSystem>();
            o.Add("precipitation", Math.Round((float)c.precipitation, 3));
            o.Add("cloudiness", Math.Round((float)c.cloudiness, 3));
            o.Add("raining", c.isRaining);
            o.Add("snowing", c.isSnowing);
            o.Add("averageTemperature", Math.Round(c.averageTemperature, 1));
            o.Add("note", "temperature in °C as the clock widget shows it (ClimateUISystem). Colder weather raises heating (power) use, rain keeps people out of parks, the weather changes tourism (economy.json tourism.weatherEffect) and wind/solar output.");
            return o;
        }

        // ---------- tourism (economy.json "tourismDetails") ----------

        public static JsonObject Tourism(GameData g)
        {
            var em = g.EntityManager;
            var attractions = new List<(int value, string name, float x, float z)>();
            var q = Query(em, ComponentType.ReadOnly<AttractivenessProvider>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.ReadOnly<Transform>());
            using (var providers = q.ToComponentDataArray<AttractivenessProvider>(Allocator.Temp))
            using (var prefabs = q.ToComponentDataArray<PrefabRef>(Allocator.Temp))
            using (var transforms = q.ToComponentDataArray<Transform>(Allocator.Temp))
            {
                for (int i = 0; i < providers.Length; i++)
                    if (providers[i].m_Attractiveness > 0)
                        attractions.Add((providers[i].m_Attractiveness, g.Prefabs.GetPrefabName(prefabs[i].m_Prefab), transforms[i].m_Position.x, transforms[i].m_Position.z));
            }
            attractions.Sort((a, b) => b.value.CompareTo(a.value));
            var top = new List<object>();
            for (int i = 0; i < attractions.Count && i < 15; i++)
                top.Add(new JsonObject { { "building", attractions[i].name }, { "attractiveness", attractions[i].value }, { "x", Math.Round(attractions[i].x) }, { "z", Math.Round(attractions[i].z) } });
            int lodgingUsed = g.Statistics.GetStatisticValue(StatisticType.LodgingUsed);
            int lodgingTotal = g.Statistics.GetStatisticValue(StatisticType.LodgingTotal);
            return new JsonObject
            {
                { "tourists", g.Statistics.GetStatisticValue(StatisticType.TouristCount) },
                { "touristIncome", g.Statistics.GetStatisticValue(StatisticType.TouristIncome) },
                { "hotelRoomsUsed", lodgingUsed },
                { "hotelRooms", lodgingTotal },
                { "hotelRoomsUsedPercent", lodgingTotal > 0 ? (object)Math.Round(100.0 * lodgingUsed / lodgingTotal, 1) : null },
                { "attractions", attractions.Count },
                { "topAttractions", top },
                { "note", "The game's own statistics (TouristCount, TouristIncome, LodgingUsed, LodgingTotal). Tourists who find no hotel room leave (TouristFindTargetSystem). Attractiveness per building = its base value x efficiency x park maintenance x scenery (game code AttractivenessSection)." },
            };
        }

        // ---------- disasters (disasters.json) ----------

        public static JsonObject Disasters(GameData g, World world, string trigger)
        {
            var em = g.EntityManager;
            var o = g.Header("disasters", trigger);
            o.Add("naturalDisastersOn", GameData.Safe("naturalDisastersOn", () => g.Config.naturalDisasters));
            o.Add("now", GameData.Safe("now", () =>
            {
                int buildingsOnFire = 0, treesOnFire = 0;
                using (var entities = Query(em, ComponentType.ReadOnly<OnFire>()).ToEntityArray(Allocator.Temp))
                    foreach (var e in entities)
                    {
                        if (em.HasComponent<Building>(e)) buildingsOnFire++;
                        else treesOnFire++;
                    }
                int destroyed = Query(em, ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Destroyed>()).CalculateEntityCount();
                int inDanger = Query(em, ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<InDanger>()).CalculateEntityCount();
                int flooded = Query(em, ComponentType.ReadOnly<Building>(), ComponentType.ReadOnly<Flooded>()).CalculateEntityCount();
                var weather = new List<object>();
                var wq = Query(em, ComponentType.ReadOnly<Game.Events.WeatherPhenomenon>(), ComponentType.ReadOnly<PrefabRef>());
                using (var ph = wq.ToComponentDataArray<Game.Events.WeatherPhenomenon>(Allocator.Temp))
                using (var prefabs = wq.ToComponentDataArray<PrefabRef>(Allocator.Temp))
                    for (int i = 0; i < ph.Length; i++)
                        weather.Add(new JsonObject
                        {
                            { "type", g.Prefabs.GetPrefabName(prefabs[i].m_Prefab) },
                            { "intensity", Math.Round(ph[i].m_Intensity, 2) },
                            { "x", Math.Round(ph[i].m_HotspotPosition.x) }, { "z", Math.Round(ph[i].m_HotspotPosition.z) },
                        });
                return new JsonObject
                {
                    { "buildingsOnFire", buildingsOnFire },
                    { "treesOnFire", treesOnFire },
                    { "destroyedBuildings", destroyed },
                    { "buildingsInDanger", inDanger },
                    { "floodedBuildings", flooded },
                    { "weatherEvents", weather },
                    { "earlyWarningBuildings", Query(em, ComponentType.ReadOnly<Game.Buildings.EarlyDisasterWarningSystem>()).CalculateEntityCount() },
                };
            }));
            o.Add("eventJournal", GameData.Safe("eventJournal", () => EventJournal(g, world)));
            o.Add("note", "Disasters only happen when naturalDisastersOn is true (the save's settings); fires and accidents happen either way. now = this moment. eventJournal = the game's event journal (big events with damages, casualties and their effect on crime, happiness, tax and trade income, tourism).");
            return o;
        }

        /// <summary>
        /// The event journal as the game's journal panel reads it (EventJournalUISystem.BindJournalEntry):
        /// EventJournalSystem.eventJournal, newest last; the event type comes from the journal, because the event
        /// itself is often gone once it ended.
        /// </summary>
        private static JsonObject EventJournal(GameData g, World world)
        {
            var journal = world.GetOrCreateSystemManaged<EventJournalSystem>();
            var list = journal.eventJournal;
            var latest = new List<object>();
            var recentTypes = new SortedDictionary<string, int>();
            int firstRecent = Math.Max(0, list.Length - 100);
            for (int i = list.Length - 1; i >= firstRecent; i--)
            {
                var entry = list[i];
                string type = g.Prefabs.GetPrefabName(journal.GetPrefab(entry));
                recentTypes.TryGetValue(type, out var n);
                recentTypes[type] = n + 1;
                if (latest.Count >= 15)
                    continue;
                var info = journal.GetInfo(entry);
                var j = new JsonObject
                {
                    { "event", type },
                    { "gameDaysAgo", Math.Round((g.Simulation.frameIndex - info.m_StartFrame) / 262144.0, 2) },
                };
                if (journal.TryGetData(entry, out var data))
                    for (int d = 0; d < data.Length; d++)
                        j.Add(data[d].m_Type.ToString().ToLowerInvariant(), data[d].m_Value);
                if (journal.TryGetCityEffects(entry, out var effects))
                {
                    var e = new JsonObject();
                    for (int k = 0; k < effects.Length; k++)
                        e.Add(effects[k].m_Type.ToString(), EventJournalUtils.GetPercentileChange(effects[k]));
                    j.Add("cityEffectPercent", e);
                }
                latest.Add(j);
            }
            var types = new JsonObject();
            foreach (var kv in recentTypes) types.Add(kv.Key, kv.Value);
            return new JsonObject { { "entries", list.Length }, { "last100ByType", types }, { "latest", latest } };
        }

        // ---------- daily life (wellbeing.json "dailyLife") ----------

        private static readonly string[] kHappyWords = { "Depressed", "Sad", "Neutral", "Content", "Happy" };

        public static JsonObject DailyLife(GameData g)
        {
            var em = g.EntityManager;
            var happy = new int[5];
            int residents = 0, weak = 0, unwell = 0;
            using (var citizens = Query(em, ComponentType.ReadOnly<Citizen>(), ComponentType.ReadOnly<HouseholdMember>()).ToComponentDataArray<Citizen>(Allocator.Temp))
            {
                foreach (var c in citizens)
                {
                    if ((c.m_State & (CitizenFlags.Tourist | CitizenFlags.Commuter)) != 0)
                        continue;
                    residents++;
                    // CitizenUtils.GetHappinessKey: >70 Happy, >55 Content, >40 Neutral, >25 Sad, else Depressed.
                    int w = c.m_WellBeing;
                    happy[w > 70 ? 4 : w > 55 ? 3 : w > 40 ? 2 : w > 25 ? 1 : 0]++;
                    if (c.m_Health <= 25) weak++;
                    if (c.m_WellBeing <= 25) unwell++;
                }
            }
            var happiness = new JsonObject();
            for (int i = 4; i >= 0; i--)
                happiness.Add(kHappyWords[i], happy[i]);

            int workers = 0, overqualified = 0;
            double commute = 0;
            var shifts = new Dictionary<string, int>();
            var wq = Query(em, ComponentType.ReadOnly<Worker>(), ComponentType.ReadOnly<Citizen>());
            using (var w = wq.ToComponentDataArray<Worker>(Allocator.Temp))
            using (var c = wq.ToComponentDataArray<Citizen>(Allocator.Temp))
            {
                for (int i = 0; i < w.Length; i++)
                {
                    if ((c[i].m_State & CitizenFlags.Commuter) != 0)
                        continue;
                    workers++;
                    commute += w[i].m_LastCommuteTime;
                    if (c[i].GetEducationLevel() > w[i].m_Level) overqualified++;
                    string s = w[i].m_Shift.ToString();
                    shifts.TryGetValue(s, out var n);
                    shifts[s] = n + 1;
                }
            }
            var shiftJson = new JsonObject();
            foreach (var kv in shifts) shiftJson.Add(kv.Key, kv.Value);
            int students = 0;
            double studentCommute = 0;
            using (var s = Query(em, ComponentType.ReadOnly<Game.Citizens.Student>()).ToComponentDataArray<Game.Citizens.Student>(Allocator.Temp))
                foreach (var st in s) { students++; studentCommute += st.m_LastCommuteTime; }
            return new JsonObject
            {
                { "residents", residents },
                { "happinessWords", happiness },
                { "weakHealth", weak },
                { "unwell", unwell },
                { "workers", workers },
                { "averageCommuteTime", workers > 0 ? (object)Math.Round(commute / workers, 1) : null },
                { "overqualifiedWorkers", overqualified },
                { "overqualifiedPercent", workers > 0 ? (object)Math.Round(100.0 * overqualified / workers, 1) : null },
                { "shifts", shiftJson },
                { "students", students },
                { "averageStudentCommuteTime", students > 0 ? (object)Math.Round(studentCommute / students, 1) : null },
                { "note", "Residents only (no tourists or commuters). happinessWords = the citizen panel's words (CitizenUtils). weakHealth = health 25 or less, unwell = well-being 25 or less (the citizen panel's conditions). Commute time in the game's own units (Worker.m_LastCommuteTime), useful to compare months and districts, not as minutes. Overqualified = education above the job's level: they keep looking for better jobs." },
            };
        }

        // ---------- Chirper (wellbeing.json "chirper") ----------

        public static JsonObject Chirper(GameData g, World world)
        {
            var em = g.EntityManager;
            var q = Query(em, ComponentType.ReadOnly<Game.Triggers.Chirp>(), ComponentType.ReadOnly<PrefabRef>());
            var dictionary = Game.SceneFlow.GameManager.instance?.localizationManager?.activeDictionary;
            var ui = world.GetExistingSystemManaged<ChirperUISystem>();
            var all = new List<(uint frame, Entity e)>();
            using (var entities = q.ToEntityArray(Allocator.Temp))
            using (var chirps = q.ToComponentDataArray<Game.Triggers.Chirp>(Allocator.Temp))
                for (int i = 0; i < entities.Length; i++)
                    all.Add((chirps[i].m_CreationFrame, entities[i]));
            all.Sort((a, b) => b.frame.CompareTo(a.frame));
            var list = new List<object>();
            for (int i = 0; i < all.Count && i < 20; i++)
            {
                var e = all[i].e;
                string id = ui?.GetMessageID(e);
                string text = null;
                if (!string.IsNullOrEmpty(id))
                    dictionary?.TryGetValue(id, out text);
                list.Add(new JsonObject
                {
                    { "type", g.Prefabs.GetPrefabName(em.GetComponentData<PrefabRef>(e).m_Prefab) },
                    { "text", text ?? id },
                    { "gameDaysAgo", Math.Round((g.Simulation.frameIndex - all[i].frame) / 262144.0, 2) },
                    { "likes", em.GetComponentData<Game.Triggers.Chirp>(e).m_Likes },
                });
            }
            return new JsonObject
            {
                { "chirps", all.Count },
                { "latest", list },
                { "note", "The citizens' messages, newest first. {LINK_1} etc. in the text stand for the place or building the game links. Most chirps are triggered by the same problems as the warning icons and happiness factors; use them as colour, not as data." },
            };
        }

        // ---------- the game's statistic history (statistics.json) ----------

        public static JsonObject History(GameData g, string trigger)
        {
            var o = g.Header("statistics", trigger);
            o.Add("history", GameData.Safe("history", () =>
            {
                var series = new JsonObject();
                int samples = 0;
                for (var t = StatisticType.Population; t < StatisticType.Count; t++)
                {
                    using (var data = g.Statistics.GetStatisticDataArray(t, 0))
                    {
                        if (data.Length <= 1)
                            continue;
                        samples = Math.Max(samples, data.Length);
                        // One value per game day (= one calendar month): every 32nd sample, newest last, up to 36.
                        var values = new List<int>();
                        for (int i = data.Length - 1; i >= 0 && values.Count < 36; i -= 32)
                            values.Add(data[i]);
                        values.Reverse();
                        series.Add(t.ToString(), values);
                    }
                }
                return new JsonObject
                {
                    { "samplesKept", samples },
                    { "monthsPerValue", 1 },
                    { "series", series },
                    { "note", "The game's own history (the City Information graphs), one value per in-game month (every 32nd sample, 32 samples per game day), newest last, at most 36 months. Only the first parameter of each statistic (e.g. Income and Expense show their first source only; per-source figures are in finance.json). Values before the mod was installed are included." },
                };
            }));
            return o;
        }
    }
}
