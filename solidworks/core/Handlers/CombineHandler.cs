using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class CombineHandler {
    public const string TypeName = "Combine";

    // SW quirk: feature.GetTypeName2() returns "BodyOperation".
    public const string SwTypeName = "BodyOperation";

    // Wire says "Common" for Intersect, matching the SolidWorks UI label.
    // SW quirk: ICombineBodiesFeatureData.OperationType returns 0/1/2 (logical), NOT
    // the swBodyOperationType_e values (15901..15903). InsertCombineFeature also accepts
    // the 0/1/2 form.
    private enum BoolOp {
        Add       = 0,
        Subtract  = 1,
        Common    = 2,
    }

    private static int ToSwConst(BoolOp op) => op switch {
        BoolOp.Add      => (int)swBodyOperationType_e.SWBODYADD,
        BoolOp.Subtract => (int)swBodyOperationType_e.SWBODYCUT,
        BoolOp.Common   => (int)swBodyOperationType_e.SWBODYINTERSECT,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    public static JsonNode Add(File file, JsonNode input) {
        var args = CombineArgs.Parse(input);

        // Resolve up-front so we fail before touching selection state.
        var toolBodies = ResolveBodies(file, args.Tools, "Combine.bodies");
        Body2? mainBody = null;
        if (args.Operation == BoolOp.Subtract) {
            if (args.MainBody is null) {
                throw new ArgumentException("Combine: 'main_body' required when operation is 'Subtract'");
            }
            mainBody = ResolveBody(file, args.MainBody, "Combine.main_body");
        }

        file.ModelDoc.ClearSelection2(true);
        if (args.Operation == BoolOp.Subtract) {
            // SW quirk: subtract uses mark 1 for main, mark 2 for tools. Uniform
            // decreasing-mark rule applies here: tools (mark 2) first, main (mark 1) last.
            foreach (var b in toolBodies) b.Select2(true, MakeMarkSelectData(file, 2));
            mainBody!.Select2(true, MakeMarkSelectData(file, 1));
        } else {
            // Add and Common: tools on mark 2. Mark 0 gets cleared by SW's typical
            // default-selection housekeeping; mark 2 is the documented operand mark
            // for InsertCombineFeature.
            foreach (var b in toolBodies) b.Select2(true, MakeMarkSelectData(file, 2));
        }

        Feature? feature;
        try {
            feature = (Feature?)file.ModelDoc.FeatureManager.InsertCombineFeature(
                ToSwConst(args.Operation), null, null);
        } finally {
            file.ModelDoc.ClearSelection2(true);
        }

        if (feature is null) {
            // SW quirk: returns null on non-intersecting operands, single-tool Add, or
            // tools sharing a face exactly with the main body.
            throw new InvalidOperationException(
                "InsertCombineFeature failed — operands may not overlap, " +
                "or only one body is selected. Verify body definitions resolve to " +
                "distinct overlapping solids.");
        }

        SldworksLog.Information("CombineHandler.Add({Op}): created {Name}",
            args.Operation, feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return BuildAddResult(file, input, feature, captureResultBodies: true);
    }

    // Edit an existing Combine IN PLACE (GetDefinition -> AccessSelections -> mutate ->
    // ModifyDefinition); we do NOT delete + re-add, so the feature name — and every
    // downstream name-ref / probe — survives. `input` is a FULL combine payload (same
    // shape Inspect emits / Add consumes), parsed by CombineArgs so edit and create share
    // one validation. Applies the non-selection scalar (OperationType) AND re-points the
    // body SELECTIONS (MainBody + ToolBodies) when the incoming refs DIFFER from what's
    // built — matching what Add resolves+selects. Unchanged selections are left untouched.
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = CombineArgs.Parse(input);

        var data = feature.GetDefinition() as ICombineBodiesFeatureData
            ?? throw new InvalidOperationException(
                $"Combine.Edit: feature {feature.Name} ({feature.GetTypeName2()}) does not expose ICombineBodiesFeatureData");

        // SW quirk: AccessSelections required before reading/writing MainBody /
        // BodiesToCombine — and the live OperationType is only meaningful within that scope.
        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException($"Combine.Edit: AccessSelections failed for {feature.Name}");
        }
        try {
            var currentOp = (BoolOp)data.OperationType;

            // Subtract is the only operation that designates a distinct main body
            // (mark 1) separate from the tools (mark 2). Crossing the Subtract boundary
            // re-partitions main-body vs tools, which this in-place edit cannot encode —
            // the operands would have to migrate between the MainBody slot and the
            // BodiesToCombine array. EXCLUDED — delete + re-add instead. (Re-pointing the
            // operands onto DIFFERENT bodies of the SAME partition is in scope, below.)
            if ((args.Operation == BoolOp.Subtract) != (currentOp == BoolOp.Subtract)) {
                throw new InvalidOperationException(
                    $"Combine.Edit: operation {OperationName(currentOp)} -> {OperationName(args.Operation)} on "
                    + $"'{feature.Name}' can't be changed in place — Subtract designates a separate main body, "
                    + "so the body selections must be re-pointed — delete and re-add.");
            }

            // EDIT: the add/subtract/common operation.
            // SW quirk: OperationType is the logical 0/1/2 enum, NOT swBodyOperationType_e.
            data.OperationType = (int)args.Operation;

            // RE-POINT the body selections (only when the incoming refs differ from the
            // built ones). MainBody applies only to Subtract; ToolBodies (BodiesToCombine)
            // apply to every operation.
            if (args.Operation == BoolOp.Subtract) {
                RepointMainBodyIfChanged(file, data, args.MainBody, feature.Name);
            }
            RepointToolBodiesIfChanged(file, data, args.Tools, feature.Name);

            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"Combine.Edit: ModifyDefinition returned false on '{feature.Name}' — "
                    + "the operation may be invalid against the body selections.");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        // Roll to end so the change propagates downstream (ForceRebuildAll). NOT
        // AfterFeature: a mid-tree combine must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information("CombineHandler.Edit: {Name}", feature.Name);
        return Inspect(file, feature);
    }

    // Re-point a Subtract combine's MAIN body in place. The incoming def arrives in the Edit
    // payload (CombineArgs.MainBody, the SAME ref Add resolves+selects). Resolve it to a live
    // Body2 and set the data interface's MainBody property directly, inside the open
    // AccessSelections block, before ModifyDefinition — unconditionally (assume the payload is
    // authoritative; re-applying the same body is idempotent, so scalar-only edits are unaffected).
    private static void RepointMainBodyIfChanged(
        File file, ICombineBodiesFeatureData data, BodyDefinition? mainDef, string featureName) {
        if (mainDef is null) {
            // CombineArgs.Parse already requires main_body for Subtract; defensive.
            throw new InvalidOperationException(
                $"Combine.Edit: Subtract combine '{featureName}' requires 'main_body'.");
        }

        var requested = ResolveBody(file, mainDef, $"Combine.Edit.main_body on '{featureName}'");

        // SW quirk: MainBody is a scalar dispatch property (set_MainBody(Body2)) — a single
        // live Body2, NOT a SAFEARRAY, so no DispatchWrapper (mirrors Chamfer's IVertex).
        data.MainBody = requested;
        SldworksLog.Information(
            "CombineHandler.Edit: applied main body of '{Name}'", featureName);
    }

    // Re-point the TOOL bodies (BodiesToCombine) in place. The incoming defs arrive in the Edit
    // payload (CombineArgs.Tools, the SAME refs Add resolves+selects). Resolve each to a live
    // Body2 and set the data interface's BodiesToCombine property directly, inside the open
    // AccessSelections block, before ModifyDefinition — unconditionally (assume the payload is
    // authoritative; re-applying the same set is idempotent, so scalar-only edits are unaffected).
    private static void RepointToolBodiesIfChanged(
        File file, ICombineBodiesFeatureData data, IReadOnlyList<BodyDefinition> toolDefs, string featureName) {
        if (toolDefs.Count == 0) {
            // CombineArgs.Parse already rejects empty tools; defensive.
            throw new InvalidOperationException(
                $"Combine.Edit: '{featureName}' has an empty tool-bodies list.");
        }

        var resolvedLive = new List<Body2>(toolDefs.Count);
        for (var i = 0; i < toolDefs.Count; i++) {
            resolvedLive.Add(ResolveBody(file, toolDefs[i], $"Combine.Edit.bodies[{i}] on '{featureName}'"));
        }

        // SW quirk: BodiesToCombine's setter takes Object (a SAFEARRAY-of-IDispatch); like
        // every other SAFEARRAY-of-IDispatch feature-data setter (Chamfer.Edges, Fillet
        // .HoldLines, SetBodiesToKeep) it wants DispatchWrapper[] — a bare object[] crashes the
        // marshaller. The getter hands back object[] of Body2, so wrapped live bodies round-trip.
        data.BodiesToCombine = resolvedLive.Select(b => new DispatchWrapper(b)).ToArray();
        SldworksLog.Information(
            "CombineHandler.Edit: applied '{Name}' onto {Count} tool body(ies)",
            featureName, resolvedLive.Count);
    }

    public static JsonNode Inspect(File file, Feature feature) {
        // SW quirk: data interface is ICombineBodiesFeatureData (not ICombineFeatureData).
        var data = feature.GetDefinition() as ICombineBodiesFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose ICombineBodiesFeatureData");

        // SW quirk: AccessSelections required before reading MainBody / BodiesToCombine.
        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException($"Combine.Inspect: AccessSelections failed for {feature.Name}");
        }
        try {
            var op = (BoolOp)data.OperationType;

            var tools = new JsonArray();
            if (data.BodiesToCombine is object?[] toolObjs) {
                foreach (var t in toolObjs) {
                    if (t is Body2 b) tools.Add(DefinitionCapture.Capture(b).ToJson());
                }
            }

            var result = new JsonObject {
                ["type"]      = TypeName,
                ["name"]      = feature.Name,
                ["operation"] = OperationName(op),
                ["bodies"]    = tools,
            };

            if (op == BoolOp.Subtract && data.MainBody is Body2 mainBody) {
                result["main_body"] = DefinitionCapture.Capture(mainBody).ToJson();
            }

            return result;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private static List<Body2> ResolveBodies(File file, IReadOnlyList<BodyDefinition> defs, string fieldName) {
        var bodies = new List<Body2>(defs.Count);
        for (var i = 0; i < defs.Count; i++) {
            bodies.Add(ResolveBody(file, defs[i], $"{fieldName}[{i}]"));
        }
        return bodies;
    }

    private static Body2 ResolveBody(File file, BodyDefinition def, string fieldName) {
        var live = DefinitionResolver.Resolve(file, def);
        if (live is not Body2 b) {
            throw new InvalidOperationException(
                $"{fieldName}: BodyDefinition did not resolve to a Body2 (got {live?.GetType().Name ?? "null"})");
        }
        return b;
    }

    private static string OperationName(BoolOp op) => op switch {
        BoolOp.Add      => "Add",
        BoolOp.Subtract => "Subtract",
        BoolOp.Common   => "Common",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
    };

    private static BoolOp ParseOperation(string s) {
        return s switch {
            "Add"          => BoolOp.Add,
            "Subtract"     => BoolOp.Subtract,
            "Common"       => BoolOp.Common,
            _ => throw new ArgumentException(
                $"Combine: unknown operation '{s}' (expected Add | Subtract | Common)"),
        };
    }

    private static JsonNode BuildAddResult(File file, JsonNode input, Feature feature, bool captureResultBodies) {
        var output = input.DeepClone();
        output["name"] = feature.Name;
        if (captureResultBodies) {
            output["echo_bodies_result"] = CaptureCurrentSolidBodies(file);
        }
        return output;
    }

    private static JsonArray CaptureCurrentSolidBodies(File file) {
        // SW quirk: GetBodies2 lives on IPartDoc, not IModelDoc2 — cast to reach it.
        var arr = new JsonArray();
        var raw = ((IPartDoc)file.ModelDoc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
        if (raw is null) return arr;
        foreach (Body2 body in raw) {
            arr.Add(DefinitionCapture.Capture(body).ToJson());
        }
        return arr;
    }

    private static SelectData MakeMarkSelectData(File file, int mark) {
        var sd = (SelectData)file.ModelDoc.ISelectionManager.CreateSelectData();
        sd.Mark = mark;
        return sd;
    }

    private sealed record CombineArgs(
        BoolOp Operation,
        BodyDefinition? MainBody,
        IReadOnlyList<BodyDefinition> Tools) {

        internal static CombineArgs Parse(JsonNode input) {
            var opName = input["operation"]?.GetValue<string>()
                ?? throw new ArgumentException("Combine: missing 'operation'");
            var op = ParseOperation(opName);

            var tools = ParseBodyArray(input["bodies"], "Combine.bodies");
            BodyDefinition? main = null;
            if (op == BoolOp.Subtract) {
                var mainNode = input["main_body"]
                    ?? throw new ArgumentException("Combine.Subtract: missing 'main_body'");
                main = Definition.FromJson(mainNode) as BodyDefinition
                    ?? throw new ArgumentException("Combine.main_body: not a body definition");
            }
            if (tools.Count == 0) {
                throw new ArgumentException($"Combine.{op}: 'bodies' must contain at least one body");
            }
            return new CombineArgs(op, main, tools);
        }
    }

    private static List<BodyDefinition> ParseBodyArray(JsonNode? node, string fieldName) {
        if (node is not JsonArray arr) return [];
        var result = new List<BodyDefinition>(arr.Count);
        for (var i = 0; i < arr.Count; i++) {
            var def = Definition.FromJson(arr[i])
                ?? throw new ArgumentException($"{fieldName}[{i}]: malformed definition");
            if (def is not BodyDefinition bd) {
                throw new ArgumentException(
                    $"{fieldName}[{i}]: expected body definition, got {def.GetType().Name}");
            }
            result.Add(bd);
        }
        return result;
    }
}
