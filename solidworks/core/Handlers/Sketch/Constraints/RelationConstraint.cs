using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Sketches;

// Geometric relations (e.g. Coincident, Tangent, Parallel) and named non-token relations (e.g.
// DoubleDistanceRelation, ParallelXZ). All authored through SketchRelationManager.AddRelation by
// int. The ModelDoc.SketchAddConstraints("sgXXX") API silently no-ops on constraints SW deems
// redundant against the existing sketch state without any error signal, which masks real
// round-trip drops for relations like Horizontal on a fully-dimensioned line. AddRelation
// reports failure via either an exception or a null return, both of which we surface.
//
// AtPierce is the one documented exception: AddRelation hands SW only entity Dispatches with no
// pick location, so when the sketch point sits near multiple curves SW chooses a default pierce
// target that may disagree with the source (e.g. snapping onto a nearby SketchLine instead of
// the intended circular Edge, breaking downstream reference resolution). AtPierce takes a
// separate legacy path: Select4 every ref at the same XYZ pick location (the sketch point in
// model space, mirroring the SW UI's pierce flow), then fire
// ModelDoc.SketchAddConstraints("sgATPIERCE") and verify a relation actually got added by
// checking the manager's count delta — the legacy API silently no-ops on rejection.
internal static class RelationConstraint {

    // SW quirk: SketchRelation.GetRelationType() returns a bare int from swSketchRelations_e.
    private const int RelationHorizontal       = 4;
    private const int RelationVertical         = 5;
    private const int RelationTangent          = 6;
    private const int RelationParallel         = 7;
    private const int RelationPerpendicular    = 8;
    private const int RelationCoincident       = 9;
    private const int RelationConcentric       = 10;
    private const int RelationSymmetric        = 11;
    private const int RelationMidpoint         = 12;
    private const int RelationAtIntersect      = 13;
    private const int RelationEqual            = 14;
    private const int RelationFixed            = 17;
    private const int RelationArcAngle90       = 18;
    private const int RelationArcAngle180      = 19;
    private const int RelationArcAngle270      = 20;
    private const int RelationArcAngleTop      = 21;
    private const int RelationArcAngleBottom   = 22;
    private const int RelationArcAngleLeft     = 23;
    private const int RelationArcAngleRight    = 24;
    private const int RelationHorizontalPoints = 25;
    private const int RelationVerticalPoints   = 26;
    private const int RelationCollinear        = 27;
    private const int RelationCoradial         = 28;
    private const int RelationUseEdge          = 32;
    private const int RelationEllipseAngle90   = 33;
    private const int RelationEllipseAngle180  = 34;
    private const int RelationEllipseAngle270  = 35;
    private const int RelationEllipseAngleTop  = 36;
    private const int RelationEllipseAngleBottom = 37;
    private const int RelationEllipseAngleLeft = 38;
    private const int RelationEllipseAngleRight = 39;
    private const int RelationAtPierce         = 40;
    private const int RelationMergePoints      = 42;
    private const int RelationAlongX           = 48;
    private const int RelationAlongY           = 49;
    private const int RelationAlongZ           = 50;
    private const int RelationDoubleDistance   = 41;   // not the same as DimensionType.DoubleDistance (15)
    private const int RelationAngle3Points     = 43;
    private const int RelationNormal           = 45;
    private const int RelationNormalPoints     = 46;
    private const int RelationAlongXPoints     = 51;
    private const int RelationAlongYPoints     = 52;
    private const int RelationAlongZPoints     = 53;
    private const int RelationParallelYZ       = 54;
    private const int RelationParallelXZ       = 55;
    private const int RelationIntersection     = 56;
    private const int RelationFitSpline        = 60;
    private const int RelationEqualCurvature   = 61;
    private const int RelationEqualTangent     = 62;
    private const int RelationTangentFace      = 63;
    private const int RelationBlockFixedLock   = 70;
    private const int RelationBlockNormalLock  = 71;
    private const int RelationBlockRotateLock  = 72;
    private const int RelationFixedSlot        = 74;
    private const int RelationSameSlot         = 75;
    private const int RelationRadialOffset     = 78;
    private const int RelationPlanarOffset     = 79;
    private const int RelationConicRho         = 82;
    private const int RelationC3Touch          = 83;
    private const int RelationDoubleAngle      = 84;
    private const int RelationSameCurveLength  = 85;
    // Structural relation type 57 backs the polygon composite: SW emits one Patterned
    // relation per consecutive edge pair around the polygon ring. Surfaced through Read
    // so PolygonParser can fold them back into the polygon composite; never authored.
    internal const int RelationPatterned       = 57;

