using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

// Projected Curve = a 3D curve produced by projecting a 2D sketch onto a set of
// faces ("Sketch on Faces") or by intersecting the projections of two sketches
// ("Sketch on Sketch"). Mode is implicit on the wire: `faces` set ⇒ Sketch on
// Faces, `second_sketch` set ⇒ Sketch on Sketch. Exactly one of the two is
// required.
//
// SW quirk: GetTypeName2() returns "RefCurve" for projected curves AND for
// other reference curves (composite, imported). The discriminator is the
// typed feature data; only projected curves expose IProjectionCurveFeatureData.
public static class ProjectedCurveHandler {
    public const string TypeName = "ProjectedCurve";
    public const string SwTypeName = "RefCurve";

    // SW-documented selection marks for projection curves.
    private const int MarkTargetFaces = 1;
    private const int MarkSketchToProject = 2;
    private const int MarkTargetSketch = 4;
    private const int MarkProjectionDirection = 8;

    // ---- Add -----------------------------------------------------------------------

    public static JsonNode Add(File file, JsonNode input) {
        var args = ProjectedCurveArgs.Parse(input);

        file.ModelDoc.ClearSelection2(true);

        // Sequence selections in strictly decreasing mark order: projection
        // direction (8) → target sketch (4) → sketch to project (2) →
        // target faces (1).
        //
        // SW quirk: ProjectionCurveFeatureData.Sketch always returns the
        // mark-2 entry on read, in BOTH SketchOnFaces and SketchOnSketch
        // modes — verified empirically via post-Add inspect (SW API docs
        // are silent on this in SketchOnSketch mode). The wire field
        // `sketch` mirrors `data.Sketch`, so route it to mark 2 in both
        // modes; the second sketch (when present) goes to mark 4 and is
        // recovered on the Inspect side from the FaceArray.
        if (args.ProjectionDirection is not null) {
            if (!args.ProjectionDirection.Select(file, MarkProjectionDirection)) {
                throw new InvalidOperationException(
                    $"ProjectedCurve: projection_direction " +
                    $"({args.ProjectionDirection.GetType().Name}) did not resolve+select " +
                    $"onto mark {MarkProjectionDirection}");
            }
        }

        if (args.SecondSketchName is not null) {
            var second = FindFeatureByName(file, args.SecondSketchName)
                ?? throw new ArgumentException(
                    $"ProjectedCurve: second sketch '{args.SecondSketchName}' not found");
            second.Select2(true, MarkTargetSketch);
        }

        var sketchFeat = FindFeatureByName(file, args.SketchName)
            ?? throw new ArgumentException(
                $"ProjectedCurve: source sketch '{args.SketchName}' not found");
        sketchFeat.Select2(true, MarkSketchToProject);

        if (args.Faces is not null) {
            Definition.SelectAll(file, args.Faces, MarkTargetFaces, "ProjectedCurve.faces");
        }

        // SW-documented authoring path: CreateDefinition(swFmRefCurve) →
        // populate non-entity properties → CreateFeature.
        var fm = file.ModelDoc.FeatureManager;
        var data = fm.CreateDefinition((int)swFeatureNameID_e.swFmRefCurve) as ProjectionCurveFeatureData
            ?? throw new InvalidOperationException(
                "ProjectedCurve: CreateDefinition(swFmRefCurve) did not return a ProjectionCurveFeatureData");

        data.Bidirectional = args.Bidirectional;
        data.Reverse = args.Reverse;

        var feature = fm.CreateFeature(data) as Feature
            ?? throw new InvalidOperationException(
                "ProjectedCurve: CreateFeature returned null — verify the source sketch " +
                "intersects the target(s) when projected normal to its plane.");

        SldworksLog.Information("ProjectedCurveHandler.Add: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    // ---- Edit ----------------------------------------------------------------------
    //
    // No in-place Edit: SW 2026's modify-definition operation CRASHES the process (RPC
    // dies mid-call) on a projected curve. THREE distinct
    // commit paths were tried, all crash at the same point — so it's the underlying SW
    // operation for RefCurve reference geometry that's unsupported, NOT the selection-access
    // or the COM binding:
    //   1. Feature.ModifyDefinition (Object/late-bound) WITH AccessSelections
    //   2. Feature.ModifyDefinition (Object/late-bound) WITHOUT AccessSelections (Helix-style)
    //   3. Feature.IModifyDefinition2 + IAccessSelections2 (fully-typed ModelDoc2 vtable)
    // The FeatEditDef + RunCommand(Ok) dialog route can't help (re-binds selections only; no
    // API to toggle reverse/bidirectional). Delete+re-add loses identity and fails anyway on
    // a mid-tree curve with dependents. The only editable surface is two direction flags on a
    // construction curve (no CheckBench value), so ProjectedCurve is left out of EditFeature
    // dispatch (clean "not supported"). Closed — do not retry.


    // ---- Inspect -------------------------------------------------------------------

    public static JsonNode Inspect(File file, Feature feature) {
        var data = feature.GetDefinition() as ProjectionCurveFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose " +
                "IProjectionCurveFeatureData (the feature may be a composite curve / " +
                "imported curve / split-line, which round-trip via other handlers).");

        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"ProjectedCurve.Inspect({feature.Name}): AccessSelections failed");
        }
        try {
            var sketchFeat = data.Sketch as Feature
                ?? throw new InvalidOperationException(
                    $"ProjectedCurve.Inspect({feature.Name}): source Sketch was null");
            var sketchName = FeatureName.Resolve(file, sketchFeat.Name);

            var faceCount = data.GetFaceArrayCount();
            var rawTargets = faceCount > 0
                ? (data.IGetFaceArray(faceCount) as object[] ?? [])
                : [];

            var faces = new JsonArray();
            string? secondSketchName = null;
            foreach (var item in rawTargets) {
                if (item is null) continue;
                if (item is Feature secondFeat) {
                    // SketchOnSketch — first non-null Feature in FaceArray is the
                    // second sketch. The redist may also expose it as a sub-feature
                    // of the projection (we don't need that path while FaceArray works).
                    secondSketchName = FeatureName.Resolve(file, secondFeat.Name);
                    break;
                }
                if (item is Face2 face) {
                    faces.Add(DefinitionCapture.Capture(face).ToJson());
                } else {
                    throw new InvalidOperationException(
                        $"ProjectedCurve.Inspect({feature.Name}): unsupported FaceArray entry " +
                        $"of type {item.GetType().Name}");
                }
            }

            // SW quirk: when FaceArray is empty (count=0) but the projection
            // is still SketchOnSketch, recover the second sketch from the
            // feature's sub-feature tree. The projection's sub-features are
            // its two source sketches; the one matching `data.Sketch` is the
            // projector, the other is the target sketch.
            if (faces.Count == 0 && secondSketchName is null) {
                var sub = feature.GetFirstSubFeature() as Feature;
                while (sub is not null) {
                    if (sub.GetTypeName2() == "ProfileFeature" && sub.Name != sketchFeat.Name) {
                        secondSketchName = FeatureName.Resolve(file, sub.Name);
                        break;
                    }
                    sub = sub.IGetNextSubFeature() as Feature;
                }
            }

            if (secondSketchName is null && faces.Count == 0) {
                throw new InvalidOperationException(
                    $"ProjectedCurve.Inspect({feature.Name}): could not resolve either target " +
                    "faces or a second sketch — the feature may have lost its references.");
            }

            var result = new JsonObject {
                ["type"] = TypeName,
                ["name"] = feature.Name,
                ["sketch"] = new JsonObject {
                    ["type"] = "Sketch",
                    ["name"] = sketchName,
                },
                ["bidirectional"] = data.Bidirectional,
                ["reverse"] = data.Reverse,
            };
            if (secondSketchName is not null) {
                result["second_sketch"] = new JsonObject {
                    ["type"] = "Sketch",
                    ["name"] = secondSketchName,
                };
                result["faces"] = new JsonArray();
            } else {
                result["faces"] = faces;
                result["second_sketch"] = null;
            }
            return result;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    // SW quirk: IModelDoc2 has no FeatureByName accessor; walk the feature tree.
    private static Feature? FindFeatureByName(File file, string name) {
        var feature = file.ModelDoc.IFirstFeature() as Feature;
        while (feature is not null) {
            if (feature.Name == name) return feature;
            feature = feature.IGetNextFeature() as Feature;
        }
        return null;
    }

    // ---- Typed args ----------------------------------------------------------------

    private sealed record ProjectedCurveArgs(
        string SketchName,
        IReadOnlyList<Definition>? Faces,
        string? SecondSketchName,
        Definition? ProjectionDirection,
        bool Bidirectional,
        bool Reverse) {

        internal static ProjectedCurveArgs Parse(JsonNode input) {
            var sketchName = input["sketch"]?["name"]?.GetValue<string>()
                ?? throw new ArgumentException("ProjectedCurve: missing 'sketch.name'");

            List<Definition>? faces = null;
            if (input["faces"] is JsonArray fArr) {
                faces = new List<Definition>();
                foreach (var node in fArr) {
                    if (node is null) continue;
                    var d = Definition.FromJson(node)
                        ?? throw new ArgumentException("ProjectedCurve: faces entry is not a Definition");
                    faces.Add(d);
                }
                if (faces.Count == 0) faces = null;
            }

            string? secondSketchName = null;
            if (input["second_sketch"] is { } ss
                && ss.GetValueKind() != System.Text.Json.JsonValueKind.Null) {
                secondSketchName = ss["name"]?.GetValue<string>()
                    ?? throw new ArgumentException("ProjectedCurve: second_sketch.name missing");
            }

            if ((faces is null) == (secondSketchName is null)) {
                throw new ArgumentException(
                    "ProjectedCurve: exactly one of `faces` (non-empty) or `second_sketch` " +
                    "must be provided");
            }

            Definition? projDir = null;
            if (input["projection_direction"] is { } pd
                && pd.GetValueKind() != System.Text.Json.JsonValueKind.Null) {
                projDir = Definition.FromJson(pd);
            }

            var bidirectional = ReadBool(input, "bidirectional", false);
            var reverse = ReadBool(input, "reverse", false);

            return new ProjectedCurveArgs(
                sketchName, faces, secondSketchName, projDir, bidirectional, reverse);
        }
    }

    private static bool ReadBool(JsonNode node, string field, bool fallback) {
        var v = node[field];
        return v is null ? fallback : v.GetValue<bool>();
    }
}
