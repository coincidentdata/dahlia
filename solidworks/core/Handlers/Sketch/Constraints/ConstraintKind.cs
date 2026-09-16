namespace Sldworks.Core.Handlers.Sketches;

// Wire-level constraint kind. Names match the JSON `kind` string verbatim — round-trip via
// Enum.TryParse / ToString. Unrecognized wire strings throw at the dispatcher boundary.
internal enum ConstraintKind {
    // Relations with canonical sgXXX tokens (write via ModelDoc.SketchAddConstraints).
    Horizontal,
    Vertical,
    Coincident,
    Tangent,
    Parallel,
    Perpendicular,
    Equal,
    Concentric,
    Midpoint,
    Symmetric,
    Fixed,
    HorizontalPoints,
    VerticalPoints,
    Collinear,
    Coradial,
    AtPierce,
    AtIntersect,
    MergePoints,
    AlongX,
    AlongY,
    AlongZ,
    OffsetEdge,
    UseEdge,
    ArcAngle90,
    ArcAngle180,
    ArcAngle270,
    ArcAngleTop,
    ArcAngleBottom,
    ArcAngleLeft,
    ArcAngleRight,
    EllipseAngle90,
    EllipseAngle180,
    EllipseAngle270,
    EllipseAngleTop,
    EllipseAngleBottom,
    EllipseAngleLeft,
    EllipseAngleRight,

    // Relations without sgXXX tokens (write via SketchRelationManager.AddRelation by int).
    DoubleDistanceRelation,
    Angle3Points,
    Normal,
    NormalPoints,
    AlongXPoints,
    AlongYPoints,
    AlongZPoints,
    ParallelYZ,
    ParallelXZ,
    Intersection,
    FitSpline,
    EqualCurvature,
    EqualTangent,
    TangentFace,
    BlockFixedLock,
    BlockNormalLock,
    BlockRotateLock,
    SameSlot,
    RadialOffset,
    PlanarOffset,
    ConicRho,
    C3Touch,
    DoubleAngle,
    SameCurveLength,

    // Read-only: recognized on Inspect, never authored on Add (SW exposes no API to author).
    FixedSlot,

    // Read-only structural kind backing the polygon composite. SW emits one Patterned
    // relation per consecutive edge pair around the polygon ring (edge[i] → edge[i+1],
    // closing back to edge[0]). PolygonParser consumes these to re-form the SketchPolygon
    // composite on Inspect; any Patterned relations that don't match a polygon ring are
    // stripped before the constraints array is returned. Never crosses the wire on Add.
    Patterned,

    // Non-angle dimensions.
    Distance,
    Radius,
    Diameter,
    ArcLength,
    Ordinate,
    HorizontalOrdinate,
    VerticalOrdinate,
    ChamferDimension,
    HorizontalDistance,
    VerticalDistance,
    Scalar,
    DoubleAngular,
    DoubleDistance,
    AngularOrdinate,
    ZAxis,

    // Angle dimension (its own family because of direction + Supplementary/Explementary flips).
    Angle,
}