    // Read-side: int -> kind. FixedSlot is here so Inspect can identify it; the write side does not author it.
    private static readonly Dictionary<int, ConstraintKind> KindByTypeInt = new() {
        [RelationHorizontal]        = ConstraintKind.Horizontal,
        [RelationVertical]          = ConstraintKind.Vertical,
        [RelationCoincident]        = ConstraintKind.Coincident,
        [RelationTangent]           = ConstraintKind.Tangent,
        [RelationParallel]          = ConstraintKind.Parallel,
        [RelationPerpendicular]     = ConstraintKind.Perpendicular,
        [RelationEqual]             = ConstraintKind.Equal,
        [RelationConcentric]        = ConstraintKind.Concentric,
        [RelationMidpoint]          = ConstraintKind.Midpoint,
        [RelationSymmetric]         = ConstraintKind.Symmetric,
        [RelationFixed]             = ConstraintKind.Fixed,
        [RelationHorizontalPoints]  = ConstraintKind.HorizontalPoints,
        [RelationVerticalPoints]    = ConstraintKind.VerticalPoints,
        [RelationCollinear]         = ConstraintKind.Collinear,
        [RelationCoradial]          = ConstraintKind.Coradial,
        [RelationAtIntersect]       = ConstraintKind.AtIntersect,
        [RelationAtPierce]          = ConstraintKind.AtPierce,
        [RelationMergePoints]       = ConstraintKind.MergePoints,
        [RelationAlongX]            = ConstraintKind.AlongX,
        [RelationAlongY]            = ConstraintKind.AlongY,
        [RelationAlongZ]            = ConstraintKind.AlongZ,
        [RelationUseEdge]           = ConstraintKind.UseEdge,
        [RelationFixedSlot]         = ConstraintKind.FixedSlot,
        [RelationArcAngle90]        = ConstraintKind.ArcAngle90,
        [RelationArcAngle180]       = ConstraintKind.ArcAngle180,
        [RelationArcAngle270]       = ConstraintKind.ArcAngle270,
        [RelationArcAngleTop]       = ConstraintKind.ArcAngleTop,
        [RelationArcAngleBottom]    = ConstraintKind.ArcAngleBottom,
        [RelationArcAngleLeft]      = ConstraintKind.ArcAngleLeft,
        [RelationArcAngleRight]     = ConstraintKind.ArcAngleRight,
        [RelationEllipseAngle90]    = ConstraintKind.EllipseAngle90,
        [RelationEllipseAngle180]   = ConstraintKind.EllipseAngle180,
        [RelationEllipseAngle270]   = ConstraintKind.EllipseAngle270,
        [RelationEllipseAngleTop]   = ConstraintKind.EllipseAngleTop,
        [RelationEllipseAngleBottom]= ConstraintKind.EllipseAngleBottom,
        [RelationEllipseAngleLeft]  = ConstraintKind.EllipseAngleLeft,
        [RelationEllipseAngleRight] = ConstraintKind.EllipseAngleRight,
        // RelationDoubleDistance maps to DoubleDistanceRelation to avoid colliding with the dim kind DoubleDistance.
        [RelationDoubleDistance]    = ConstraintKind.DoubleDistanceRelation,
        [RelationAngle3Points]      = ConstraintKind.Angle3Points,
        [RelationNormal]            = ConstraintKind.Normal,
        [RelationNormalPoints]      = ConstraintKind.NormalPoints,
        [RelationAlongXPoints]      = ConstraintKind.AlongXPoints,
        [RelationAlongYPoints]      = ConstraintKind.AlongYPoints,
        [RelationAlongZPoints]      = ConstraintKind.AlongZPoints,
        [RelationParallelYZ]        = ConstraintKind.ParallelYZ,
        [RelationParallelXZ]        = ConstraintKind.ParallelXZ,
        [RelationIntersection]      = ConstraintKind.Intersection,
        [RelationFitSpline]         = ConstraintKind.FitSpline,
        [RelationEqualCurvature]    = ConstraintKind.EqualCurvature,
        [RelationEqualTangent]      = ConstraintKind.EqualTangent,
        [RelationTangentFace]       = ConstraintKind.TangentFace,
        [RelationBlockFixedLock]    = ConstraintKind.BlockFixedLock,
        [RelationBlockNormalLock]   = ConstraintKind.BlockNormalLock,
        [RelationBlockRotateLock]   = ConstraintKind.BlockRotateLock,
        [RelationSameSlot]          = ConstraintKind.SameSlot,
        [RelationRadialOffset]      = ConstraintKind.RadialOffset,
        [RelationPlanarOffset]      = ConstraintKind.PlanarOffset,
        [RelationConicRho]          = ConstraintKind.ConicRho,
        [RelationC3Touch]           = ConstraintKind.C3Touch,
        [RelationDoubleAngle]       = ConstraintKind.DoubleAngle,
        [RelationSameCurveLength]   = ConstraintKind.SameCurveLength,
        [RelationPatterned]         = ConstraintKind.Patterned,
    };

