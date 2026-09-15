"""Dahlia — Python client for industry CAD software.

Use lowercase factories and File methods after connect(). Lengths are meters
and angles are radians; MM, INCH, and DEG are unit multipliers.
"""
from __future__ import annotations

# --- types --------------------------------------------------------------
from .types import (
    Point2D,
    Point3D,
    Direction,
    Plane,
    Ray,
    BoundingBox,
    Definition,
    PlanarFaceDefinition,
    CylindricalFaceDefinition,
    ConicalFaceDefinition,
    SphericalFaceDefinition,
    ToroidalFaceDefinition,
    BSurfaceFaceDefinition,
    LineEdgeDefinition,
    CircularEdgeDefinition,
    EllipticalEdgeDefinition,
    SplineEdgeDefinition,
    VertexDefinition,
    BodyDefinition,
    BodyProbe,
    SketchEntityId,
    SketchEntityDefinition,
    SketchLineDefinition,
    SketchCircleDefinition,
    SketchArcDefinition,
    SketchPointDefinition,
    SketchEllipseDefinition,
    SketchEllipticalArcDefinition,
    SketchParabolaDefinition,
    SketchSplineDefinition,
    SketchContourDefinition,
    RegionDefinition,
    RefAxisDefinition,
    RefPlaneDefinition,
    ray,
    point,
)

# --- constants ----------------------------------------------------------
from .constants import (
    FACE, EDGE, VERTEX, BODY, PLANE,
    TOP, FRONT, RIGHT,
    MM, CM, M, INCH,
    DEG, RAD,
)

# --- sketch + features --------------------------------------------------
from .sketch import (
    Sketch,
    SketchPoint, SketchLine, SketchCircle, SketchArc,
    SketchRectangle, SketchPolygon,
    SketchEllipse, SketchEllipticalArc, SketchParabola, SketchSpline,
    SketchLinearPattern,
    SketchCircularPattern, SketchOffsetEntity,
    SketchConstraint,
    sketch,
    # entity factories (build entities to embed in a sketch(entities=[...]) literal)
    ORIGIN,
    sketch_point, sketch_line, sketch_circle, sketch_arc,
    sketch_rectangle, sketch_polygon,
    sketch_linear_pattern, sketch_circular_pattern,
    sketch_ellipse, sketch_elliptical_arc, sketch_parabola, sketch_spline,
    # constraint factories (apply after add_feature via add_constraints(s, [...]))
    constrain,
    coincident, horizontal, vertical, parallel, perpendicular, tangent,
    concentric, collinear, equal, coradial, at_midpoint, symmetric, fixed,
    horizontal_points, vertical_points, merge_points, at_pierce, at_intersect,
    use_edge, offset_edge,
    distance, horizontal_distance, vertical_distance, angle,
    radius, diameter, arc_length,
)
from .features import (
    Extrude, extrude,
    CutExtrude, cut_extrude,
    Revolve, revolve,
    CutRevolve, cut_revolve,
    Fillet, fillet, face_fillet, full_round_fillet,
    Chamfer, chamfer,
    LinearPattern, linear_pattern,
    CircularPattern, circular_pattern,
    Mirror, mirror,
    Shell, shell, ShellWall, shell_wall,
    Draft, draft, DraftEdge, draft_edge,
    Rib, rib,
    Sweep, sweep,
    CutSweep, cut_sweep,
    Helix, helix,
    Loft, loft, CutLoft, cut_loft,
    ProjectedCurve, projected_curve,
    Combine, combine,
    Split, split,
    RefPlane, ref_plane,
    RefAxis, ref_axis,
    Feature,
)

# --- helpers ------------------------------------------------------------
from .helpers import (circle_points, contour, linspace, midpoint,
                      offset_points, feature_ref)

# --- file handle + session ---------------------------------------------
from .file import File
from .session import (
    connect,
    disconnect,
    view_file,
    open_file,
    create_file,
    list_files,
    get_active_file,
    close_all_files,
)


