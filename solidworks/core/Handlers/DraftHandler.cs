using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class DraftHandler {
    public const string TypeName = "Draft";
    public const string SwTypeName = "Draft";
    private static readonly string[] Propagations = ["None", "Tangent", "AllLoops", "InnerLoops", "OuterLoops"];

    public static JsonNode Add(File file, JsonNode input) {
        var args = Args.Parse(input);
        file.ModelDoc.ClearSelection2(true);
        foreach (var edge in args.Edges) Select(file, Resolve<Edge>(file, edge.Edge), 4);
        foreach (var face in args.Faces) Select(file, Resolve<Face2>(file, face), 2);
        foreach (var direction in ResolveDirection(file, args)) Select(file, direction, 1);
        var feature = file.ModelDoc.FeatureManager.InsertMultiFaceDraft(args.Angle, args.Reversed,
            args.Kind != 0, args.Propagation, args.Kind == 3, false)
            ?? throw new InvalidOperationException("Draft: creation failed; check the selected faces, direction and angle");
        Apply(file, feature, args);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return Inspect(file, feature);
    }

    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        Apply(file, feature, Args.Parse(input));
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        return Inspect(file, feature);
    }

    private static void Apply(File file, Feature feature, Args args) {
        var data = (IDraftFeatureData2)feature.GetDefinition();
        if (Kind(data) != args.Kind) throw new NotSupportedException("Draft: changing draft kind requires a new feature");
        if (!data.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Draft: cannot access selections");
        try {
            var direction = ResolveDirection(file, args).Select(SpecificPlane).ToArray();
            if (args.Kind == 0) {
                data.NeutralPlane = direction[0];
                data.FacesToDraft = Wrap(args.Faces.Select(d => Resolve<Face2>(file, d)));
            } else {
                data.DirectionPull = direction.Length == 1 ? direction[0] : Wrap(direction);
                data.PartingLines = Wrap(args.Edges.Select(e => Resolve<Edge>(file, e.Edge)));
                for (short i = 0; i < args.Edges.Length; i++) data.SetOtherFacesFlagAtIndex(i, args.Edges[i].OtherFace);
            }
            data.Angle = args.Angle;
            data.ReverseDirection = args.Reversed;
            data.FacePropagation = args.Propagation;
            if (args.Kind == 3) data.StepType = args.StepType;
            if (args.Kind == 1) data.AllowReducedAngle = args.AllowReducedAngle;
            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) throw new InvalidOperationException("Draft: edit failed");
        } finally { data.ReleaseSelectionAccess(); }
    }

    public static JsonNode Inspect(File file, Feature feature) {
        var data = (IDraftFeatureData2)feature.GetDefinition();
        if (!data.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Draft: cannot access selections");
        try {
            var nativeKind = Kind(data);
            var kind = nativeKind switch { 0 => "NeutralPlane", 1 => "PartingLine", _ => "Step" };
            var lines = new JsonArray();
            if (nativeKind != 0) {
                var edges = (object[]?)data.PartingLines ?? [];
                for (short i = 0; i < edges.Length; i++) lines.Add(new JsonObject {
                    ["edge"] = Capture(edges[i]), ["other_face"] = data.GetOtherFacesFlagAtIndex(i),
                });
            }
            var propagation = data.FacePropagation;
            if (propagation < 0 || propagation >= Propagations.Length) throw new NotSupportedException($"Draft: unknown propagation {propagation}");
            return new JsonObject {
                ["type"] = TypeName, ["name"] = feature.Name, ["kind"] = kind,
                ["angle"] = data.Angle, ["reversed"] = data.ReverseDirection,
                ["neutral_plane"] = nativeKind == 0 ? Capture(data.NeutralPlane) : null,
                ["faces"] = nativeKind == 0 ? CaptureArray(data.FacesToDraft) : new JsonArray(),
                ["direction"] = nativeKind == 0 ? null : Capture(data.DirectionPull),
                ["parting_lines"] = lines, ["propagation"] = Propagations[propagation],
                ["step_type"] = nativeKind == 3 ? data.StepType switch { 3 => "Tapered", 6 => "Perpendicular", var s => throw new NotSupportedException($"Draft: unknown step type {s}") } : "Tapered",
                ["allow_reduced_angle"] = nativeKind == 1 && data.AllowReducedAngle,
            };
        } finally { data.ReleaseSelectionAccess(); }
    }

    private static int Kind(IDraftFeatureData2 data) => data.Type switch {
        0 => 0, 1 or -1 => 1, 3 => 3, var k => throw new NotSupportedException($"Draft: unknown type {k}"),
    };
    private static object SpecificPlane(object live) => live is Feature f && f.GetTypeName2() == "RefPlane" ? f.GetSpecificFeature2() : live;
    private static object[] ResolveDirection(File file, Args args) {
        var definitions = args.Kind == 0 ? new[] { args.NeutralPlane! } : args.Direction;
        var objects = definitions.Select(d => DefinitionResolver.Resolve(file, d)
            ?? throw new ArgumentException("Draft: direction did not resolve")).ToArray();
        if (objects.Length == 2) {
            if (objects.Any(o => o is not Vertex)) throw new ArgumentException("Draft: a direction pair must contain two vertices");
        } else if (objects[0] is not (Face2 or Edge or Feature or RefPlane or RefAxis)) {
            throw new ArgumentException("Draft: unsupported direction reference");
        }
        return objects;
    }
    private static T Resolve<T>(File file, Definition definition) where T : class => DefinitionResolver.Resolve(file, definition) as T
        ?? throw new ArgumentException($"Draft: selection must resolve to {typeof(T).Name}");
    private static void Select(File file, object live, int mark) {
        if (!Definition.SelectLive(file, live, mark)) throw new InvalidOperationException("Draft: selection failed");
    }
    private static DispatchWrapper[] Wrap(IEnumerable<object> objects) => objects.Select(o => new DispatchWrapper(o)).ToArray();
    private static JsonArray CaptureArray(object? value) => new(((object[]?)value ?? []).Select(Capture).ToArray());
    private static JsonNode Capture(object value) => value is object[] ? CaptureArray(value)
        : DefinitionCapture.Capture(value)?.ToJson() ?? throw new NotSupportedException("Draft: cannot capture reference");

    private sealed record DraftLine(Definition Edge, bool OtherFace);
    private sealed record Args(int Kind, double Angle, bool Reversed, Definition? NeutralPlane, Definition[] Faces,
        Definition[] Direction, DraftLine[] Edges, short Propagation, short StepType, bool AllowReducedAngle) {
        public static Args Parse(JsonNode input) {
            static Definition Ref(JsonNode? node) => Definition.FromJson(node) ?? throw new ArgumentException("Draft: invalid reference");
            var kind = (input["kind"]?.GetValue<string>() ?? "NeutralPlane") switch {
                "NeutralPlane" => 0, "PartingLine" => 1, "Step" => 3, var k => throw new ArgumentException($"Draft: unknown kind {k}"),
            };
            var angle = input["angle"]?.GetValue<double>() ?? 0;
            if (!double.IsFinite(angle) || angle <= 0 || angle >= Math.PI / 2) throw new ArgumentException("Draft: angle must be between 0 and pi/2 radians");
            var neutral = input["neutral_plane"] is { } plane ? Ref(plane) : null;
            var faces = Definition.FromJsonArray(input["faces"] ?? new JsonArray(), "Draft.faces").ToArray();
            var direction = input["direction"] is JsonArray directions ? directions.Select(Ref).ToArray()
                : input["direction"] is { } d ? new[] { Ref(d) } : [];
            var edges = (input["parting_lines"]?.AsArray() ?? []).Select(n => new DraftLine(Ref(n?["edge"]), n?["other_face"]?.GetValue<bool>() ?? false)).ToArray();
            if (kind == 0 ? neutral is null || faces.Length == 0 || direction.Length > 0 || edges.Length > 0
                          : neutral is not null || faces.Length > 0 || direction.Length is < 1 or > 2 || edges.Length == 0)
                throw new ArgumentException("Draft: selections do not match the requested draft kind");
            var propagation = Array.IndexOf(Propagations, input["propagation"]?.GetValue<string>() ?? "None");
            if (propagation < 0) throw new ArgumentException("Draft: unknown propagation");
            short step = (input["step_type"]?.GetValue<string>() ?? "Tapered") switch {
                "Tapered" => 3, "Perpendicular" => 6, var s => throw new ArgumentException($"Draft: unknown step type {s}"),
            };
            if (kind != 3 && step != 3) throw new ArgumentException("Draft: step_type requires Step draft");
            if (kind != 1 && input["allow_reduced_angle"]?.GetValue<bool>() == true)
                throw new ArgumentException("Draft: allow_reduced_angle requires PartingLine draft");
            return new Args(kind, angle, input["reversed"]?.GetValue<bool>() ?? false, neutral, faces,
                direction, edges, (short)propagation, step, input["allow_reduced_angle"]?.GetValue<bool>() ?? false);
        }
    }
}
