using System.Text.Json.Nodes;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Sketches;

// Non-angle dimensions (Distance, Radius, Diameter, ArcLength, Ordinate, ...).
// Distance/ArcLength/Ordinate/Scalar use ModelDoc.AddDimension2; everything else routes through
// Extension.AddSpecificDimension with a per-kind dimType int from swDimensionType_e.
internal static class DimensionConstraint {
    // swDimensionType_e int values.
    internal const int Angular            = 3;   // exposed for the read-side dispatcher.
    internal const int DoubleAngular      = 14;
    internal const int DoubleDistance     = 15;
    // SW relation-type ints whose backing DisplayDimension.Type2 lies about the dim family.
    private const int RelationDoubleDistance = 41;  // SW reports Type2=6 (Diameter) for these.
    private const int RelationDoubleAngle    = 84;  // SW reports Type2=3 (Angular) for these.
    private const int Ordinate            = 1;
    private const int Distance            = 2;
    private const int ArcLength           = 4;
    private const int Radial              = 5;
    private const int Diameter            = 6;
    private const int HorizontalOrdinate  = 7;
    private const int VerticalOrdinate    = 8;
    private const int ZAxis               = 9;   // SW quirk: ZAxis is grouped with ArcLength/Scalar — uses AddDimension2.
    private const int Chamfer             = 10;
    private const int HorizontalDistance  = 11;
    private const int VerticalDistance    = 12;
    private const int Scalar              = 13;
    private const int AngularOrdinate     = 16;

    private static readonly Dictionary<int, ConstraintKind> KindByType = new() {
        [Ordinate]           = ConstraintKind.Ordinate,
        [Distance]           = ConstraintKind.Distance,
        [Angular]            = ConstraintKind.Angle,
        [ArcLength]          = ConstraintKind.ArcLength,
        [Radial]             = ConstraintKind.Radius,
        [Diameter]           = ConstraintKind.Diameter,
        [HorizontalOrdinate] = ConstraintKind.HorizontalOrdinate,
        [VerticalOrdinate]   = ConstraintKind.VerticalOrdinate,
        [ZAxis]              = ConstraintKind.ZAxis,
        [Chamfer]            = ConstraintKind.ChamferDimension,
        [HorizontalDistance] = ConstraintKind.HorizontalDistance,
        [VerticalDistance]   = ConstraintKind.VerticalDistance,
        [Scalar]             = ConstraintKind.Scalar,
        [DoubleAngular]      = ConstraintKind.DoubleAngular,
        [DoubleDistance]     = ConstraintKind.DoubleDistance,
        [AngularOrdinate]    = ConstraintKind.AngularOrdinate,
    };

    private static readonly Dictionary<ConstraintKind, int> TypeByKind = new() {
        [ConstraintKind.Ordinate]           = Ordinate,
        [ConstraintKind.Distance]           = Distance,
        [ConstraintKind.Angle]              = Angular,
        [ConstraintKind.ArcLength]          = ArcLength,
        [ConstraintKind.Radius]             = Radial,
        [ConstraintKind.Diameter]           = Diameter,
        [ConstraintKind.HorizontalOrdinate] = HorizontalOrdinate,
        [ConstraintKind.VerticalOrdinate]   = VerticalOrdinate,
        [ConstraintKind.ZAxis]              = ZAxis,
        [ConstraintKind.ChamferDimension]   = Chamfer,
        [ConstraintKind.HorizontalDistance] = HorizontalDistance,
        [ConstraintKind.VerticalDistance]   = VerticalDistance,
        [ConstraintKind.Scalar]             = Scalar,
        [ConstraintKind.DoubleAngular]      = DoubleAngular,
        [ConstraintKind.DoubleDistance]     = DoubleDistance,
        [ConstraintKind.AngularOrdinate]    = AngularOrdinate,
    };

    internal static bool HandlesKind(ConstraintKind kind) => TypeByKind.ContainsKey(kind);

    // SW quirk: DisplayDimension.Type2 lies for revolved-diameter dims (reports 6=Diameter
    // when relation.GetRelationType()=41=DoubleDistance) and for "double-angle" dims
    // (reports 3=Angular when relation type=84=DoubleAngle). When the relation type is
    // the doubled variant, force the dim family accordingly.
    // Used by both this class's Read AND SketchConstraint.Read (for Angular vs Dimension routing).
    internal static int ResolveDimType(SketchRelation relation, DisplayDimension disp) =>
        relation.GetRelationType() switch {
            RelationDoubleDistance => DoubleDistance,
            RelationDoubleAngle    => DoubleAngular,
            _                      => disp.Type2,
        };

