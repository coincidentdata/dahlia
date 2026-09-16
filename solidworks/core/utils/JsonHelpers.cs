using System.Text.Json.Nodes;

namespace Sldworks.Core.Utils;

// Strongly-typed reads off a wire JsonNode payload. Throws on missing required fields rather
// than silently defaulting — the wire format is the contract, and a feature handler that
// quietly receives a default value where one wasn't authored produces the wrong geometry.
internal static class JsonHelpers {
    internal static double ReadDouble(JsonNode node, string field) {
        var v = node[field] ?? throw new ArgumentException($"Missing required numeric field '{field}'");
        return v.GetValue<double>();
    }

    internal static double? ReadOptionalDouble(JsonNode node, string field) {
        var v = node[field];
        return v is null ? null : v.GetValue<double>();
    }

    internal static bool ReadBool(JsonNode node, string field, bool fallback) {
        var v = node[field];
        return v is null ? fallback : v.GetValue<bool>();
    }
}
