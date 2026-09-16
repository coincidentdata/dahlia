using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class CutExtrudeHandler {
    public const string TypeName = "CutExtrude";

    // SW quirk: GetTypeName2() returns "Cut-Extrude" (hyphenated).
    public const string SwTypeName = "Cut-Extrude";

    // ---- Add -----------------------------------------------------------------------

    public static JsonNode Add(File file, JsonNode input) {
        var args = CutExtrudeArgs.Parse(input);
        var reversed = args.Reversed;

        var (endType1, distance1, endRef1, offsetReverse1, translateSurface1) =
            ExtrudeEndCondition.Translate(args.End1);
        var (endType2, distance2, endRef2, offsetReverse2, translateSurface2) = args.End2 is null
            ? (default(int), 0.0, default(Definition?), false, false)
            : ExtrudeEndCondition.Translate(args.End2);

        // SW quirk: multi-body cuts pop a "select bodies to keep" modal; only
        // PromptBodiesToKeepNotify can answer it from code.
        // SW quirk: an empty (but non-null) feature_scope means "user-narrowed to zero
        // bodies"; this collapses to AutoSelect — passing UseAutoSelect=false without
        // selecting any bodies makes FeatureCut4 silently return null.
        // SW quirk: select feature_scope BEFORE end-condition reference. FeatureCut4
        // is sensitive to selection-list order when both end-condition (mark 1) and
        // feature_scope (mark 8) are bodies — feature_scope-first selects the right
        // body, end-cond-first substitutes the wrong one.
        // Sequence selections in strictly decreasing mark order: start (32) → feature_scope (8)
        // → end-condition refs (1) → contours/sketch (0) → direction (default).
        var hasExplicitBodies = args.FeatureScope is { Count: > 0 };

        file.ModelDoc.ClearSelection2(true);

        var (startType, startOffsetDistance, flipStartOffset) = ApplyStartCondition(file, args.Start);

        if (hasExplicitBodies) {
            SelectFeatureScopeBodiesForAdd(file, args.FeatureScope!);
        }
        ExtrudeEndCondition.AppendEndConditionSelection(file, endRef1);
        ExtrudeEndCondition.AppendEndConditionSelection(file, endRef2);

        if (args.Contours.Count > 0) {
            SelectContours(file, args.Contours, args.SketchName);
        } else {
            SelectSketchByFeature(file, args.SketchName);
        }

        if (args.Direction is not null) {
            SelectDirection(file, args.Direction);
        }

        // Multi-body cuts pop a "select bodies to keep" modal that only
        // PromptBodiesToKeepNotify can answer from code. Keep every candidate
        // here; the actual fragment discard is a separate `DropBodies` command
        // the caller issues after Add (driven by `GetDiscardProbes(name)` on
        // the source side during round-trip).
        using var bodiesScope = BodiesToKeepScope.Register(file);

        var draftA = args.DraftAngle;
        var draftB = args.DraftAngleB ?? args.DraftAngle;

        // SW quirk: depths in meters, draft angles in radians; sign of draft_angle encodes outward (negative = outward).
        var fm = file.ModelDoc.FeatureManager;
        var feature = (Feature?)fm.FeatureCut4(
            Sd:                          !args.BothDirections,
            Flip:                        args.FlipSideToCut,
            Dir:                         reversed,
            T1:                          endType1,
            T2:                          endType2,
            D1:                          distance1,
            D2:                          distance2,
            Dchk1:                       draftA != 0.0,
            Dchk2:                       args.BothDirections && draftB != 0.0,
            Ddir1:                       draftA < 0,
            Ddir2:                       draftB < 0,
            Dang1:                       Math.Abs(draftA),
            Dang2:                       Math.Abs(draftB),
            OffsetReverse1:              offsetReverse1,
            OffsetReverse2:              offsetReverse2,
            TranslateSurface1:           translateSurface1,
            TranslateSurface2:           translateSurface2,
            NormalCut:                   false,
            UseFeatScope:                true,
            UseAutoSelect:               !hasExplicitBodies,
            AssemblyFeatureScope:        false,
            AutoSelectComponents:        false,
            PropagateFeatureToParts:     false,
            T0:                          startType,
            StartOffset:                 startOffsetDistance,
            FlipStartOffset:             flipStartOffset,
            OptimizeGeometry:            false);

        if (feature is null) {
            throw new InvalidOperationException(
                "FeatureCut4 failed; check sketch has closed regions, depth > 0, and end-condition is valid");
        }

        // SW quirk: FeatureCut4 has no slot for direction-2 axis; apply post-create via SetDirectionReference.
        if (args.DirectionB is not null) {
            ApplyDirectionB(file, feature, args.Direction, args.DirectionB);
        }

        SldworksLog.Information("CutExtrudeHandler.Add: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return BuildAddResult(input, feature);
    }

    private static void ApplyDirectionB(File file, Feature feature, Definition? dir1, Definition dir2) {
        var data = feature.GetDefinition() as IExtrudeFeatureData2
            ?? throw new InvalidOperationException(
                "CutExtrude: could not edit feature data to apply direction_b");
        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                "CutExtrude: AccessSelections failed when applying direction_b");
        }
        try {
            object? live1 = null;
            if (dir1 is not null) {
                live1 = DefinitionResolver.Resolve(file, dir1)
                    ?? throw new InvalidOperationException(
                        "CutExtrude: direction (slot 1) did not resolve to a live entity");
            }
            var live2 = DefinitionResolver.Resolve(file, dir2)
                ?? throw new InvalidOperationException(
                    "CutExtrude: direction_b (slot 2) did not resolve to a live entity");
            data.SetDirectionReference(live1!, live2);
            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    "CutExtrude: ModifyDefinition failed when applying direction_b");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    // ---- Edit ----------------------------------------------------------------------

    // Edit an existing cut extrude IN PLACE (GetDefinition -> AccessSelections -> mutate
    // scalars/settings AND re-point references -> ModifyDefinition); we do NOT delete +
    // re-add, so the feature name — and every downstream name-ref / probe — survives.
    // `input` is a FULL cut-extrude payload (same shape Inspect emits / Add consumes),
    // parsed by CutExtrudeArgs so edit and create share one validation. Edit applies EVERY
    // field Add does, including geometry REFERENCES: each incoming reference Definition is
    // resolved via DefinitionResolver and set through the I-interface setter inside the
    // AccessSelections block, only when the payload supplies one. Symmetric to
    // ExtrudeHandler.Edit (the data interface IExtrudeFeatureData2 is shared); the only
    // delta is FlipSideToCut in place of Merge. References handled:
    //   end-condition up-to / off-from  -> SetEndConditionReference(forward, live)
    //   surface/vertex start            -> SetFromEntity(live)
    //   direction-1 / direction-2 axes  -> SetDirectionReference(live1, live2)
    //   profile contours                -> Contours = object[]{ live region/contour/segment }
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = CutExtrudeArgs.Parse(input);

        var data = feature.GetDefinition() as IExtrudeFeatureData2
            ?? throw new InvalidOperationException(
                $"CutExtrude.Edit: feature {feature.Name} ({feature.GetTypeName2()}) does not expose IExtrudeFeatureData2");

        data.AccessSelections(file.ModelDoc, null);
        try {
            ApplyDirection(file, data, primary: true, args.End1, args.DraftAngle, "CutExtrude");
            data.BothDirections = args.BothDirections;
            if (args.BothDirections) {
                var end2 = args.End2
                    ?? throw new InvalidOperationException(
                        "CutExtrude.Edit: both_directions is set but end_condition_b is missing");
                ApplyDirection(file, data, primary: false, end2, args.DraftAngleB ?? args.DraftAngle, "CutExtrude");
            }
            data.ReverseDirection = args.Reversed;
            ApplyStartConditionEdit(file, data, args.Start, "CutExtrude");
            ApplyDirectionReferenceEdit(file, data, args.Direction, args.DirectionB);
            ApplyContoursEdit(file, data, args.Contours, args.SketchName, "CutExtrude");
            data.FlipSideToCut = args.FlipSideToCut;

            // Apply feature_scope unconditionally (assume the payload is authoritative — no
            // compare/skip). null/empty => AutoSelect over all bodies; non-empty => restrict to
            // the resolved bodies. ModifyDefinition's bool return is the validity gate.
            var scopeBodies = FeatureScope.ResolveScopeBodies(file, args.FeatureScope);
            if (scopeBodies is null) data.AutoSelect = true;
            else { data.AutoSelect = false; data.FeatureScopeBodies = scopeBodies; }

            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"CutExtrude.Edit: ModifyDefinition returned false on '{feature.Name}' — "
                    + "value invalid against local geometry?");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        // Roll to end so the change propagates downstream (ForceRebuildAll). NOT
        // AfterFeature: a mid-tree cut must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information("CutExtrudeHandler.Edit: {Name}", feature.Name);
        return Inspect(file, feature);
    }

    // Apply one direction's end-condition + draft + (for up-to / off-from kinds) a re-pointed
    // reference onto the live data. Set the end-condition CODE first (SetEndCondition), then,
    // when the kind carries a reference Definition, resolve it and re-point in place via the
    // reflection-confirmed setter `SetEndConditionReference(bool Forward, object PDisp)`. The
    // end-condition KIND is set independently of the reference, so changing only the up-to
    // GEOMETRY (same kind) and changing the kind+geometry together both work here. SW quirk:
    // GetEndConditionReference's companion setter takes the live Face2/Edge/Vertex/Body2 as a
    // bare COM dispatch (no IEntity cast, no selection mark) — the same live types
    // DefinitionResolver yields for the captured face/edge/vertex/body Definitions.
    private static void ApplyDirection(
            File file, IExtrudeFeatureData2 data, bool primary,
            ExtrudeEndCondition.Payload end, double draftSigned, string label) {
        var (endCode, distance, endRef, offsetReverse, translateSurface) =
            ExtrudeEndCondition.Translate(end);
        data.SetEndCondition(primary, endCode);
        // SW quirk: depth is only meaningful for Blind/MidPlane; Translate returns 0 otherwise
        // and SetDepth on a through/up-to condition is a no-op, so it's safe to always set it.
        data.SetDepth(primary, distance);

        if (endRef is not null) {
            var live = DefinitionResolver.Resolve(file, endRef)
                ?? throw new InvalidOperationException(
                    $"{label}.Edit: end-condition '{end.Kind}' reference "
                    + $"({endRef.GetType().Name}) did not resolve to a live entity");
            data.SetEndConditionReference(primary, live);
            // Off-from carries two extra flags alongside its surface ref; UpTo* kinds
            // leave them at the SW defaults Translate returns (false/false).
            data.SetReverseOffset(primary, offsetReverse);
            data.SetTranslateSurface(primary, translateSurface);
        }

        // SW quirk: draft_angle is signed — negative encodes DraftOutward; gate the flag
        // off when zero so Inspect (which reads GetDraftWhileExtruding) round-trips cleanly.
        var draftOn = draftSigned != 0.0;
        data.SetDraftWhileExtruding(primary, draftOn);
        data.SetDraftOutward(primary, draftSigned < 0.0);
        data.SetDraftAngle(primary, Math.Abs(draftSigned));
    }

    // Start condition. Sketch/Offset are scalar-only; Surface/Vertex re-point the "from"
    // entity in place via the reflection-confirmed setter `SetFromEntity(object FromEntity)`
    // (the companion of GetFromEntity, used in Inspect). The live entity is whatever
    // DefinitionResolver yields for the captured reference (Face2 for Surface, Vertex for
    // Vertex) — passed as a bare dispatch, no mark, mirroring SetEndConditionReference.
    private static void ApplyStartConditionEdit(
            File file, IExtrudeFeatureData2 data, ExtrudeStartArgs start, string label) {
        switch (start.Kind) {
            case ExtrudeStartKind.Sketch:
                data.FromType = (int)swStartConditions_e.swStartSketchPlane;
                break;
            case ExtrudeStartKind.Offset:
                data.FromType = (int)swStartConditions_e.swStartOffset;
                data.FromOffsetDistance = start.Offset;
                data.FromOffsetReverse = start.Reversed;
                break;
            case ExtrudeStartKind.Surface:
            case ExtrudeStartKind.Vertex: {
                var live = DefinitionResolver.Resolve(file, start.Reference!)
                    ?? throw new InvalidOperationException(
                        $"{label}.Edit: start '{start.Kind}' reference "
                        + $"({start.Reference!.GetType().Name}) did not resolve to a live entity");
                // SW quirk: SetFromEntity re-points the reference; FromType selects how SW
                // reads it (surface plane vs vertex). Set the entity first, then the type.
                data.SetFromEntity(live);
                data.FromType = start.Kind == ExtrudeStartKind.Surface
                    ? (int)swStartConditions_e.swStartSurface
                    : (int)swStartConditions_e.swStartVertex;
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"{label}.Edit: unknown start kind {start.Kind}");
        }
    }

    // Re-point direction-1 / direction-2 axes when the payload supplies them. SW quirk:
    // SetDirectionReference(object Ref1, object Ref2) sets BOTH slots in one call — there's
    // no per-slot setter — so when only direction_b is given we must read the live ref-1
    // back off the data and pass it through unchanged, or SW clears the existing axis-1.
    // No-op when neither is provided (leaves the as-built axes alone).
    private static void ApplyDirectionReferenceEdit(
            File file, IExtrudeFeatureData2 data, Definition? dir1, Definition? dir2) {
        if (dir1 is null && dir2 is null) return;

        object? live1 = dir1 is not null
            ? DefinitionResolver.Resolve(file, dir1)
                ?? throw new InvalidOperationException(
                    $"CutExtrude.Edit: direction (slot 1) ({dir1.GetType().Name}) did not resolve to a live entity")
            : null;
        object? live2 = dir2 is not null
            ? DefinitionResolver.Resolve(file, dir2)
                ?? throw new InvalidOperationException(
                    $"CutExtrude.Edit: direction_b (slot 2) ({dir2.GetType().Name}) did not resolve to a live entity")
            : null;

        // Preserve the slot the payload didn't touch: read the current axes and substitute.
        if (live1 is null || live2 is null) {
            data.GetDirectionReference(out var cur1, out _, out var cur2, out _);
            live1 ??= cur1;
            live2 ??= cur2;
        }
        data.SetDirectionReference(live1!, live2!);
    }

    // Re-point the profile contour set when the payload supplies one. SW quirk: the
    // `Contours` property round-trips an object[] of live SketchRegion / SketchContour /
    // SketchSegment (the same shape Inspect reads via `data.Contours as object[]`). Resolve
    // each captured contour Definition to its live entity through the same subset-matching
    // resolver Add uses (ResolveContoursForSelection), restricted to the owning sketch so
    // reissued per-sketch segment ids don't collide. No-op when contours is empty (leaves
    // the as-built profile alone — Inspect emits an empty array for a whole-sketch cut).
    private static void ApplyContoursEdit(
            File file, IExtrudeFeatureData2 data,
            IReadOnlyList<Definition> contours, string sketchName, string label) {
        if (contours.Count == 0) return;

        var ownerSketch = string.IsNullOrEmpty(sketchName)
            ? null
            : FindFeatureByName(file, sketchName)?.GetSpecificFeature2() as Sketch;
        var live = DefinitionResolver.ResolveContoursForSelection(file, contours, ownerSketch);
        if (live.Count == 0) {
            throw new InvalidOperationException(
                $"{label}.Edit: no live contour matched the captured "
                + $"{contours.Count} source contour(s) under segment-/edge-subset rule");
        }
        data.Contours = live.ToArray();
    }

    // ---- Inspect -------------------------------------------------------------------

    public static JsonNode Inspect(File file, Feature feature) {
        // SW quirk: GetDefinition() returns IExtrudeFeatureData2 for both boss and cut extrudes.
        var data = feature.GetDefinition() as IExtrudeFeatureData2
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose IExtrudeFeatureData2");

        // SW quirk: AccessSelections required before reading refs; pair with ReleaseSelectionAccess.
        data.AccessSelections(file.ModelDoc, null);
        try {
            var (directionNode, directionBNode) = CaptureDirections(data);

            // Sketch profile is the first sub-feature.
            var sketchSub = feature.GetFirstSubFeature() as Feature;
            var sketchName = FeatureName.Resolve(file, sketchSub?.Name ?? "");

            var bothDirections = data.BothDirections;
            var draftA = ExtrudeEndCondition.CaptureDraftAngle(data, primary: true);
            double? draftB = null;
            if (bothDirections) {
                var captured = ExtrudeEndCondition.CaptureDraftAngle(data, primary: false);
                if (Math.Abs(captured - draftA) > Flags.GeometryTolerance) {
                    draftB = captured;
                }
            }

            var end1 = ExtrudeEndCondition.CaptureEndCondition(data, primary: true, "CutExtrude");
            JsonNode? end2 = bothDirections
                ? ExtrudeEndCondition.CaptureEndCondition(data, primary: false, "CutExtrude")
                : null;

            var contours = CaptureContours(data);
            var startNode = CaptureStartCondition(data);
            var featureScope = FeatureScope.Capture(file, data.FeatureScopeBodies);

            // Discarded-fragment info moved off Inspect to the on-demand
            // `File.GetDiscardProbes(name)` endpoint — the underlying
            // IModifyDefinition2 dance drifts the surviving body's mass-props
            // by ~1e-7, which downstream Inspect callers shouldn't pay for
            // when they only want to read the feature.

            var result = new JsonObject {
                ["type"] = TypeName,
                ["name"] = feature.Name,
                ["sketch"] = new JsonObject {
                    ["type"] = "Sketch",
                    ["name"] = sketchName,
                },
                ["depth"] = ExtrudeEndCondition.SelectTopLevelDepth(data),
                ["reversed"] = data.ReverseDirection,
                ["both_directions"] = bothDirections,
                ["draft_angle"] = draftA,
                ["contours"] = contours,
                ["start"] = startNode,
                ["end_condition"] = end1,
                ["feature_scope"] = featureScope,
                ["flip_side_to_cut"] = data.FlipSideToCut,
            };
            if (draftB is not null) result["draft_angle_b"] = draftB.Value;
            if (directionNode is not null) result["direction"] = directionNode;
            if (directionBNode is not null) result["direction_b"] = directionBNode;
            if (end2 is not null) result["end_condition_b"] = end2;
            return result;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    // ---- Direction-reference capture & selection ---------------------------------

    private static (JsonNode? dir1, JsonNode? dir2) CaptureDirections(IExtrudeFeatureData2 data) {
        // SW quirk: GetDirectionReference exposes two slots (one per direction).
        data.GetDirectionReference(out var dirRef, out _, out var dirRef2, out _);
        // SW quirk: RefAxis returned for cylinder/cone temp-axis picks.
        var d1 = dirRef is null ? null : DefinitionCapture.Capture(dirRef)?.ToJson()
            ?? throw new InvalidOperationException(
                $"Inspect(CutExtrude): direction reference is unsupported runtime type {dirRef.GetType().Name}");
        var d2 = dirRef2 is null ? null : DefinitionCapture.Capture(dirRef2)?.ToJson()
            ?? throw new InvalidOperationException(
                $"Inspect(CutExtrude): direction reference is unsupported runtime type {dirRef2.GetType().Name}");
        return (d1, d2);
    }

    // ---- Contour capture ---------------------------------------------------------------

    private static JsonArray CaptureContours(IExtrudeFeatureData2 data) {
        var arr = new JsonArray();
        var contours = data.Contours as object[];
        if (contours is null || contours.Length == 0) return arr;
        foreach (var contour in contours) {
            if (contour is null) {
                throw new InvalidOperationException("Inspect(CutExtrude): contour entry is null");
            }
            arr.Add(DefinitionCapture.Capture(contour)?.ToJson()
                ?? throw new InvalidOperationException(
                    $"Inspect(CutExtrude): contour entry of unsupported type {contour.GetType().Name} " +
                    "(expected SketchRegion or SketchContour)"));
        }
        return arr;
    }

    // ---- Start condition capture --------------------------------------------------

    private static JsonNode CaptureStartCondition(IExtrudeFeatureData2 data) {
        var fromType = data.FromType;
        switch (fromType) {
            case (int)swStartConditions_e.swStartSketchPlane:
                return new JsonObject { ["type"] = "Sketch" };
            case (int)swStartConditions_e.swStartSurface:
                return new JsonObject {
                    ["type"] = "Surface",
                    ["reference"] = CaptureStartReference(data, "Surface"),
                };
            case (int)swStartConditions_e.swStartVertex:
                return new JsonObject {
                    ["type"] = "Vertex",
                    ["reference"] = CaptureStartReference(data, "Vertex"),
                };
            case (int)swStartConditions_e.swStartOffset:
                return new JsonObject {
                    ["type"] = "Offset",
                    ["offset"] = data.FromOffsetDistance,
                    ["reversed"] = data.FromOffsetReverse,
                };
            default:
                throw new InvalidOperationException(
                    $"Inspect(CutExtrude): unknown swStartCondition_e value {fromType}");
        }
    }

    private static JsonNode CaptureStartReference(IExtrudeFeatureData2 data, string label) {
        data.GetFromEntity(out var entity, out _);
        if (entity is null) {
            throw new InvalidOperationException(
                $"Inspect(CutExtrude): start.{label} reference is null");
        }
        return DefinitionCapture.Capture(entity)?.ToJson()
            ?? throw new InvalidOperationException(
                $"Inspect(CutExtrude): start.{label} reference is unsupported runtime type {entity.GetType().Name}");
    }

    // SW quirk: feature-scope bodies select at mark 8.
    private const int MarkFeatureScopeBodies = 8;

    private static void SelectFeatureScopeBodiesForAdd(File file, IReadOnlyList<Definition> bodies) {
        if (bodies.Count == 0) return;
        Definition.SelectAll(file, bodies, MarkFeatureScopeBodies, "CutExtrude.feature_scope");
    }

    // ---- Selection helpers ---------------------------------------------------------

    private static void SelectSketchByFeature(File file, string sketchName) {
        if (string.IsNullOrEmpty(sketchName)) {
            throw new ArgumentException("CutExtrude: sketch name missing — sketch must be added first");
        }

        var feat = FindFeatureByName(file, sketchName)
            ?? throw new ArgumentException($"Sketch feature '{sketchName}' not found");
        // Mark 0 is the sketch profile slot. Append=true so prior higher-mark selections survive.
        feat.Select2(true, 0);
    }

    // SW quirk: ModelDoc2 has no FeatureByName helper — walk the feature tree.
    private static Feature? FindFeatureByName(File file, string name) {
        var feature = file.ModelDoc.IFirstFeature() as Feature;
        while (feature is not null) {
            if (feature.Name == name) return feature;
            feature = feature.IGetNextFeature() as Feature;
        }
        return null;
    }

    private static void SelectContours(File file, IReadOnlyList<Definition> contours, string sketchName) {
        // SW quirk: SketchSegment uses Select4; SketchRegion/SketchContour use Select2.
        // SW quirk: SketchContour/SketchRegion decomposition isn't stable across rebuilds.
        // Append-only — caller cleared the selection set up front so prior higher-mark selections survive.
        var sd = MakeMarkSelectData(file, 0);
        var ownerSketch = string.IsNullOrEmpty(sketchName)
            ? null
            : FindFeatureByName(file, sketchName)?.GetSpecificFeature2() as Sketch;
        var liveEntities = DefinitionResolver.ResolveContoursForSelection(file, contours, ownerSketch);
        if (liveEntities.Count == 0) {
            throw new InvalidOperationException(
                $"CutExtrude: no live contour matched the captured " +
                $"{contours.Count} source contour(s) under segment-/edge-subset rule");
        }
        foreach (var live in liveEntities) {
            switch (live) {
                case SketchRegion region:
                    region.Select2(true, sd);
                    break;
                case SketchContour ctr:
                    ctr.Select2(true, sd);
                    break;
                case SketchSegment seg:
                    seg.Select4(true, sd);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"CutExtrude: contour resolved to unsupported type {live.GetType().Name} " +
                        "(expected SketchRegion / SketchContour / SketchSegment)");
            }
        }
    }

    private static void SelectDirection(File file, Definition directionDef) {
        var live = DefinitionResolver.Resolve(file, directionDef)
            ?? throw new InvalidOperationException("CutExtrude direction did not resolve to a live entity");

        switch (live) {
            case Edge e:
                ((IEntity)e).Select4(true, null);
                break;
            case Feature feat:
                feat.Select2(true, 0);
                break;
            case SketchSegment seg:
                seg.Select4(true, null);
                break;
            default:
                throw new InvalidOperationException(
                    $"CutExtrude direction resolved to unsupported type {live.GetType().Name}");
        }
    }

    private static (int startType, double startOffset, bool flipStartOffset) ApplyStartCondition(
            File file, ExtrudeStartArgs start) {
        // SW quirk: start-from-entity selection mark is 32.
        const int startMark = 32;
        switch (start.Kind) {
            case ExtrudeStartKind.Sketch:
                return ((int)swStartConditions_e.swStartSketchPlane, 0.0, false);
            case ExtrudeStartKind.Surface:
                if (!start.Reference!.Select(file, startMark)) {
                    throw new InvalidOperationException(
                        $"CutExtrude: start.Surface Definition ({start.Reference.GetType().Name}) did not resolve+select onto mark {startMark}");
                }
                return ((int)swStartConditions_e.swStartSurface, 0.0, false);
            case ExtrudeStartKind.Vertex:
                if (!start.Reference!.Select(file, startMark)) {
                    throw new InvalidOperationException(
                        $"CutExtrude: start.Vertex Definition ({start.Reference.GetType().Name}) did not resolve+select onto mark {startMark}");
                }
                return ((int)swStartConditions_e.swStartVertex, 0.0, false);
            case ExtrudeStartKind.Offset:
                return ((int)swStartConditions_e.swStartOffset, start.Offset, start.Reversed);
            default:
                throw new InvalidOperationException($"CutExtrude: unknown start kind {start.Kind}");
        }
    }

    private static SelectData MakeMarkSelectData(File file, int mark) {
        var sd = (SelectData)file.ModelDoc.ISelectionManager.CreateSelectData();
        sd.Mark = mark;
        return sd;
    }

    // ---- Result shape --------------------------------------------------------------

    private static JsonNode BuildAddResult(JsonNode input, Feature feature) {
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    // ---- Typed args ----------------------------------------------------------------

    private sealed record CutExtrudeArgs(
        string SketchName,
        double Depth,
        bool Reversed,
        bool BothDirections,
        bool FlipSideToCut,
        double DraftAngle,
        double? DraftAngleB,
        Definition? Direction,
        Definition? DirectionB,
        IReadOnlyList<Definition> Contours,
        ExtrudeStartArgs Start,
        ExtrudeEndCondition.Payload End1,
        ExtrudeEndCondition.Payload? End2,
        IReadOnlyList<Definition>? FeatureScope     // null = AutoSelect
    ) {

        internal static CutExtrudeArgs Parse(JsonNode input) {
            var sketchName = input["sketch"]?["name"]?.GetValue<string>() ?? "";
            var depth = ReadDouble(input, "depth");
            var reversed = ReadBool(input, "reversed", false);
            var bothDir = ReadBool(input, "both_directions", false);
            var flipSide = ReadBool(input, "flip_side_to_cut", false);
            var draft = ReadDouble(input, "draft_angle", 0.0);
            double? draftB = ReadOptionalDouble(input, "draft_angle_b");
            var direction = Definition.FromJson(input["direction"]);
            var directionB = Definition.FromJson(input["direction_b"]);
            var contours = ReadDefinitionList(input, "contours");
            var start = ExtrudeStartArgs.Parse(input["start"]);
            var end1 = ExtrudeEndCondition.Payload.Parse(input["end_condition"])
                ?? ExtrudeEndCondition.Payload.Blind(depth);
            var end2 = bothDir
                ? ExtrudeEndCondition.Payload.Parse(input["end_condition_b"])
                  ?? ExtrudeEndCondition.Payload.Blind(depth)
                : null;
            // null/absent => AutoSelect; empty array => narrowed-to-no-bodies (must not collapse to null).
            IReadOnlyList<Definition>? featureScope = ReadFeatureScopeList(input, "feature_scope");
            return new CutExtrudeArgs(sketchName, depth, reversed, bothDir, flipSide,
                draft, draftB, direction, directionB, contours, start, end1, end2,
                featureScope);
        }
    }

    private static IReadOnlyList<Definition>? ReadFeatureScopeList(JsonNode node, string field) {
        // Empty array preserved as non-null; only null/absent collapses to AutoSelect.
        var v = node[field];
        if (v is null) return null;
        if (v.GetValueKind() == System.Text.Json.JsonValueKind.Null) return null;
        if (v is not JsonArray arr) return null;
        var list = new List<Definition>(arr.Count);
        foreach (var item in arr) {
            var d = Definition.FromJson(item);
            if (d is not null) list.Add(d);
        }
        return list;
    }

    private static IReadOnlyList<Definition> ReadDefinitionList(JsonNode node, string field) {
        var arr = node[field] as JsonArray;
        if (arr is null) return Array.Empty<Definition>();
        var list = new List<Definition>(arr.Count);
        foreach (var item in arr) {
            var d = Definition.FromJson(item);
            if (d is not null) list.Add(d);
        }
        return list;
    }

    private static double ReadDouble(JsonNode node, string field, double fallback = double.NaN) {
        var v = node[field];
        if (v is null) {
            if (!double.IsNaN(fallback)) return fallback;
            throw new ArgumentException($"Missing required numeric field '{field}'");
        }
        return v.GetValue<double>();
    }

    private static double? ReadOptionalDouble(JsonNode node, string field) {
        var v = node[field];
        return v is null ? null : v.GetValue<double>();
    }

    private static bool ReadBool(JsonNode node, string field, bool fallback) {
        var v = node[field];
        return v is null ? fallback : v.GetValue<bool>();
    }
}