    internal static void Add(File file, Sketch sketch, ConstraintKind kind, JsonNode node, List<object> resolved) {
        var value = node["value"]?.GetValue<double>();
        SketchHandler.SelectAllForConstraint(file, resolved);
        var (tx, ty, tz) = ConstraintHelpers.ResolveTextLocation(file, sketch, node);

        // SW quirk: AddDimension2 pops a modal value-prompt dialog unless swInputDimValOnCreate is off; restore after.
        var sld = file.SldWorks;
        sld.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, false);
        // SW quirk: Distance=0 between coincident SketchPoints is rejected (no arrow direction). Nudge one by 1e-5, then restore.
        SketchPoint? jitterPoint = null;
        double jitterX = 0, jitterY = 0, jitterZ = 0;
        if (kind == ConstraintKind.Distance && value.HasValue && Math.Abs(value.Value) < 1e-12) {
            foreach (var live in resolved) {
                if (live is SketchPoint sp) {
                    jitterPoint = sp;
                    jitterX = sp.X; jitterY = sp.Y; jitterZ = sp.Z;
                    sp.SetCoords(jitterX - 1e-5, jitterY - 1e-5, jitterZ - 1e-5);
                    break;
                }
            }
        }
        try {
            // AddDimension2 handles Distance/ArcLength/Ordinate/Scalar; everything else goes through AddSpecificDimension.
            DisplayDimension? disp;
            int specificDimError = 0;
            int specificDimType = 0;
            if (kind is ConstraintKind.Distance or ConstraintKind.ArcLength or ConstraintKind.Ordinate or ConstraintKind.Scalar or ConstraintKind.ZAxis) {
                disp = file.ModelDoc.AddDimension2(tx, ty, tz) as DisplayDimension;
            } else {
                if (!TypeByKind.TryGetValue(kind, out specificDimType)) {
                    throw new ArgumentException($"Dimension kind '{kind}' not handled");
                }
                disp = file.ModelDoc.Extension.AddSpecificDimension(tx, ty, tz, specificDimType, ref specificDimError) as DisplayDimension;
            }
            if (disp is null) {
                throw new InvalidOperationException(
                    $"Dimension {kind} could not be created (AddSpecificDimension dimType={specificDimType} error={specificDimError} at " +
                    $"tx={tx:G6} ty={ty:G6} tz={tz:G6} sketch='{((Feature)sketch).Name}' selectedCount={resolved.Count})");
            }
            if (value.HasValue) {
                var dimension = disp.GetDimension2(0);
                dimension.SetSystemValue3(value.Value, (int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration, null);
            }
        } finally {
            sld.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate, true);
            if (jitterPoint is not null) {
                jitterPoint.SetCoords(jitterX, jitterY, jitterZ);
            }
        }
    }

    internal static JsonObject? Read(File file, Sketch sketch, SketchRelation relation, DisplayDimension disp,
                                     HashSet<(string Kind, long Id)> emittedIds) {
        var dim = disp.GetDimension2(0);
        if (dim is null) return null;

        var dimTypeInt = ResolveDimType(relation, disp);
        if (!KindByType.TryGetValue(dimTypeInt, out var kind)) {
            throw new InvalidOperationException($"Inspect(Sketch): unknown dimension Type2={dimTypeInt}");
        }

        // SW quirk: Scalar (Type2=13) is the sketch-pattern instance-count dim. SW
        // stores it inside SketchLinearPattern / SketchCircularPattern composites
        // alongside the Patterned relation that links seed → instances. The pattern
        // parsers fold those at the post-Inspect pass and the count becomes a
        // composite field — the standalone Scalar dim is redundant. When folding
        // fails (no Patterned relation in source) the dim is orphan: AddDimension2
        // between two unpatterned circles can't reauthor "instance count", and SW
        // either silently drops it (clean) or reinterprets as Distance (destructive).
        // Drop at capture in either case.
        if (dimTypeInt == Scalar) {
            SldworksLog.Warning(
                "Inspect(Sketch): skipping Scalar (pattern instance-count) dim — " +
                "wire format can't reauthor it standalone; folded patterns carry the " +
                "count as a composite field instead");
            return null;
        }

        // Skip the whole constraint when refs can't be captured (silhouette / use-edge
        // proxies, unrecoverable Revolve temp axis). Coercing to empty refs would emit a
        // poison entry that crashes Add at AddDimension2 with selectedCount=0.
        var refsJson = ConstraintHelpers.ReadRelationRefs(file, relation, sketch, emittedIds);
        if (refsJson is null) return null;

        // SW quirk: DisplayDimension.Type2 can report Radial even when the dimension
        // is really a distance from/to another selected entity. A true radius authors
        // from one curved ref; with multiple refs AddSpecificDimension(Radial) is
        // rejected or changes the sketch, while AddDimension2 reauthors the intended
        // distance relation.
        if (kind == ConstraintKind.Radius && refsJson.Count > 1) {
            SldworksLog.Information(
                "Inspect(Sketch): coercing multi-ref Radius dimension to Distance " +
                "(relationType={RelationType}, refCount={RefCount})",
                relation.GetRelationType(), refsJson.Count);
            kind = ConstraintKind.Distance;
        }

        return new JsonObject {
            ["kind"] = kind.ToString(),
            ["refs"] = refsJson,
            ["value"] = ReadValue(dim),
            // SW dimension name ("D1@Sketch1"), addressable via edit_dimension / delete_constraint.
            // echo_ = read-only here (Add auto-assigns names); rename happens via edit_dimension.
            ["echo_dim_name"] = dim.FullName,
            ["echo_text_location"] = ReadTextLocation(file, sketch, disp),
        };
    }

    internal static double ReadValue(Dimension dim) {
        // SW quirk: GetSystemValue3 returns double[] (one per resolved config); [0] for single-config.
        var systemValue = (double[])dim.GetSystemValue3((int)swInConfigurationOpts_e.swThisConfiguration, null);
        return systemValue.Length > 0 ? systemValue[0] : 0.0;
    }

    // Project annotation position through ModelToSketchTransform into sketch-plane coords.
    internal static JsonNode? ReadTextLocation(File file, Sketch sketch, DisplayDimension disp) {
        var ann = disp.GetAnnotation() as Annotation;
        var modelToSketch = ((ISketch)sketch).ModelToSketchTransform;
        if (ann is null || modelToSketch is null) return null;
        if (ann.GetPosition() is not double[] pos || pos.Length < 3) return null;
        var mp = (MathPoint)file.MathUtil.CreatePoint(pos);
        var projected = (double[])mp.IMultiplyTransform(modelToSketch).ArrayData;
        return new JsonArray(projected[0], projected[1]);
    }
}