from .assembly import Component, Transform, transform
from . import mates

__all__ = [
    "Component", "Transform", "transform", "mates",
    # types
    "Point2D", "Point3D", "Direction", "Plane", "Ray", "BoundingBox",
    "Definition",
    "PlanarFaceDefinition", "CylindricalFaceDefinition",
    "ConicalFaceDefinition", "SphericalFaceDefinition",
    "ToroidalFaceDefinition",
    "BSurfaceFaceDefinition",
    "LineEdgeDefinition", "CircularEdgeDefinition",
    "EllipticalEdgeDefinition",
    "SplineEdgeDefinition", "VertexDefinition",
    "BodyDefinition", "BodyProbe",
    "SketchEntityId", "SketchEntityDefinition",
    "SketchLineDefinition", "SketchCircleDefinition", "SketchArcDefinition",
    "SketchPointDefinition", "SketchEllipseDefinition",
    "SketchEllipticalArcDefinition", "SketchParabolaDefinition",
    "SketchSplineDefinition",
    "SketchContourDefinition",
    "RegionDefinition", "RefAxisDefinition", "RefPlaneDefinition",
    "ray", "point",
    # constants
    "FACE", "EDGE", "VERTEX", "BODY", "PLANE",
    "TOP", "FRONT", "RIGHT",
    "MM", "CM", "M", "INCH", "DEG", "RAD",
    # sketch + features
    "Sketch",
    "SketchPoint", "SketchLine", "SketchCircle", "SketchArc",
    "SketchRectangle", "SketchPolygon",
    "SketchEllipse", "SketchEllipticalArc", "SketchParabola", "SketchSpline",
    "SketchLinearPattern",
    "SketchCircularPattern", "SketchOffsetEntity",
    "SketchConstraint",
    "sketch",
    # entity factories
    "ORIGIN",
    "contour",
    "sketch_point", "sketch_line", "sketch_circle", "sketch_arc",
    "sketch_rectangle", "sketch_polygon",
    "sketch_linear_pattern", "sketch_circular_pattern",
    "sketch_ellipse", "sketch_elliptical_arc", "sketch_parabola", "sketch_spline",
    # constraint factories
    "constrain",
    "coincident", "horizontal", "vertical", "parallel", "perpendicular",
    "tangent", "concentric", "collinear", "equal", "coradial", "at_midpoint",
    "symmetric", "fixed", "horizontal_points", "vertical_points",
    "merge_points", "at_pierce", "at_intersect", "use_edge", "offset_edge",
    "distance", "horizontal_distance", "vertical_distance", "angle",
    "radius", "diameter", "arc_length",
    "Extrude", "extrude",
    "CutExtrude", "cut_extrude",
    "Revolve", "revolve",
    "CutRevolve", "cut_revolve",
    "Fillet", "fillet", "face_fillet", "full_round_fillet",
    "Chamfer", "chamfer",
    "LinearPattern", "linear_pattern",
    "CircularPattern", "circular_pattern",
    "Mirror", "mirror",
    "Shell", "shell", "ShellWall", "shell_wall",
    "Draft", "draft", "DraftEdge", "draft_edge",
    "Rib", "rib",
    "Sweep", "sweep",
    "CutSweep", "cut_sweep",
    "Helix", "helix",
    "Loft", "loft", "CutLoft", "cut_loft",
    "ProjectedCurve", "projected_curve",
    "Combine", "combine",
    "Split", "split",
    "RefPlane", "ref_plane",
    "RefAxis", "ref_axis",
    "Feature",
    # helpers
    "circle_points", "linspace", "midpoint", "offset_points",
    "feature_ref",
    # file handle + session
    "File",
    "connect", "disconnect",
    "view_file", "open_file", "create_file",
    "list_files", "get_active_file", "close_all_files",
]
