using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class RefAxisHandler {
    public const string TypeName = "RefAxis";
    public const string SwTypeName = "RefAxis";

    // ---- Add ---------------------------------------------------------------

    // SolidWorks swRefAxisType_e values, named for the wire.
    private enum RefAxisKind {
        Edge        = 0,   // swRAOneEdge
        TwoPlanes   = 1,   // swRATwoPlane
        TwoPoints   = 2,   // swRATwoPoint
        Cylindrical = 3,   // swRACylFace
        PointFace   = 4,   // swRAPtFace
    }

    public static JsonNode Add(File file, JsonNode input) {
        var args = RefAxisArgs.Parse(input);
        if (args.References.Count == 0)
            throw new ArgumentException("RefAxis: at least one reference is required");
        if (args.References.Count > 2)
            throw new ArgumentException("RefAxis: at most two references are supported");

        var expected = args.AxisType switch {
            RefAxisKind.Edge        => 1,
            RefAxisKind.Cylindrical => 1,
            RefAxisKind.TwoPlanes   => 2,
            RefAxisKind.TwoPoints   => 2,
            RefAxisKind.PointFace   => 2,
            _ => throw new ArgumentException($"RefAxis: unknown axis_type {args.AxisType}"),
        };
        if (args.References.Count != expected) {
            throw new ArgumentException(
                $"RefAxis ({args.AxisType}): expected {expected} reference(s), got {args.References.Count}");
        }

        file.ModelDoc.ClearSelection2(true);

        for (var i = 0; i < args.References.Count; i++) {
            var def = args.References[i];
            var live = DefinitionResolver.Resolve(file, def)
                ?? throw new InvalidOperationException(
                    $"RefAxis: reference[{i}] ({def.GetType().Name}) did not resolve to a live entity");
            // SW quirk: InsertAxis2 reads selection as a flat list; mark is irrelevant.
            SelectAtMark(file, live, mark: 0);
        }

        // SW quirk: only the auto-detect form InsertAxis2(bool) is exposed (no typed overload).
        // Auto-detect can pick wrong on ambiguous entities, so we read back
        // IRefAxisFeatureData.Type and fix via ModifyDefinition if it disagrees.
        if (!file.ModelDoc.InsertAxis2(true)) {
            throw new InvalidOperationException(
                $"InsertAxis2 failed for axis_type {args.AxisType} — geometry incompatible with the requested axis kind");
        }

        // SW quirk: InsertAxis2 returns bool — recover the new feature by scanning
        // FeatureManager for the most recent RefAxis.
        var feature = FindMostRecentFeatureByTypeName(file, SwTypeName)
            ?? throw new InvalidOperationException(
                "InsertAxis2 returned true but no RefAxis feature was found in the FeatureManager tree");

        EnsureAxisType(file, feature, args.AxisType);

        SldworksLog.Information("RefAxisHandler.Add: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return BuildAddResult(input, feature);
    }

    private static void EnsureAxisType(File file, Feature feature, RefAxisKind requested) {
        var data = feature.GetDefinition() as IRefAxisFeatureData;
        if (data is null) return;
        if (!data.AccessSelections(file.ModelDoc, null)) {
            return;
        }
        try {
            var current = (RefAxisKind)data.Type;
            if (current == requested) return;
            data.Type = (int)requested;
            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"RefAxis: ModifyDefinition rejected axis_type={requested} (auto-detected as {current}) — "
                    + "geometry is incompatible with the requested axis kind");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private static Feature? FindMostRecentFeatureByTypeName(File file, string typeName) {
        var first = file.ModelDoc.FirstFeature() as Feature;
        Feature? current = first;
        Feature? mostRecent = null;
        while (current is not null) {
            if (current.GetTypeName2() == typeName) {
                mostRecent = current;
            }
            current = current.GetNextFeature() as Feature;
        }
        return mostRecent;
    }

    // ---- Inspect -----------------------------------------------------------

    public static JsonNode Inspect(File file, Feature feature) {
        var data = feature.GetDefinition() as IRefAxisFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose IRefAxisFeatureData");

        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException($"RefAxis.Inspect: AccessSelections failed for {feature.Name}");
        }

        try {
            // SW quirk: GetSelections returns a Variant (cast to object[]) and emits a
            // selection-mark out-param we ignore.
            var raw = data.GetSelections(out _) as object[];
            var kind = (RefAxisKind)data.Type;
            // GetSelections reverses the two-plane construction order, which determines axis direction.
            if (kind == RefAxisKind.TwoPlanes && raw is not null) Array.Reverse(raw);
            var references = new JsonArray();
            if (raw is not null) {
                foreach (var sel in raw) {
                    if (sel is null) continue;
                    references.Add(DefinitionCapture.Capture(sel)?.ToJson()
                        ?? throw new InvalidOperationException(
                            $"RefAxis.Inspect: reference is unsupported runtime type {sel.GetType().Name}"));
                }
            }

            return new JsonObject {
                ["type"] = TypeName,
                ["name"] = feature.Name,
                ["references"] = references,
                ["axis_type"] = kind.ToString(),
            };
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    // ---- Selection helpers -------------------------------------------------

    private static void SelectAtMark(File file, object live, int mark) {
        // Delegate to Definition.SelectLive — central place for IFace2/IEdge/IVertex via IEntity,
        // SketchSegment/SketchPoint direct, Body2.Select2, Feature.Select2 quirks. SketchSegment
        // and SketchPoint must be included because a TwoPoints RefAxis built from sketch
        // endpoints is a real authoring case.
        if (!Definition.SelectLive(file, live, mark)) {
            throw new InvalidOperationException(
                $"RefAxis: SW rejected selection of {live.GetType().Name} on mark {mark}");
        }
    }

    // ---- Result shape ------------------------------------------------------

    private static JsonNode BuildAddResult(JsonNode input, Feature feature) {
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    // ---- Typed args --------------------------------------------------------

    private sealed record RefAxisArgs(
        List<Definition> References,
        RefAxisKind AxisType,
        string? Name) {

        internal static RefAxisArgs Parse(JsonNode input) {
            var refs = ParseReferences(input["references"]);
            var typeStr = input["axis_type"]?.GetValue<string>()
                ?? throw new ArgumentException("RefAxis: 'axis_type' is required");
            if (!Enum.TryParse<RefAxisKind>(typeStr, ignoreCase: false, out var kind)) {
                throw new ArgumentException(
                    $"RefAxis: unknown axis_type '{typeStr}' (expected Edge, TwoPoints, TwoPlanes, Cylindrical, or PointFace)");
            }
            var name = input["name"]?.GetValue<string>();
            return new RefAxisArgs(refs, kind, name);
        }
    }

    private static List<Definition> ParseReferences(JsonNode? node) {
        if (node is not JsonArray arr) {
            throw new ArgumentException("RefAxis: 'references' must be a JSON array");
        }
        var list = new List<Definition>(arr.Count);
        for (var i = 0; i < arr.Count; i++) {
            var def = Definition.FromJson(arr[i])
                ?? throw new ArgumentException($"RefAxis: references[{i}] is not a valid Definition");
            list.Add(def);
        }
        return list;
    }
}
