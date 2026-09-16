using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using static Sldworks.Core.Utils.JsonHelpers;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class LoftHandler {
    public const string TypeName = "Loft";
    public const string SwTypeName = "Blend";
    public const string CutTypeName = "CutLoft";
    public const string CutSwTypeName = "BlendCut";

    // SW-mandated selection marks. The data-driven path (CreateDefinition →
    // populate → CreateFeature) still requires profiles/guides/centerline to
    // be selected on the right mark before CreateDefinition is called — SW's
    // internal LoftFeatureData reads them off the selection list at create-
    // time, then exposes them through the typed getters afterward.
    private const int MarkProfile = 1;
    private const int MarkGuideCurves = 2;
    private const int MarkCenterline = 4;
    private const int MarkFeatureScopeBodies = 8;
    private const int MarkStartDirection = 16;
    private const int MarkEndDirection = 32;

    // SW quirk: ILoftFeatureData.StartTangencyType / EndTangencyType use a
    // small int enum that isn't surfaced in swconst. Values come from the
    // SolidWorks API help page for ILoftFeatureData (start/end tangency type):
    //   0 = None / Default (no constraint)
    //   1 = Normal to profile
    //   2 = Direction vector
    //   3 = Tangent to face   (only valid when the end profile is a face/edge)
    //   4 = Curvature to face (only valid when the end profile is a face/edge)
    private enum WireTangency { None = 0, NormalToProfile = 1, DirectionVector = 2, TangentToFace = 3, CurvatureToFace = 4 }

    // ---- Add -----------------------------------------------------------------------

    public static JsonNode Add(File file, JsonNode input) {
        var args = LoftArgs.Parse(input);
        var isCut = input["type"]?.GetValue<string>() == CutTypeName;
        if (isCut && input["merge"] is not null) throw new ArgumentException("CutLoft: merge is a boss-loft option");

        if (args.Profiles.Count < 2) {
            throw new ArgumentException(
                $"Loft: requires at least two profiles, got {args.Profiles.Count}");
        }

        file.ModelDoc.ClearSelection2(true);

        // SW quirk: `CreateDefinition(swFmBlend)` returns null in this redist
        // (the typed `LoftFeatureData` class is only reachable through
        // `Feature.GetDefinition` on an already-created loft — verified
        // empirically: the call literally returns null on the SW-2026 interop
        // assembly). The data-driven path therefore isn't available; we
        // author via `InsertProtrusionBlend2` and then patch the few options
        // it doesn't expose through `ModifyDefinition`.
        //
        // Selections in strictly decreasing mark order so SW's last-mark-wins
        // doesn't clobber earlier groups: end-direction (32) → start-direction
        // (16) → feature-scope (8) → centerline (4) → guide curves (2) →
        // profiles (1, in author order).
        SelectDirection(file, args.EndDirection, MarkEndDirection, "end_direction");
        SelectDirection(file, args.StartDirection, MarkStartDirection, "start_direction");
        SelectFeatureScopeBodies(file, args.FeatureScope);
        SelectCenterline(file, args.Centerline);
        SelectGuideCurves(file, args.GuideCurves);
        SelectProfiles(file, args.Profiles);

        var fm = file.ModelDoc.FeatureManager;

        // SW quirk: InsertProtrusionBlend2 returns null when the active
        // selection list doesn't satisfy its constraints (profiles must be on
        // mark 1, guides on 2, centerline on 4). Tessellation tolerance
        // factor 0.1 is the dialog default. AutoSelect=true unless we
        // explicitly scoped bodies. ForceNonRational is hard-coded to false
        // (the dialog default — rational NURBS allowed): it controls surface
        // rationality, not AdvancedSmoothing as a previous version assumed.
        // AdvancedSmoothing is written explicitly in ApplyPostCreateOptions.
        var autoSelect = !(args.FeatureScope is { Count: > 0 });
        var feature = isCut ? fm.InsertCutBlend(
            Closed: args.Close, KeepTangency: args.MergeTangentFaces,
            ForceNonRational: false, TessToleranceFactor: 0.1,
            StartMatchingType: (short)args.StartTangency, EndMatchingType: (short)args.EndTangency,
            IsThinBody: args.ThinFeature, Thickness1: args.ThinThickness ?? 0,
            Thickness2: args.ThinThickness2 ?? 0, ThinType: (short)args.ThinWallType,
            UseFeatScope: !autoSelect, UseAutoSelect: autoSelect) : fm.InsertProtrusionBlend2(
            Closed: args.Close,
            KeepTangency: args.MergeTangentFaces,
            ForceNonRational: false,
            TessToleranceFactor: 0.1,
            StartMatchingType: (short)args.StartTangency,
            EndMatchingType: (short)args.EndTangency,
            StartTangentLength: args.StartTangentLength,
            EndTangentLength: args.EndTangentLength,
            StartTangentDir: args.StartReverseTangent,
            EndTangentDir: args.EndReverseTangent,
            IsThinBody: args.ThinFeature,
            Thickness1: args.ThinThickness ?? 0.0,
            Thickness2: args.ThinThickness2 ?? 0.0,
            ThinType: (short)args.ThinWallType,
            Merge: args.Merge,
            UseFeatScope: args.FeatureScope is { Count: > 0 },
            UseAutoSelect: autoSelect,
            GuideCurveInfluence: (int)args.GuideInfluence) as Feature;

        if (feature is null) {
            throw new InvalidOperationException(
                "Loft: insertion returned null — check profile order, that all sketches " +
                "are closed, and that any guide curves are continuous from first to last profile.");
        }

        ApplyPostCreateOptions(file, feature, args);

        SldworksLog.Information("LoftHandler.Add: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return BuildAddResult(input, feature);
    }

    // Patch options that InsertProtrusionBlend2 doesn't take as parameters
    // (or sets to the wrong default). Per-guide tangency, draft angle +
    // reverse, centerline, AdvancedSmoothing, and NumberOfSections all land
    // here.
    //
    // SW quirk: `StartConstraintApplyToAll` / `EndConstraintApplyToAll` are
    // **get-only** per the SW API docs ("Set is not implemented") — the
    // setters always return E_NOTIMPL regardless of tangency state. We
    // never write them; Inspect preserves the native values.
    //
    // SW quirk: the draft setters return E_NOTIMPL when the matching
    // tangency is `None` (there's no constraint to draft against). Only
    // write draft when tangency is a real constraint.
    //
    // SW quirk: InsertProtrusionBlend2 leaves `AdvancedSmoothing=true` on
    // the resulting feature regardless of any input parameter (including
    // ForceNonRational) — we MUST explicitly write the desired value (true
    // OR false) every time to faithfully round-trip.
    private static void ApplyPostCreateOptions(File file, Feature feature, LoftArgs args) {
        var data = feature.GetDefinition() as ILoftFeatureData
            ?? throw new InvalidOperationException("Loft: feature data is unavailable");

        if (!data.AccessSelections(file.ModelDoc, null)) throw new InvalidOperationException("Loft: cannot access selections");
        try {
            data.StartTangentLength = args.StartTangentLength;
            data.EndTangentLength = args.EndTangentLength;
            data.ReverseStartTangentDirection = args.StartReverseTangent;
            data.ReverseEndTangentDirection = args.EndReverseTangent;
            data.GuideCurveInfluence = (int)args.GuideInfluence;
            if (args.StartTangency != WireTangency.None) {
                data.StartConstraintDraftAngle = args.StartDraftAngle;
                data.StartConstraintDraftAngleDirection = args.StartDraftReverse;
            }
            if (args.EndTangency != WireTangency.None) {
                data.EndConstraintDraftAngle = args.EndDraftAngle;
                data.EndConstraintDraftAngleDirection = args.EndDraftReverse;
            }
            for (short i = 0; i < args.GuideTangencyTypes.Count; i++) {
                data.SetGuideTangencyType(i, (short)args.GuideTangencyTypes[i]);
            }
            if (args.Centerline is not null) {
                // SW quirk: `InsertProtrusionBlend2` ignores the mark-4
                // centerline selection — the only reliable way to attach one
                // is to write it on the typed data and re-modify.
                data.Centerline = DefinitionResolver.Resolve(file, args.Centerline)
                    ?? throw new ArgumentException("Loft: centerline did not resolve");
            }
            data.AdvancedSmoothing = args.AdvancedSmoothing;
            if (args.NumberOfSections is double n) data.NumberOfSections = n;

            // SW quirk: `ModifyDefinition` must be called while AccessSelections
            // is still held — releasing first leaves the typed data
            // disconnected from the live feature and the commit is rejected
            // (silently, on this redist). Mirror FilletHandler's post-create
            // modify pattern.
            var ok = feature.ModifyDefinition(data, file.ModelDoc, null);
            if (!ok) {
                throw new InvalidOperationException(
                    "Loft: ModifyDefinition returned false during post-create patch");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    // ---- Edit ----------------------------------------------------------------------

    // Edit an existing loft IN PLACE (GetDefinition -> AccessSelections ->
    // mutate -> ModifyDefinition); we do NOT delete + re-add, so the feature
    // name — and every downstream name-ref / probe — survives. `input` is a FULL
    // loft payload (same shape Inspect emits / Add consumes), parsed by LoftArgs
    // so edit and create share one validation. The parametric scalars/options
    // Inspect reports are applied, AND the geometry SELECTIONS that Inspect emits
    // and Add resolves are re-resolved + re-set unconditionally from the provided
    // refs: the ordered `profiles` list, the unordered `guide_curves`, and the
    // single `centerline` (re-applying the same ref is idempotent; ModifyDefinition's
    // bool return is the validity gate — no compare-and-skip).
    //
    // Use the I-prefixed interface for setters; co-class setters can be no-ops.
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = LoftArgs.Parse(input);
        var isCut = feature.GetTypeName2() == CutSwTypeName;
        if (isCut && input["merge"] is not null) throw new ArgumentException("CutLoft: merge is a boss-loft option");

        var data = feature.GetDefinition() as ILoftFeatureData
            ?? throw new InvalidOperationException(
                $"Loft.Edit: feature {feature.Name} ({feature.GetTypeName2()}) does not expose ILoftFeatureData");

        // SW quirk: AccessSelections required before mutating referenced data;
        // missing ReleaseSelectionAccess wedges the document.
        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"Loft.Edit: AccessSelections failed on '{feature.Name}'");
        }
        try {
            // Thin <-> solid is a different feature topology, not an in-place
            // scalar edit (ILoftFeatureData exposes no IsThinFeature setter —
            // only the wall-thickness/type accessors). Refuse the mode flip.
            if (data.IsThinFeature() != args.ThinFeature) {
                throw new InvalidOperationException(
                    $"Loft.Edit: thin_feature can't be changed in place on '{feature.Name}' "
                    + $"({(data.IsThinFeature() ? "thin -> solid" : "solid -> thin")}) "
                    + "— delete and re-add");
            }
            if (args.Centerline is null && data.Centerline is not null)
                throw new NotSupportedException("Loft: clearing an existing centerline is not supported");

            // Re-point the geometry selections FIRST (before the per-guide tangency
            // setters, which are positional against the live guide list) so the
            // scalars apply to the NEW selection, mirroring Chamfer's "re-point
            // edges first". The provided refs are resolved and set unconditionally;
            // ModifyDefinition's bool return is the validity gate.
            RepointProfiles(file, data, args.Profiles, feature.Name);
            RepointGuideCurves(file, data, args.GuideCurves, feature.Name);
            RepointCenterline(file, data, args.Centerline, feature.Name);

            // Tangency types + lengths + reverse flags.
            data.StartTangencyType = (short)args.StartTangency;
            data.EndTangencyType = (short)args.EndTangency;
            if (args.StartTangency == WireTangency.DirectionVector)
                data.StartDirectionVector = DefinitionResolver.Resolve(file, args.StartDirection
                    ?? throw new ArgumentException("Loft: start_direction is required"))
                    ?? throw new ArgumentException("Loft: start_direction did not resolve");
            if (args.EndTangency == WireTangency.DirectionVector)
                data.EndDirectionVector = DefinitionResolver.Resolve(file, args.EndDirection
                    ?? throw new ArgumentException("Loft: end_direction is required"))
                    ?? throw new ArgumentException("Loft: end_direction did not resolve");
            data.StartTangentLength = args.StartTangentLength;
            data.EndTangentLength = args.EndTangentLength;
            data.ReverseStartTangentDirection = args.StartReverseTangent;
            data.ReverseEndTangentDirection = args.EndReverseTangent;

            // SW quirk: the draft setters return E_NOTIMPL when the matching
            // tangency is `None` (no constraint to draft against). Mirror
            // ApplyPostCreateOptions — only write draft for a real constraint.
            if (args.StartTangency != WireTangency.None) {
                data.StartConstraintDraftAngle = args.StartDraftAngle;
                data.StartConstraintDraftAngleDirection = args.StartDraftReverse;
            }
            if (args.EndTangency != WireTangency.None) {
                data.EndConstraintDraftAngle = args.EndDraftAngle;
                data.EndConstraintDraftAngleDirection = args.EndDraftReverse;
            }
            // SW quirk: `*ConstraintApplyToAll` setters return E_NOTIMPL.

            // Guide tangencies are positional against the live guide list.
            data.GuideCurveInfluence = (int)args.GuideInfluence;
            for (short i = 0; i < args.GuideTangencyTypes.Count; i++) {
                data.SetGuideTangencyType(i, (short)args.GuideTangencyTypes[i]);
            }

            data.Close = args.Close;
            if (!isCut) data.Merge = args.Merge;
            data.MaintainTangency = args.MergeTangentFaces;
            data.AdvancedSmoothing = args.AdvancedSmoothing;
            if (args.NumberOfSections is double n) data.NumberOfSections = n;

            // Thin-wall params only apply when the feature is (and stays) thin.
            if (args.ThinFeature) {
                data.ThinWallType = (short)args.ThinWallType;
                if (!isCut) {
                    if (args.ThinThickness is double t1) data.SetWallThickness(true, t1);
                    if (args.ThinWallType == swThinWallType_e.swThinWallTwoDirection && args.ThinThickness2 is double t2)
                        data.SetWallThickness(false, t2);
                }
            }

            // Apply feature_scope unconditionally (assume the payload is authoritative — no
            // compare/skip). null/empty => AutoSelect over all bodies; non-empty => restrict to
            // the resolved bodies. ModifyDefinition's bool return is the validity gate.
            var scopeBodies = FeatureScope.ResolveScopeBodies(file, args.FeatureScope);
            if (scopeBodies is null) data.AutoSelect = true;
            else { data.AutoSelect = false; data.FeatureScopeBodies = scopeBodies; }

            // SW quirk: ModifyDefinition must be called while AccessSelections is
            // still held — releasing first disconnects the typed data from the
            // live feature and the commit is silently rejected on this redist.
            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"Loft.Edit: ModifyDefinition returned false on '{feature.Name}' "
                    + "— a value (tangency/draft/length/sections) invalid against the profiles?");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        if (isCut && args.ThinFeature) {
            SetCutWallThickness(feature, true, args.ThinThickness
                ?? throw new ArgumentException("CutLoft: thin_thickness is required"));
            if (args.ThinWallType == swThinWallType_e.swThinWallTwoDirection)
                SetCutWallThickness(feature, false, args.ThinThickness2
                    ?? throw new ArgumentException("CutLoft: thin_thickness2 is required"));
        }

        // Roll to end so the change propagates downstream (ForceRebuildAll). NOT
        // AfterFeature: a mid-tree loft must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information("LoftHandler.Edit: {Name}", feature.Name);
        return Inspect(file, feature);
    }

    // ---- Edit: selection re-pointing ----------------------------------------------

    // Resolve a single Loft profile spec to its live COM object: a sketch ref
    // resolves to the live sketch Feature by name; a Definition (face/edge/vertex/
    // refaxis) resolves through DefinitionResolver — the SAME resolution Add's
    // SelectProfiles performs (it Select2's the sketch feature / Definition.Select's
    // the geometry). Throws (never silently drops) on an unresolvable ref, matching
    // the no-silent-fallback bar.
    private static object ResolveProfileLive(File file, LoftProfileSpec spec, string label) {
        if (spec.SketchName is not null) {
            return FindFeatureByName(file, spec.SketchName)
                ?? throw new InvalidOperationException(
                    $"Loft.Edit: {label} sketch '{spec.SketchName}' not found");
        }
        if (spec.Definition is not null) {
            return DefinitionResolver.Resolve(file, spec.Definition)
                ?? throw new InvalidOperationException(
                    $"Loft.Edit: {label} ({spec.Definition.GetType().Name}) did not resolve to a live entity");
        }
        throw new InvalidOperationException(
            $"Loft.Edit: {label} has neither sketch name nor Definition");
    }

    // Re-point the ORDERED profiles list in place. Loft profiles are order-sensitive
    // (the surface interpolates profile[0] -> profile[1] -> ...); set the resolved list
    // in author order, unconditionally.
    private static void RepointProfiles(
        File file, ILoftFeatureData data, IReadOnlyList<LoftProfileSpec> profiles, string featureName) {
        if (profiles.Count < 2) {
            // LoftArgs/Add already enforce >= 2; defensive — never ship a degenerate loft.
            throw new InvalidOperationException(
                $"Loft.Edit: '{featureName}' requires at least two profiles, got {profiles.Count}");
        }

        // Resolve every profile and set the ordered list unconditionally (re-applying the same
        // profiles is idempotent). ModifyDefinition's bool return is the validity gate.
        var resolvedLive = new List<object>(profiles.Count);
        for (var i = 0; i < profiles.Count; i++) {
            var live = ResolveProfileLive(file, profiles[i], $"profiles[{i}]");
            resolvedLive.Add(live);
        }

        // SW quirk: set_Profiles(Object PDisp) is a SAFEARRAY-of-IDispatch setter
        // (reflection-confirmed: `set_Profiles(System.Object)`, getter hands back
        // object[]). A bare object[] crashes the marshaller — wrap each live entry
        // in a DispatchWrapper, exactly as ChamferHandler does for set_Edges.
        data.Profiles = resolvedLive.Select(o => new DispatchWrapper(o)).ToArray();
        SldworksLog.Information(
            "LoftHandler.Edit: re-pointed '{Name}' onto {Count} profile(s)",
            featureName, resolvedLive.Count);
    }

    private static void RepointGuideCurves(
        File file, ILoftFeatureData data, IReadOnlyList<Definition> guideDefs, string featureName) {
        var resolvedLive = new List<object>(guideDefs.Count);
        for (var i = 0; i < guideDefs.Count; i++) {
            var live = DefinitionResolver.Resolve(file, guideDefs[i])
                ?? throw new InvalidOperationException(
                    $"Loft.Edit: guide_curves[{i}] ({guideDefs[i].GetType().Name}) did not resolve "
                    + $"to a live entity on '{featureName}'.");
            resolvedLive.Add(live);
        }

        data.GuideCurves = resolvedLive.Select(o => new DispatchWrapper(o)).ToArray();
        SldworksLog.Information(
            "LoftHandler.Edit: re-pointed '{Name}' onto {Count} guide curve(s)",
            featureName, resolvedLive.Count);
    }

    private static void RepointCenterline(
        File file, ILoftFeatureData data, Definition? centerlineDef, string featureName) {
        if (centerlineDef is null) return;

        // Resolve and set unconditionally (re-applying the same centerline is idempotent).
        // ModifyDefinition's bool return is the validity gate — no compare-and-skip.
        var live = DefinitionResolver.Resolve(file, centerlineDef)
            ?? throw new InvalidOperationException(
                $"Loft.Edit: centerline ({centerlineDef.GetType().Name}) did not resolve to a live "
                + $"entity on '{featureName}'.");

        // SW quirk: Centerline is a single-dispatch property (Add sets it as a bare
        // object); set directly, no DispatchWrapper.
        data.Centerline = live;
        SldworksLog.Information("LoftHandler.Edit: re-pointed centerline on '{Name}'", featureName);
    }

    // ---- Inspect -------------------------------------------------------------------

    public static JsonNode Inspect(File file, Feature feature) {
        var data = feature.GetDefinition() as LoftFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose ILoftFeatureData");

        // SW quirk: AccessSelections required before reading referenced selections;
        // missing ReleaseSelectionAccess wedges the document.
        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"Loft.Inspect({feature.Name}): AccessSelections failed");
        }
        try {
            var profiles = CaptureEntityList(file, data.Profiles, "profile");
            if (profiles.Count == 0) {
                throw new InvalidOperationException(
                    $"Loft.Inspect({feature.Name}): no profiles read off LoftFeatureData.Profiles");
            }
            var guideCurves = CaptureEntityList(file, data.GuideCurves, "guide_curve");
            var centerline = CaptureOptionalEntity(file, data.Centerline);
            var startDirection = CaptureOptionalEntity(file, data.StartDirectionVector);
            var endDirection = CaptureOptionalEntity(file, data.EndDirectionVector);

            var guideTangencyTypes = new JsonArray();
            for (short i = 0; i < data.GetGuideCurvesCount(); i++) {
                guideTangencyTypes.Add(TangencyToWire((WireTangency)data.GetGuideTangencyType(i)));
            }

            var featureScope = FeatureScope.Capture(file, data.FeatureScopeBodies);
            var bodies = CollectBodies(file);

            var result = new JsonObject {
                ["type"] = feature.GetTypeName2() == CutSwTypeName ? CutTypeName : TypeName,
                ["name"] = feature.Name,
                ["profiles"] = profiles,
                ["guide_curves"] = guideCurves,
                ["centerline"] = centerline,
                ["start_tangency"] = TangencyToWire((WireTangency)data.StartTangencyType),
                ["start_direction"] = startDirection,
                ["start_tangent_length"] = data.StartTangentLength,
                ["start_reverse_tangent"] = data.ReverseStartTangentDirection,
                ["start_draft_angle"] = data.StartConstraintDraftAngle,
                ["start_draft_reverse"] = data.StartConstraintDraftAngleDirection,
                ["start_apply_to_all"] = data.StartConstraintApplyToAll,
                ["end_tangency"] = TangencyToWire((WireTangency)data.EndTangencyType),
                ["end_direction"] = endDirection,
                ["end_tangent_length"] = data.EndTangentLength,
                ["end_reverse_tangent"] = data.ReverseEndTangentDirection,
                ["end_draft_angle"] = data.EndConstraintDraftAngle,
                ["end_draft_reverse"] = data.EndConstraintDraftAngleDirection,
                ["end_apply_to_all"] = data.EndConstraintApplyToAll,
                ["guide_influence"] = GuideInfluenceToWire((swGuideCurveInfluence_e)data.GuideCurveInfluence),
                ["guide_tangency_types"] = guideTangencyTypes,
                ["close"] = data.Close,
                ["merge_tangent_faces"] = data.MaintainTangency,
                ["advanced_smoothing"] = data.AdvancedSmoothing,
                // Centerline cross-section sample count (SW dialog: "Number of
                // sections" slider, only active when a centerline is set).
                // Typed `Double` on ILoftFeatureData; SW stores the slider as
                // a whole number but the API exposes a double. Drives the
                // tessellation of the lofted surface along the centerline —
                // omitting it leaks SW's default (which differs per part) and
                // produces a subtly different body shape on rebuild.
                ["number_of_sections"] = data.NumberOfSections,
                ["thin_feature"] = data.IsThinFeature(),
                ["thin_wall_type"] = ThinWallTypeToWire((swThinWallType_e)data.ThinWallType),
                ["feature_scope"] = featureScope,
                ["echo_bodies"] = bodies,
            };
            if (feature.GetTypeName2() != CutSwTypeName) result["merge"] = data.Merge;
            if (data.IsThinFeature()) {
                var cut = feature.GetTypeName2() == CutSwTypeName;
                result["thin_thickness"] = cut ? CutWallDimension(feature, true).SystemValue : data.GetWallThickness(true);
                result["thin_thickness2"] = data.ThinWallType == (short)swThinWallType_e.swThinWallTwoDirection
                    ? cut ? CutWallDimension(feature, false).SystemValue : data.GetWallThickness(false) : null;
            }
            return result;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private static IDimension CutWallDimension(Feature feature, bool forward) => feature.Parameter(forward ? "D1" : "D2") as IDimension
        ?? throw new NotSupportedException($"CutLoft: driving wall dimension {(forward ? "D1" : "D2")} is unavailable");

    private static void SetCutWallThickness(Feature feature, bool forward, double thickness) {
        // SW's thin-cut feature-data values do not drive its wall geometry; the dimensions do.
        var status = CutWallDimension(feature, forward).SetSystemValue3(thickness,
            (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
        if (status != 0) throw new InvalidOperationException($"CutLoft: wall dimension update failed ({status})");
    }

    private static JsonArray CaptureEntityList(File file, object? raw, string label) {
        var arr = new JsonArray();
        foreach (var item in (object[]?)raw ?? []) {
            arr.Add(CaptureLoftEntity(file, item
                ?? throw new InvalidOperationException($"Loft.Inspect: null {label} reference"), label));
        }
        return arr;
    }

    private static JsonNode? CaptureOptionalEntity(File file, object? raw) {
        if (raw is null) return null;
        return CaptureLoftEntity(file, raw, "centerline/direction");
    }

    // Loft entries can be sketches (Feature), faces (Face2), edges (Edge),
    // vertices (Vertex), reference axes (RefAxis), or sketch entities. Sketches
    // are emitted in the same {type=Sketch, name=<n>} shape the runner uses
    // everywhere else so the emitter can resolve them back to a registered
    // Sketch object; everything else goes through DefinitionCapture.
    private static JsonNode CaptureLoftEntity(File file, object entity, string label) {
        if (entity is Feature feat) {
            var typeName = feat.GetTypeName2() ?? "";
            if (typeName == "ProfileFeature" || typeName == "3DProfileFeature") {
                return new JsonObject {
                    ["type"] = "Sketch",
                    ["name"] = FeatureName.Resolve(file, feat.Name),
                };
            }
            // RefAxis / RefPlane round-trip via geometric Definition capture below.
        }
        var def = DefinitionCapture.Capture(entity)
            ?? throw new InvalidOperationException(
                $"Loft.Inspect: unsupported {label} runtime type {entity.GetType().Name}");
        return def.ToJson();
    }

    private static JsonArray CollectBodies(File file) {
        var arr = new JsonArray();
        var raw = (object[]?)((IPartDoc)file.ModelDoc).GetBodies2((int)swBodyType_e.swSolidBody, true);
        if (raw is null) return arr;
        foreach (Body2 body in raw) {
            arr.Add(DefinitionCapture.Capture(body).ToJson());
        }
        return arr;
    }

    // ---- Selection helpers ---------------------------------------------------------

    private static void SelectProfiles(File file, IReadOnlyList<LoftProfileSpec> profiles) {
        for (var i = 0; i < profiles.Count; i++) {
            var p = profiles[i];
            if (p.SketchName is not null) {
                var feat = FindFeatureByName(file, p.SketchName)
                    ?? throw new ArgumentException(
                        $"Loft: profile[{i}] sketch '{p.SketchName}' not found");
                feat.Select2(true, MarkProfile);
            } else if (p.Definition is not null) {
                if (!p.Definition.Select(file, MarkProfile)) {
                    throw new InvalidOperationException(
                        $"Loft: profile[{i}] ({p.Definition.GetType().Name}) did not resolve+select onto mark {MarkProfile}");
                }
            } else {
                throw new ArgumentException($"Loft: profile[{i}] has neither sketch name nor Definition");
            }
        }
    }

    private static void SelectGuideCurves(File file, IReadOnlyList<Definition> guideCurves) {
        Definition.SelectAll(file, guideCurves, MarkGuideCurves, "Loft.guide_curves");
    }

    private static void SelectCenterline(File file, Definition? centerline) {
        if (centerline is null) return;
        if (!centerline.Select(file, MarkCenterline)) {
            throw new InvalidOperationException(
                $"Loft: centerline ({centerline.GetType().Name}) did not resolve+select onto mark {MarkCenterline}");
        }
    }

    private static void SelectDirection(File file, Definition? direction, int mark, string label) {
        if (direction is null) return;
        if (!direction.Select(file, mark)) {
            throw new InvalidOperationException(
                $"Loft: {label} ({direction.GetType().Name}) did not resolve+select onto mark {mark}");
        }
    }

    private static void SelectFeatureScopeBodies(File file, IReadOnlyList<Definition>? bodies) {
        if (bodies is null || bodies.Count == 0) return;
        Definition.SelectAll(file, bodies, MarkFeatureScopeBodies, "Loft.feature_scope");
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

    private static JsonNode BuildAddResult(JsonNode input, Feature feature) {
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    // ---- Wire enum mapping ---------------------------------------------------------

    private static string TangencyToWire(WireTangency value) => value switch {
        WireTangency.None => "None",
        WireTangency.NormalToProfile => "NormalToProfile",
        WireTangency.DirectionVector => "DirectionVector",
        WireTangency.TangentToFace => "TangentToFace",
        WireTangency.CurvatureToFace => "CurvatureToFace",
        _ => throw new ArgumentException($"Loft: unknown tangency int {(int)value}"),
    };

    private static WireTangency ParseTangency(string? s) => s switch {
        null or "None" => WireTangency.None,
        "NormalToProfile" => WireTangency.NormalToProfile,
        "DirectionVector" => WireTangency.DirectionVector,
        "TangentToFace" => WireTangency.TangentToFace,
        "CurvatureToFace" => WireTangency.CurvatureToFace,
        _ => throw new ArgumentException($"Loft: unknown tangency literal '{s}'"),
    };

    private static string GuideInfluenceToWire(swGuideCurveInfluence_e v) => v switch {
        swGuideCurveInfluence_e.swGuideCurveInfluenceNextGuide  => "NextGuide",
        swGuideCurveInfluence_e.swGuideCurveInfluenceNextSharp  => "NextSharp",
        swGuideCurveInfluence_e.swGuideCurveInfluenceNextEdge   => "NextEdge",
        swGuideCurveInfluence_e.swGuideCurveInfluenceNextGlobal => "NextGlobal",
        _ => throw new ArgumentException($"Loft: unknown guide influence int {(int)v}"),
    };

    private static swGuideCurveInfluence_e ParseGuideInfluence(string? s) => s switch {
        null or "NextGuide" => swGuideCurveInfluence_e.swGuideCurveInfluenceNextGuide,
        "NextSharp"  => swGuideCurveInfluence_e.swGuideCurveInfluenceNextSharp,
        "NextEdge"   => swGuideCurveInfluence_e.swGuideCurveInfluenceNextEdge,
        "NextGlobal" => swGuideCurveInfluence_e.swGuideCurveInfluenceNextGlobal,
        _ => throw new ArgumentException($"Loft: unknown guide_influence literal '{s}'"),
    };

    private static string ThinWallTypeToWire(swThinWallType_e v) => v switch {
        swThinWallType_e.swThinWallOneDirection  => "OneDirection",
        swThinWallType_e.swThinWallOppDirection  => "OppositeDirection",
        swThinWallType_e.swThinWallMidPlane      => "MidPlane",
        swThinWallType_e.swThinWallTwoDirection  => "TwoDirections",
        _ => throw new ArgumentException($"Loft: unknown thin wall type int {(int)v}"),
    };

    private static swThinWallType_e ParseThinWallType(string? s) => s switch {
        null or "OneDirection" => swThinWallType_e.swThinWallOneDirection,
        "OppositeDirection" => swThinWallType_e.swThinWallOppDirection,
        "MidPlane"          => swThinWallType_e.swThinWallMidPlane,
        "TwoDirections"     => swThinWallType_e.swThinWallTwoDirection,
        _ => throw new ArgumentException($"Loft: unknown thin_wall_type literal '{s}'"),
    };

    // ---- Typed args ----------------------------------------------------------------

    // A Loft profile is either a sketch (by name) or a Definition (face/edge/vertex/refaxis).
    private sealed record LoftProfileSpec(string? SketchName, Definition? Definition);

    // SW emits sketch refs (centerline, start/end direction) as
    // `{"type": "Sketch", "name": "<n>"}` — not the `{"kind": "feature", ...}`
    // shape `Definition.FromJson` expects. Translate the Sketch ref to a
    // FeatureDefinition before dispatching so the resolver finds the sketch
    // feature by name; pass other shapes through unchanged.
    private static Definition? ParseDefinitionOrSketchRef(JsonNode? node) {
        if (node is null) return null;
        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Null) return null;
        if (node["type"]?.GetValue<string>() == "Sketch") {
            var name = node["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Loft: sketch reference requires a name");
            return new FeatureDefinition(name);
        }
        return Definition.FromJson(node)
            ?? throw new ArgumentException("Loft: invalid centerline or direction reference");
    }

    private sealed record LoftArgs(
        IReadOnlyList<LoftProfileSpec> Profiles,
        IReadOnlyList<Definition> GuideCurves,
        Definition? Centerline,
        WireTangency StartTangency,
        Definition? StartDirection,
        double StartTangentLength,
        bool StartReverseTangent,
        double StartDraftAngle,
        bool StartDraftReverse,
        WireTangency EndTangency,
        Definition? EndDirection,
        double EndTangentLength,
        bool EndReverseTangent,
        double EndDraftAngle,
        bool EndDraftReverse,
        swGuideCurveInfluence_e GuideInfluence,
        IReadOnlyList<WireTangency> GuideTangencyTypes,
        bool Close,
        bool Merge,
        bool MergeTangentFaces,
        bool AdvancedSmoothing,
        bool ThinFeature,
        swThinWallType_e ThinWallType,
        double? ThinThickness,
        double? ThinThickness2,
        IReadOnlyList<Definition>? FeatureScope,
        double? NumberOfSections) {

        internal static LoftArgs Parse(JsonNode input) {
            if (ReadBool(input, "start_apply_to_all", false) || ReadBool(input, "end_apply_to_all", false))
                throw new NotSupportedException("Loft: apply-to-all constraint setters are not implemented by SolidWorks");
            var profilesNode = input["profiles"]
                ?? throw new ArgumentException("Loft: missing 'profiles'");
            if (profilesNode is not JsonArray pArr) {
                throw new ArgumentException("Loft: 'profiles' must be an array");
            }
            var profiles = new List<LoftProfileSpec>(pArr.Count);
            for (var i = 0; i < pArr.Count; i++) {
                profiles.Add(ParseProfile(pArr[i], i));
            }

            var guides = Definition.FromJsonArray(input["guide_curves"] ?? new JsonArray(), "Loft.guide_curves");

            var centerline = ParseDefinitionOrSketchRef(input["centerline"]);
            var startDir = ParseDefinitionOrSketchRef(input["start_direction"]);
            var endDir = ParseDefinitionOrSketchRef(input["end_direction"]);

            var startTangency = ParseTangency(input["start_tangency"]?.GetValue<string>());
            var endTangency = ParseTangency(input["end_tangency"]?.GetValue<string>());

            var guideTangencyTypes = new List<WireTangency>();
            if (input["guide_tangency_types"]?.AsArray() is { } gtArr) {
                foreach (var node in gtArr) {
                    guideTangencyTypes.Add(ParseTangency(node?.GetValue<string>()
                        ?? throw new ArgumentException("Loft: guide tangency must be a tangency literal")));
                }
            }
            if (guideTangencyTypes.Count != 0 && guideTangencyTypes.Count != guides.Count)
                throw new ArgumentException("Loft: guide_tangency_types must have one entry per guide curve");

            var featureScope = Definition.FromJsonArray(input["feature_scope"] ?? new JsonArray(), "Loft.feature_scope");

            return new LoftArgs(
                profiles, guides, centerline,
                startTangency, startDir,
                ReadOptionalDouble(input, "start_tangent_length") ?? 1.0,
                ReadBool(input, "start_reverse_tangent", false),
                ReadOptionalDouble(input, "start_draft_angle") ?? 0.0,
                ReadBool(input, "start_draft_reverse", false),
                endTangency, endDir,
                ReadOptionalDouble(input, "end_tangent_length") ?? 1.0,
                ReadBool(input, "end_reverse_tangent", false),
                ReadOptionalDouble(input, "end_draft_angle") ?? 0.0,
                ReadBool(input, "end_draft_reverse", false),
                ParseGuideInfluence(input["guide_influence"]?.GetValue<string>()),
                guideTangencyTypes,
                ReadBool(input, "close", false),
                ReadBool(input, "merge", true),
                ReadBool(input, "merge_tangent_faces", false),
                ReadBool(input, "advanced_smoothing", false),
                ReadBool(input, "thin_feature", false),
                ParseThinWallType(input["thin_wall_type"]?.GetValue<string>()),
                ReadOptionalDouble(input, "thin_thickness"),
                ReadOptionalDouble(input, "thin_thickness2"),
                featureScope,
                ReadOptionalDouble(input, "number_of_sections"));
        }

        private static LoftProfileSpec ParseProfile(JsonNode? node, int index) {
            if (node is null) {
                throw new ArgumentException($"Loft: profiles[{index}] is null");
            }
            // Sketch ref: {"type": "Sketch", "name": "<n>"}
            var typeField = node["type"]?.GetValue<string>();
            if (typeField == "Sketch") {
                var name = node["name"]?.GetValue<string>()
                    ?? throw new ArgumentException($"Loft: profiles[{index}] is a Sketch ref with no 'name'");
                return new LoftProfileSpec(name, null);
            }
            // Otherwise must be a Definition (face/edge/vertex/refaxis/refplane).
            var def = Definition.FromJson(node)
                ?? throw new ArgumentException(
                    $"Loft: profiles[{index}] is neither a Sketch ref nor a recognized Definition");
            return new LoftProfileSpec(null, def);
        }
    }

}
