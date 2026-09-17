using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace K2.UnityLink
{
    /// <summary>
    /// Writes a nested <c>Dictionary&lt;string, object&gt;</c> tree as JSON text.
    ///
    /// <para>Deliberately hand-rolled instead of a package reference: this assembly loads into
    /// whatever Unity game BepInEx is dropped in, and a JSON library shipped alongside risks
    /// colliding with a same-named one the game (or another mod) already loads into the same
    /// AppDomain — Mono has no assembly-load isolation between BepInEx plugins. A ~40-line writer
    /// with no dependencies cannot collide with anything.</para>
    /// </summary>
    internal static class MiniJsonWriter
    {
        internal static string Write(object? value)
        {
            var sb = new StringBuilder(4096);
            WriteValue(sb, value);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object? value)
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
                    sb.Append(!float.IsNaN(f) && !float.IsInfinity(f)
                        ? f.ToString("R", CultureInfo.InvariantCulture) : "null");
                    break;
                case double d:
                    sb.Append(!double.IsNaN(d) && !double.IsInfinity(d)
                        ? d.ToString("R", CultureInfo.InvariantCulture) : "null");
                    break;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object?> dict:
                    WriteObject(sb, dict);
                    break;
                case IEnumerable list and not string:
                    WriteArray(sb, list);
                    break;
                default:
                    // Anything left (an enum, mostly) prints as its own string form — good enough
                    // for a tile, and never a crash.
                    WriteString(sb, value.ToString() ?? "");
                    break;
            }
        }

        private static void WriteObject(StringBuilder sb, IDictionary<string, object?> dict)
        {
            sb.Append('{');
            bool first = true;
            foreach (var pair in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, pair.Key);
                sb.Append(':');
                WriteValue(sb, pair.Value);
            }
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IEnumerable list)
        {
            sb.Append('[');
            bool first = true;
            foreach (var item in list)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteValue(sb, item);
            }
            sb.Append(']');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
