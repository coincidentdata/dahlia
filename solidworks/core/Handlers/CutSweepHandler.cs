using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class CutSweepHandler {
    public const string TypeName = "CutSweep";

    // SW quirk: GetTypeName2() returns "Cut-Sweep" (with hyphen).
    public const string SwTypeName = "Cut-Sweep";

    // SW quirk: InsertCutSwept2 reads selections from these specific marks; otherwise the call silently fails.
    private const int MarkProfile = 1;
    private const int MarkPath = 4;
    private const int MarkGuideCurves = 2;
    private const int MarkTwistDirection = 128;
    private const int MarkFeatureScopeBodies = 8;

    private enum WireTwistKind { None, Specified, KeepNormalConstant, Direction, MinimumTwist, TangentAdjacentFaces }

    private enum WireProfileType { Sketch, Circular, Solid }

    private enum WireSweepDirection { None = -1, DirectionA = 0, Both = 1, DirectionB = 2 }

    private static short ResolveTangency(string s) => s switch {
        "None" => (short)swTangencyType_e.swTangencyNone,
        "NormalToProfile" => (short)swTangencyType_e.swTangencyNormalToProfile,
        _ => throw new ArgumentException($"CutSweep: unknown tangency literal '{s}'"),
    };

    private static string TangencyToWire(int v) => (swTangencyType_e)v switch {
        swTangencyType_e.swTangencyNormalToProfile => "NormalToProfile",
        _ => "None",
    };

    // ---- Add -----------------------------------------------------------------------

    public static JsonNode Add(File file, JsonNode input) {
        var args = SweepArgs.Parse(input);

        if (args.ProfileType == WireProfileType.Solid) {
            throw new NotSupportedException(
                "CutSweep: profile_type='Solid' is forward-compat schema only; "
                + "handler implementation deferred until a real authoring case lands");
        }

        // SW quirk: InsertCutSwept2 silently returns null on a wide class of profile/path/direction
        // combinations that the data-driven CreateDefinition + CreateFeature path handles fine.
        // Sequence selections in strictly decreasing mark order: twist direction (128) →
        // feature_scope (8) → path (4) → guide curves (2) → profile (1).
        file.ModelDoc.ClearSelection2(true);

        // Twist-direction reference (when twist=Direction) uses mark 128.
        SelectTwistDirection(file, args.Twist);
        // Feature-scope bodies on mark 8.
        SelectFeatureScopeBodies(file, args.FeatureScope);
        SelectPath(file, args.Path);
        SelectGuideCurves(file, args.GuideCurves);
        if (args.ProfileType == WireProfileType.Sketch) {
            SelectProfile(file, args.ProfileSketchName);
        }

        // SW quirk: cuts spanning multiple bodies open a "select bodies to
        // keep" dialog; PromptBodiesToKeepNotify is the only way to answer
        // from code. Keep every candidate here — the post-Add DropBodies
        // command re-fires the cut and applies any drops via ray-resolved
        // probes.
        using var bodiesScope = BodiesToKeepScope.Register(file);

        var fm = file.ModelDoc.FeatureManager;
        var data = fm.CreateDefinition((int)swFeatureNameID_e.swFmSweepCut) as SweepFeatureData
            ?? throw new InvalidOperationException(
                "CutSweep: CreateDefinition(swFmSweepCut) did not return a SweepFeatureData");

        PopulateSweepData(data, args);

        var feature = (Feature?)fm.CreateFeature(data);
        if (feature is null) {
            throw new InvalidOperationException(
                "CutSweep: CreateFeature failed — check profile is closed, path is connected, and (if guide curves) they're tangent to the profile");
        }

        SldworksLog.Information("CutSweepHandler.Add: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return BuildAddResult(input, feature);
    }

    // Populate SweepFeatureData fields BEFORE CreateFeature.
    private static void PopulateSweepData(SweepFeatureData data, SweepArgs args) {
        var t = args.Twist;
        if (args.ProfileType == WireProfileType.Circular) {
            data.CircularProfile = true;
            data.CircularProfileDiameter = args.CircularDiameter
                ?? throw new ArgumentException("CutSweep: profile_type='Circular' requires circular_diameter");
        } else {
            data.CircularProfile = false;
        }
        // SW quirk: only write Direction when the wire carries an explicit value (DirectionA/Both/DirectionB).
        // Direction=None=-1 is the "unspecified" sentinel; writing -1 directly to the FeatureData makes
        // CreateFeature reject the call. Leaving Direction at its CreateDefinition default lets SW pick
        // the sweep direction.
        if (args.Direction != WireSweepDirection.None) {
            data.Direction = (int)args.Direction;
        }
        data.StartTangencyType = ResolveTangency(args.StartTangency);
        data.EndTangencyType = ResolveTangency(args.EndTangency);

        // Twist control + path alignment.
        switch (t.Kind) {
            case WireTwistKind.None:
                data.TwistControlType = (short)swTwistControlType_e.swTwistControlFollowPath;
                break;
            case WireTwistKind.KeepNormalConstant:
                data.TwistControlType = (short)swTwistControlType_e.swTwistControlKeepNormalConstant;
                break;
            case WireTwistKind.Specified:
                data.TwistControlType = (short)(t.KeepNormal
                    ? swTwistControlType_e.swTwistControlNormalConstantTwistAlongPath
                    : swTwistControlType_e.swTwistControlConstantTwistAlongPath);
                data.D1ReverseTwistDir = t.Angle < 0;
                data.SetTwistAngle(Math.Abs(t.Angle));
                if (t.AngleB.HasValue) {
                    data.D2ReverseTwistDir = t.AngleB.Value < 0;
                    data.SetD2TwistAngle(Math.Abs(t.AngleB.Value));
                }
                break;
            case WireTwistKind.MinimumTwist:
                data.TwistControlType = (short)swTwistControlType_e.swTwistControlFollowPath;
                data.PathAlignmentType = (int)swTangencyType_e.swMinimumTwist;
                break;
            case WireTwistKind.Direction:
                data.TwistControlType = (short)swTwistControlType_e.swTwistControlFollowPath;
                data.PathAlignmentType = (int)swTangencyType_e.swTangencyDirectionVector;
                break;
            case WireTwistKind.TangentAdjacentFaces:
                data.TwistControlType = (short)swTwistControlType_e.swTwistControlFollowPath;
                data.PathAlignmentType = (int)swTangencyType_e.swTangencyAllFaces;
                break;
        }

        // SW quirk: only flip AutoSelect to true when no explicit bodies — never write false.
        // Flipping AutoSelect to false makes CreateFeature reject otherwise-valid input on
        // some configurations.
        if (!(args.FeatureScope is { Count: > 0 })) {
            data.AutoSelect = true;
        }
        data.MaintainTangency = args.MergeTangentFaces;
        data.AlignWithEndFaces = args.AlignWithEndFaces;
        data.ThinFeature = args.ThinFeature;
        if (args.ThinFeature && args.ThinThickness.HasValue) {
            data.SetWallThickness(false, args.ThinThickness.Value);
        }
    }

    private static void SelectFeatureScopeBodies(File file, IReadOnlyList<Definition>? bodies) {
        if (bodies is null || bodies.Count == 0) return;
        Definition.SelectAll(file, bodies, MarkFeatureScopeBodies, "CutSweep.feature_scope");
    }

    // ---- Edit ----------------------------------------------------------------------

    // Edit an existing cut-sweep IN PLACE (GetDefinition -> AccessSelections -> mutate ->
    // ModifyDefinition) so the feature name — and every downstream name-ref / probe —
    // survives; we never delete + re-add. `input` is a FULL cut-sweep payload (same shape
    // Inspect emits / Add consumes), parsed by SweepArgs so edit and create share one
    // validation. Parametric scalars/options/flags are applied — exactly the fields Inspect
    // READS — AND the re-pointable geometry selections (Profile sketch, Path, GuideCurves)
    // are re-resolved + re-set unconditionally from the provided refs (re-applying the same
    // ref is idempotent; ModifyDefinition's bool return is the validity gate — no
    // compare-and-skip). What stays
    // as-built: FeatureScopeBodies (Inspect reads it but it's an AutoSelect-driven scope, not
    // an authored profile/path/guide ref) and the twist=Direction vector (a mark-128
    // selection refused upstream).
    //
    // CRITICAL: cast to ISweepFeatureData (the interface), NOT SweepFeatureData (the
    // read-only co-class Add/Inspect use). The co-class exposes zero setters; the
    // interface exposes the 30+ set_* members this edit needs.
    //
    // NOTE: cuts never merge — there is no `merge` field in this handler's Inspect output,
    // so (unlike boss Sweep) Edit does NOT touch data.Merge.
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = SweepArgs.Parse(input);

        if (args.ProfileType == WireProfileType.Solid) {
            throw new NotSupportedException(
                "CutSweep.Edit: profile_type='Solid' is forward-compat schema only; "
                + "handler implementation deferred until a real authoring case lands");
        }

        var data = feature.GetDefinition() as ISweepFeatureData
            ?? throw new InvalidOperationException(
                $"CutSweep.Edit: feature {feature.Name} does not expose ISweepFeatureData");

        // Refuse a profile-type change: Sketch<->Circular re-points (or drops) the profile
        // selection — a different feature, not an in-place edit.
        var currentlyCircular = data.CircularProfile;
        var requestedCircular = args.ProfileType == WireProfileType.Circular;
        if (currentlyCircular != requestedCircular) {
            throw new InvalidOperationException(
                $"CutSweep.Edit: profile_type can't be changed in place on '{feature.Name}' "
                + $"({(currentlyCircular ? "Circular" : "Sketch")} -> {(requestedCircular ? "Circular" : "Sketch")}) "
                + "— delete and re-add");
        }
        // Refuse a twist=Direction change: its vector is a geometry selection (mark 128),
        // which Edit never re-points.
        if (args.Twist.Kind == WireTwistKind.Direction) {
            throw new InvalidOperationException(
                $"CutSweep.Edit: twist='Direction' can't be changed in place on '{feature.Name}' "
                + "— its direction reference is a geometry selection; delete and re-add");
        }

        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"CutSweep.Edit({feature.Name}): AccessSelections failed");
        }
        try {
            // Re-point geometry selections FIRST (inside the open AccessSelections block, before
            // ModifyDefinition) so the scalar/option edits below apply to the NEW selection.
            // The provided refs are resolved and set unconditionally; ModifyDefinition's bool
            // return is the validity gate.
            RepointProfileIfChanged(file, data, args, feature.Name);
            RepointPathIfChanged(file, data, args.Path, feature.Name);
            RepointGuideCurvesIfChanged(file, data, args.GuideCurves, feature.Name);

            ApplyEditableFields(data, args);

            // Apply feature_scope unconditionally (assume the payload is authoritative — no
            // compare/skip). null/empty => AutoSelect over all bodies; non-empty => restrict to
            // the resolved bodies. ModifyDefinition's bool return is the validity gate.
            var scopeBodies = FeatureScope.ResolveScopeBodies(file, args.FeatureScope);
            if (scopeBodies is null) data.AutoSelect = true;
            else { data.AutoSelect = false; data.FeatureScopeBodies = scopeBodies; }

            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"CutSweep.Edit: ModifyDefinition returned false on '{feature.Name}' "
                    + "— value invalid against the profile/path?");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        // Roll to end so the change propagates downstream (ForceRebuildAll). NOT
        // AfterFeature: a mid-tree cut must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information("CutSweepHandler.Edit: {Name}", feature.Name);
        return Inspect(file, feature);
    }

    // Set ONLY the scalar/option/flag fields Inspect reads. KEEP geometry selections
    // (Profile, Path, GuideCurves, FeatureScopeBodies) as built — Edit never re-points them.
    private static void ApplyEditableFields(ISweepFeatureData data, SweepArgs args) {
        var t = args.Twist;

        if (args.ProfileType == WireProfileType.Circular) {
            data.CircularProfileDiameter = args.CircularDiameter
                ?? throw new ArgumentException("CutSweep.Edit: profile_type='Circular' requires circular_diameter");
        }

        // SW quirk: Direction=None=-1 is the "unspecified" sentinel; never write -1 — it
        // makes ModifyDefinition reject otherwise-valid input on some configurations. Leave
        // the built value when the wire says None.
        if (args.Direction != WireSweepDirection.None) {
            data.Direction = (int)args.Direction;
        }
        data.StartTangencyType = ResolveTangency(args.StartTangency);
        data.EndTangencyType = ResolveTangency(args.EndTangency);

        switch (t.Kind) {
            case WireTwistKind.None:
                data.TwistControlType = (short)swTwistControlType_e.swTwistControlFollowPath;
                break;
            case WireTwistKind.KeepNormalConstant:
                data.TwistControlType = (short)swTwistControlType_e.swTwistControlKeepNormalConstant;
                break;
            case WireTwistKind.Specified:
                data.TwistControlType = (short)(t.KeepNormal
                    ? swTwistControlType_e.swTwistControlNormalConstantTwistAlongPath
                    : swTwistControlType_e.swTwistControlConstantTwistAlongPath);
                // SW quirk: D1ReverseTwistDir's getter is INVERTED vs its setter (write True =>
                // visibly reversed twist; read returns False — see ReadTwist's ProbeD1 sign
                // recovery). The proven Add write path is `= angle < 0` paired with the
                // abs-value SetTwistAngle, and Add->Inspect round-trips today; mirror it
                // exactly so the edit round-trips too.
                data.D1ReverseTwistDir = t.Angle < 0;
                data.SetTwistAngle(Math.Abs(t.Angle));
                if (t.AngleB.HasValue) {
                    data.D2ReverseTwistDir = t.AngleB.Value < 0;
                    data.SetD2TwistAngle(Math.Abs(t.AngleB.Value));
                }
                break;
            case WireTwistKind.MinimumTwist:
                data.TwistControlType = (short)swTwistControlType_e.swTwistControlFollowPath;
                data.PathAlignmentType = (int)swTangencyType_e.swMinimumTwist;
                break;
            case WireTwistKind.TangentAdjacentFaces:
                data.TwistControlType = (short)swTwistControlType_e.swTwistControlFollowPath;
                data.PathAlignmentType = (int)swTangencyType_e.swTangencyAllFaces;
                break;
            case WireTwistKind.Direction:
                // Guarded out above (geometry-selection change).
                throw new InvalidOperationException(
                    "CutSweep.Edit: twist='Direction' is not in-place editable");
        }

        // No data.Merge — cuts don't merge (not in Inspect's output).
        data.MaintainTangency = args.MergeTangentFaces;
        data.AlignWithEndFaces = args.AlignWithEndFaces;
        data.ThinFeature = args.ThinFeature;
        if (args.ThinFeature && args.ThinThickness.HasValue) {
            // SW quirk: SetWallThickness(false, …) writes direction 1 (outward); matches
            // Inspect's GetWallThickness(false) read.
            data.SetWallThickness(false, args.ThinThickness.Value);
        }
    }

    // ---- Selection re-pointing (Edit) ----------------------------------------------

    // Re-point the PROFILE sketch in place onto the sketch the incoming profile names
    // (set unconditionally). The profile is addressed by sketch NAME on the wire
    // (input["profile"]["name"], parsed into args.ProfileSketchName) — same handle Add selects
    // via FindFeatureByName — NOT by a geometric Definition. Circular profiles have no sketch
    // (data.Profile is null); the profile_type guard upstream already refuses Sketch<->Circular,
    // so here we only ever touch a sketch profile.
    //
    // Reflection-confirmed setter: ISweepFeatureData.set_Profile(Object PDisp) — a SINGLE
    // dispatch (one COM object), NOT a SAFEARRAY, so the live Feature is assigned directly with
    // no DispatchWrapper.
    private static void RepointProfileIfChanged(
        File file, ISweepFeatureData data, SweepArgs args, string featureName) {
        if (args.ProfileType == WireProfileType.Circular) return; // no sketch profile

        // The profile is named on the wire; a Sketch sweep must carry one.
        var requestedName = args.ProfileSketchName;
        if (string.IsNullOrEmpty(requestedName)) {
            throw new InvalidOperationException(
                $"CutSweep.Edit: profile sketch name missing on '{featureName}' — a Sketch sweep "
                + "must name its profile.");
        }
        // Resolve requested -> canonical so a "<N>"-suffixed payload name still resolves; then
        // set it unconditionally (re-applying the same sketch is idempotent). ModifyDefinition's
        // bool return is the validity gate — no compare-and-skip.
        var resolvedRequested = FeatureName.Resolve(file, requestedName);

        var newFeat = FindFeatureByName(file, resolvedRequested)
            ?? throw new InvalidOperationException(
                $"CutSweep.Edit: profile sketch '{requestedName}' not found on '{featureName}'.");
        data.Profile = newFeat;
        SldworksLog.Information(
            "CutSweepHandler.Edit: re-pointed profile of '{Name}' onto sketch '{Sketch}'",
            featureName, resolvedRequested);
    }

    // Re-point the PATH in place onto the live entity the incoming path Definition resolves to
    // (set unconditionally). Add resolves args.Path via DefinitionResolver and
    // selects it on mark 4; here we resolve the same Definition and set data.Path directly.
    //
    // Reflection-confirmed setter: ISweepFeatureData.set_Path(Object PDisp) — a SINGLE dispatch
    // (one COM object: a Sketch Feature, an Edge, or a RefCurve composite), NOT a SAFEARRAY, so
    // the live entity is assigned directly with no DispatchWrapper.
    private static void RepointPathIfChanged(
        File file, ISweepFeatureData data, Definition pathDef, string featureName) {
        var live = DefinitionResolver.Resolve(file, pathDef);
        if (live is null) {
            throw new InvalidOperationException(
                $"CutSweep.Edit: path ({pathDef.GetType().Name}) did not resolve to a live entity "
                + $"on '{featureName}' — payload={pathDef.ToJson().ToJsonString()}");
        }

        // Set the resolved path unconditionally (re-applying the same entity is idempotent).
        // ModifyDefinition's bool return is the validity gate — no compare-and-skip.
        data.Path = live;
        SldworksLog.Information(
            "CutSweepHandler.Edit: re-pointed path of '{Name}' onto {LiveType}",
            featureName, live.GetType().Name);
    }

    // Re-point the GUIDE CURVES in place onto the incoming guide set (set unconditionally). An
    // explicitly-empty incoming set clears the built guides.
    //
    // Reflection-confirmed setter: ISweepFeatureData.set_GuideCurves(Object ArrayIn) — a SINGLE
    // Object param that is a SAFEARRAY-of-IDispatch. Per the Chamfer.Edges precedent, SAFEARRAY
    // feature-data setters want DispatchWrapper[] (a bare object[] crashes the marshaller), so
    // each resolved live guide is wrapped. The getter (data.GuideCurves as object[]) hands back
    // live Sketch Features / Edges, which round-trip through this setter.
    private static void RepointGuideCurvesIfChanged(
        File file, ISweepFeatureData data, IReadOnlyList<Definition> guideDefs, string featureName) {
        // Resolve requested -> live. An explicitly-empty guide set is applied (clears guides);
        // ModifyDefinition's bool return is the validity gate — no compare-and-skip.
        var resolvedLive = new List<object>(guideDefs.Count);
        for (var i = 0; i < guideDefs.Count; i++) {
            var live = DefinitionResolver.Resolve(file, guideDefs[i]);
            if (live is null) {
                throw new InvalidOperationException(
                    $"CutSweep.Edit: guide_curves[{i}] ({guideDefs[i].GetType().Name}) did not "
                    + $"resolve to a live entity on '{featureName}'.");
            }
            resolvedLive.Add(live);
        }

        // SW quirk: SAFEARRAY-of-IDispatch feature-data setter wants DispatchWrapper[] (same as
        // ChamferHandler.Edges / HoldLines). An empty array clears guide curves.
        data.GuideCurves = resolvedLive.Select(g => new DispatchWrapper(g)).ToArray();
        SldworksLog.Information(
            "CutSweepHandler.Edit: re-pointed guide curves of '{Name}' onto {Count} curve(s)",
            featureName, resolvedLive.Count);
    }

    // ---- Inspect -------------------------------------------------------------------

    public static JsonNode Inspect(File file, Feature feature) {
        var data = feature.GetDefinition() as SweepFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose ISweepFeatureData");

        // SW quirk: AccessSelections required before reading referenced selections; missing ReleaseSelectionAccess wedges the document.
        if (!data.AccessSelections(file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"CutSweep.Inspect({feature.Name}): AccessSelections failed");
        }
        try {
            // Circular profile uses a built-in circle around the path; data.Profile is null.
            var isCircular = data.CircularProfile;
            string? profileName = null;
            if (!isCircular) {
                var profileFeat = data.Profile as Feature;
                // SW quirk: a sketch reused by multiple features comes back through the consumer
                // with a "<N>" suffix indicating Nth-consumer. FeatureName.Resolve strips it back
                // to the canonical top-level name so downstream lookups match.
                profileName = FeatureName.Resolve(file, profileFeat?.Name ?? "");
            }

            var sweepDirection = CaptureSweepDirection(data);

            var pathDef = CapturePath(file, data.Path);

            var guideCurves = CaptureGuideCurves(file, data);

            var twistJson = ReadTwist(file, feature, data);
            // SW quirk: ReadTwist → TwistDirSignProbe.ProbeD1 may call
            // ModifyDefinition, which releases AccessSelections under `data`.
            // Subsequent reads off `data` (FeatureScopeBodies in particular)
            // silently return empty when access is released. Re-fetch the live
            // definition and re-acquire before reading the remaining fields.
            if (twistJson["twist"]?.GetValue<string>() == "Specified") {
                data = (SweepFeatureData)feature.GetDefinition();
                if (!data.AccessSelections(file.ModelDoc, null)) {
                    throw new InvalidOperationException(
                        $"CutSweep.Inspect({feature.Name}): AccessSelections failed after twist sign probe");
                }
            }

            var bodies = CollectBodies(file);

            var featureScope = FeatureScope.Capture(file, data.FeatureScopeBodies);

            // Discarded-fragment info moved off Inspect to the on-demand
            // `File.GetDiscardProbes(name)` endpoint — the IModifyDefinition2
            // dance is doubly painful for Cut-Sweep (forces the dirty-rebuild
            // fixup via FeatEditDef + Ok_Command). Inspect callers shouldn't
            // pay for it; the runner fetches probes only when shipping the
            // round-trip drop_bodies follow-up.

            var result = new JsonObject {
                ["type"] = TypeName,
                ["name"] = feature.Name,
                ["path"] = pathDef,
                ["guide_curves"] = guideCurves,
                ["twist"] = twistJson,
                ["align_with_end_faces"] = data.AlignWithEndFaces,
                ["merge_tangent_faces"] = data.MaintainTangency,
                ["thin_feature"] = data.ThinFeature,
                ["echo_bodies"] = bodies,
                ["profile_type"] = isCircular ? "Circular" : "Sketch",
                ["start_tangency"] = TangencyToWire(data.StartTangencyType),
                ["end_tangency"] = TangencyToWire(data.EndTangencyType),
                ["feature_scope"] = featureScope,
                ["direction"] = SweepDirectionToWire(sweepDirection),
            };
            if (isCircular) {
                result["circular_diameter"] = data.CircularProfileDiameter;
            } else {
                result["profile"] = new JsonObject {
                    ["type"] = "Sketch",
                    ["name"] = profileName,
                };
            }
            if (data.ThinFeature) {
                result["thin_thickness"] = data.GetWallThickness(false);
            }
            return result;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private static JsonNode CapturePath(File file, object? path) {
        // SW quirk: SweepFeatureData.Path can be a Sketch, Edge, or ReferenceCurve/3D-sketch composite.
        if (path is null) {
            throw new InvalidOperationException("Inspect(CutSweep): path is null");
        }
        var def = DefinitionCapture.Capture(path)
            ?? throw new InvalidOperationException(
                $"Inspect(CutSweep): path is unsupported runtime type {path.GetType().Name}");
        // SW quirk: a sketch reused by multiple features comes back through the
        // consumer with a "<N>" suffix indicating Nth-consumer (same gotcha as
        // the `profile` field above). FeatureName.Resolve strips it back to the
        // canonical top-level name so the round-trip diff doesn't trip when
        // source and target have different consumer counts.
        if (def is FeatureDefinition fd) {
            var resolved = FeatureName.Resolve(file, fd.Name);
            if (resolved != fd.Name) def = fd with { Name = resolved };
        }
        return def.ToJson();
    }

    private static JsonArray CaptureGuideCurves(File file, SweepFeatureData data) {
        var arr = new JsonArray();
        var count = data.GetGuideCurvesCount();
        if (count <= 0) return arr;
        // SW quirk: 2026 redist exposes guide curves via the GuideCurves property (object[]); IGetFirstGuideCurve/IGetNextGuideCurve do not exist.
        if (data.GuideCurves is object[] guides) {
            foreach (var g in guides) {
                if (g is null) continue;
                arr.Add(CapturePath(file, g));
            }
        }
        return arr;
    }

    private static JsonObject ReadTwist(File file, Feature feature, SweepFeatureData data) {
        // SW quirk: twist is split across TwistControlType and PathAlignmentType (the latter only meaningful when TCT == FollowPath).
        var tct = (swTwistControlType_e)data.TwistControlType;
        switch (tct) {
            case swTwistControlType_e.swTwistControlConstantTwistAlongPath:
            case swTwistControlType_e.swTwistControlNormalConstantTwistAlongPath:
                var keepNormal = tct == swTwistControlType_e.swTwistControlNormalConstantTwistAlongPath;
                // SW quirk: `D1ReverseTwistDir` getter on `SweepFeatureData`
                // (shared by boss Sweep and Cut-Sweep) returns False regardless
                // of the authored UI direction. Recover the sign via the shared
                // probe instead of trusting the readback.
                var sign = TwistDirSignProbe.ProbeD1(file, feature, data);
                var angleA = Math.Abs(data.GetTwistAngle()) * sign;
                double? angleB = null;
                var rawB = data.GetD2TwistAngle();
                var rawBSigned = data.D2ReverseTwistDir ? -Math.Abs(rawB) : Math.Abs(rawB);
                if (Math.Abs(rawBSigned) > 1e-12) angleB = rawBSigned;
                var spec = new JsonObject {
                    ["twist"] = "Specified",
                    ["angle"] = angleA,
                    ["keep_normal"] = keepNormal,
                };
                if (angleB.HasValue) spec["angle_b"] = angleB.Value;
                return spec;
            case swTwistControlType_e.swTwistControlKeepNormalConstant:
                return new JsonObject { ["twist"] = "KeepNormalConstant" };
            default:
                var pat = (swTangencyType_e)data.PathAlignmentType;
                return pat switch {
                    swTangencyType_e.swMinimumTwist =>
                        new JsonObject { ["twist"] = "MinimumTwist" },
                    swTangencyType_e.swTangencyDirectionVector =>
                        ReadDirectionTwist(file, data),
                    swTangencyType_e.swTangencyAllFaces =>
                        new JsonObject { ["twist"] = "TangentAdjacentFaces" },
                    _ => new JsonObject { ["twist"] = "FollowPath" },
                };
        }
    }

    private static JsonObject ReadDirectionTwist(File file, SweepFeatureData data) {
        // SW quirk: GetPathAlignmentDirectionVector's out-param is a swSelectType_e (entity-kind
        // discriminator), NOT a flip flag. No setter for the path-alignment flip is exposed on
        // SweepFeatureData either, so the wire carries no flip field at all.
        var dir = data.GetPathAlignmentDirectionVector(out _);
        return new JsonObject {
            ["twist"] = "Direction",
            ["direction"] = CapturePath(file, dir),
        };
    }

    private static JsonArray CollectBodies(File file) {
        var arr = new JsonArray();
        // SW quirk: GetBodies2 lives on IPartDoc, not IModelDoc2.
        // SW quirk: arg 2 is `bVisibleOnly` — `true` drops hidden bodies which
        // silently undercounts when an upstream feature merged into a body SW
        // left hidden. Use `false` so the inspect echo reflects every solid in
        // the doc.
        var raw = ((IPartDoc)file.ModelDoc).GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
        if (raw is null) return arr;
        foreach (Body2 body in raw) {
            arr.Add(DefinitionCapture.Capture(body).ToJson());
        }
        return arr;
    }

    // SW quirk: IModelDoc2 has no FeatureByName accessor; walk via IFirstFeature/IGetNextFeature.
    private static Feature? FindFeatureByName(File file, string name) {
        var feature = file.ModelDoc.IFirstFeature() as Feature;
        while (feature is not null) {
            if (feature.Name == name) return feature;
            feature = feature.IGetNextFeature() as Feature;
        }
        return null;
    }

    // ---- Selection helpers ---------------------------------------------------------

    private static void SelectProfile(File file, string sketchName) {
        if (string.IsNullOrEmpty(sketchName)) {
            throw new ArgumentException("CutSweep: profile sketch name missing — sketch must be added first");
        }
        var feat = FindFeatureByName(file, sketchName)
            ?? throw new ArgumentException($"CutSweep profile sketch '{sketchName}' not found");
        feat.Select2(true, MarkProfile);
    }

    private static void SelectPath(File file, Definition path) {
        // Split Resolve from SelectLive so the thrown message distinguishes
        // "no live entity matched" from "SW rejected the selection" (live entity
        // resolved but Select4 returned false). Resolve already logs the
        // unresolvable payload at Information level.
        var live = path.Resolve(file);
        if (live is null) {
            throw new InvalidOperationException(
                $"CutSweep: path Definition ({path.GetType().Name}) did not resolve to a live entity — " +
                $"payload={path.ToJson().ToJsonString()}");
        }
        if (!Definition.SelectLive(file, live, MarkPath)) {
            throw new InvalidOperationException(
                $"CutSweep: SW rejected selection of {live.GetType().Name} onto mark {MarkPath} " +
                $"for path Definition ({path.GetType().Name})");
        }
    }

    private static void SelectGuideCurves(File file, IReadOnlyList<Definition> guideCurves) {
        Definition.SelectAll(file, guideCurves, MarkGuideCurves, "CutSweep.guide_curves");
    }

    private static void SelectTwistDirection(File file, WireTwistPayload t) {
        if (t.Kind != WireTwistKind.Direction || t.Direction is null) return;
        if (!t.Direction.Select(file, MarkTwistDirection)) {
            throw new InvalidOperationException(
                $"CutSweep: twist-direction Definition ({t.Direction.GetType().Name}) did not resolve+select onto mark {MarkTwistDirection}");
        }
    }

    // ---- Result shape --------------------------------------------------------------

    private static JsonNode BuildAddResult(JsonNode input, Feature feature) {
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    // ---- Typed args ----------------------------------------------------------------

    private sealed record SweepArgs(
        string ProfileSketchName,
        Definition Path,
        IReadOnlyList<Definition> GuideCurves,
        WireTwistPayload Twist,
        bool AlignWithEndFaces,
        bool MergeTangentFaces,
        bool ThinFeature,
        double? ThinThickness,
        WireProfileType ProfileType,
        double? CircularDiameter,
        Definition? SolidBody,
        string StartTangency,
        string EndTangency,
        IReadOnlyList<Definition>? FeatureScope,
        WireSweepDirection Direction) {

        internal static SweepArgs Parse(JsonNode input) {
            var profileName = input["profile"]?["name"]?.GetValue<string>() ?? "";

            var pathNode = input["path"]
                ?? throw new ArgumentException("CutSweep: missing 'path'");
            var path = Definition.FromJson(pathNode)
                ?? throw new ArgumentException("CutSweep: 'path' could not be parsed as a Definition");

            var guideCurves = new List<Definition>();
            if (input["guide_curves"] is JsonArray gcArr) {
                foreach (var node in gcArr) {
                    if (node is null) continue;
                    var d = Definition.FromJson(node)
                        ?? throw new ArgumentException("CutSweep: guide_curves entry is not a Definition");
                    guideCurves.Add(d);
                }
            }

            var twist = WireTwistPayload.Parse(input["twist"]) ?? WireTwistPayload.None();
            var align = ReadBool(input, "align_with_end_faces", false);
            var mergeTangent = ReadBool(input, "merge_tangent_faces", false);
            var thin = ReadBool(input, "thin_feature", false);
            double? thinThickness = input["thin_thickness"] is { } tt && tt.GetValue<object?>() is not null
                ? tt.GetValue<double>()
                : null;

            var profileTypeStr = input["profile_type"]?.GetValue<string>() ?? "Sketch";
            var profileType = profileTypeStr switch {
                "Sketch"   => WireProfileType.Sketch,
                "Circular" => WireProfileType.Circular,
                "Solid"    => WireProfileType.Solid,
                _ => throw new ArgumentException($"CutSweep: unknown profile_type '{profileTypeStr}'"),
            };
            double? circDiam = input["circular_diameter"] is { } cd && cd.GetValue<object?>() is not null
                ? cd.GetValue<double>()
                : null;
            Definition? solidBody = null;
            if (input["solid_body"] is { } sb && sb.GetValueKind() != System.Text.Json.JsonValueKind.Null) {
                solidBody = Definition.FromJson(sb);
            }

            var startTan = input["start_tangency"]?.GetValue<string>() ?? "None";
            var endTan = input["end_tangency"]?.GetValue<string>() ?? "None";

            IReadOnlyList<Definition>? featureScope = null;
            if (input["feature_scope"] is JsonArray fsArr) {
                var list = new List<Definition>();
                foreach (var node in fsArr) {
                    if (node is null) continue;
                    var d = Definition.FromJson(node)
                        ?? throw new ArgumentException("CutSweep: feature_scope entry is not a Definition");
                    list.Add(d);
                }
                featureScope = list.Count > 0 ? list : null;
            }

            var direction = ParseSweepDirection(input["direction"]?.GetValue<string>());

            return new SweepArgs(profileName, path, guideCurves, twist, align, mergeTangent,
                thin, thinThickness, profileType, circDiam, solidBody,
                startTan, endTan, featureScope,
                direction);
        }
    }

    private static WireSweepDirection ParseSweepDirection(string? s) => s switch {
        null            => WireSweepDirection.None,
        ""              => WireSweepDirection.None,
        "None"          => WireSweepDirection.None,
        "DirectionA"    => WireSweepDirection.DirectionA,
        "Both"          => WireSweepDirection.Both,
        "DirectionB"    => WireSweepDirection.DirectionB,
        _ => throw new ArgumentException($"CutSweep: unknown direction literal '{s}'"),
    };

    private static string SweepDirectionToWire(WireSweepDirection d) => d switch {
        WireSweepDirection.DirectionA => "DirectionA",
        WireSweepDirection.Both       => "Both",
        WireSweepDirection.DirectionB => "DirectionB",
        _                             => "None",
    };

    private static WireSweepDirection CaptureSweepDirection(SweepFeatureData data) {
        var raw = data.Direction;
        return raw switch {
            0 => WireSweepDirection.DirectionA,
            1 => WireSweepDirection.Both,
            2 => WireSweepDirection.DirectionB,
            _ => WireSweepDirection.None,
        };
    }

    private sealed record WireTwistPayload(
        WireTwistKind Kind,
        double Angle,
        Definition? Direction,
        double? AngleB,
        bool KeepNormal) {

        internal static WireTwistPayload None() =>
            new(WireTwistKind.None, 0.0, null, null, false);

        internal static WireTwistPayload? Parse(JsonNode? node) {
            if (node is null) return null;
            var name = node["twist"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) return null;
            return name switch {
                "None"                 => new(WireTwistKind.None,                0.0, null, null, false),
                "FollowPath"           => new(WireTwistKind.None,                0.0, null, null, false),
                "KeepNormalConstant"   => new(WireTwistKind.KeepNormalConstant,  0.0, null, null, false),
                "Specified"            => new(WireTwistKind.Specified,           ReadDouble(node, "angle"),
                                              null,
                                              ReadOptionalDouble(node, "angle_b"),
                                              ReadBool(node, "keep_normal", false)),
                "MinimumTwist"         => new(WireTwistKind.MinimumTwist,        0.0, null, null, false),
                "TangentAdjacentFaces" => new(WireTwistKind.TangentAdjacentFaces, 0.0, null, null, false),
                "Direction"            => new(WireTwistKind.Direction,           0.0,
                                              Definition.FromJson(node["direction"])
                                              ?? throw new ArgumentException("CutSweep twist=Direction requires a 'direction' Definition"),
                                              null, false),
                _ => throw new ArgumentException($"Unknown sweep twist mode: {name}"),
            };
        }
    }

    private static double? ReadOptionalDouble(JsonNode node, string field) {
        var v = node[field];
        if (v is null) return null;
        if (v.GetValueKind() == System.Text.Json.JsonValueKind.Null) return null;
        return v.GetValue<double>();
    }

    private static double ReadDouble(JsonNode node, string field, double fallback = double.NaN) {
        var v = node[field];
        if (v is null) {
            if (!double.IsNaN(fallback)) return fallback;
            throw new ArgumentException($"Missing required numeric field '{field}'");
        }
        return v.GetValue<double>();
    }

    private static bool ReadBool(JsonNode node, string field, bool fallback) {
        var v = node[field];
        return v is null ? fallback : v.GetValue<bool>();
    }
}
