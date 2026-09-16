using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Shared;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

public static class CutRevolveHandler {
    public const string TypeName = "CutRevolve";

    // SW quirk: GetTypeName2() returns "Cut-Revolve" (hyphenated).
    public const string SwTypeName = "Cut-Revolve";

    // ---- Add -----------------------------------------------------------------------

    public static JsonNode Add(File file, JsonNode input) {
        var args = CutRevolveArgs.Parse(input);
        // Sequence selections in strictly decreasing mark order: axis (4) → feature_scope (1) → sketch+contours (0).
        // SW quirk: CutRevolve requires BOTH the sketch and the contours selected at mark 0 —
        // selecting contours alone makes FeatureRevolve2 reject the profile when the contour
        // comes from a multi-region sketch. Plain Revolve only needs contours OR sketch.
        file.ModelDoc.ClearSelection2(true);
        SelectAxis(file, args.Axis);

        // SW quirk: empty (non-null) feature_scope must collapse to AutoSelect.
        var hasExplicitBodies = args.FeatureScope is { Count: > 0 };
        if (hasExplicitBodies) {
            SelectFeatureScopeBodiesForAdd(file, args.FeatureScope!);
        }

        SelectSketch(file, args.SketchName);
        if (args.Contours.Count > 0) {
            AppendContours(file, args.Contours, args.SketchName);
        }

        var reversed = args.Reversed;

        // SW quirk: multi-body cuts pop a "select bodies to keep" modal; only
        // PromptBodiesToKeepNotify can answer it from code. Keep every
        // candidate here — the post-Add DropBodies command applies any
        // intended discards via ray-resolved probes.
        using var bodiesScope = BodiesToKeepScope.Register(file);

        // SW quirk: FeatureRevolve2's Dir1Type / Dir2Type are swEndConditions_e (Blind /
        // MidPlane), NOT swRevolveType_e — same quirk as boss Revolve. The revolve TYPE
        // (one-direction vs mid-plane vs full-360) is implied by SingleDir + the angles;
        // SW classifies a 2π single-direction revolve as swRevolveTypeOneDirection360Degrees
        // on Inspect.
        bool singleDir = args.AngleB is null && !args.MidPlane;
        int dir1Type = args.MidPlane
            ? (int)swEndConditions_e.swEndCondMidPlane
            : (int)swEndConditions_e.swEndCondBlind;
        double angle1 = args.Angle;
        double angle2 = args.AngleB ?? 0.0;
        var fm = file.ModelDoc.FeatureManager;
        var feature = (Feature?)fm.FeatureRevolve2(
            SingleDir:                   singleDir,
            IsSolid:                     true,
            IsThin:                      false,
            IsCut:                       true,
            ReverseDir:                  reversed,
            BothDirectionUpToSameEntity: false,
            Dir1Type:                    dir1Type,
            Dir2Type:                    (int)swEndConditions_e.swEndCondBlind,
            Dir1Angle:                   angle1,
            Dir2Angle:                   angle2,
            OffsetReverse1:              false,
            OffsetReverse2:              false,
            OffsetDistance1:             0.0,
            OffsetDistance2:             0.0,
            ThinType:                    0,
            ThinThickness1:              0.0,
            ThinThickness2:              0.0,
            Merge:                       true,
            UseFeatScope:                true,
            UseAutoSelect:               !hasExplicitBodies);

        if (feature is null) {
            throw new InvalidOperationException(
                "FeatureRevolve2 (cut) failed; check sketch has a closed profile, axis is valid, and angle is in (0, 2π].");
        }

        SldworksLog.Information("CutRevolveHandler.Add: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        return BuildAddResult(input, feature);
    }

    // ---- Edit ----------------------------------------------------------------------

    // Edit an existing cut revolve IN PLACE (GetDefinition -> AccessSelections -> mutate ->
    // ModifyDefinition); we do NOT delete + re-add, so the feature name — and every
    // downstream name-ref / probe — survives. `input` is a FULL cut-revolve payload (same
    // shape Inspect emits / Add consumes), parsed by CutRevolveArgs so edit and create share
    // one validation. Parametric scalars/settings are applied (angle, angle_b, mid_plane,
    // reversed) AND the two re-pointable geometry refs Add resolves+selects — the revolve AXIS
    // (data.Axis) and the profile CONTOURS (data.Contours) — are re-pointed when the payload's
    // refs DIFFER from what's currently built (multiset of canonical Definition JSON). Unchanged
    // refs are left untouched. The owning SKETCH (first sub-feature) is structural and is NOT
    // re-pointed (different parent sketch = delete + re-add). CutRevolve has no `merge` on the
    // wire — Add hardcodes Merge=true, so Edit holds it (doesn't touch it). Mirrors the boss
    // Revolve / Chamfer Edit shape (resolve -> capture -> multiset-compare -> setter).
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = CutRevolveArgs.Parse(input);

        var data = feature.GetDefinition() as IRevolveFeatureData2
            ?? throw new InvalidOperationException(
                $"CutRevolve.Edit: feature {feature.Name} ({feature.GetTypeName2()}) does not expose IRevolveFeatureData2");

        // SW quirk: AccessSelections required before mutating the definition; pair with
        // ReleaseSelectionAccess in finally — skipping the release wedges the document.
        data.AccessSelections(file.ModelDoc, null);
        try {
            // Re-point geometry refs FIRST (before the type/angle scalars), so the angle(s)
            // apply against the new profile/axis — same ordering rationale as Chamfer.
            RepointAxisIfChanged(file, data, args.Axis, feature.Name);
            RepointContoursIfChanged(file, data, args.Contours, args.SketchName, feature.Name);

            // SW quirk: a revolve's angle(s) + mid-plane + full-360 are all folded into a
            // single swRevolveType_e Type value. Set Type to the EXACT inverse of what
            // ReadRevolveAngles decodes, then set the per-direction angle(s), or the
            // round-trip drifts.
            ApplyRevolveTypeAndAngles(data, args.Angle, args.AngleB, args.MidPlane);
            data.ReverseDirection = args.Reversed;
            // Merge intentionally left untouched (held at true — Add hardcodes it; no wire field).

            // Apply feature_scope unconditionally (assume the payload is authoritative — no
            // compare/skip). null/empty => AutoSelect over all bodies; non-empty => restrict to
            // the resolved bodies. ModifyDefinition's bool return is the validity gate.
            var scopeBodies = FeatureScope.ResolveScopeBodies(file, args.FeatureScope);
            if (scopeBodies is null) data.AutoSelect = true;
            else { data.AutoSelect = false; data.FeatureScopeBodies = scopeBodies; }

            if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                throw new InvalidOperationException(
                    $"CutRevolve.Edit: ModifyDefinition returned false on '{feature.Name}' — "
                    + "angle(s) invalid against the profile/axis?");
            }
        } finally {
            data.ReleaseSelectionAccess();
        }

