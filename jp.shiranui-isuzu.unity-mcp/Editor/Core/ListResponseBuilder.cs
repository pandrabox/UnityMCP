using System;
using System.Collections.Generic;

using Newtonsoft.Json.Linq;

namespace UnityMCP.Editor.Core
{
    /// <summary>
    /// Utility for building paginated list responses with optional field filtering.
    /// Returns a result body <c>{items, truncated, next}</c> that the envelope writer passes through.
    /// </summary>
    internal static class ListResponseBuilder
    {
        /// <summary>
        /// Builds a paginated result object from a flat list.
        /// </summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="items">Full (pre-filtered) list of items.</param>
        /// <param name="offset">Zero-based start index.</param>
        /// <param name="limit">Maximum number of items to return. Use <c>int.MaxValue</c> for unlimited.</param>
        /// <param name="projector">Converts each item to a JObject.</param>
        /// <param name="fieldsFilter">
        /// Optional allowlist of JSON keys to keep. If <c>null</c> or empty, all keys are kept.
        /// </param>
        /// <returns>
        /// A JObject with keys <c>items</c> (JArray), <c>truncated</c> (bool), and
        /// <c>next</c> ({offset, limit} or null).
        /// </returns>
        public static JObject Build<T>(
            IReadOnlyList<T> items,
            int offset,
            int limit,
            Func<T, JObject> projector,
            string[] fieldsFilter = null)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (projector == null) throw new ArgumentNullException(nameof(projector));

            // Clamp offset / limit to sane values
            offset = Math.Max(0, offset);
            limit = limit <= 0 ? int.MaxValue : limit;

            var total = items.Count;
            var end = (long)offset + limit;          // use long to avoid int overflow
            var actualEnd = (int)Math.Min(end, total);
            var truncated = actualEnd < total;

            var resultArray = new JArray();

            for (var i = offset; i < actualEnd; i++)
            {
                var projected = projector(items[i]);
                if (fieldsFilter != null && fieldsFilter.Length > 0)
                {
                    projected = ApplyFieldsFilter(projected, fieldsFilter);
                }
                resultArray.Add(projected);
            }

            JObject next = null;
            if (truncated)
            {
                next = new JObject
                {
                    ["offset"] = actualEnd,
                    ["limit"] = limit == int.MaxValue ? (JToken)JValue.CreateNull() : limit
                };
            }

            return new JObject
            {
                ["items"] = resultArray,
                ["truncated"] = truncated,
                ["next"] = next != null ? next : JValue.CreateNull()
            };
        }

        /// <summary>
        /// Parses a comma-separated fields string into an array, e.g. "t,m,f" → ["t","m","f"].
        /// Returns null if the input is null or empty (meaning "no filter").
        /// </summary>
        public static string[] ParseFieldsParam(string fields)
        {
            if (string.IsNullOrWhiteSpace(fields))
                return null;

            var result = fields.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < result.Length; i++)
                result[i] = result[i].Trim();

            return result.Length == 0 ? null : result;
        }

        private static JObject ApplyFieldsFilter(JObject source, string[] allowedKeys)
        {
            var filtered = new JObject();
            var allowed = new HashSet<string>(allowedKeys, StringComparer.Ordinal);
            foreach (var prop in source.Properties())
            {
                if (allowed.Contains(prop.Name))
                    filtered[prop.Name] = prop.Value;
            }
            return filtered;
        }
    }
}