    // Write fallback: kind -> int for SketchRelationManager.AddRelation. Only kinds the Add
    // path can author. The structural kinds backing composite entities
    // (Patterned/SketchOffset/OffsetEdge) are read-only round-trip artifacts, and
    // FixedSlot has no write API — none of them appear here.
    private static readonly Dictionary<ConstraintKind, int> TypeIntByKind = new() {
        [ConstraintKind.Horizontal]        = RelationHorizontal,
        [ConstraintKind.Vertical]          = RelationVertical,
        [ConstraintKind.Coincident]        = RelationCoincident,
        [ConstraintKind.Tangent]           = RelationTangent,
        [ConstraintKind.Parallel]          = RelationParallel,
        [ConstraintKind.Perpendicular]     = RelationPerpendicular,
        [ConstraintKind.Equal]             = RelationEqual,
        [ConstraintKind.Concentric]        = RelationConcentric,
        [ConstraintKind.Midpoint]          = RelationMidpoint,
        [ConstraintKind.Symmetric]         = RelationSymmetric,
        [ConstraintKind.Fixed]             = RelationFixed,
        [ConstraintKind.HorizontalPoints]  = RelationHorizontalPoints,
        [ConstraintKind.VerticalPoints]    = RelationVerticalPoints,
        [ConstraintKind.Collinear]         = RelationCollinear,
        [ConstraintKind.Coradial]          = RelationCoradial,
        [ConstraintKind.AtIntersect]       = RelationAtIntersect,
        [ConstraintKind.AtPierce]          = RelationAtPierce,
        [ConstraintKind.MergePoints]       = RelationMergePoints,
        [ConstraintKind.AlongX]            = RelationAlongX,
        [ConstraintKind.AlongY]            = RelationAlongY,
        [ConstraintKind.AlongZ]            = RelationAlongZ,
        [ConstraintKind.UseEdge]           = RelationUseEdge,
        [ConstraintKind.ArcAngle90]        = RelationArcAngle90,
        [ConstraintKind.ArcAngle180]       = RelationArcAngle180,
        [ConstraintKind.ArcAngle270]       = RelationArcAngle270,
        [ConstraintKind.ArcAngleTop]       = RelationArcAngleTop,
        [ConstraintKind.ArcAngleBottom]    = RelationArcAngleBottom,
        [ConstraintKind.ArcAngleLeft]      = RelationArcAngleLeft,
        [ConstraintKind.ArcAngleRight]     = RelationArcAngleRight,
        [ConstraintKind.EllipseAngle90]    = RelationEllipseAngle90,
        [ConstraintKind.EllipseAngle180]   = RelationEllipseAngle180,
        [ConstraintKind.EllipseAngle270]   = RelationEllipseAngle270,
        [ConstraintKind.EllipseAngleTop]   = RelationEllipseAngleTop,
        [ConstraintKind.EllipseAngleBottom]= RelationEllipseAngleBottom,
        [ConstraintKind.EllipseAngleLeft]  = RelationEllipseAngleLeft,
        [ConstraintKind.EllipseAngleRight] = RelationEllipseAngleRight,
        [ConstraintKind.DoubleDistanceRelation] = RelationDoubleDistance,
        [ConstraintKind.Angle3Points]      = RelationAngle3Points,
        [ConstraintKind.Normal]            = RelationNormal,
        [ConstraintKind.NormalPoints]      = RelationNormalPoints,
        [ConstraintKind.AlongXPoints]      = RelationAlongXPoints,
        [ConstraintKind.AlongYPoints]      = RelationAlongYPoints,
        [ConstraintKind.AlongZPoints]      = RelationAlongZPoints,
        [ConstraintKind.ParallelYZ]        = RelationParallelYZ,
        [ConstraintKind.ParallelXZ]        = RelationParallelXZ,
        [ConstraintKind.Intersection]      = RelationIntersection,
        [ConstraintKind.FitSpline]         = RelationFitSpline,
        [ConstraintKind.EqualCurvature]    = RelationEqualCurvature,
        [ConstraintKind.EqualTangent]      = RelationEqualTangent,
        [ConstraintKind.TangentFace]       = RelationTangentFace,
        [ConstraintKind.BlockFixedLock]    = RelationBlockFixedLock,
        [ConstraintKind.BlockNormalLock]   = RelationBlockNormalLock,
        [ConstraintKind.BlockRotateLock]   = RelationBlockRotateLock,
        [ConstraintKind.SameSlot]          = RelationSameSlot,
        [ConstraintKind.RadialOffset]      = RelationRadialOffset,
        [ConstraintKind.PlanarOffset]      = RelationPlanarOffset,
        [ConstraintKind.ConicRho]          = RelationConicRho,
        [ConstraintKind.C3Touch]           = RelationC3Touch,
        [ConstraintKind.DoubleAngle]       = RelationDoubleAngle,
        [ConstraintKind.SameCurveLength]   = RelationSameCurveLength,
    };

