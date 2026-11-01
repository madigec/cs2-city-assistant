using System;
using System.Reflection;
using Colossal.UI.Binding;
using Game.UI.InGame;
using Unity.Mathematics;
using Unity.Entities;

namespace CityAssistant.Export
{
    /// <summary>
    /// Reads the numbers a game info view panel shows (Garbage, Healthcare, Police, ...).
    /// Each info view has a UI system that calculates its values into named bindings such as
    /// m_PatientCapacity or m_CemeteryUse. We ask it to recalculate, as if the player had opened the panel,
    /// and collect every binding, so the export matches what the player can check on screen.
    /// Also works for other UI systems (e.g. the city info panel), which have no recalculate step.
    ///
    /// This uses private members (found by inspecting game version 1.6.1f1), so a game update can break it;
    /// the caller wraps it in GameData.Safe so a broken panel becomes {"error": ...} instead of a crash.
    /// </summary>
    public static class InfoviewReader
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>
        /// Runs one of a panel's private writer methods (the ones behind its map bindings, e.g.
        /// ServiceBudgetUISystem.WriteServiceDetails(writer, serviceEntity)) and returns what it wrote.
        /// </summary>
        public static object CallWriter<TSystem>(World world, string method, params object[] args) where TSystem : ComponentSystemBase
        {
            var system = world.GetExistingSystemManaged<TSystem>();
            if (system == null)
                throw new InvalidOperationException($"{typeof(TSystem).Name} not found");
            MethodInfo found = null;
            foreach (var m in typeof(TSystem).GetMethods(Private | BindingFlags.Public))
            {
                var p = m.GetParameters();
                if (m.Name == method && p.Length == args.Length + 1 && p[0].ParameterType == typeof(IJsonWriter))
                {
                    found = m;
                    break;
                }
            }
            if (found == null)
                throw new MissingMethodException(typeof(TSystem).Name, method);
            return JsonCapture.Capture(w =>
            {
                var all = new object[args.Length + 1];
                all[0] = w;
                Array.Copy(args, 0, all, 1, args.Length);
                found.Invoke(system, all);
            });
        }

        public static JsonObject Read<TSystem>(World world) where TSystem : ComponentSystemBase
        {
            var system = world.GetExistingSystemManaged<TSystem>();
            if (system == null)
                throw new InvalidOperationException($"{typeof(TSystem).Name} not found");

            // Recalculate the panel's values (info views only).
            if (system is InfoviewUISystemBase)
            {
                var perform = typeof(TSystem).GetMethod("PerformUpdate", Private | BindingFlags.Public);
                if (perform == null)
                    throw new MissingMethodException(typeof(TSystem).Name, "PerformUpdate");
                perform.Invoke(system, null);
            }

            var result = new JsonObject();
            foreach (var field in typeof(TSystem).GetFields(Private | BindingFlags.DeclaredOnly))
            {
                var type = field.FieldType;
                if (type == typeof(RawValueBinding))
                {
                    // Panels that write their data straight to the UI: run their writer into ours.
                    var raw = field.GetValue(system);
                    var writer = typeof(RawValueBinding).GetField("m_WriterDelegate", Private)?.GetValue(raw) as Action<IJsonWriter>;
                    if (writer != null)
                        result.Add(KeyName(field.Name), JsonCapture.Capture(writer));
                    continue;
                }
                if (!type.IsGenericType)
                    continue;

                var binding = field.GetValue(system);
                if (binding == null)
                    continue;

                object value;
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(ValueBinding<>))
                {
                    value = type.GetProperty("value").GetValue(binding);
                }
                else if (definition == typeof(GetterValueBinding<>))
                {
                    // Call the same function the panel calls to compute this value.
                    var getter = type.GetField("m_Getter", Private)?.GetValue(binding) as Delegate;
                    if (getter == null)
                        continue;
                    value = getter.DynamicInvoke();
                }
                else
                {
                    continue;
                }

                result.Add(KeyName(field.Name), Convert(value));
            }
            return result;
        }

        /// <summary>"m_PatientCapacity" -> "patientCapacity"</summary>
        private static string KeyName(string fieldName)
        {
            var name = fieldName.StartsWith("m_") ? fieldName.Substring(2) : fieldName;
            return name.Length == 0 ? fieldName : char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static object Convert(object value)
        {
            // Availability indicators: the panel's bar, "current" between "min" and "max".
            if (value is IndicatorValue indicator)
            {
                return new JsonObject
                {
                    { "current", indicator.current },
                    { "min", indicator.min },
                    { "max", indicator.max },
                };
            }
            if (value is int2 pair)
                return new[] { pair.x, pair.y };
            // Structured values (e.g. jobs by education level) describe themselves as JSON.
            if (value is IJsonWritable writable)
                return JsonCapture.Capture(writable);
            return value;
        }
    }
}
