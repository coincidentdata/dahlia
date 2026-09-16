using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;

namespace Sldworks.Core.Handlers.Shared;

internal enum ExtrudeStartKind {
    Sketch = 0,
    Surface = 1,
    Vertex = 2,
    Offset = 3,
}

// SW quirk: Surface / Vertex references for extrude start condition go on selection mark 32.
internal sealed record ExtrudeStartArgs(
    ExtrudeStartKind Kind,
    Definition? Reference,
    double Offset,
    bool Reversed) {

    internal static ExtrudeStartArgs Default { get; } =
        new(ExtrudeStartKind.Sketch, null, 0.0, false);

    internal static ExtrudeStartArgs Parse(JsonNode? node) {
        if (node is null) return Default;
        var typeName = node["type"]?.GetValue<string>();
        if (string.IsNullOrEmpty(typeName)) return Default;
        switch (typeName) {
            case "Sketch":
                return Default;
            case "Surface": {
                var refDef = Definition.FromJson(node["reference"])
                    ?? throw new ArgumentException("start.Surface: 'reference' missing or malformed");
                return new ExtrudeStartArgs(ExtrudeStartKind.Surface, refDef, 0.0, false);
            }
            case "Vertex": {
                var refDef = Definition.FromJson(node["reference"])
                    ?? throw new ArgumentException("start.Vertex: 'reference' missing or malformed");
                return new ExtrudeStartArgs(ExtrudeStartKind.Vertex, refDef, 0.0, false);
            }
            case "Offset": {
                var offset = node["offset"]?.GetValue<double>()
                    ?? throw new ArgumentException("start.Offset: 'offset' is required");
                var reversed = node["reversed"]?.GetValue<bool>() ?? false;
                return new ExtrudeStartArgs(ExtrudeStartKind.Offset, null, offset, reversed);
            }
            default:
                throw new ArgumentException(
                    $"Extrude: unknown start.type '{typeName}' (expected Sketch | Surface | Vertex | Offset)");
        }
    }
}