    internal static bool HandlesKind(ConstraintKind kind) => TypeIntByKind.ContainsKey(kind);

    internal static string KindNameForTypeInt(int typeInt) =>
        KindByTypeInt.TryGetValue(typeInt, out var kind) ? kind.ToString() : $"Unknown{typeInt}";

    // Legacy "sgXXX" tokens for ModelDoc.SketchAddConstraints — the pre-AddRelation API path
    // SW's UI command bar still uses. Covers every kind that maps to one of SW's documented
    // tokens (SOLIDWORKS 2006+ names; 2D-sketch variants where the kind is 2D-only and 3D
    // variants where the kind is 3D-only). The IDispatch AddRelation path is preferred — this
    // map only matters for kinds whose AddRelation path rejects certain ref combinations
    // (AtPierce always; Coincident on face/edge/axis + point) and for the unconditional
    // legacy path used by AtPierce.
    private static readonly Dictionary<ConstraintKind, string> TokenByKind = new() {
        [ConstraintKind.Horizontal]        = "sgHORIZONTAL2D",
        [ConstraintKind.Vertical]          = "sgVERTICAL2D",
        [ConstraintKind.HorizontalPoints]  = "sgHORIZONTALPOINTS2D",
        [ConstraintKind.VerticalPoints]    = "sgVERTICALPOINTS2D",
        [ConstraintKind.AlongX]            = "sgALONGX3D",
        [ConstraintKind.AlongY]            = "sgALONGY3D",
        [ConstraintKind.AlongZ]            = "sgALONGZ3D",
        [ConstraintKind.AlongXPoints]      = "sgALONGXPOINTS3D",
        [ConstraintKind.AlongYPoints]      = "sgALONGYPOINTS3D",
        [ConstraintKind.AlongZPoints]      = "sgALONGZPOINTS3D",
        [ConstraintKind.Collinear]         = "sgCOLINEAR",
        [ConstraintKind.Coradial]          = "sgCORADIAL",
        [ConstraintKind.Perpendicular]     = "sgPERPENDICULAR",
        [ConstraintKind.Parallel]          = "sgPARALLEL",
        [ConstraintKind.Tangent]           = "sgTANGENT",
        [ConstraintKind.Concentric]        = "sgCONCENTRIC",
        [ConstraintKind.Coincident]        = "sgCOINCIDENT",
        [ConstraintKind.Symmetric]         = "sgSYMMETRIC",
        [ConstraintKind.Midpoint]          = "sgATMIDDLE",
        [ConstraintKind.AtIntersect]       = "sgATINTERSECT",
        [ConstraintKind.AtPierce]          = "sgATPIERCE",
        [ConstraintKind.Fixed]             = "sgFIXED",
        [ConstraintKind.Equal]             = "sgSAMELENGTH",
        [ConstraintKind.MergePoints]       = "sgMERGEPOINTS",
        [ConstraintKind.OffsetEdge]        = "sgOFFSETEDGE",
        [ConstraintKind.UseEdge]           = "sgUSEEDGE",
        [ConstraintKind.ArcAngle90]        = "sgARCANG90",
        [ConstraintKind.ArcAngle180]       = "sgARCANG180",
        [ConstraintKind.ArcAngle270]       = "sgARCANG270",
        [ConstraintKind.ArcAngleTop]       = "sgARCANGTOP",
        [ConstraintKind.ArcAngleBottom]    = "sgARCANGBOTTOM",
        [ConstraintKind.ArcAngleLeft]      = "sgARCANGLEFT",
        [ConstraintKind.ArcAngleRight]     = "sgARCANGRIGHT",
    };

