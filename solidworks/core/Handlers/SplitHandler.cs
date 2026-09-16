using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class SplitHandler {
    public const string TypeName = "Split";

    // SW quirk: feature.GetTypeName2() returns "BodySplit".
    public const string SwTypeName = "BodySplit";

    public static JsonNode Add(File file, JsonNode input) {
        // Phase 1: parse trim_surfaces only. `consume_marked_bodies` and
        // `marked_bodies` are accepted on the wire for shape completeness
        // (an LLM-authored Split() carries them through) but Add ignores
        // them — every commit lands with every PreSplitBody2 candidate
        // marked and consume=false. EditSplit (called immediately after
        // by the runner) applies source's real consume + marked subset.
        var trim = ParseDefinitionArray(input["trim_surfaces"], "Split.trim_surfaces");
        if (trim.Count == 0) {
            throw new ArgumentException("Split: 'trim_surfaces' must contain at least one entry");
        }
        return CommitSplit(file, trim, consume: false,
                           markedBodies: Array.Empty<BodyDefinition>(),
                           inputForResult: input);
    }

    // Re-commit an existing Split with new `consume_marked_bodies` +
    // `marked_bodies` settings. SW 2026 quirk: SetSplitBodies on a committed
    // Split feature is a complete no-op (the FlagVar write is silently
    // dropped even within the same AccessSelections context). The only
    // reliable mutation path is delete + re-commit via PostSplitBody2.
    //
    // `selectAll`: when true, IGNORE `markedBodies` and mark every
    // PreSplitBody2 candidate (the "keep all bodies" case the source Inspect
    // flagged with `select_all: true`). The mark-all is expressed by passing an
    // empty marked-bodies list to CommitSplit, which selects every candidate —
    // the same benign-default path Add uses. Callers pass `selectAll=true` for
    // the common keep-all Split; `selectAll=false` keeps the explicit-subset
    // behavior (resolve each given BodyDefinition against the candidates).
    internal static JsonNode Edit(
        File file, string featureName, bool consumeMarkedBodies,
        IReadOnlyList<BodyDefinition> markedBodies, bool selectAll,
        IReadOnlyList<Ray>? markedBodyRays = null)
    {
        var feature = FindFeatureByName(file, featureName)
            ?? throw new ArgumentException(
                $"EditSplit: no feature named '{featureName}' in the feature tree");
        if (File.ResolveFeatureTypeName(feature) != SwTypeName) {
            throw new InvalidOperationException(
                $"EditSplit: feature '{featureName}' is not a Split feature");
        }

        // Capture trim_surfaces from the live data interface before delete.
        var data = feature.GetDefinition() as ISplitBodyFeatureData
            ?? throw new InvalidOperationException(
                $"EditSplit: feature '{featureName}' does not expose ISplitBodyFeatureData");
        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"EditSplit: AccessSelections failed for '{featureName}'");
        }
        List<Definition> trimDefs;
        try {
            trimDefs = new List<Definition>();
            if (data.TrimTools is object?[] trimObjs) {
                foreach (var t in trimObjs) {
                    var def = CaptureAny(t);
                    if (def is not null) trimDefs.Add(def);
                }
            }
        } finally {
            data.ReleaseSelectionAccess();
        }
        if (trimDefs.Count == 0) {
            throw new InvalidOperationException(
                $"EditSplit: feature '{featureName}' has no trim surfaces (TrimTools empty)");
        }

        // Delete the existing feature.
        feature.Select2(false, -1);
        if (!file.ModelDoc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed)) {
            throw new InvalidOperationException(
                $"EditSplit: failed to delete feature '{featureName}' before re-commit");
        }
        // SW quirk: post-delete the model holds leftover state from the
        // deleted Split — a subsequent PreSplitBody2 returns 0 candidates
        // unless we force the model to fully rebuild first.
        file.ModelDoc.ForceRebuild3(false);

        // Re-commit with the real consume + marked subset. select_all → empty
        // marked list so CommitSplit selects every PreSplitBody2 candidate (the
        // mark-all path); otherwise re-commit the explicit subset.
        IReadOnlyList<BodyDefinition> commitMarked =
            selectAll ? Array.Empty<BodyDefinition>() : markedBodies;
        var synthInput = new JsonObject { ["name"] = featureName };
        var result = CommitSplit(file, trimDefs, consumeMarkedBodies,
                                 commitMarked, inputForResult: synthInput,
                                 markedBodyRays: selectAll ? null : markedBodyRays);
        // (Rollback-bar move now lives in CommitSplit, so it covers Add too.)

        SldworksLog.Information(
            "SplitHandler.Edit: {Name} re-committed via delete+recommit (select_all={SelectAll}, {Marked} marked, consume={Consume})",
            featureName, selectAll, commitMarked.Count, consumeMarkedBodies);
        return result;
    }

    private static Feature? FindFeatureByName(File file, string name) {
        var feat = file.ModelDoc.IFirstFeature() as Feature;
        while (feat is not null) {
            if (feat.Name == name) return feat;
            feat = feat.IGetNextFeature() as Feature;
        }
        return null;
    }

    private static JsonNode CommitSplit(
        File file, IReadOnlyList<Definition> trimSurfaces, bool consume,
        IReadOnlyList<BodyDefinition> markedBodies, JsonNode inputForResult,
        IReadOnlyList<Ray>? markedBodyRays = null)
    {
        // Rebuilding before PreSplitBody2 can lose downstream trim intersections.
        // Keep the incremental body state here; editing after deletion still requires
        // a rebuild before split candidates become available.
        var trimEntities = new List<object>(trimSurfaces.Count);
        foreach (var def in trimSurfaces) {
            var live = DefinitionResolver.Resolve(file, def)
                ?? throw new InvalidOperationException(
                    "Split: trim surface definition did not resolve");
            trimEntities.Add(live);
        }

        file.ModelDoc.ClearSelection2(true);
        foreach (var entity in trimEntities) SelectAsTrimSurface(file, entity);

        var preview = file.ModelDoc.FeatureManager.PreSplitBody2() as object[];
        if (preview is null || preview.Length == 0) {
            throw new InvalidOperationException(
                "Split: PreSplitBody2 returned no candidate bodies — trim surfaces may not " +
                "intersect any solid body.");
        }
        var previewBodies = preview.OfType<Body2>().ToList();

        // Match input markedBodies → PreSplitBody2 candidates using the
        // canonical DefinitionMatch.Approx (mass-props at BodyMassPropertyTol,
        // bbox at GeomApproxTol — same semantics the resolver uses for body
        // identity across source/target rebuilds). Empty marked_bodies → use
        // every candidate (SW rejects empty Bodies arg outright).
        // Rays are the ONLY way to name a marked fragment. There is deliberately no
        // mass-property matching path: fragments are transient, their mass props
        // drift between rebuilds, and a fingerprint that "nearly" matches picks a
        // plausible wrong fragment silently. A ray either resolves or throws
        // (ResolveBodyByRay rejects a >10 mm miss), which is the failure we want.
        //
        // Empty marked set is NOT a fallback — it is the keep-all Split, the
        // benign default Add commits, and SW rejects an empty Bodies arg outright.
        List<Body2> selectedBodies;
        if (markedBodyRays is { Count: > 0 }) {
            selectedBodies = markedBodyRays
                .Select(r => File.ResolveBodyByRay(previewBodies, r))
                .ToList();
            SldworksLog.Information(
                "SplitHandler: selected {N} fragment(s) by ray out of {Total} candidate(s)",
                selectedBodies.Count, previewBodies.Count);
        } else if (markedBodies.Count == 0) {
            selectedBodies = previewBodies;
        } else {
            throw new InvalidOperationException(
                $"Split: {markedBodies.Count} marked body/bodies were supplied without " +
                "marked_body_rays. Mass-property matching was removed — it silently " +
                "picked a wrong fragment when props drifted. Re-Inspect the source " +
                "Split to capture rays.");
        }

        // SW quirk: PostSplitBody2 wants DispatchWrapper[] for `bodies` and
        // `savePath`; in-place case needs DispatchWrapper(null) explicitly,
        // not a bare null array.
        var bodyWrappers = selectedBodies.Select(b => new DispatchWrapper(b)).ToArray();
        var pathWrappers = selectedBodies.Select(_ => new DispatchWrapper(null)).ToArray();
        var pieceNames   = selectedBodies.Select(_ => "").ToArray();

        var feature = (Feature?)file.ModelDoc.FeatureManager.PostSplitBody2(
            bodyWrappers, consume, pathWrappers, pieceNames, "");

        if (feature is null) {
            throw new InvalidOperationException(
                "PostSplitBody2 failed — split could not be committed. " +
                "Trim surfaces may not intersect any solid body.");
        }

        // Move the rollback bar to after the new Split so downstream features
        // insert in the right place. PostSplitBody2 (and, for an Edit re-commit,
        // the preceding DeleteSelection2) doesn't reliably leave the bar after the
        // feature; a wrong bar position desyncs where later features land, which
        // makes the post-split body state — and the body-probes that resolve
        // against it — diverge between builds. Done here in the commit phase so
        // Add is self-sufficient (no edit_split needed for the keep-all case).
        File.ApplyFeatureName(feature, inputForResult);
        file.MoveRollbackBar(
            swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);

        SldworksLog.Information(
            "SplitHandler.CommitSplit: created {Name} ({Selected}/{Pre} candidates selected; consume={Consume})",
            feature.Name, selectedBodies.Count, previewBodies.Count, consume);
        return BuildAddResult(file, inputForResult, feature, captureResultBodies: true);
    }

    private static void SelectAsTrimSurface(File file, object entity) {
        // SW quirk: trim tools are read from the current selection (mark 0).
        // IFace2/IEdge need cast through Entity for Select4; IBody2.Select2 takes SelectData.
        switch (entity) {
            case Face2 f:    ((Entity)f).Select4(true, MakeMarkSelectData(file, 0)); break;
            case Body2 b:    b.Select2(true, MakeMarkSelectData(file, 0)); break;
            case Feature ft: ft.Select2(true, 0); break;
            case Edge e:     ((Entity)e).Select4(true, MakeMarkSelectData(file, 0)); break;
            default:
                throw new InvalidOperationException(
                    $"Split: unsupported trim surface type {entity.GetType().Name}");
        }
    }

    private static SelectData MakeMarkSelectData(File file, int mark) {
        var sd = (SelectData)file.ModelDoc.ISelectionManager.CreateSelectData();
        sd.Mark = mark;
        return sd;
    }

    public static JsonNode Inspect(File file, Feature feature) {
        // SW quirk: data interface is ISplitBodyFeatureData (not ISplitFeatureData2).
        var data = feature.GetDefinition() as ISplitBodyFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose ISplitBodyFeatureData");

        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException($"Split.Inspect: AccessSelections failed for {feature.Name}");
        }
        try {
            var trimSurfaces = new JsonArray();
            if (data.TrimTools is object?[] trimObjs) {
                foreach (var t in trimObjs) {
                    var def = CaptureAny(t);
                    if (def is not null) trimSurfaces.Add(def.ToJson());
                }
            }

            // `marked_bodies`: GetSplitBodies entries with `FlagVar[i] == true`
            // are the fragments PostSplitBody2 acted on (the Bodies arg). With
            // `consume=true` they were removed from the part; with
            // `consume=false` they were materialized as separate part-bodies.
            // Either way, this is the same selection the source author made,
            // and we replay it as-is — the consume flag (independent field)
            // controls the parent-body handling.
            //
            // `select_all`: when EVERY candidate fragment is marked (the benign
            // default the Add itself commits — "keep all bodies"), we don't bake
            // the verbose per-body mass-property fingerprints. The fragments are
            // transient PreSplitBody2 bodies with no stable geometry to probe on
            // the target side, so the round-trip can't swap them for `probe(...)`
            // calls the way Cut discards can. Instead we flag the all-marked case
            // and let EditSplit re-mark every candidate. `total` is the count of
            // candidate fragments (every Body2 in GetSplitBodies, marked or not),
            // matched against `markedCount` so select_all is true iff none were
            // left unmarked.
            var marked = new JsonArray();
            var markedCount = 0;
            var total = 0;
            var candidates = new List<Body2>();
            var markedLive = new List<Body2>();
            data.GetSplitBodies(out var bodyObjsRaw, out _, out var flagsRaw);
            if (bodyObjsRaw is object[] bodyObjs && flagsRaw is bool[] flags) {
                for (var i = 0; i < bodyObjs.Length && i < flags.Length; i++) {
                    if (bodyObjs[i] is not Body2 b) continue;
                    total++;
                    candidates.Add(b);
                    if (!flags[i]) continue;
                    markedCount++;
                    markedLive.Add(b);
                    marked.Add(DefinitionCapture.Capture(b).ToJson());
                }
            }

            // A RAY per marked fragment, aimed at it and away from its siblings.
            // The fragments are transient — they exist only inside AccessSelections
            // here and inside PreSplitBody2 on the Add side — so neither side can
            // hold a durable handle to one. A ray does not need one: it names a
            // place, and the target re-fires it against ITS candidates at the
            // equivalent moment. Same trick as the Cut-discard path
            // (BodiesToKeepScope.ProbeBodiesToDiscard).
            //
            // This is also what an author can write. A mass-property fingerprint
            // is not: nobody types a centroid and a surface area to say "that
            // fragment".
            var markedRays = new JsonArray();
            foreach (var mb in markedLive) {
                markedRays.Add(File.SerializeRay(
                    File.BuildBodyProbeAgainstSiblings(mb, candidates)));
            }
            // select_all only when there's at least one candidate and all are
            // marked. A zero-candidate Split should never happen (PostSplitBody2
            // would have failed at Add), but guard against `total == 0` so an
            // empty/degenerate read doesn't claim "select all".
            var selectAll = total > 0 && markedCount == total;

            return new JsonObject {
                ["type"]                  = TypeName,
                ["name"]                  = feature.Name,
                ["trim_surfaces"]         = trimSurfaces,
                ["consume_marked_bodies"] = data.Consume,
                // When select_all, omit the body fingerprints — EditSplit re-marks
                // every candidate from `select_all` alone. Keep the shape stable
                // (empty array, not absent) so the Add/FromJson path and the diff
                // harness see a consistent `marked_bodies` key on both sides.
                // `marked_body_rays` is the authorable selector and what Add
                // prefers. `marked_bodies` stays as the descriptive fallback for
                // older payloads and for the diff harness; when rays are present
                // Add never reads it.
                ["marked_body_rays"]      = selectAll ? new JsonArray() : markedRays,
                ["marked_bodies"]         = selectAll ? new JsonArray() : marked,
                ["select_all"]            = selectAll,
            };
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private static Definition? CaptureAny(object? entity) {
        // Vertex is rejected: not a valid trim tool, so Inspect must not emit one.
        return entity switch {
            null         => null,
            Face2 f      => DefinitionCapture.Capture(f),
            Edge e       => DefinitionCapture.Capture(e),
            Body2 b      => DefinitionCapture.Capture(b),
            Feature feat => DefinitionCapture.Capture(feat),
            Vertex      => throw new InvalidOperationException(
                "Inspect(Split): trim_surfaces contains a Vertex — vertices aren't valid trim tools."),
            _ => throw new InvalidOperationException(
                $"Inspect(Split): trim_surfaces entry of type {entity.GetType().Name} not recognized"),
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

    private static List<Definition> ParseDefinitionArray(JsonNode? node, string fieldName) {
        if (node is not JsonArray arr) return [];
        var result = new List<Definition>(arr.Count);
        for (var i = 0; i < arr.Count; i++) {
            var def = Definition.FromJson(arr[i])
                ?? throw new ArgumentException($"{fieldName}[{i}]: malformed definition");
            result.Add(def);
        }
        return result;
    }
}
