using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class ShellHandler {
    public const string TypeName = "Shell";
    public const string SwTypeName = "Shell";

    public static JsonNode Add(File file, JsonNode input) {
        var args = Args.Parse(input);
        file.ModelDoc.ClearSelection2(true);
        foreach (var wall in args.Walls) SelectFace(file, wall.Face, 2);
        foreach (var face in args.Faces) SelectFace(file, face, 1);
        foreach (var wall in args.Walls) file.ModelDoc.InsertFeatureShellAddThickness(wall.Thickness);
        var previous = (Feature?)file.ModelDoc.FeatureByPositionReverse(0);
        file.ModelDoc.InsertFeatureShell(args.Thickness, args.Outward);
        var feature = (Feature?)file.ModelDoc.FeatureByPositionReverse(0);
        if (feature is null || feature.GetTypeName2() != SwTypeName || feature.Name == previous?.Name)
            throw new InvalidOperationException("Shell: creation failed; check face selections and wall thicknesses");
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return Inspect(file, feature);
    }

    public static JsonNode Inspect(File file, Feature feature) {
        var data = (IShellFeatureData)feature.GetDefinition();
        if (!data.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Shell: cannot access selections");
        try {
            var walls = new JsonArray();
            var faces = (object[]?)data.MultipleThicknessFaces ?? [];
            for (var i = 0; i < faces.Length; i++) walls.Add(new JsonObject {
                ["face"] = Capture(faces[i]), ["thickness"] = data.GetMultipleThicknessAtIndex(i),
            });
            return new JsonObject {
                ["type"] = TypeName, ["name"] = feature.Name, ["thickness"] = data.Thickness,
                ["outward"] = data.Direction switch { 0 => false, 1 or -1 => true, var d => throw new NotSupportedException($"Shell: unknown direction {d}") },
                ["faces"] = new JsonArray(((object[]?)data.FacesRemoved ?? []).Select(Capture).ToArray()),
                ["face_thicknesses"] = walls,
            };
        } finally { data.ReleaseSelectionAccess(); }
    }

    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = Args.Parse(input);
        var data = (IShellFeatureData)feature.GetDefinition();
        if (args.Walls.Count != data.GetMultipleThicknessFacesCount())
            throw new NotSupportedException("Shell: changing the number of thickness overrides during edit is not yet supported by the integration; specify them when creating the shell");
        if (args.Faces.Count == 0 && data.FacesRemovedCount > 0)
            throw new NotSupportedException("Shell: closing the last opening during edit is not yet supported by the integration; create a closed shell with faces=[]");
        if (!data.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Shell: cannot access selections");
        try {
            data.FacesRemoved = args.Faces.Select(d => new DispatchWrapper(ResolveFace(file, d))).ToArray();
            data.MultipleThicknessFaces = args.Walls.Select(w => new DispatchWrapper(ResolveFace(file, w.Face))).ToArray();
            data.Thickness = args.Thickness;
            data.Direction = args.Outward ? 1 : 0;
            for (var i = 0; i < args.Walls.Count; i++) data.SetMultipleThicknessAtIndex(i, args.Walls[i].Thickness);
            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) throw new InvalidOperationException("Shell: edit failed");
        } finally { data.ReleaseSelectionAccess(); }
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        return Inspect(file, feature);
    }

    private static Face2 ResolveFace(File file, Definition definition) => DefinitionResolver.Resolve(file, definition) as Face2
        ?? throw new ArgumentException("Shell: selection must resolve to a face");

    private static void SelectFace(File file, Definition definition, int mark) {
        if (!Definition.SelectLive(file, ResolveFace(file, definition), mark)) throw new InvalidOperationException("Shell: face selection failed");
    }

    private static JsonNode Capture(object face) => DefinitionCapture.Capture(face)?.ToJson()
        ?? throw new NotSupportedException("Shell: cannot capture face");

    private sealed record Wall(Definition Face, double Thickness);
    private sealed record Args(double Thickness, bool Outward, IReadOnlyList<Definition> Faces, IReadOnlyList<Wall> Walls) {
        public static Args Parse(JsonNode input) {
            static double Thickness(JsonNode? node) {
                var value = node?.GetValue<double>() ?? 0;
                return double.IsFinite(value) && value > 0 ? value : throw new ArgumentException("Shell: thickness must be positive and finite");
            }
            static Definition Face(JsonNode? node) => Definition.FromJson(node)
                ?? throw new ArgumentException("Shell: invalid face definition");
            return new Args(Thickness(input["thickness"]), input["outward"]?.GetValue<bool>() ?? false,
                Definition.FromJsonArray(input["faces"] ?? new JsonArray(), "Shell.faces"),
                (input["face_thicknesses"]?.AsArray() ?? []).Select(n => new Wall(Face(n?["face"]), Thickness(n?["thickness"]))).ToArray());
        }
    }
}