    internal static void Add(File file, Sketch sketch, ConstraintKind kind, JsonArray refsArr) {
        if (kind == ConstraintKind.AtPierce) {
            AddViaLegacySelection(file, sketch, kind, refsArr, TokenByKind[kind]);
            return;
        }
        AddViaManager(file, sketch, kind, refsArr);
    }

    // Legacy creation path: clear selection, resolve each ref via DefinitionResolver
    // (with the active sketch as hint so same-sketch refs that omit sketch_name resolve
    // correctly), select the live entity via Definition.SelectLive, then fire
    // ModelDoc.SketchAddConstraints(token). SketchAddConstraints is silent on failure;
    // we verify by relation count delta.
    //
    // Used for kinds the IDispatch-array AddRelation path rejects:
    //   - AtPierce — fires sgATPIERCE unconditionally.
    //   - Anything else — entered after AddRelation returns null and the kind has
    //     a TokenByKind entry; the legacy UI command path sometimes accepts a
    //     selection the API path rejects (e.g. Coincident on face+point).
    private static void AddViaLegacySelection(
        File file, Sketch sketch, ConstraintKind kind, JsonArray refsArr, string token) {
        var modelDoc = file.ModelDoc;
        modelDoc.ClearSelection2(true);

        // Selection order matters for sgATPIERCE / sgCOINCIDENT on face+point:
        // SW's UI flow is "click the SketchPoint first, then click the external
        // geometry it pierces". Mirror that by selecting sketch entities
        // (SketchPoint / SketchSegment) before non-sketch refs.
        var resolvedInOrder = new List<(JsonNode? RefNode, object Live, bool IsSketchAnchor)>(refsArr.Count);
        foreach (var refNode in refsArr) {
            var def = Definition.FromJson(refNode)
                ?? throw new InvalidOperationException(
                    $"{kind}: ref {refNode?.ToJsonString()} is not a valid Definition");
            var live = DefinitionResolver.Resolve(file, def, sketch)
                ?? throw new InvalidOperationException(
                    $"{kind}: ref {refNode?.ToJsonString()} did not resolve to a live entity");
            resolvedInOrder.Add((refNode, live, SelectionOrderRank(live) == 0));
        }
        resolvedInOrder.Sort((a, b) => SelectionOrderRank(a.Live).CompareTo(SelectionOrderRank(b.Live)));

        // For AtPierce: derive a click-point hint from the sketch anchor's
        // source coords. SW's pierce solver uses the SelectData.X/Y/Z to pick
        // the nearest pierce branch on the curve — without it, SW arbitrarily
        // picks one of the two valid pierce points on a circle, often
        // mirroring the sketch on rebuild.
        Point3D? clickPointForCurves = null;
        if (kind == ConstraintKind.AtPierce) {
            clickPointForCurves = TryComputeAtPierceClickHint(file, sketch, refsArr);
        }

        foreach (var (refNode, live, isSketchAnchor) in resolvedInOrder) {
            // Only apply the click hint to the non-sketch (curve / face) refs —
            // the sketch anchor itself is a point, no positional ambiguity.
            var hint = (!isSketchAnchor) ? clickPointForCurves : null;
            if (!Definition.SelectLive(file, live, /*mark*/ 0, hint)) {
                modelDoc.ClearSelection2(true);
                throw new InvalidOperationException(
                    $"{kind}: SW rejected SelectLive on {live.GetType().Name} for ref {refNode?.ToJsonString()}");
            }
        }

        var mgr = sketch.RelationManager;
        var before = mgr.GetRelationsCount((int)swSketchRelationFilterType_e.swAll);
        modelDoc.SketchAddConstraints(token);
        var after = mgr.GetRelationsCount((int)swSketchRelationFilterType_e.swAll);
        modelDoc.ClearSelection2(true);

        if (after != before + 1) {
            throw new InvalidOperationException(
                $"{kind}: SketchAddConstraints(\"{token}\") did not author a new " +
                $"relation (count {before} → {after}); SW silently rejected — likely redundant " +
                $"against existing sketch state, or the selection didn't form an authorable " +
                $"target. refs={refsArr.ToJsonString()}");
        }
    }

