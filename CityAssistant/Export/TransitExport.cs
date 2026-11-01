using System;
using System.Collections.Generic;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Tools;
using Game.UI;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Phase 6 hole 7: every public transport and cargo line, with the Transportation overview's own numbers
    /// (TransportUIUtils.BuildTransportLine: length, stops, vehicles, usage, schedule) plus ticket price,
    /// vehicle interval, the "not enough vehicles" flag and the passengers waiting at its stops.
    /// </summary>
    public static class TransitExport
    {
        public static JsonObject Build(GameData g, World world)
        {
            var em = g.EntityManager;
            var names = world.GetExistingSystemManaged<NameSystem>();
            var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<Route>(), ComponentType.ReadOnly<TransportLine>(), ComponentType.ReadOnly<RouteWaypoint>(), ComponentType.ReadOnly<PrefabRef>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            var list = new List<object>();
            int short_ = 0;
            using (var lines = TransportUIUtils.GetSortedLines(query, em, g.Prefabs))
            {
                foreach (var line in lines)
                {
                    var e = line.entity;
                    var t = em.GetComponentData<TransportLine>(e);
                    int waiting = 0, waitSum = 0, stopsWithData = 0;
                    var waypoints = em.GetBuffer<RouteWaypoint>(e, true);
                    for (int i = 0; i < waypoints.Length; i++)
                    {
                        if (!em.HasComponent<WaitingPassengers>(waypoints[i].m_Waypoint))
                            continue;
                        var w = em.GetComponentData<WaitingPassengers>(waypoints[i].m_Waypoint);
                        waiting += w.m_Count;
                        waitSum += w.m_AverageWaitingTime;
                        stopsWithData++;
                    }
                    bool notEnough = (t.m_Flags & TransportLineFlags.NotEnoughVehicles) != 0;
                    if (notEnough) short_++;
                    string name = null;
                    try { name = names?.GetRenderedLabelName(e); } catch (Exception) { }
                    int number = em.HasComponent<RouteNumber>(e) ? em.GetComponentData<RouteNumber>(e).m_Number : 0;
                    list.Add(new JsonObject
                    {
                        // Unnamed lines: the label is the line tool's name ("Bus Line Tool"); the overview shows "Bus line 1".
                        { "name", string.IsNullOrEmpty(name) || name.EndsWith("Tool") ? $"{(line.isCargo ? "Cargo " + line.type.ToString().ToLowerInvariant() : line.type.ToString())} line {number}" : name },
                        { "number", number },
                        { "type", line.type.ToString() },
                        { "cargo", line.isCargo },
                        { "active", line.active },
                        { "schedule", ((RouteSchedule)line.schedule).ToString() },
                        { "lengthKm", Math.Round(line.length / 1000.0, 2) },
                        { "stops", line.stops },
                        { "vehicles", line.vehicles },
                        { "usagePercent", Math.Round(100.0 * line.usage, 1) },
                        { "onBoard", line.cargo },
                        { "notEnoughVehicles", notEnough },
                        { "vehicleInterval", Math.Round(t.m_VehicleInterval, 1) },
                        { "ticketPrice", line.isCargo ? null : (object)(int)t.m_TicketPrice },
                        { "waitingPassengers", line.isCargo ? null : (object)waiting },
                        { "averageWaitSeconds", line.isCargo || stopsWithData == 0 ? null : (object)(waitSum / stopsWithData) },
                    });
                }
            }
            return new JsonObject
            {
                { "count", list.Count },
                { "linesShortOfVehicles", short_ },
                { "lines", list },
                { "note", "usagePercent = passengers (or cargo) on board now / the vehicles' capacity, as the Transportation overview shows it. averageWaitSeconds = average of the stops' waiting times (game seconds, as the game's developer info shows them). vehicleInterval = the game's time between vehicles (internal units)." },
            };
        }
    }
}