        // Roll to end so the change propagates downstream (ForceRebuildAll). NOT
        // AfterFeature: a mid-tree cut revolve must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information("CutRevolveHandler.Edit: {Name}", feature.Name);
        return Inspect(file, feature);
    }

    // Inverse of ReadRevolveAngles: pick the swRevolveType_e that encodes the requested
    // angle(s)/mid-plane combination, then set the per-direction angle(s) in RADIANS.
    private static void ApplyRevolveTypeAndAngles(
            IRevolveFeatureData2 data, double angle, double? angleB, bool midPlane) {
        const double tau = 2.0 * Math.PI;
        var isFull = Math.Abs(angle - tau) <= 1e-9;
        if (angleB is double ab) {
            // Two-direction. (Parse forbids mid_plane + angle_b, so midPlane is false here.)
            data.Type = (int)swRevolveType_e.swRevolveTypeTwoDirection;
            data.SetRevolutionAngle(true, angle);
            data.SetRevolutionAngle(false, ab);
        } else if (midPlane) {
            data.Type = isFull
                ? (int)swRevolveType_e.swRevolveTypeMidPlane360Degrees
                : (int)swRevolveType_e.swRevolveTypeMidPlane;
            data.SetRevolutionAngle(true, angle);
        } else {
            data.Type = isFull
                ? (int)swRevolveType_e.swRevolveTypeOneDirection360Degrees
                : (int)swRevolveType_e.swRevolveTypeOneDirection;
            data.SetRevolutionAngle(true, angle);
        }
    }

    // ---- Edit: geometry re-pointing -------------------------------------------------

    // Re-point the revolve AXIS in place. Incoming axis def = CutRevolveArgs.Axis (the SAME ref Add
    // resolves+selects via SelectAxis). Resolve, then assign data.Axis directly inside the open
    // AccessSelections block — unconditionally (assume the payload is authoritative; re-applying
    // the same axis is idempotent, so scalar-only edits are unaffected).
    //
    // Reflection-confirmed setter: IRevolveFeatureData2.set_Axis(Object) marshals as
    // UnmanagedType.IDispatch — a SINGLE live dispatch object, NOT a SAFEARRAY (same shape as
    // Chamfer.IVertex). Assign the resolved live entity DIRECTLY, with NO DispatchWrapper.
    private static void RepointAxisIfChanged(
        File file, IRevolveFeatureData2 data, Definition axisDef, string featureName) {
        var live = DefinitionResolver.Resolve(file, axisDef)
            ?? throw new InvalidOperationException(
                $"CutRevolve.Edit: axis ({axisDef.GetType().Name}) did not resolve to a live entity "
                + $"on '{featureName}'.");

        // SW quirk: set_Axis is a scalar IDispatch property — assign the single live entity, no
        // DispatchWrapper (contrast data.Contours which is a SAFEARRAY).
        data.Axis = live;
    }

    // Re-point the profile CONTOURS in place. Incoming contour defs = CutRevolveArgs.Contours (the
    // SAME refs Add resolves+selects via AppendContours), resolved through the SAME
    // ResolveContoursForSelection Add uses (subset-matched against the owning sketch —
    // decomposition isn't rebuild-stable), then set unconditionally (assume the payload is
    // authoritative; re-applying the same selection is idempotent).
    //
    // Reflection-confirmed setter: IRevolveFeatureData2.set_Contours(Object) marshals as a
    // SAFEARRAY (UnmanagedType.Struct — same shape as Chamfer.Edges / Fillet.Edges) — wants
    // DispatchWrapper[]; a bare object[] crashes the marshaller.
    //
    // When the payload carries NO explicit contours (Add selected the whole sketch), there's
    // nothing an authorable contour-ref edit can apply (the profile is the sketch itself,
    // structural / out of scope) — leave the built selection untouched. (That's "the field wasn't
    // provided", not a compare-and-skip guard.)
    private static void RepointContoursIfChanged(
        File file, IRevolveFeatureData2 data, IReadOnlyList<Definition> contourDefs,
        string sketchName, string featureName) {
        if (contourDefs.Count == 0) {
            return;
        }

        var ownerSketch = string.IsNullOrEmpty(sketchName)
            ? null
            : FindFeatureByName(file, sketchName)?.GetSpecificFeature2() as Sketch;
        var resolvedLive = DefinitionResolver.ResolveContoursForSelection(file, contourDefs, ownerSketch);
        if (resolvedLive.Count == 0) {
            throw new InvalidOperationException(
                $"CutRevolve.Edit: no live contour matched the requested {contourDefs.Count} contour "
                + $"def(s) on '{featureName}' under the segment-/edge-subset rule.");
        }

        // SW quirk: set_Contours is a SAFEARRAY-of-IDispatch setter — wrap each live contour in
        // DispatchWrapper; a bare object[] crashes the marshaller (same as Chamfer/Fillet Edges).
        data.Contours = resolvedLive.Select(o => new DispatchWrapper(o)).ToArray();
        SldworksLog.Information(
            "CutRevolveHandler.Edit: applied '{Name}' onto {Count} contour(s)",
            featureName, resolvedLive.Count);
    }

    // ---- Inspect -------------------------------------------------------------------

    public static JsonNode Inspect(File file, Feature feature) {
        // SW quirk: GetDefinition() returns IRevolveFeatureData2 for both boss and cut revolves.
        var data = feature.GetDefinition() as IRevolveFeatureData2
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose IRevolveFeatureData2");

        // SW quirk: AccessSelections required before reading axis/contours; pair with ReleaseSelectionAccess.
        data.AccessSelections(file.ModelDoc, null);
        try {
            // Sketch profile is the first sub-feature.
            var sketchSub = feature.GetFirstSubFeature() as Feature;
            var sketchName = FeatureName.Resolve(file, sketchSub?.Name ?? "");

            var reversed = data.ReverseDirection;
            var revolveType = data.Type;
            var (angle, angleB, midPlane) = ReadRevolveAngles(data, revolveType);

            var contours = CaptureContours(data);
            var axisNode = CaptureAxis(file, data);
            var featureScope = FeatureScope.Capture(file, data.FeatureScopeBodies);

            // Discarded-fragment info moved off Inspect to the on-demand
            // `File.GetDiscardProbes(name)` endpoint — see CutExtrudeHandler
            // for the rationale (Parasolid rebuild + mass-props drift).

            var result = new JsonObject {
                ["type"] = TypeName,
                ["name"] = feature.Name,
                ["sketch"] = new JsonObject {
                    ["type"] = "Sketch",
                    ["name"] = sketchName,
                },
                ["axis"] = axisNode,
                ["angle"] = angle,
                ["reversed"] = reversed,
                ["mid_plane"] = midPlane,
                ["contours"] = contours,
                ["feature_scope"] = featureScope,
            };
            if (angleB is double ab) {
                result["angle_b"] = ab;
            } else {
                result["angle_b"] = null;
            }
            return result;
        } finally {
            data.ReleaseSelectionAccess();
        }
    }

    private static (double Angle, double? AngleB, bool MidPlane) ReadRevolveAngles(
            IRevolveFeatureData2 data, int revolveType) {
        // SW quirk: *360Degrees variants store angle as the enum itself; map to 2π explicitly.
        const double tau = 2.0 * Math.PI;
        switch (revolveType) {
            case (int)swRevolveType_e.swRevolveTypeMidPlane360Degrees:
                return (tau, null, true);
            case (int)swRevolveType_e.swRevolveTypeOneDirection360Degrees:
                return (tau, null, false);
            case (int)swRevolveType_e.swRevolveTypeTwoDirection360Degrees:
                return (tau, null, false);
            case (int)swRevolveType_e.swRevolveTypeTwoDirection:
                return (data.GetRevolutionAngle(true), data.GetRevolutionAngle(false), false);
            case (int)swRevolveType_e.swRevolveTypeMidPlane:
                return (data.GetRevolutionAngle(true), null, true);
            case (int)swRevolveType_e.swRevolveTypeOneDirection:
                return (data.GetRevolutionAngle(true), null, false);
            default:
                throw new InvalidOperationException(
                    $"Inspect(CutRevolve): unknown swRevolveType_e value {revolveType}");
        }
    }

    private static JsonArray CaptureContours(IRevolveFeatureData2 data) {
        var arr = new JsonArray();
        var contours = data.Contours as object[];
        if (contours is null || contours.Length == 0) return arr;
        foreach (var contour in contours) {
            if (contour is null) {
                throw new InvalidOperationException("Inspect(CutRevolve): contour entry is null");
            }
            arr.Add(DefinitionCapture.Capture(contour)?.ToJson()
                ?? throw new InvalidOperationException(
                    $"Inspect(CutRevolve): contour entry of unsupported type {contour.GetType().Name}"));
        }
        return arr;
    }

    // SW quirk: revolve feature-scope bodies select at mark 1 (Extrude/Sweep use mark 8).
    private const int MarkFeatureScopeBodies = 1;

    private static void SelectFeatureScopeBodiesForAdd(File file, IReadOnlyList<Definition> bodies) {
        if (bodies.Count == 0) return;
        var sd = MakeMarkSelectData(file, MarkFeatureScopeBodies);
        foreach (var def in bodies) {
            var live = DefinitionResolver.Resolve(file, def)
                ?? throw new InvalidOperationException(
                    "CutRevolve: feature_scope body did not resolve to a live entity");
            switch (live) {
                case Body2 b:    b.Select2(true, sd); break;
                case Feature ft: ft.Select2(true, MarkFeatureScopeBodies); break;
                default:
                    throw new InvalidOperationException(
                        $"CutRevolve: feature_scope entry resolved to unsupported type {live.GetType().Name}");
            }
        }
    }

    private static JsonNode CaptureAxis(File file, IRevolveFeatureData2 data) {
        // SW quirk: data.Axis is null for temporary axes (sketch line not promoted to a real entity).
        object? axisObj = data.Axis
            ?? throw new InvalidOperationException(
                "Inspect(CutRevolve): axis is null (temporary axis not representable on the wire)");
        // SW quirk: RefAxis returned for cylinder/cone temp-axis picks.
        return DefinitionCapture.Capture(axisObj)?.ToJson()
            ?? throw new InvalidOperationException(
                $"Inspect(CutRevolve): axis is unsupported runtime type {axisObj.GetType().Name}");
    }

    // ---- Selection helpers ---------------------------------------------------------

    // Appends contours to the existing selection at mark 0 — does NOT clear (sketch
    // is selected first by SelectSketch).
    private static void AppendContours(File file, IReadOnlyList<Definition> contours, string sketchName) {
        // SW quirk: SketchContour/SketchRegion decomposition isn't stable across rebuilds.
        var sd = MakeMarkSelectData(file, 0);
        var ownerSketch = string.IsNullOrEmpty(sketchName)
            ? null
            : FindFeatureByName(file, sketchName)?.GetSpecificFeature2() as Sketch;
        var liveEntities = DefinitionResolver.ResolveContoursForSelection(file, contours, ownerSketch);
        if (liveEntities.Count == 0) {
            throw new InvalidOperationException(
                $"CutRevolve: no live contour matched the captured " +
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
                        $"CutRevolve: contour resolved to unsupported type {live.GetType().Name}");
            }
        }
    }

    private static void SelectSketch(File file, string sketchName) {
        if (string.IsNullOrEmpty(sketchName)) {
            throw new ArgumentException("CutRevolve: sketch name missing — sketch must be added first");
        }
        var feat = FindFeatureByName(file, sketchName)
            ?? throw new ArgumentException($"Sketch feature '{sketchName}' not found");
        // Append-only — caller cleared the selection set up front so prior higher-mark selections survive.
        feat.Select2(true, 0);
    }

    private static void SelectAxis(File file, Definition axisDef) {
        var live = DefinitionResolver.Resolve(file, axisDef)
            ?? throw new InvalidOperationException("CutRevolve axis definition did not resolve to a live entity");

        // SW quirk: revolve axis goes on mark 4; sketch occupies mark 0 so always Append.
        const int axisMark = 4;
        switch (live) {
            case Edge e:
                // SW quirk: Select4 lives on IEntity, not on Edge.
                ((IEntity)e).Select4(true, MakeMarkSelectData(file, axisMark));
                break;
            case Feature feat:
                feat.Select2(true, axisMark);
                break;
            case SketchSegment seg:
                seg.Select4(true, MakeMarkSelectData(file, axisMark));
                break;
            default:
                throw new InvalidOperationException(
                    $"CutRevolve axis resolved to unsupported type {live.GetType().Name}");
        }
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

    private sealed record CutRevolveArgs(
        string SketchName,
        Definition Axis,
        double Angle,
        double? AngleB,
        bool MidPlane,
        bool Reversed,
        IReadOnlyList<Definition> Contours,
        IReadOnlyList<Definition>? FeatureScope
    ) {

        internal static CutRevolveArgs Parse(JsonNode input) {
            var sketchName = input["sketch"]?["name"]?.GetValue<string>() ?? "";
            var axis = Definition.FromJson(input["axis"])
                ?? throw new ArgumentException("CutRevolve: 'axis' missing or malformed");
            var angle = ReadDouble(input, "angle");
            // SW quirk: zero-angle revolves are silently rejected (FeatureRevolve2 returns null).
            if (angle <= 0 || angle > 2.0 * Math.PI + 1e-9) {
                throw new ArgumentException($"CutRevolve: angle {angle} radians outside (0, 2π]");
            }
            double? angleB = null;
            var angleBNode = input["angle_b"];
            if (angleBNode is not null && angleBNode.GetValueKind() != System.Text.Json.JsonValueKind.Null) {
                var v = angleBNode.GetValue<double>();
                if (v <= 0 || v > 2.0 * Math.PI + 1e-9) {
                    throw new ArgumentException($"CutRevolve: angle_b {v} radians outside (0, 2π]");
                }
                angleB = v;
            }
            var midPlane = ReadBool(input, "mid_plane", false);
            if (midPlane && angleB is not null) {
                throw new ArgumentException("CutRevolve: mid_plane and angle_b are mutually exclusive");
            }
            var reversed = ReadBool(input, "reversed", false);
            var contours = ReadDefinitionList(input, "contours");
            IReadOnlyList<Definition>? featureScope = ReadOptionalDefinitionList(input, "feature_scope");
            return new CutRevolveArgs(sketchName, axis, angle, angleB, midPlane, reversed, contours,
                featureScope);
        }
    }

    private static IReadOnlyList<Definition>? ReadOptionalDefinitionList(JsonNode node, string field) {
        // null/absent = AutoSelect; non-empty = user-narrowed; empty collapses to null.
        var v = node[field];
        if (v is null) return null;
        if (v.GetValueKind() == System.Text.Json.JsonValueKind.Null) return null;
        if (v is not JsonArray arr) return null;
        var list = new List<Definition>(arr.Count);
        foreach (var item in arr) {
            var d = Definition.FromJson(item);
            if (d is not null) list.Add(d);
        }
        return list.Count > 0 ? list : null;
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

    private static bool ReadBool(JsonNode node, string field, bool fallback) {
        var v = node[field];
        return v is null ? fallback : v.GetValue<bool>();
    }
}