    internal static JsonObject? Read(File file, Sketch sketch, SketchRelation relation, int typeInt,
                                     HashSet<(string Kind, long Id)> emittedIds) {
        string kindName;
        if (KindByTypeInt.TryGetValue(typeInt, out var kind)) {
            if (kind == ConstraintKind.Intersection) {
                SldworksLog.Warning(
                    "Inspect(Sketch): skipping {Kind} constraint — SolidWorks can crash or drop COM " +
                    "while exposing its definition refs; constraint will be lost on round-trip",
                    kind);
                return null;
            }
            kindName = kind.ToString();
        } else {
            kindName = "Unknown" + typeInt;
            SldworksLog.Debug("Inspect(Sketch): emitting unmapped relation type {Type} as {Kind}", typeInt, kindName);
        }
        var refsJson = ConstraintHelpers.ReadRelationRefs(file, relation, sketch, emittedIds);
        if (refsJson is null) return null;
        return new JsonObject {
            ["kind"] = kindName,
            ["refs"] = refsJson,
            ["value"] = null,
        };
    }

    // Sketch-anchor entities first (0), everything else after (1) — matches the SW UI
    // pierce / coincident flow: click the SketchPoint first, then click the geometry
    // it pierces. AddRelation by IDispatch[] is order-insensitive, but the legacy
    // `SketchAddConstraints(sgXXX)` selection-based path is not.
    private static int SelectionOrderRank(object live) => live switch {
        SketchPoint or SketchSegment => 0,
        _                            => 1,
    };

