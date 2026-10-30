using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CityAssistant.Export
{
    /// <summary>
    /// A JSON object that keeps keys in the order they were added, so exported files read top-down.
    /// </summary>
    public class JsonObject : List<KeyValuePair<string, object>>
    {
        public JsonObject Add(string key, object value)
        {
            Add(new KeyValuePair<string, object>(key, value));
            return this;
        }

        /// <summary>The value stored under a key, or null if there is none.</summary>
        public object Get(string key)
        {
            foreach (var entry in this)
                if (entry.Key == key)
                    return entry.Value;
            return null;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            Json.Write(sb, this, 0);
            sb.AppendLine();
            return sb.ToString();
        }
    }

    /// <summary>
    /// Minimal indented JSON writer. The game ships no JSON library we can rely on across updates.
    /// </summary>
    public static class Json
    {
        public static void Write(StringBuilder sb, object value, int indent)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case float f:
                    WriteNumber(sb, f);
                    break;
                case double d:
                    WriteNumber(sb, d);
                    break;
                case Enum e:
                    WriteString(sb, e.ToString());
                    break;
                case DateTime dt:
                    WriteString(sb, dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                    break;
                case IFormattable n:
                    sb.Append(n.ToString(null, CultureInfo.InvariantCulture));
                    break;
                case JsonObject obj:
                    WriteObject(sb, obj, indent);
                    break;
                case IEnumerable list:
                    WriteArray(sb, list, indent);
                    break;
                default:
                    WriteString(sb, value.ToString());
                    break;
            }
        }

        private static void WriteObject(StringBuilder sb, JsonObject obj, int indent)
        {
            if (obj.Count == 0) { sb.Append("{}"); return; }
            sb.Append('{');
            for (int i = 0; i < obj.Count; i++)
            {
                sb.Append(i == 0 ? "\n" : ",\n");
                Indent(sb, indent + 1);
                WriteString(sb, obj[i].Key);
                sb.Append(": ");
                Write(sb, obj[i].Value, indent + 1);
            }
            sb.Append('\n');
            Indent(sb, indent);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable list, int indent)
        {
            var first = true;
            sb.Append('[');
            foreach (var item in list)
            {
                sb.Append(first ? "\n" : ",\n");
                first = false;
                Indent(sb, indent + 1);
                Write(sb, item, indent + 1);
            }
            if (!first)
            {
                sb.Append('\n');
                Indent(sb, indent);
            }
            sb.Append(']');
        }

        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                sb.Append("null");
            else
                sb.Append(Math.Round(d, 4).ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void Indent(StringBuilder sb, int indent)
        {
            sb.Append(' ', indent * 2);
        }
    }
}
