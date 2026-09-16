using System.Text.Json.Nodes;
using Sldworks.Core.Handlers.Shared;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers;

// SW quirk: any iteration of helix ReferenceCurve segments (GetSegments, IGetFirstSegment,
// IGetNextSegment, GetSegmentCount) corrupts the curve until part reload. Build only from
// IHelixFeatureData parametric values.
public static class HelixHandler {
    public const string TypeName = "Helix";

    // SW quirk: GetTypeName2() returns "Helix" for both helix and spiral (DefinedBy enum differentiates).
    public const string SwTypeName = "Helix";

    // ---- Add -----------------------------------------------------------------------

    public static JsonNode Add(File file, JsonNode input) {
        var args = HelixArgs.Parse(input);

        if (args.Segments is not null) {
            throw new NotSupportedException(
                $"Helix.Add({args.SketchName}): variable-pitch helices (`segments` field) " +
                "are not yet implemented.");
        }

        if (string.IsNullOrEmpty(args.SketchName)) {
            throw new ArgumentException("Helix: axis sketch name missing — sketch must be added first");
        }
        var sketchFeat = FindFeatureByName(file, args.SketchName)
            ?? throw new ArgumentException($"Helix axis sketch '{args.SketchName}' not found");

        var feature = InsertHelixOnce(file, sketchFeat, args, args.StartAngle);

        // SW quirk: StartingAngle drifts across rebuilds (SW snaps/clamps); single-retry correction at `wanted + diff`.
        var data = feature.GetDefinition() as HelixFeatureData
            ?? throw new InvalidOperationException(
                $"Helix.Add: GetDefinition did not return IHelixFeatureData on freshly-created {feature.Name}");
        var diameter = GetAxisCircleDiameter(sketchFeat);
        var actualAngle = GetTrueAngle(file, sketchFeat, data.StartingAngle, diameter);
        var diff = args.StartAngle - actualAngle;
        if (Math.Abs(diff) > Flags.GeometryTolerance) {
            var adjusted = NormalizeAngle(args.StartAngle + diff);
            SldworksLog.Information(
                "HelixHandler.Add: start-angle drift {Diff} rad on {Name} — recreating at adjusted angle {Adjusted}",
                diff, feature.Name, adjusted);

            feature.Select2(false, 0);
            file.ModelDoc.Extension.DeleteSelection2(0);
            file.ModelDoc.ClearSelection2(true);

            feature = InsertHelixOnce(file, sketchFeat, args, adjusted);
        }

        // Hide the helix to reduce feature-tree / graphics clutter for the user. A helix is
        // reference-curve geometry, so it's blanked via IModelDoc2.BlankRefGeom (select feature,
        // blank, clear). Display-only: BlankRefGeom changes visibility, not geometry/topology, so
        // Inspect (geometry read) and downstream features that reference the helix are unaffected.
        // Cosmetic — a hide failure must not abort a successfully-created feature, so swallow and
        // warn rather than throw (per the cosmetic-hide exception to the fail-loudly bar).
        try {
            feature.Select2(false, 0);
            file.ModelDoc.BlankRefGeom();
            file.ModelDoc.ClearSelection2(true);
        } catch (Exception ex) {
            SldworksLog.Warning(
                "HelixHandler.Add: failed to hide helix {Name} — leaving it visible: {Error}",
                feature.Name, ex.Message);
        }

        SldworksLog.Information("HelixHandler.Add: created {Name}", feature.Name);
        File.ApplyFeatureName(feature, input);
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);
        var output = input.DeepClone();
        output["name"] = feature.Name;
        return output;
    }

    // SW quirk: InsertHelix is positional only; named-arg variant's parameter names don't match the COM signature.
    // Helixdef is the swHelixDefinedBy_e int (0..3); SW ignores whichever of Height/Pitch isn't selected. TaperAngle is absolute (sign rides on Outward).
    private static Feature InsertHelixOnce(File file, Feature sketchFeat, HelixArgs args, double startAngle) {
        file.ModelDoc.ClearSelection2(true);
        sketchFeat.Select2(false, 0);

        file.ModelDoc.InsertHelix(
            args.Reversed,
            args.Clockwise,
            args.TaperAngle != 0.0,
            args.TaperOutward || args.TaperAngle < 0,
            (int)args.DefinedBy,
            args.Height,
            args.Pitch,
            args.Revolutions,
            Math.Abs(args.TaperAngle),
            startAngle);

        // SW quirk: InsertHelix returns void; pull via GetLastFeatureAdded.
        var feature = (Feature?)file.ModelDoc.Extension.GetLastFeatureAdded();
        if (feature is null || feature.GetTypeName2() != SwTypeName) {
            throw new InvalidOperationException(
                "Helix: InsertHelix did not produce a Helix feature — check sketch contains exactly one circle on a plane normal to the desired axis");
        }
        return feature;
    }

    // ---- Edit ----------------------------------------------------------------------

    // Edit an existing helix IN PLACE (GetDefinition -> mutate scalars -> ModifyDefinition);
    // we do NOT delete + re-add, so the feature name — and every downstream name-ref / probe —
    // survives. `input` is a FULL helix payload (same shape Inspect emits / Add consumes),
    // parsed by HelixArgs so edit and create share one validation. Only the parametric scalars
    // are applied; the defining base-circle SKETCH (the helix's first sub-feature) is kept as
    // built. Mirrors the Chamfer/Revolve Edit shape.
    //
    // SELECTION PARITY — the base-circle sketch CANNOT be re-pointed in place:
    // reflection of IHelixFeatureData (api_redist/2026) shows ONLY scalar properties
    // (Pitch/Height/Revolution/StartingAngle/Clockwise/ReverseDirection/Taper*/VariablePitch)
    // and the variable-pitch record methods — there is NO sketch / circle / axis / selection
    // property, and no IHelixFeatureData2. The helix's defining circle lives in the axis
    // sub-sketch (GetFirstSubFeature), reached structurally, not through a settable feature-data
    // reference. Add doesn't select it through feature-data either: it Select2's the sketch
    // feature then calls InsertHelix (a fresh-create API). So a request to point the helix at a
    // DIFFERENT base sketch is a different feature — we detect it (sketch.name mismatch) and
    // throw "delete and re-add" rather than silently ignore. An unchanged sketch.name is the
    // common scalar-only edit and passes through untouched.
    //
    // Helix has NO IAccessSelections lifecycle (the axis sketch is a sub-feature, not a
    // selection set on the feature data), so — unlike Chamfer/RefPlane/Loft — there is no
    // AccessSelections/ReleaseSelectionAccess pair here; we read/mutate the definition directly
    // exactly as Inspect/Add already do.
    public static JsonNode Edit(File file, Feature feature, JsonNode input) {
        var args = HelixArgs.Parse(input);

        // Variable-pitch is unsupported on Add AND Inspect; reject it here too rather than
        // silently editing only the constant-pitch scalars of a variable-pitch helix.
        if (args.Segments is not null) {
            throw new NotSupportedException(
                $"Helix.Edit({feature.Name}): variable-pitch helices (`segments` field) are not yet implemented.");
        }

        // Cast to the IHelixFeatureData interface (not the HelixFeatureData co-class interface):
        // the scalar SETTERS are declared on IHelixFeatureData, while the co-class interface
        // re-exposes a read-only view. Inspect reads through the co-class cast; Edit writes, so it
        // needs the settable interface.
        var data = feature.GetDefinition() as IHelixFeatureData
            ?? throw new InvalidOperationException(
                $"Helix.Edit: feature {feature.Name} does not expose IHelixFeatureData");

        if (data.VariablePitch) {
            throw new NotSupportedException(
                $"Helix.Edit({feature.Name}): the existing helix is variable-pitch, which is not yet implemented.");
        }

        // Selection parity: refuse a base-sketch (defining circle) re-point. IHelixFeatureData
        // exposes no setter to re-attach the axis sketch (see method header), so a different
        // sketch.name is a different feature. Compare the incoming name to the built helix's
        // first sub-feature (its axis sketch), resolving both through FeatureName so a "<N>"
        // shared-consumer suffix doesn't trip a false mismatch. An empty incoming name means the
        // payload didn't pin the sketch — treat as "no change" (don't fabricate a mismatch).
        var builtSketch = feature.GetFirstSubFeature() as Feature;
        if (builtSketch is not null && !string.IsNullOrEmpty(args.SketchName)) {
            var builtName = FeatureName.Resolve(file, builtSketch.Name ?? "");
            var requestedName = FeatureName.Resolve(file, args.SketchName);
            if (!string.Equals(builtName, requestedName, StringComparison.Ordinal)) {
                throw new InvalidOperationException(
                    $"Helix.Edit: cannot re-point the base sketch on '{feature.Name}' "
                    + $"('{builtName}' -> '{requestedName}'); IHelixFeatureData exposes no setter for "
                    + "the defining circle — delete and re-add.");
            }
        }

        // Refuse a defined-by mode change. The base-circle sketch + InsertHelix's defined-by
        // enum determine which of pitch/height/revolution are the independent inputs; changing
        // the mode in place would reinterpret the very scalars we are about to set against a
        // different solve. That is a different feature — delete and re-add.
        var currentDefinedBy = (swHelixDefinedBy_e)data.DefinedBy;
        if (currentDefinedBy != args.DefinedBy) {
            throw new InvalidOperationException(
                $"Helix.Edit: cannot change defined_by on '{feature.Name}' "
                + $"({DefinedByToWire(currentDefinedBy)} -> {DefinedByToWire(args.DefinedBy)}); "
                + "that reinterprets the helix's independent scalars — delete and re-add.");
        }

        // Mutate ONLY the scalars Inspect reads. The defined-by enum picks which of
        // Pitch/Height/Revolution SW treats as independent; the other is ignored by the solver
        // (per InsertHelixOnce's comment), so setting all three is harmless and keeps the data
        // self-consistent. Taper sign convention mirrors Add: wire taper_angle < 0 == outward,
        // and SW's TaperAngle is the absolute magnitude with direction on TaperOutward.
        data.DefinedBy = (int)args.DefinedBy;
        data.Pitch = args.Pitch;
        data.Height = args.Height;
        data.Revolution = args.Revolutions;
        data.Clockwise = args.Clockwise;
        data.ReverseDirection = args.Reversed;

        var taperOutward = args.TaperOutward || args.TaperAngle < 0;
        data.Taper = args.TaperAngle != 0.0;
        data.TaperAngle = Math.Abs(args.TaperAngle);
        data.TaperOutward = taperOutward;

        // StartingAngle is the raw stored angle. Add corrects for SW's rebuild drift by a
        // projection probe (GetTrueAngle); Inspect reports the recovered TRUE angle. Here we
        // set StartingAngle to the requested start_angle and then, after ModifyDefinition lands,
        // measure the true angle and re-solve once at the drift-corrected value — the same
        // single-retry correction Add uses, so an edited start_angle round-trips like a created one.
        data.StartingAngle = NormalizeAngle(args.StartAngle);

        if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
            throw new InvalidOperationException(
                $"Helix.Edit: ModifyDefinition returned false on '{feature.Name}' — "
                + "pitch/height/revolutions/taper invalid against the axis sketch?");
        }

        // SW quirk: StartingAngle drifts across rebuilds (SW snaps/clamps). Mirror Add's
        // single-retry correction: probe the true angle off the axis-sketch circle and, if it
        // missed, re-solve once at `wanted + diff`. The axis sketch is the helix's first
        // sub-feature (kept as built).
        var sketchSub = feature.GetFirstSubFeature() as Feature;
        if (sketchSub is not null) {
            var diameter = GetAxisCircleDiameter(sketchSub);
            var actualAngle = GetTrueAngle(file, sketchSub, data.StartingAngle, diameter);
            var diff = args.StartAngle - actualAngle;
            if (Math.Abs(diff) > Flags.GeometryTolerance) {
                var adjusted = NormalizeAngle(args.StartAngle + diff);
                SldworksLog.Information(
                    "HelixHandler.Edit: start-angle drift {Diff} rad on {Name} — re-solving at adjusted angle {Adjusted}",
                    diff, feature.Name, adjusted);
                data.StartingAngle = adjusted;
                if (!feature.ModifyDefinition(data, file.ModelDoc, null)) {
                    throw new InvalidOperationException(
                        $"Helix.Edit: ModifyDefinition (start-angle correction) returned false on '{feature.Name}'");
                }
            }
        }

        // Roll to end so the change propagates downstream (ForceRebuildAll). NOT AfterFeature:
        // a mid-tree helix must not roll later features back.
        file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        SldworksLog.Information("HelixHandler.Edit: {Name}", feature.Name);
        return Inspect(file, feature);
    }

    // ---- Inspect -------------------------------------------------------------------

    public static JsonNode Inspect(File file, Feature feature) {
        var resolvedType = File.ResolveFeatureTypeName(feature);
        if (resolvedType != SwTypeName) {
            throw new ArgumentException(
                $"HelixHandler.Inspect: unsupported feature type {resolvedType}");
        }

        var data = feature.GetDefinition() as HelixFeatureData
            ?? throw new InvalidOperationException(
                $"Feature {feature.Name} ({feature.GetTypeName2()}) does not expose IHelixFeatureData");

        if (data.VariablePitch) {
            throw new NotSupportedException(
                $"Helix.Inspect({feature.Name}): variable-pitch helices are not yet implemented.");
        }

        // Axis sketch is the helix's first sub-feature.
        var sketchSub = feature.GetFirstSubFeature() as Feature;
        var sketchName = FeatureName.Resolve(file, sketchSub?.Name ?? "");

        // SW quirk: Taper can be true while TaperAngle is 0 on legacy data; gate emission on TaperAngle.
        var taperAngle = 0.0;
        var taperOutward = false;
        if (data.Taper && data.TaperAngle != 0.0) {
            taperAngle = data.TaperAngle;
            taperOutward = data.TaperOutward;
            if (taperOutward) {
                // Wire convention: negative taper_angle = outward.
                taperAngle = -taperAngle;
            }
        }

        // SW quirk: StartingAngle drifts across rebuilds; recover via projection probe. Diameter isn't on IHelixFeatureData, so read from axis-sketch circle.
        var trueAngle = sketchSub is null
            ? data.StartingAngle
            : GetTrueAngle(file, sketchSub, data.StartingAngle, GetAxisCircleDiameter(sketchSub));

        // Read-only `_computed_axes`: lift ModelToSketchTransform directly. HelixArgs.Parse ignores it on Add.
        JsonNode? computedAxes = null;
        if (sketchSub is not null) {
            var sk = sketchSub.GetSpecificFeature2() as Sketch;
            var t = (sk as ISketch)?.ModelToSketchTransform;
            if (t is not null) {
                var axes = MathUtils.GetTransformMatrix(t);
                var arr = new JsonArray();
                foreach (var row in axes) {
                    var rowArr = new JsonArray();
                    foreach (var v in row) {
                        rowArr.Add(v);
                    }
                    arr.Add(rowArr);
                }
                computedAxes = arr;
            }
        }

        var result = new JsonObject {
            ["type"] = TypeName,
            ["name"] = feature.Name,
            ["sketch"] = new JsonObject {
                ["type"] = "Sketch",
                ["name"] = sketchName,
            },
            ["defined_by"] = DefinedByToWire((swHelixDefinedBy_e)data.DefinedBy),
            ["pitch"] = data.Pitch,
            ["height"] = data.Height,
            ["revolutions"] = data.Revolution,
            ["start_angle"] = trueAngle,
            ["clockwise"] = data.Clockwise,
            ["reversed"] = data.ReverseDirection,
            ["taper_angle"] = taperAngle,
            ["taper_outward"] = taperOutward,
            ["echo_computed_axes"] = computedAxes,
        };
        return result;
    }

    // SW quirk: StartingAngle drifts across rebuilds. Synthesize the start vertex from parameters in sketch-local coords,
    // round-trip through sketch-to-model and back via ModelToSketchTransform, then atan2 — never touches helix topology (which iteration would corrupt).
    private static double GetTrueAngle(File file, Feature sketchFeat, double startAngle, double diameter) {
        var center = GetAxisCircleCenterInSketch(sketchFeat);
        var sketch = sketchFeat.GetSpecificFeature2() as Sketch;
        var modelToSketch = (sketch as ISketch)?.ModelToSketchTransform;
        if (modelToSketch is null) {
            return MathUtils.NormalizeAngle(startAngle);
        }

        var radius = diameter / 2.0;
        var localX = center.X + radius * Math.Cos(startAngle);
        var localY = center.Y + radius * Math.Sin(startAngle);

        var sketchToModel = modelToSketch.IInverse();
        var localPt = (MathPoint)file.MathUtil.CreatePoint(new[] { localX, localY, 0.0 });
        var modelPt = (MathPoint)localPt.IMultiplyTransform(sketchToModel);
        var projected = (double[])modelPt.IMultiplyTransform(modelToSketch).ArrayData;

        return MathUtils.NormalizeAngle(
            Math.Atan2(projected[1] - center.Y, projected[0] - center.X));
    }

    private static (double X, double Y) GetAxisCircleCenterInSketch(Feature sketchFeat) {
        var sketch = sketchFeat.GetSpecificFeature2() as Sketch
            ?? throw new InvalidOperationException(
                $"Helix axis '{sketchFeat.Name}': GetSpecificFeature2 did not return an ISketch");
        var segments = sketch.GetSketchSegments() as object[];
        if (segments is null) {
            throw new InvalidOperationException(
                $"Helix axis sketch '{sketchFeat.Name}' is empty — expected exactly one (non-construction) circle");
        }
        // SW quirk: a circle is a swSketchARC whose IsCircle() is non-zero.
        foreach (SketchSegment seg in segments) {
            if (seg.GetType() != (int)swSketchSegments_e.swSketchARC) continue;
            if (seg is not SketchArc arc) continue;
            if (arc.IsCircle() == 0) continue;
            if (seg.ConstructionGeometry) continue;
            var c = (SketchPoint)arc.IGetCenterPoint2();
            return (c.X, c.Y);
        }
        throw new InvalidOperationException(
            $"Helix axis sketch '{sketchFeat.Name}': no non-construction circle found");
    }

    // SW quirk: IHelixFeatureData has no Diameter property; read it off the axis-sketch circle.
    private static double GetAxisCircleDiameter(Feature sketchFeat) {
        var sketch = sketchFeat.GetSpecificFeature2() as Sketch
            ?? throw new InvalidOperationException(
                $"Helix axis '{sketchFeat.Name}': GetSpecificFeature2 did not return an ISketch");
        var segments = sketch.GetSketchSegments() as object[];
        if (segments is null) {
            throw new InvalidOperationException(
                $"Helix axis sketch '{sketchFeat.Name}' is empty — expected exactly one (non-construction) circle");
        }
        foreach (SketchSegment seg in segments) {
            if (seg.GetType() != (int)swSketchSegments_e.swSketchARC) continue;
            if (seg is not SketchArc arc) continue;
            if (arc.IsCircle() == 0) continue;
            if (seg.ConstructionGeometry) continue;
            return arc.GetRadius() * 2.0;
        }
        throw new InvalidOperationException(
            $"Helix axis sketch '{sketchFeat.Name}': no non-construction circle found");
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

    private static double NormalizeAngle(double a) => MathUtils.NormalizeAngle(a);

    // ---- Typed args ----------------------------------------------------------------

    private sealed record HelixArgs(
        string SketchName,
        swHelixDefinedBy_e DefinedBy,
        double Pitch,
        double Height,
        double Revolutions,
        double StartAngle,
        bool Clockwise,
        bool Reversed,
        double TaperAngle,
        bool TaperOutward,
        // Null = constant-pitch; non-null currently throws in Add.
        IReadOnlyList<(double Pitch, double Revolutions)>? Segments) {

        internal static HelixArgs Parse(JsonNode input) {
            var sketchName = input["sketch"]?["name"]?.GetValue<string>() ?? "";
            var definedByName = input["defined_by"]?.GetValue<string>() ?? "PitchAndRevolution";
            var definedBy = ParseDefinedBy(definedByName);
            var pitch = ReadDouble(input, "pitch", 0.0);
            var height = ReadDouble(input, "height", 0.0);
            var revs = ReadDouble(input, "revolutions", 0.0);
            var startAngle = ReadDouble(input, "start_angle", 0.0);
            var clockwise = ReadBool(input, "clockwise", false);
            var reversed = ReadBool(input, "reversed", false);
            var taperAngle = ReadDouble(input, "taper_angle", 0.0);
            var taperOutward = ReadBool(input, "taper_outward", false);
            var segments = ReadSegments(input["segments"]);
            return new HelixArgs(sketchName, definedBy, pitch, height, revs, startAngle,
                clockwise, reversed, taperAngle, taperOutward, segments);
        }

        private static IReadOnlyList<(double, double)>? ReadSegments(JsonNode? node) {
            if (node is null) return null;
            if (node is not JsonArray arr) {
                throw new ArgumentException("Helix.segments: expected array of [pitch, revolutions] pairs");
            }
            var list = new List<(double, double)>(arr.Count);
            foreach (var entry in arr) {
                if (entry is not JsonArray pair || pair.Count != 2) {
                    throw new ArgumentException(
                        "Helix.segments: each entry must be a [pitch, revolutions] pair");
                }
                list.Add((pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>()));
            }
            return list;
        }

        private static swHelixDefinedBy_e ParseDefinedBy(string s) {
            return s switch {
                "PitchAndRevolution"  => swHelixDefinedBy_e.swHelixDefinedByPitchAndRevolution,
                "HeightAndRevolution" => swHelixDefinedBy_e.swHelixDefinedByHeightAndRevolution,
                "HeightAndPitch"      => swHelixDefinedBy_e.swHelixDefinedByHeightAndPitch,
                "Spiral"              => swHelixDefinedBy_e.swHelixDefinedBySpiral,
                _ => throw new ArgumentException($"Helix: unknown defined_by '{s}'"),
            };
        }
    }

    private static string DefinedByToWire(swHelixDefinedBy_e value) =>
        value switch {
            swHelixDefinedBy_e.swHelixDefinedByPitchAndRevolution  => "PitchAndRevolution",
            swHelixDefinedBy_e.swHelixDefinedByHeightAndRevolution => "HeightAndRevolution",
            swHelixDefinedBy_e.swHelixDefinedByHeightAndPitch      => "HeightAndPitch",
            swHelixDefinedBy_e.swHelixDefinedBySpiral              => "Spiral",
            _ => throw new InvalidOperationException(
                $"Helix.Inspect: unknown swHelixDefinedBy_e value {(int)value}"),
        };

    private static double ReadDouble(JsonNode node, string field, double fallback) {
        var v = node[field];
        return v is null ? fallback : v.GetValue<double>();
    }

    private static bool ReadBool(JsonNode node, string field, bool fallback) {
        var v = node[field];
        return v is null ? fallback : v.GetValue<bool>();
    }
}