    // For AtPierce: find the SketchEntityDefinition ref's authored coords and
    // project them to 3D world space via the sketch's local-to-model transform.
    // The result feeds SelectData.X/Y/Z when selecting the curve ref, biasing
    // SW's pierce solver to the branch closest to where the sketch anchor was
    // authored. Returns null if no sketch-anchor ref carries a `p`/`start`/`end`
    // payload — the caller falls back to no-hint selection (SW's default
    // branch-picking, which is what was happening before this hint existed).
    private static Point3D? TryComputeAtPierceClickHint(File file, Sketch sketch, JsonArray refsArr) {
        var modelToSketch = ((ISketch)sketch).ModelToSketchTransform;
        if (modelToSketch is null) return null;
        var sketchToModel = modelToSketch.IInverse();
        foreach (var refNode in refsArr) {
            if (refNode is not JsonObject refObj) continue;
            var kindStr = refObj["kind"]?.GetValue<string>();
            // Prefer sketch_point's `p` (the dimensional anchor). For a sketch
            // line whose endpoint is the pierce anchor, `start.p` works too.
            JsonNode? pNode = kindStr switch {
                "sketch_point" => refObj["p"],
                "sketch_line" or "sketch_arc" or "sketch_circle"
                    or "sketch_ellipse" or "sketch_parabola" or "sketch_spline"
                    => refObj["start"]?["p"] ?? refObj["end"]?["p"],
                _ => null,
            };
            if (pNode is not JsonObject pObj) continue;
            var px = pObj["x"]?.GetValue<double>();
            var py = pObj["y"]?.GetValue<double>();
            if (px is null || py is null) continue;
            // 2D sketch-local (x, y, 0) → 3D world via sketch-to-model.
            var localPt = (MathPoint)file.MathUtil.CreatePoint(new[] { px.Value, py.Value, 0.0 });
            var modelPt = (MathPoint)localPt.IMultiplyTransform(sketchToModel);
            var arr = (double[])modelPt.ArrayData;
            return new Point3D(arr[0], arr[1], arr[2]);
        }
        return null;
    }

    private static void AddViaManager(File file, Sketch sketch, ConstraintKind kind, JsonArray refsArr) {
        if (!TypeIntByKind.TryGetValue(kind, out var typeInt)) {
            throw new InvalidOperationException(
                $"Constraint kind '{kind}' has no swConstraintType_e mapping for fallback");
        }
        var resolved = new List<object>(refsArr.Count);
        foreach (var refNode in refsArr) {
            var def = Definition.FromJson(refNode)
                ?? throw new InvalidOperationException(
                    $"Constraint {kind}: ref {refNode?.ToJsonString()} is not a valid Definition");
            var live = DefinitionResolver.Resolve(file, def, sketch)
                ?? throw new InvalidOperationException(
                    $"Constraint {kind}: ref {refNode?.ToJsonString()} did not resolve to a live entity");
            resolved.Add(live);
        }
        var wrappers = new DispatchWrapper[resolved.Count];
        for (var i = 0; i < resolved.Count; i++) wrappers[i] = new DispatchWrapper(resolved[i]);
        SketchRelation? relation;
        try {
            relation = sketch.RelationManager.AddRelation(wrappers, typeInt) as SketchRelation;
        } catch (Exception ex) {
            throw new InvalidOperationException(
                $"Constraint {kind}: AddRelation failed: {ex.Message}", ex);
        }
        // SW quirk: AddRelation returns null for some ref combinations the API
        // path rejects (e.g. Coincident on face/edge/axis + point) — but the
        // legacy UI command path with the matching sgXXX token sometimes accepts
        // the same selection. Fall back through TokenByKind before raising.
        if (relation is null && TokenByKind.TryGetValue(kind, out var token)) {
            AddViaLegacySelection(file, sketch, kind, refsArr, token);
            return;
        }
        // SW quirk: AddRelation can also return null on self-relations (both refs
        // resolve to the same live entity, which happens when target SW fuses what
        // were two distinct source SketchPoints). The self-relation case must be
        // filtered upstream (Python runner skips when refs share the same
        // SketchEntityId triplet); anything that reaches here is a real authoring
        // failure and we surface it as an error.
        if (relation is null) {
            throw new InvalidOperationException(
                $"RelationManager.AddRelation returned null for kind={kind} (refs={refsArr.ToJsonString()}); " +
                "SW rejected the relation — either it's a self-relation that the upstream same-id " +
                "guard missed, or it's redundant against the existing sketch state");
        }
    }
}
