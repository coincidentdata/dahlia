using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class RibHandler {
    public const string TypeName = "Rib";
    public const string SwTypeName = "Rib";

    public static JsonNode Add(File file, JsonNode input) {
        var args = Args.Parse(input);
        var sketch = ResolveSketch(file, args.Sketch);
        file.ModelDoc.ClearSelection2(true);
        if (!sketch.Select2(false, 0)) throw new InvalidOperationException("Rib: cannot select sketch");
        var previous = (Feature?)file.ModelDoc.FeatureByPositionReverse(0);
        file.ModelDoc.FeatureManager.InsertRib(args.TwoSided, args.ReverseThickness, args.Thickness,
            args.ReferenceSegment, args.Flipped, args.DraftAngle > 0, args.DraftOutward,
            args.DraftAngle, args.Direction == 1, args.DraftFromWall);
        var feature = (Feature?)file.ModelDoc.FeatureByPositionReverse(0);
        if (feature is null || feature.GetTypeName2() != SwTypeName || feature.Name == previous?.Name)
            throw new InvalidOperationException("Rib: creation failed; check sketch, thickness and material direction");
        Apply(file, feature, args);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return Inspect(file, feature);
    }

    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = Args.Parse(input);
        if (SketchName(file, feature) != args.Sketch)
            throw new NotSupportedException("Rib: changing the driving sketch requires a new feature; edit the existing sketch in place");
        Apply(file, feature, args);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        return Inspect(file, feature);
    }

    private static void Apply(File file, Feature feature, Args args) {
        var data = (IRibFeatureData2)feature.GetDefinition();
        if (!data.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Rib: cannot access selections");
        try {
            if (args.Body is not null) data.Body = DefinitionResolver.Resolve(file, args.Body) as Body2
                ?? throw new ArgumentException("Rib: body did not resolve");
            data.IsTwoSided = args.TwoSided;
            data.ReverseThicknessDir = args.ReverseThickness;
            data.Thickness = args.Thickness;
            data.FlipSide = args.Flipped;
            data.ExtrusionDirection = args.Direction;
            if (args.Direction == 1) data.Type = args.Extension;
            data.RefSketchIndex = args.ReferenceSegment;
            data.EnableDraft = args.DraftAngle > 0;
            if (args.DraftAngle > 0) {
                data.DraftAngle = args.DraftAngle;
                data.DraftOutward = args.DraftOutward;
                data.DraftFromWall = args.DraftFromWall;
            }
            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) throw new InvalidOperationException("Rib: edit failed");
        } finally { data.ReleaseSelectionAccess(); }
    }

    public static JsonNode Inspect(File file, Feature feature) {
        var data = (IRibFeatureData2)feature.GetDefinition();
        if (!data.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Rib: cannot access selections");
        try {
            var direction = data.ExtrusionDirection switch {
                0 => "ParallelToSketch", 1 => "NormalToSketch", var d => throw new NotSupportedException($"Rib: unknown direction {d}"),
            };
            return new JsonObject {
                ["type"] = TypeName, ["name"] = feature.Name,
                ["sketch"] = new JsonObject { ["type"] = "Sketch", ["name"] = SketchName(file, feature) },
                ["thickness"] = data.Thickness, ["two_sided"] = data.IsTwoSided,
                ["reverse_thickness"] = !data.IsTwoSided && data.ReverseThicknessDir,
                ["flipped"] = data.FlipSide, ["direction"] = direction,
                ["extension"] = data.ExtrusionDirection == 0 ? "Linear" : data.Type switch {
                    0 => "Linear", 1 => "Natural", var t => throw new NotSupportedException($"Rib: unknown extension {t}"),
                },
                ["reference_segment"] = data.RefSketchIndex,
                ["draft_angle"] = data.EnableDraft ? data.DraftAngle : 0,
                ["draft_outward"] = data.EnableDraft && data.DraftOutward,
                ["draft_from_wall"] = data.EnableDraft && data.DraftFromWall,
                ["body"] = data.Body is { } body ? DefinitionCapture.Capture(body)?.ToJson()
                    ?? throw new NotSupportedException("Rib: cannot capture body") : null,
            };
        } finally { data.ReleaseSelectionAccess(); }
    }

    private static Feature ResolveSketch(File file, string name) {
        var feature = (Feature?)((IPartDoc)file.ModelDoc).FeatureByName(name);
        return feature?.GetSpecificFeature2() is Sketch ? feature : throw new ArgumentException($"Rib: sketch '{name}' did not resolve");
    }
    private static string SketchName(File file, Feature feature) {
        for (var child = feature.GetFirstSubFeature() as Feature; child is not null; child = child.GetNextSubFeature() as Feature)
            if (child.GetSpecificFeature2() is Sketch) return FeatureName.Resolve(file, child.Name);
        throw new NotSupportedException("Rib: driving sketch not found");
    }

    private sealed record Args(string Sketch, double Thickness, bool TwoSided, bool ReverseThickness,
        bool Flipped, int Direction, int Extension, int ReferenceSegment, double DraftAngle,
        bool DraftOutward, bool DraftFromWall, Definition? Body) {
        public static Args Parse(JsonNode input) {
            var sketch = input["sketch"]?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(sketch)) throw new ArgumentException("Rib: sketch name is required");
            var thickness = input["thickness"]?.GetValue<double>() ?? 0;
            var angle = input["draft_angle"]?.GetValue<double>() ?? 0;
            if (!double.IsFinite(thickness) || thickness <= 0) throw new ArgumentException("Rib: thickness must be positive and finite");
            if (!double.IsFinite(angle) || angle < 0 || angle >= Math.PI / 2) throw new ArgumentException("Rib: draft angle must be in [0, pi/2)");
            var twoSided = input["two_sided"]?.GetValue<bool>() ?? true;
            var reverse = input["reverse_thickness"]?.GetValue<bool>() ?? false;
            if (twoSided && reverse) throw new ArgumentException("Rib: reverse_thickness requires a single-sided rib");
            var direction = (input["direction"]?.GetValue<string>() ?? "ParallelToSketch") switch {
                "ParallelToSketch" => 0, "NormalToSketch" => 1, var d => throw new ArgumentException($"Rib: unknown direction {d}"),
            };
            var extension = (input["extension"]?.GetValue<string>() ?? "Linear") switch {
                "Linear" => 0, "Natural" => 1, var t => throw new ArgumentException($"Rib: unknown extension {t}"),
            };
            if (direction == 0 && extension != 0) throw new ArgumentException("Rib: Natural extension requires NormalToSketch");
            var reference = input["reference_segment"]?.GetValue<int>() ?? 0;
            if (reference < 0) throw new ArgumentException("Rib: reference_segment must be nonnegative");
            var outward = input["draft_outward"]?.GetValue<bool>() ?? false;
            var fromWall = input["draft_from_wall"]?.GetValue<bool>() ?? false;
            if (angle == 0 && (outward || fromWall)) throw new ArgumentException("Rib: draft options require a positive draft_angle");
            var body = input["body"] is { } node ? Definition.FromJson(node) ?? throw new ArgumentException("Rib: invalid body") : null;
            return new Args(sketch, thickness, twoSided, reverse, input["flipped"]?.GetValue<bool>() ?? false,
                direction, extension, reference, angle, outward, fromWall, body);
        }
    }
}
