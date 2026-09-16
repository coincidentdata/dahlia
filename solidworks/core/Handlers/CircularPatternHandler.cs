using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swcommands;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class CircularPatternHandler {
    public const string TypeName = "CircularPattern";

    // SW quirk: Feature.GetTypeName2() returns "CirPattern" for feature-level circular patterns.
    public const string SwTypeName = "CirPattern";

    private const int MarkCircularAxis = 1;

    public static JsonNode Add(File file, JsonNode input) {
        var args = CircularPatternArgs.Parse(input);

        file.ModelDoc.ClearSelection2(true);
        // Sequence selections in strictly decreasing mark order: seeds (4 features / 256 bodies) → axis (1).
        PatternInstances.SelectSeeds(file, args.Seeds);
        PatternInstances.SelectDirection(file, args.Axis, MarkCircularAxis, "axis");

        // total_angle ⇒ EqualSpacing=true, Spacing=total; angle_step ⇒ EqualSpacing=false.
        var (spacingA, equalSpacingA) = ResolveSpacing(args.AngleStep, args.TotalAngle);
        var hasB = args.DirectionB is not null;

        // SW 2026 quirk (data-driven path): `CreateDefinition(swFmCirPattern)` returns a
        // CircularPatternFeatureData whose Axis / PatternFeatureArray / PatternBodyArray
        // fields stay null/empty regardless of what's in SelectionMgr. Confirmed across
        // marks (1, 0), selection orders (axis-first, seeds-first), and selection APIs
        // (Select2/Select4 vs SelectByID2 with EXTSKETCHSEGMENT/SKETCHSEGMENT). The
        // entities ARE in SelectionMgr at the right marks (verified via DumpSelection),
        // but the data object never reads them. AccessSelections on a fresh data object
        // crashes SW (no underlying feature yet). Wrap CreateFeature in
        // AllowFailedFeatureCreation(true) — SW returns a feature *shell* instead of
        // null — then FixSketchSegmentAxisPostCreation rebinds the sketch-line axis
        // via FeatEditDef + Select4 + Ok.
        var fm = file.ModelDoc.FeatureManager;
        var data = fm.CreateDefinition((int)swFeatureNameID_e.swFmCirPattern) as CircularPatternFeatureData
            ?? throw new InvalidOperationException(
                "CircularPattern.Add: CreateDefinition(swFmCirPattern) did not return CircularPatternFeatureData");

        // SW quirk: order matters — set EqualSpacing FIRST so SW knows whether `Spacing`
        // is interpreted as per-step (false) vs total (true). If we set Spacing before
        // EqualSpacing, the EqualSpacing flip can re-derive Spacing from a default
        // (2π for per-step mode), wiping our value.
        data.EqualSpacing = equalSpacingA;
        data.TotalInstances = args.Count;
        data.Spacing = spacingA;
        data.ReverseDirection = args.Reversed;
        data.GeometryPattern = args.GeometryPattern;
        data.VarySketch = args.VarySketch;

        if (hasB) {
            // SW quirk: Direction2 shares the primary axis — it's a boolean toggle, not a
            // separate axis selection. The wire's direction_b axis is captured for shape
            // parity with LinearPattern but no second axis selection is made here.
            var (spacingB, equalSpacingB) = ResolveSpacing(args.AngleStepB, args.TotalAngleB);
            data.Direction2 = true;
            data.EqualSpacing2 = equalSpacingB;
            data.TotalInstances2 = args.CountB ?? 1;
            data.Spacing2 = spacingB;
        }

        file.SldWorks.AllowFailedFeatureCreation(true);
        Feature? feature;
        try {
            feature = fm.CreateFeature(data) as Feature;
        } finally {
            file.SldWorks.AllowFailedFeatureCreation(false);
        }
        if (feature is null) {
            throw new InvalidOperationException(
                "CircularPattern.Add: CreateFeature returned null even with AllowFailedFeatureCreation(true); check axis / seeds resolved");
        }

        if (args.Axis is SketchEntityDefinition) {
            FixSketchSegmentAxisPostCreation(file, feature, args.Axis);
        }

        PatternInstances.ApplySkippedInstances(feature, args.Deleted, "CircularPattern", file);

        SldworksLog.Information("CircularPatternHandler.Add: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        return PatternInstances.BuildAddResult(input, feature);
    }

    private static void FixSketchSegmentAxisPostCreation(File file, Feature feature, Definition axisDef) {
        var data = feature.GetDefinition() as ICircularPatternFeatureData
            ?? throw new InvalidOperationException(
                $"CircularPattern {feature.Name}: GetDefinition did not return ICircularPatternFeatureData (sketch-segment fixup)");

        if (!data.IAccessSelections2(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"CircularPattern {feature.Name}: IAccessSelections2 failed during sketch-segment axis fixup");
        }

        feature.Select2(false, -1);
        file.ModelDoc.FeatEditDef();

        var live = DefinitionResolver.Resolve(file, axisDef)
            ?? throw new InvalidOperationException(
                $"CircularPattern {feature.Name}: sketch-segment axis did not re-resolve during fixup");
        if (live is not SketchSegment seg) {
            throw new InvalidOperationException(
                $"CircularPattern {feature.Name}: SketchEntityDefinition resolved to {live.GetType().Name}, expected SketchSegment");
        }
        // SW quirk: Select4's return value is unreliable here — verify via GetSelectedObject6.
        seg.Select4(false, PatternInstances.MakeMarkSelectData(file, MarkCircularAxis));

        var selObject = file.ModelDoc.ISelectionManager.GetSelectedObject6(1, 1);
        var success = selObject is SketchSegment;
        file.ModelDoc.Extension.RunCommand((int)swCommands_e.swCommands_Ok_Command, "");
        if (!success) {
            throw new InvalidOperationException(
                $"CircularPattern {feature.Name}: sketch-segment axis fixup failed to bind axis (selected object was {selObject?.GetType().Name ?? "null"})");
        }

        // SW quirk: FeatEditDef + Ok doesn't reliably restore the rollback bar to its
        // pre-edit position. After the fixup the bar can sit *before* this CirPattern,
        // which leaves the feature rolled back so subsequent Add calls (e.g. a second
        // CircularPattern that references this one as a seed) can't see it. Force the
        // bar to after this feature.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
    }

    private static (double spacing, bool equalSpacing) ResolveSpacing(double? angleStep, double? totalAngle) {
        if (angleStep is not null && totalAngle is null) {
            return (angleStep.Value, false);
        }
        if (totalAngle is not null && angleStep is null) {
            return (totalAngle.Value, true);
        }
        throw new ArgumentException(
            "CircularPattern: exactly one of 'angle_step' / 'total_angle' must be set");
    }

    // Edit an existing circular pattern IN PLACE (GetDefinition -> AccessSelections ->
    // re-point selections + mutate scalars -> ModifyDefinition); we do NOT delete + re-add,
    // so the feature name — and every downstream name-ref / probe — survives. `input` is a
    // FULL CircularPattern payload (same shape Inspect emits / Add consumes), parsed by
    // CircularPatternArgs so edit and create share one validation.
    //
    // Selection parity with Add: the SEED features/bodies and the primary AXIS reference are
    // re-pointed to the wire's refs UNCONDITIONALLY (resolve and assign — no compare-and-skip;
    // ModifyDefinition's bool return is the validity gate, and re-applying the same ref on a
    // scalar-only edit is idempotent). There is only ONE axis: Direction2 is a boolean toggle
    // that SHARES the primary axis (no separate D2 axis selection), so the wire's direction_b
    // ref never re-points a second axis — it round-trips the same axis for shape parity with
    // LinearPattern. A sketch-segment axis additionally needs the post-commit FeatEditDef fixup
    // (run after ModifyDefinition; see below).
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = CircularPatternArgs.Parse(input);

        var data = feature.GetDefinition() as ICircularPatternFeatureData
            ?? throw new InvalidOperationException(
                $"CircularPattern.Edit: feature {feature.Name} ({feature.GetTypeName2()}) does not expose ICircularPatternFeatureData");

        // Vary-sketch patterns carry per-instance dimension overrides that Inspect already
        // rejects on the wire; there is nothing on the wire to edit them with.
        if (data.InstancesToVary) {
            throw new InvalidOperationException(
                $"CircularPattern.Edit: '{feature.Name}' is an InstancesToVary (vary-sketch) pattern — not editable on the wire.");
        }

        // total_angle => EqualSpacing=true, Spacing=total; angle_step => EqualSpacing=false.
        var (spacingA, equalSpacingA) = ResolveSpacing(args.AngleStep, args.TotalAngle);
        var hasB = args.DirectionB is not null;

        data.AccessSelections(file.ModelDoc, null);
        try {
            // SELECTION PARITY: re-point seeds + the primary axis to the wire's refs. Do this
            // INSIDE the open AccessSelections block, before the scalar sets and the single
            // ModifyDefinition commit, so the re-pointed refs and the new scalars settle in one
            // consistent rebuild. Only the single Axis is re-pointable — Direction2 shares it.
            // A sketch-segment axis is re-bound by the post-commit fixup below, since data.Axis
            // alone doesn't stick for sketch segments.
            RepointSeedsIfChanged(file, data, args.Seeds, feature.Name);
            RepointAxisIfChanged(file, data, args.Axis, feature.Name);

            // SW quirk: order matters — set EqualSpacing FIRST so SW knows whether `Spacing`
            // is interpreted as per-step (false) vs total (true). Setting Spacing first lets
            // the EqualSpacing flip re-derive Spacing from a default (2π per-step), wiping
            // our value. Same ordering Add uses.
            data.EqualSpacing = equalSpacingA;
            data.TotalInstances = args.Count;
            data.Spacing = spacingA;
            data.ReverseDirection = args.Reversed;
            data.GeometryPattern = args.GeometryPattern;
            data.VarySketch = args.VarySketch;

            // SW quirk: Direction2 shares the primary axis — it's a boolean toggle plus its
            // own count/spacing scalars, not a second axis selection. Mirror Add: when the
            // wire carries a direction_b, enable the toggle and set its scalars; otherwise
            // clear the toggle so an edit can remove the second direction.
            if (hasB) {
                var (spacingB, equalSpacingB) = ResolveSpacing(args.AngleStepB, args.TotalAngleB);
                data.Direction2 = true;
                data.EqualSpacing2 = equalSpacingB;
                data.TotalInstances2 = args.CountB ?? 1;
                data.Spacing2 = spacingB;
            } else {
                data.Direction2 = false;
            }

            // Skipped instances are parametric (index list into the instance grid), so they
            // can be re-applied in place alongside count/spacing. Set the array directly in
            // this same ModifyDefinition rather than a second commit.
            data.SkippedItemArray = args.Deleted.ToArray();

            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"CircularPattern.Edit: ModifyDefinition returned false on '{feature.Name}' — "
                    + "count/spacing/skipped-instances invalid against the local geometry?");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        // SW-2026 sketch-segment axis: a plain `data.Axis = live` doesn't reliably bind a
        // sketch-segment axis through ModifyDefinition (same limitation Add hits with
        // CreateDefinition). When the wire axis is a sketch entity, run the SAME post-commit
        // FeatEditDef + Select4 + Ok fixup Add uses so the axis actually re-points. This runs
        // UNCONDITIONALLY for a sketch-segment axis (no compare-skip) — it re-binds the SAME
        // axis on a scalar-only edit, which is idempotent. FixSketchSegmentAxisPostCreation
        // opens its own AccessSelections cycle, so it must run after ReleaseSelectionAccess.
        if (args.Axis is SketchEntityDefinition) {
            FixSketchSegmentAxisPostCreation(file, feature, args.Axis);
        }

        // Roll to end so the change propagates downstream (ForceRebuildAll). NOT
        // AfterFeature: a mid-tree pattern must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information("CircularPatternHandler.Edit: {Name}", feature.Name);
        return Inspect(file, feature);
    }

    // Re-point the pattern SEED set (features/bodies) in place to the wire's seeds. The incoming
    // defs arrive in the Edit payload (CircularPatternArgs.Seeds, the SAME refs Add resolves+selects
    // via PatternInstances.SelectSeeds). Here — unlike Add (select-then-CreateFeature) — we resolve
    // them and set the data interface's PatternFeatureArray / PatternBodyArray directly, inside the
    // open AccessSelections block, before ModifyDefinition. The resolved refs are applied
    // UNCONDITIONALLY (no compare-and-skip): re-applying the same refs is idempotent, so a
    // scalar-only edit is unaffected, and ModifyDefinition's bool return is the validity gate.
    // Inspect reads seeds from PatternFeatureArray + PatternBodyArray (PatternFaceArray is always
    // empty on swFmCirPattern); this writes exactly those two slots.
    private static void RepointSeedsIfChanged(
        File file, ICircularPatternFeatureData data, IReadOnlyList<Definition> seedDefs, string featureName) {
        var (features, bodies) = ResolveSeeds(file, seedDefs, featureName);

        // Unconditionally re-apply the resolved seeds: ModifyDefinition's bool return is the
        // validity gate. Re-applying the same refs is idempotent, so a scalar-only edit is
        // unaffected. (No compare-and-skip — a null-skip silently dropped re-points.)

        // SW quirk: PatternFeatureArray / PatternBodyArray are Object (SAFEARRAY-of-IDispatch)
        // setters — DispatchWrapper[] for the marshaller; a bare object[] crashes it (same as
        // Chamfer's Edges / Fillet's HoldLines). Reflection-confirmed setters on
        // ICircularPatternFeatureData; Inspect's CaptureSeeds reads back the same two slots.
        // Write null for an empty slot to clear any stale entries.
        data.PatternFeatureArray = features.Count > 0
            ? features.Select(f => new DispatchWrapper(f)).ToArray()
            : null;
        data.PatternBodyArray = bodies.Count > 0
            ? bodies.Select(b => new DispatchWrapper(b)).ToArray()
            : null;
        SldworksLog.Information(
            "CircularPatternHandler.Edit: re-pointed '{Name}' seeds onto {Feat} feature(s) + {Body} body(ies)",
            featureName, features.Count, bodies.Count);
    }

    // Re-point the single pattern AXIS reference in place to the wire's axis. The incoming def
    // arrives in the Edit payload (CircularPatternArgs.Axis, the SAME ref Add resolves+selects
    // via PatternInstances.SelectDirection). Here we resolve it and set the data interface's
    // Axis dispatch property directly, UNCONDITIONALLY (no compare-and-skip): re-applying the
    // same ref is idempotent, and ModifyDefinition's bool return is the validity gate. The old
    // `current is null` skip silently dropped the re-point when the built axis read back null
    // (e.g. a sketch-line axis) — see RefPlane/Revolve axis-guard bug.
    //
    // SW-2026 sketch-segment axis: Add could NOT bind a sketch-segment axis through the data
    // object (CreateDefinition ignores data.Axis — that's why Add runs the FeatEditDef + Select4
    // + Ok fixup). Setting data.Axis here is expected to stick for Edge/Face2/RefAxis/Feature
    // axes, but a sketch-segment axis needs the same post-modify FeatEditDef fixup Add uses; the
    // caller runs FixSketchSegmentAxisPostCreation after ModifyDefinition when the axis is a
    // SketchEntityDefinition (see Edit).
    private static void RepointAxisIfChanged(
        File file, ICircularPatternFeatureData data, Definition axisDef, string featureName) {
        var live = DefinitionResolver.Resolve(file, axisDef)
            ?? throw new InvalidOperationException(
                $"CircularPattern.Edit: axis ({axisDef.GetType().Name}) did not resolve to a "
                + $"live entity on '{featureName}'.");

        // SW quirk: Axis is a scalar dispatch property (set_Axis(Object)) — a single live
        // entity (Edge / Face2 / RefAxis / Feature / SketchSegment), NOT a SAFEARRAY, so the
        // live COM object is assigned directly (no DispatchWrapper, same as Chamfer's scalar
        // IVertex). Reflection-confirmed Object-typed setter on ICircularPatternFeatureData.
        data.Axis = live;
        SldworksLog.Information(
            "CircularPatternHandler.Edit: re-pointed '{Name}' axis onto {Type}",
            featureName, live.GetType().Name);
    }

    // Resolve seed defs to live Feature/Body2 entities (same resolver + the same two live types
    // PatternInstances.SelectSeeds accepts) and return them split by slot.
    private static (List<Feature> Features, List<Body2> Bodies) ResolveSeeds(
        File file, IReadOnlyList<Definition> seedDefs, string featureName) {
        if (seedDefs.Count == 0) {
            // CircularPatternArgs already requires a non-empty seed list; defensive.
            throw new InvalidOperationException(
                $"CircularPattern.Edit: empty seed list on '{featureName}'.");
        }
        var features = new List<Feature>();
        var bodies = new List<Body2>();
        for (var i = 0; i < seedDefs.Count; i++) {
            var live = DefinitionResolver.Resolve(file, seedDefs[i])
                ?? throw new InvalidOperationException(
                    $"CircularPattern.Edit: seeds[{i}] ({seedDefs[i].GetType().Name}) did not "
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
                        $"CircularPattern.Edit: seeds[{i}] resolved to {live.GetType().Name}, "
                        + $"expected Feature or Body on '{featureName}'.");
            }
        }
        return (features, bodies);
    }

    public static JsonNode Inspect(File file, Feature feature) {
        var data = feature.GetDefinition() as ICircularPatternFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose ICircularPatternFeatureData");

        if (data.InstancesToVary) {
            throw new InvalidOperationException(
                $"CircularPattern '{feature.Name}': InstancesToVary patterns are not supported on the wire");
        }

        // SW quirk: AccessSelections required before reading entities; pair with Release.
        data.AccessSelections(file.ModelDoc, null);
        try {
            var seeds = PatternInstances.CaptureSeeds(data.PatternFeatureArray, data.PatternBodyArray, data.PatternFaceArray);
            var axis = PatternInstances.CaptureDirectionRef(data.Axis);

            // SW quirk: Spacing is per-step when EqualSpacing=false, total when true.
            JsonNode angleField;
            string angleKey;
            if (data.EqualSpacing) {
                angleKey = "total_angle";
                angleField = data.Spacing;
            } else {
                angleKey = "angle_step";
                angleField = data.Spacing;
            }

            var deleted = PatternInstances.ReadSkippedItems(data.SkippedItemArray);

            var result = new JsonObject {
                ["type"] = TypeName,
                ["seeds"] = seeds,
                ["axis"] = axis,
                [angleKey] = angleField,
                ["count"] = data.TotalInstances,
                ["deleted"] = PatternInstances.ToIntArray(deleted),
                ["reversed"] = data.ReverseDirection,
                ["geometry_pattern"] = data.GeometryPattern,
                ["vary_sketch"] = data.VarySketch,
                ["name"] = feature.Name,
            };

            // Direction2 shares the primary axis; re-emit `axis` to mirror LinearPattern's wire shape.
            if (data.Direction2) {
                result["direction_b"] = axis.DeepClone();
                if (data.EqualSpacing2) {
                    result["total_angle_b"] = data.Spacing2;
                } else {
                    result["angle_step_b"] = data.Spacing2;
                }
                result["count_b"] = data.TotalInstances2;
            }
            return result;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private sealed record CircularPatternArgs(
        IReadOnlyList<Definition> Seeds,
        Definition Axis,
        double? AngleStep,
        double? TotalAngle,
        int Count,
        Definition? DirectionB,
        double? AngleStepB,
        double? TotalAngleB,
        int? CountB,
        bool Reversed,
        IReadOnlyList<int> Deleted,
        bool GeometryPattern,
        bool VarySketch) {

        internal static CircularPatternArgs Parse(JsonNode input) {
            var seeds = PatternInstances.ParseSeedList(input["seeds"]);
            var axis = Definition.FromJson(input["axis"])
                ?? throw new ArgumentException("CircularPattern: 'axis' is required");
            var angleStep = ReadDoubleOrNull(input, "angle_step");
            var totalAngle = ReadDoubleOrNull(input, "total_angle");
            if ((angleStep is null) == (totalAngle is null)) {
                throw new ArgumentException(
                    "CircularPattern: exactly one of 'angle_step' / 'total_angle' must be set");
            }
            var count = PatternInstances.ReadInt(input, "count");

            var dirB = input["direction_b"] is { } dbn && dbn.GetValueKind() != JsonValueKind.Null
                ? Definition.FromJson(dbn)
                : null;
            var angleStepB = ReadDoubleOrNull(input, "angle_step_b");
            var totalAngleB = ReadDoubleOrNull(input, "total_angle_b");
            var countB = ReadIntOrNull(input, "count_b");

            var anyB = dirB is not null || angleStepB is not null || totalAngleB is not null || countB is not null;
            if (anyB) {
                if (dirB is null || countB is null) {
                    throw new ArgumentException(
                        "CircularPattern: when any direction-B field is set, 'direction_b' and 'count_b' are required");
                }
                if ((angleStepB is null) == (totalAngleB is null)) {
                    throw new ArgumentException(
                        "CircularPattern: when 'direction_b' is set, exactly one of 'angle_step_b' / 'total_angle_b' must be set");
                }
            }

            var reversed = input["reversed"]?.GetValue<bool>() ?? false;
            var deleted = PatternInstances.ReadIntList(input, "deleted");
            var geometryPattern = input["geometry_pattern"]?.GetValue<bool>() ?? false;
            var varySketch = input["vary_sketch"]?.GetValue<bool>() ?? false;
            return new CircularPatternArgs(
                seeds, axis, angleStep, totalAngle, count,
                dirB, angleStepB, totalAngleB, countB,
                reversed, deleted, geometryPattern, varySketch);
        }

        private static double? ReadDoubleOrNull(JsonNode node, string field) {
            var v = node[field];
            return v is null || v.GetValueKind() == JsonValueKind.Null
                ? null
                : v.GetValue<double>();
        }

        private static int? ReadIntOrNull(JsonNode node, string field) {
            var v = node[field];
            return v is null || v.GetValueKind() == JsonValueKind.Null
                ? null
                : v.GetValue<int>();
        }
    }
}
