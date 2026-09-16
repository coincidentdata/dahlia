using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class LinearPatternHandler {
    public const string TypeName = "LinearPattern";

    // SW quirk: Feature.GetTypeName2() returns "LPattern" for linear feature patterns.
    public const string SwTypeName = "LPattern";

    // SW quirk: D2 end-ref mark (2^22) contradicts API docs but matches observed behavior.
    private const int MarkDirectionA = 1;
    private const int MarkDirectionB = 2;
    private const int MarkD1EndReference = 2097152;     // 2^21
    private const int MarkD1StartReference = 8388608;   // 2^23
    private const int MarkD2EndReference = 4194304;     // 2^22
    private const int MarkD2StartReference = 33554432;  // 2^25

    public static JsonNode Add(File file, JsonNode input) {
        var args = LinearPatternArgs.Parse(input);

        file.ModelDoc.ClearSelection2(true);

        var hasB = args.DirectionB is not null
                   && (args.EndConditionB is not null || args.CountB is > 1);

        // Sequence selections in strictly decreasing mark order across all slots:
        // cond_b start (2^25) → cond_a start (2^23) → cond_b end (2^22) → cond_a end (2^21)
        // → seeds (mixed marks 4/256/1) → direction_b (2) → direction_a (1).
        if (args.EndConditionB is UpToReferenceCondition upB && upB.StartReference is not null) {
            var startLive = DefinitionResolver.Resolve(file, upB.StartReference)
                ?? throw new InvalidOperationException("LinearPattern: end_condition_b.start_reference did not resolve");
            SelectAt(file, startLive, MarkD2StartReference, "end_condition_b.start_reference");
        }
        if (args.EndConditionA is UpToReferenceCondition upAStart && upAStart.StartReference is not null) {
            var startLive = DefinitionResolver.Resolve(file, upAStart.StartReference)
                ?? throw new InvalidOperationException("LinearPattern: end_condition_a.start_reference did not resolve");
            SelectAt(file, startLive, MarkD1StartReference, "end_condition_a.start_reference");
        }
        if (args.EndConditionB is UpToReferenceCondition upBEnd) {
            var endLive = DefinitionResolver.Resolve(file, upBEnd.EndReference)
                ?? throw new InvalidOperationException("LinearPattern: end_condition_b.end_reference did not resolve");
            SelectAt(file, endLive, MarkD2EndReference, "end_condition_b.end_reference");
        }
        if (args.EndConditionA is UpToReferenceCondition upAEnd) {
            var endLive = DefinitionResolver.Resolve(file, upAEnd.EndReference)
                ?? throw new InvalidOperationException("LinearPattern: end_condition_a.end_reference did not resolve");
            SelectAt(file, endLive, MarkD1EndReference, "end_condition_a.end_reference");
        }

        PatternInstances.SelectSeeds(file, args.Seeds);

        if (hasB) {
            PatternInstances.SelectDirection(file, args.DirectionB!, MarkDirectionB, "direction_b");
        }
        PatternInstances.SelectDirection(file, args.DirectionA, MarkDirectionA, "direction_a");

        SelectionDebug.Log(file, "LinearPattern.Add pre-CreateFeature");

        // SW quirk: `FeatureLinearPattern5` is the wizard-style API — its inline args
        // (`HasOffset1`, `Offset1`, `D2PatternSeedOnly`, `CtrlByNum1`, …) get baked into
        // the initial create, then a post-create `ModifyDefinition` flip would have to
        // overwrite them. SW computes the body geometry from the *initial* values, then
        // recomputes after the modify — and the geometry can settle to a slightly
        // different state than if the data were correct from the start (downstream
        // `BodyDefinition` resolves miss because centroid/volume drift past 1e-7).
        // The data-driven path used here — `CreateDefinition(swFmLPattern)` → populate
        // every field on the `LinearPatternFeatureData` upfront → `CreateFeature(data)`
        // — has SW see one consistent state and compute the body once.
        var fm = file.ModelDoc.FeatureManager;
        var data = fm.CreateDefinition((int)swFeatureNameID_e.swFmLPattern) as LinearPatternFeatureData
            ?? throw new InvalidOperationException(
                "LinearPattern.Add: CreateDefinition(swFmLPattern) did not return LinearPatternFeatureData");

        ApplyEndConditionA(data, args);
        ApplyEndConditionB(data, args, hasB);

        data.GeometryPattern = args.GeometryPattern;
        data.VarySketch = args.VarySketch;
        if (hasB) data.D2PatternSeedOnly = args.PatternSeedOnly;

        SldworksLog.Information(
            "LinearPattern.Add: CreateFeature D1End={D1E} D1Spacing={D1S} D1Count={D1C} D1Rev={D1R} D2End={D2E} D2Spacing={D2S} D2Count={D2C} D2Rev={D2R} hasB={HB} geomPattern={GP} varySketch={VS} patternSeedOnly={PSO}",
            (swPatternEndCondition_e)data.D1EndCondition, data.D1Spacing, data.D1TotalInstances, data.D1ReverseDirection,
            hasB ? ((swPatternEndCondition_e)data.D2EndCondition).ToString() : "(unused)",
            hasB ? data.D2Spacing : 0, hasB ? data.D2TotalInstances : 0, hasB && data.D2ReverseDirection,
            hasB, args.GeometryPattern, args.VarySketch, args.PatternSeedOnly);

        var feature = fm.CreateFeature(data) as Feature
            ?? throw new InvalidOperationException(
                "LinearPattern.Add: CreateFeature returned null; check direction_a / seeds / end-condition refs resolved");

        PatternInstances.ApplySkippedInstances(feature, args.Deleted, "LinearPattern", file);

        SldworksLog.Information("LinearPatternHandler.Add: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return PatternInstances.BuildAddResult(input, feature);
    }

    // Populate D1-* fields from args.
    private static void ApplyEndConditionA(LinearPatternFeatureData data, LinearPatternArgs args) {
        data.D1ReverseDirection = args.ReversedA;
        switch (args.EndConditionA) {
            case UpToReferenceCondition upA:
                data.D1EndCondition = (int)swPatternEndCondition_e.swPatternEndCondition_UpToReference;
                if (upA.StartReference is not null) data.D1EndUseSeedReference = true;
                data.D1EndRefOffset = upA.Offset;
                data.D1EndRefReverseOffset = upA.OffsetReversed;
                if (upA.Mode == UpToRefMode.Spacing) {
                    data.D1EndUseSpacing = true;
                    data.D1Spacing = upA.Value;
                } else {
                    data.D1EndUseSpacing = false;
                    data.D1TotalInstances = (int)upA.Value;
                }
                break;
            case SpacingAndInstancesCondition s:
                data.D1EndCondition = (int)swPatternEndCondition_e.swPatternEndCondition_SpacingAndInstances;
                data.D1Spacing = s.Spacing;
                data.D1TotalInstances = s.Count;
                break;
            default:
                // Plain spacing/count when no explicit end-condition shape on the wire.
                data.D1EndCondition = (int)swPatternEndCondition_e.swPatternEndCondition_SpacingAndInstances;
                data.D1Spacing = args.SpacingA;
                data.D1TotalInstances = args.CountA;
                break;
        }
    }

    private static void ApplyEndConditionB(LinearPatternFeatureData data, LinearPatternArgs args, bool hasB) {
        if (!hasB) return;
        data.D2ReverseDirection = args.ReversedB;
        switch (args.EndConditionB) {
            case UpToReferenceCondition upB:
                data.D2EndCondition = (int)swPatternEndCondition_e.swPatternEndCondition_UpToReference;
                if (upB.StartReference is not null) data.D2EndUseSeedReference = true;
                data.D2EndRefOffset = upB.Offset;
                data.D2EndRefReverseOffset = upB.OffsetReversed;
                if (upB.Mode == UpToRefMode.Spacing) {
                    data.D2EndUseSpacing = true;
                    data.D2Spacing = upB.Value;
                } else {
                    data.D2EndUseSpacing = false;
                    data.D2TotalInstances = (int)upB.Value;
                }
                break;
            case SpacingAndInstancesCondition s:
                data.D2EndCondition = (int)swPatternEndCondition_e.swPatternEndCondition_SpacingAndInstances;
                data.D2Spacing = s.Spacing;
                data.D2TotalInstances = s.Count;
                break;
            default:
                data.D2EndCondition = (int)swPatternEndCondition_e.swPatternEndCondition_SpacingAndInstances;
                data.D2Spacing = args.SpacingB ?? 0.0;
                data.D2TotalInstances = args.CountB ?? 1;
                break;
        }
    }

    private static void SelectAt(File file, object live, int mark, string label) {
        // Delegate to Definition.SelectLive — covers Edge/Face2/Vertex via IEntity,
        // SketchSegment / SketchPoint direct, Body2.Select2, Feature.Select2.
        // SketchPoint and Body2 are valid up-to-X end-condition references.
        if (!Definition.SelectLive(file, live, mark)) {
            throw new InvalidOperationException(
                $"LinearPattern {label}: SW rejected selection of {live.GetType().Name} on mark {mark}");
        }
    }

    // Edit an existing linear pattern IN PLACE (GetDefinition -> AccessSelections ->
    // re-point selections + mutate scalars -> ModifyDefinition); we do NOT delete + re-add,
    // so the feature name — and every downstream name-ref / probe — survives. `input` is a
    // FULL LinearPattern payload (same shape Inspect emits / Add consumes), parsed by
    // LinearPatternArgs so edit and create share one validation.
    //
    // Selection parity with Add: the SEED features/bodies and the D1/D2 direction (axis)
    // references ARE re-pointed when the wire supplies refs that DIFFER from the built
    // selection (resolve+capture both sides to canonical Definition JSON and multiset-compare
    // — same idiom as ChamferHandler.RepointEdgesIfChanged). An unchanged selection (the
    // common scalar-only edit) is never disturbed. The end-condition reference selections
    // (UpToReference end/start refs) are kept as built — re-pointing those is an excluded
    // end-condition-KIND-class change (delete + re-add).
    //
    // ApplyEndConditionA/B are ADD-only helpers (they take the concrete create-time
    // LinearPatternFeatureData and, for UpToReference, mark a fresh selection); the live
    // edit sets the data fields directly off the ILinearPatternFeatureData so we touch
    // exactly what Inspect reads and never re-mark the end-reference selection.
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = LinearPatternArgs.Parse(input);

        var data = feature.GetDefinition() as ILinearPatternFeatureData
            ?? throw new InvalidOperationException(
                $"LinearPattern.Edit: feature {feature.Name} ({feature.GetTypeName2()}) does not expose ILinearPatternFeatureData");

        // Vary-sketch patterns carry per-instance dimension overrides that Inspect already
        // rejects on the wire; there is nothing on the wire to edit them with.
        if (data.InstancesToVary) {
            throw new InvalidOperationException(
                $"LinearPattern.Edit: '{feature.Name}' is an InstancesToVary (vary-sketch) pattern — not editable on the wire.");
        }

        data.AccessSelections(file.ModelDoc, null);
        try {
            // D2 presence is fixed by the built selection (D2Axis). The wire toggling a
            // second direction on/off would re-point the axis selection set, which is NOT
            // an in-place edit — refuse it; only the D2 scalars are editable when D2 exists.
            var hasD2Built = data.D2Axis is not null;
            var hasD2Wire = args.DirectionB is not null
                && (args.EndConditionB is not null || args.CountB is > 1);
            if (hasD2Built != hasD2Wire) {
                throw new InvalidOperationException(
                    $"LinearPattern.Edit: cannot add/remove the second direction on '{feature.Name}' "
                    + $"(built hasB={hasD2Built}, wire hasB={hasD2Wire}); that re-points the axis selection — "
                    + "delete and re-add.");
            }

            // SELECTION PARITY: re-point seeds + direction axes when the wire differs from
            // what's built. Do this INSIDE the open AccessSelections block, before the scalar
            // sets and the single ModifyDefinition commit, so the re-pointed refs and the new
            // scalars settle in one consistent rebuild.
            RepointSeedsIfChanged(file, data, args.Seeds, feature.Name);
            RepointAxisIfChanged(
                file, args.DirectionA,
                live => data.D1Axis = live,
                "direction_a", feature.Name);
            if (hasD2Built) {
                RepointAxisIfChanged(
                    file, args.DirectionB!,
                    live => data.D2Axis = live,
                    "direction_b", feature.Name);
            }

            ApplyEndConditionScalarsA(data, args, feature.Name);
            data.D1ReverseDirection = args.ReversedA;

            if (hasD2Built) {
                ApplyEndConditionScalarsB(data, args, feature.Name);
                data.D2ReverseDirection = args.ReversedB;
                data.D2PatternSeedOnly = args.PatternSeedOnly;
            }

            data.GeometryPattern = args.GeometryPattern;
            data.VarySketch = args.VarySketch;

            // Skipped instances are parametric (index list into the instance grid), so they
            // can be re-applied in place alongside count/spacing. Set the array directly in
            // this same ModifyDefinition rather than a second commit.
            data.SkippedItemArray = args.Deleted.ToArray();

            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"LinearPattern.Edit: ModifyDefinition returned false on '{feature.Name}' — "
                    + "count/spacing/skipped-instances invalid against the local geometry?");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        // Roll to end so the change propagates downstream (ForceRebuildAll). NOT
        // AfterFeature: a mid-tree pattern must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information("LinearPatternHandler.Edit: {Name}", feature.Name);
        return Inspect(file, feature);
    }

    // Re-point the pattern SEED set (features/bodies) in place to the wire's seeds. The incoming
    // defs arrive in the Edit payload (LinearPatternArgs.Seeds, the SAME refs Add resolves+selects
    // via PatternInstances.SelectSeeds). Here — unlike Add (select-then-CreateFeature) — we resolve
    // them and set the data interface's PatternFeatureArray / PatternBodyArray directly, inside the
    // open AccessSelections block, before ModifyDefinition. The resolved refs are applied
    // UNCONDITIONALLY (no compare-and-skip): re-applying the same refs is idempotent, so a
    // scalar-only edit is unaffected, and ModifyDefinition's bool return is the validity gate.
    // Inspect reads seeds from PatternFeatureArray + PatternBodyArray (PatternFaceArray is always
    // empty on swFmLPattern); this writes exactly those two slots.
    private static void RepointSeedsIfChanged(
        File file, ILinearPatternFeatureData data, IReadOnlyList<Definition> seedDefs, string featureName) {
        var (features, bodies) = ResolveSeeds(file, seedDefs, featureName);

        // Unconditionally re-apply the resolved seeds: ModifyDefinition's bool return is the
        // validity gate. Re-applying the same refs is idempotent, so a scalar-only edit is
        // unaffected. (No compare-and-skip — a null-skip silently dropped re-points.)

        // SW quirk: PatternFeatureArray / PatternBodyArray are Object (SAFEARRAY-of-IDispatch)
        // setters — DispatchWrapper[] for the marshaller; a bare object[] crashes it (same as
        // Chamfer's Edges / Fillet's HoldLines). Reflection-confirmed setters on
        // ILinearPatternFeatureData; Inspect's CaptureSeeds reads back the same two slots.
        // Empty seed sets aren't possible (LinearPatternArgs requires >= 1 seed), but each
        // slot may legitimately be empty (all-feature or all-body seed sets); write null for
        // an empty slot to clear any stale entries.
        data.PatternFeatureArray = features.Count > 0
            ? features.Select(f => new DispatchWrapper(f)).ToArray()
            : null;
        data.PatternBodyArray = bodies.Count > 0
            ? bodies.Select(b => new DispatchWrapper(b)).ToArray()
            : null;
        SldworksLog.Information(
            "LinearPatternHandler.Edit: re-pointed '{Name}' seeds onto {Feat} feature(s) + {Body} body(ies)",
            featureName, features.Count, bodies.Count);
    }

    // Re-point a direction (D1/D2) axis reference in place to the wire's axis. The incoming def
    // arrives in the Edit payload (LinearPatternArgs.DirectionA/B, the SAME ref Add resolves+selects
    // via PatternInstances.SelectDirection). Here we resolve it and set the data interface's
    // D1Axis / D2Axis dispatch property directly, UNCONDITIONALLY (no compare-and-skip): re-applying
    // the same ref is idempotent, and ModifyDefinition's bool return is the validity gate. The old
    // `current is null` skip silently dropped the re-point when the built axis read back null (e.g.
    // a sketch-line axis) — see RefPlane/Revolve axis-guard bug.
    private static void RepointAxisIfChanged(
        File file, Definition axisDef,
        Action<object?> setAxis, string label, string featureName) {
        var live = DefinitionResolver.Resolve(file, axisDef)
            ?? throw new InvalidOperationException(
                $"LinearPattern.Edit: {label} ({axisDef.GetType().Name}) did not resolve to a "
                + $"live entity on '{featureName}'.");

        // Unconditionally re-apply the resolved axis: ModifyDefinition's bool return is the
        // validity gate. Re-applying the same ref is idempotent, so a scalar-only edit is
        // unaffected. (No compare-and-skip — the old `current is null` branch silently dropped
        // a re-point when the built axis read back null, e.g. a sketch-line axis.)

        // SW quirk: D1Axis / D2Axis are scalar dispatch properties (set_D1Axis(Object)) — a
        // single live entity (Edge / Face2 / RefAxis / Feature / SketchSegment), NOT a
        // SAFEARRAY, so the live COM object is assigned directly (no DispatchWrapper, same as
        // Chamfer's scalar IVertex). Reflection-confirmed Object-typed setter on
        // ILinearPatternFeatureData.
        setAxis(live);
        SldworksLog.Information(
            "LinearPatternHandler.Edit: re-pointed '{Name}' {Label} onto {Type}",
            featureName, label, live.GetType().Name);
    }

    // Resolve seed defs to live Feature/Body2 entities (same resolver + the same two live types
    // PatternInstances.SelectSeeds accepts) and return them split by slot.
    private static (List<Feature> Features, List<Body2> Bodies) ResolveSeeds(
        File file, IReadOnlyList<Definition> seedDefs, string featureName) {
        if (seedDefs.Count == 0) {
            // LinearPatternArgs already requires a non-empty seed list; defensive.
            throw new InvalidOperationException(
                $"LinearPattern.Edit: empty seed list on '{featureName}'.");
        }
        var features = new List<Feature>();
        var bodies = new List<Body2>();
        for (var i = 0; i < seedDefs.Count; i++) {
            var live = DefinitionResolver.Resolve(file, seedDefs[i])
                ?? throw new InvalidOperationException(
                    $"LinearPattern.Edit: seeds[{i}] ({seedDefs[i].GetType().Name}) did not "
                    + $"resolve to a live entity on '{featureName}'.");
            switch (live) {
                case Feature feat:
                    features.Add(feat);
                    break;
                case Body2 body:
                    bodies.Add(body);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"LinearPattern.Edit: seeds[{i}] resolved to {live.GetType().Name}, "
                        + $"expected Feature or Body on '{featureName}'.");
            }
        }
        return (features, bodies);
    }

    // Set the D1 spacing/count (or UpToReference scalar) fields exactly as Inspect reads
    // them. For UpToReference we edit only the parametric scalars (offset / mode-value);
    // the end-reference + start-reference SELECTIONS are kept as built (re-pointing them
    // is not an in-place edit). Mirrors ApplyEndConditionA but on the live interface and
    // without touching the reference selection set.
    private static void ApplyEndConditionScalarsA(ILinearPatternFeatureData data, LinearPatternArgs args, string name) {
        var builtIsUpToRef = data.D1EndCondition == (int)swPatternEndCondition_e.swPatternEndCondition_UpToReference;
        var wireIsUpToRef = args.EndConditionA is UpToReferenceCondition;
        if (builtIsUpToRef != wireIsUpToRef) {
            throw new InvalidOperationException(
                $"LinearPattern.Edit: cannot change direction-1 end-condition kind on '{name}' "
                + $"({(builtIsUpToRef ? "UpToReference" : "SpacingAndInstances")} -> "
                + $"{(wireIsUpToRef ? "UpToReference" : "SpacingAndInstances")}); that re-points the end reference — delete and re-add.");
        }

        if (args.EndConditionA is UpToReferenceCondition upA) {
            data.D1EndRefOffset = upA.Offset;
            data.D1EndRefReverseOffset = upA.OffsetReversed;
            if (upA.Mode == UpToRefMode.Spacing) {
                data.D1EndUseSpacing = true;
                data.D1Spacing = upA.Value;
            } else {
                data.D1EndUseSpacing = false;
                data.D1TotalInstances = (int)upA.Value;
            }
            return;
        }

        // SpacingAndInstances: explicit end_condition_a shape wins, else plain spacing/count.
        if (args.EndConditionA is SpacingAndInstancesCondition s) {
            data.D1Spacing = s.Spacing;
            data.D1TotalInstances = s.Count;
        } else {
            data.D1Spacing = args.SpacingA;
            data.D1TotalInstances = args.CountA;
        }
    }

    private static void ApplyEndConditionScalarsB(ILinearPatternFeatureData data, LinearPatternArgs args, string name) {
        var builtIsUpToRef = data.D2EndCondition == (int)swPatternEndCondition_e.swPatternEndCondition_UpToReference;
        var wireIsUpToRef = args.EndConditionB is UpToReferenceCondition;
        if (builtIsUpToRef != wireIsUpToRef) {
            throw new InvalidOperationException(
                $"LinearPattern.Edit: cannot change direction-2 end-condition kind on '{name}' "
                + $"({(builtIsUpToRef ? "UpToReference" : "SpacingAndInstances")} -> "
                + $"{(wireIsUpToRef ? "UpToReference" : "SpacingAndInstances")}); that re-points the end reference — delete and re-add.");
        }

        if (args.EndConditionB is UpToReferenceCondition upB) {
            data.D2EndRefOffset = upB.Offset;
            data.D2EndRefReverseOffset = upB.OffsetReversed;
            if (upB.Mode == UpToRefMode.Spacing) {
                data.D2EndUseSpacing = true;
                data.D2Spacing = upB.Value;
            } else {
                data.D2EndUseSpacing = false;
                data.D2TotalInstances = (int)upB.Value;
            }
            return;
        }

        if (args.EndConditionB is SpacingAndInstancesCondition s) {
            data.D2Spacing = s.Spacing;
            data.D2TotalInstances = s.Count;
        } else {
            data.D2Spacing = args.SpacingB ?? 0.0;
            data.D2TotalInstances = args.CountB ?? 1;
        }
    }

    public static JsonNode Inspect(File file, Feature feature) {
        var data = feature.GetDefinition() as ILinearPatternFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose ILinearPatternFeatureData");

        if (data.InstancesToVary) {
            throw new InvalidOperationException(
                $"LinearPattern '{feature.Name}': InstancesToVary patterns are not supported on the wire");
        }

        // SW quirk: AccessSelections required before reading entities off FeatureData.
        data.AccessSelections(file.ModelDoc, null);
        try {
            var seeds = PatternInstances.CaptureSeeds(data.PatternFeatureArray, data.PatternBodyArray, data.PatternFaceArray);
            var directionA = PatternInstances.CaptureDirectionRef(data.D1Axis);
            var directionB = data.D2Axis is not null ? PatternInstances.CaptureDirectionRef(data.D2Axis) : null;

            var aIsUpToRef = data.D1EndCondition == (int)swPatternEndCondition_e.swPatternEndCondition_UpToReference;
            var bIsUpToRef = data.D2EndCondition == (int)swPatternEndCondition_e.swPatternEndCondition_UpToReference;

            // SW quirk: D2Axis non-null is the reliable check; D2TotalInstances can lie.
            var hasB = data.D2Axis is not null && (bIsUpToRef || data.D2TotalInstances > 1);

            var deleted = PatternInstances.ReadSkippedItems(data.SkippedItemArray);

            var result = new JsonObject {
                ["type"] = TypeName,
                ["seeds"] = seeds,
                ["direction_a"] = directionA,
                ["reversed_a"] = data.D1ReverseDirection,
                ["reversed_b"] = data.D2ReverseDirection,
                ["deleted"] = PatternInstances.ToIntArray(deleted),
                ["geometry_pattern"] = data.GeometryPattern,
                ["vary_sketch"] = data.VarySketch,
                ["pattern_seed_only"] = data.D2PatternSeedOnly,
                ["name"] = feature.Name,
            };

            if (aIsUpToRef) {
                // No-op defaults keep the round-trip unambiguous.
                result["spacing_a"] = 0.0;
                result["count_a"] = 1;
                result["end_condition_a"] = BuildUpToReferenceJsonA(data);
            } else {
                result["spacing_a"] = data.D1Spacing;
                result["count_a"] = data.D1TotalInstances;
            }

            if (hasB) {
                result["direction_b"] = directionB;
                if (bIsUpToRef) {
                    result["end_condition_b"] = BuildUpToReferenceJsonB(data);
                } else {
                    result["spacing_b"] = data.D2Spacing;
                    result["count_b"] = data.D2TotalInstances;
                }
            }
            return result;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private static JsonNode BuildUpToReferenceJsonA(ILinearPatternFeatureData data) {
        var endRef = PatternInstances.CaptureDirectionRef(data.D1EndReference);
        var startRef = data.D1EndUseSeedReference && data.D1EndSeedReference is not null
            ? PatternInstances.CaptureDirectionRef(data.D1EndSeedReference)
            : null;
        var json = new JsonObject {
            ["end_condition"] = "UpToReference",
            ["end_reference"] = endRef,
            ["offset"] = data.D1EndRefOffset,
            ["offset_reversed"] = data.D1EndRefReverseOffset,
        };
        if (startRef is not null) json["start_reference"] = startRef;
        if (data.D1EndUseSpacing) {
            json["mode"] = "Spacing";
            json["value"] = data.D1Spacing;
        } else {
            json["mode"] = "Instances";
            json["value"] = (double)data.D1TotalInstances;
        }
        return json;
    }

    private static JsonNode BuildUpToReferenceJsonB(ILinearPatternFeatureData data) {
        var endRef = PatternInstances.CaptureDirectionRef(data.D2EndReference);
        var startRef = data.D2EndUseSeedReference && data.D2EndSeedReference is not null
            ? PatternInstances.CaptureDirectionRef(data.D2EndSeedReference)
            : null;
        var json = new JsonObject {
            ["end_condition"] = "UpToReference",
            ["end_reference"] = endRef,
            ["offset"] = data.D2EndRefOffset,
            ["offset_reversed"] = data.D2EndRefReverseOffset,
        };
        if (startRef is not null) json["start_reference"] = startRef;
        if (data.D2EndUseSpacing) {
            json["mode"] = "Spacing";
            json["value"] = data.D2Spacing;
        } else {
            json["mode"] = "Instances";
            json["value"] = (double)data.D2TotalInstances;
        }
        return json;
    }

    private abstract record EndConditionShape;
    private sealed record SpacingAndInstancesCondition(double Spacing, int Count) : EndConditionShape;
    private sealed record UpToReferenceCondition(
        Definition EndReference,
        Definition? StartReference,
        double Offset,
        bool OffsetReversed,
        UpToRefMode Mode,
        double Value) : EndConditionShape;

    private enum UpToRefMode { Spacing, Instances }

    private sealed record LinearPatternArgs(
        IReadOnlyList<Definition> Seeds,
        Definition DirectionA,
        double SpacingA,
        int CountA,
        Definition? DirectionB,
        double? SpacingB,
        int? CountB,
        bool ReversedA,
        bool ReversedB,
        EndConditionShape? EndConditionA,
        EndConditionShape? EndConditionB,
        IReadOnlyList<int> Deleted,
        bool GeometryPattern,
        bool VarySketch,
        bool PatternSeedOnly) {

        internal static LinearPatternArgs Parse(JsonNode input) {
            var seeds = PatternInstances.ParseSeedList(input["seeds"]);
            var dirA = Definition.FromJson(input["direction_a"])
                ?? throw new ArgumentException("LinearPattern: 'direction_a' is required");

            var endCondA = ParseEndCondition(input["end_condition_a"]);
            var endCondB = ParseEndCondition(input["end_condition_b"]);

            var spacingA = ReadDoubleOrDefault(input, "spacing_a", 0.0);
            var countA = ReadIntOrDefault(input, "count_a", 1);

            Definition? dirB = null;
            double? spacingB = null;
            int? countB = null;
            var dbNode = input["direction_b"];
            var sbNode = input["spacing_b"];
            var cbNode = input["count_b"];
            if (dbNode is not null && dbNode.GetValueKind() != JsonValueKind.Null) {
                dirB = Definition.FromJson(dbNode);
            }
            if (sbNode is not null && sbNode.GetValueKind() != JsonValueKind.Null) {
                spacingB = sbNode.GetValue<double>();
            }
            if (cbNode is not null && cbNode.GetValueKind() != JsonValueKind.Null) {
                countB = cbNode.GetValue<int>();
            }

            if (endCondB is null) {
                var anySet = dirB is not null || spacingB.HasValue || countB.HasValue;
                var allSet = dirB is not null && spacingB.HasValue && countB.HasValue;
                if (anySet && !allSet) {
                    throw new ArgumentException(
                        "LinearPattern: 'direction_b', 'spacing_b', and 'count_b' must all be set together (or all omitted)");
                }
            } else if (dirB is null) {
                throw new ArgumentException(
                    "LinearPattern: 'end_condition_b' requires 'direction_b'");
            }

            var reversedA = input["reversed_a"]?.GetValue<bool>() ?? false;
            var reversedB = input["reversed_b"]?.GetValue<bool>() ?? false;

            var deleted = PatternInstances.ReadIntList(input, "deleted");
            var geometryPattern = input["geometry_pattern"]?.GetValue<bool>() ?? false;
            var varySketch = input["vary_sketch"]?.GetValue<bool>() ?? false;
            var patternSeedOnly = input["pattern_seed_only"]?.GetValue<bool>() ?? false;
            return new LinearPatternArgs(
                seeds, dirA, spacingA, countA, dirB, spacingB, countB,
                reversedA, reversedB, endCondA, endCondB, deleted,
                geometryPattern, varySketch, patternSeedOnly);
        }

        private static EndConditionShape? ParseEndCondition(JsonNode? node) {
            if (node is null || node.GetValueKind() == JsonValueKind.Null) return null;
            var disc = node["end_condition"]?.GetValue<string>()
                ?? throw new ArgumentException(
                    "LinearPattern: end_condition object missing 'end_condition' discriminator");
            return disc switch {
                "SpacingAndInstances" => new SpacingAndInstancesCondition(
                    Spacing: node["spacing"]?.GetValue<double>()
                        ?? throw new ArgumentException("LinearPattern SpacingAndInstances: 'spacing' is required"),
                    Count: node["count"]?.GetValue<int>()
                        ?? throw new ArgumentException("LinearPattern SpacingAndInstances: 'count' is required")),
                "UpToReference" => new UpToReferenceCondition(
                    EndReference: Definition.FromJson(node["end_reference"])
                        ?? throw new ArgumentException("LinearPattern UpToReference: 'end_reference' is required"),
                    StartReference: node["start_reference"] is { } sr
                        && sr.GetValueKind() != JsonValueKind.Null
                            ? Definition.FromJson(sr)
                            : null,
                    Offset: node["offset"]?.GetValue<double>() ?? 0.0,
                    OffsetReversed: node["offset_reversed"]?.GetValue<bool>() ?? false,
                    Mode: ParseMode(node["mode"]?.GetValue<string>() ?? "Instances"),
                    Value: node["value"]?.GetValue<double>()
                        ?? throw new ArgumentException("LinearPattern UpToReference: 'value' is required")),
                _ => throw new ArgumentException(
                    $"LinearPattern: unknown end_condition discriminator '{disc}'"),
            };
        }

        private static UpToRefMode ParseMode(string s) => s switch {
            "Spacing" => UpToRefMode.Spacing,
            "Instances" => UpToRefMode.Instances,
            _ => throw new ArgumentException(
                $"LinearPattern UpToReference: unknown mode '{s}' (expected 'Spacing' or 'Instances')"),
        };

        private static double ReadDoubleOrDefault(JsonNode node, string field, double @default) {
            var v = node[field];
            return v is null || v.GetValueKind() == JsonValueKind.Null
                ? @default
                : v.GetValue<double>();
        }

        private static int ReadIntOrDefault(JsonNode node, string field, int @default) {
            var v = node[field];
            return v is null || v.GetValueKind() == JsonValueKind.Null
                ? @default
                : v.GetValue<int>();
        }
    }
}
