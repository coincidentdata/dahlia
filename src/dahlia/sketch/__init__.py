"""Sketch feature + sketch entity types and the sketch() factory.

Layout:

- `entities.py`       — short `Sketch*` aliases for the Definition flavors;
                        `_to_p2`, `_to_pt`, `_pending_id`, `_is_sketch_entity`.
- `composites.py`     — `_CompositeSketchEntity` base + the 5 composites
                        (Rectangle, Polygon, LinearPattern, CircularPattern,
                        OffsetEntity).
- `constraints.py`    — `CONSTRAINT_KIND` + `SketchConstraint`.
- `core.py`           — the `Sketch` feature class itself, the `SKETCH_ENTITY`
                        union, and the lowercase `sketch()` factory.
- `_factories.py`     — lowercase entity factories (`sketch_ellipse`,
                        `sketch_elliptical_arc`, `sketch_parabola`,
                        `sketch_spline`) for building free-standing entity
                        values to embed in a Sketch literal.
"""
from __future__ import annotations

# Re-export `CONSTRAINED_STATUS` so callers can import it from this package.
from ..types import CONSTRAINED_STATUS

from .entities import (
    SketchPoint,
    SketchLine,
    SketchCircle,
    SketchArc,
    SketchEllipse,
    SketchEllipticalArc,
    SketchParabola,
    SketchSpline,
    _is_sketch_entity,
    _pending_id,
    _to_p2,
    _to_pt,
)
from .composites import (
    _CompositeSketchEntity,
    SketchRectangle,
    SketchPolygon,
    SketchLinearPattern,
    SketchCircularPattern,
    SketchOffsetEntity,
)
from .constraints import (
    ANGLE_DIRECTION,
    ANGLE_LINE_DIRECTION,
    CONSTRAINT_KIND,
    SketchConstraint,
    # constraint factories
    constrain,
    coincident, horizontal, vertical, parallel, perpendicular, tangent,
    concentric, collinear, equal, coradial, at_midpoint, symmetric, fixed,
    horizontal_points, vertical_points, merge_points, at_pierce, at_intersect,
    use_edge, offset_edge,
    distance, horizontal_distance, vertical_distance, angle,
    radius, diameter, arc_length,
)
from .core import (
    SKETCH_ENTITY,
    Sketch,
    sketch,
)
from ._factories import (
    ORIGIN,
    sketch_point,
    sketch_line,
    sketch_circle,
    sketch_arc,
    sketch_rectangle,
    sketch_polygon,
    sketch_linear_pattern,
    sketch_circular_pattern,
    sketch_ellipse,
    sketch_elliptical_arc,
    sketch_parabola,
    sketch_spline,
)


__all__ = [
    # core
    "Sketch",
    "sketch",
    "SKETCH_ENTITY",
    # entity aliases
    "SketchPoint",
    "SketchLine",
    "SketchCircle",
    "SketchArc",
    "SketchEllipse",
    "SketchEllipticalArc",
    "SketchParabola",
    "SketchSpline",
    # composites
    "SketchRectangle",
    "SketchPolygon",
    "SketchLinearPattern",
    "SketchCircularPattern",
    "SketchOffsetEntity",
    # constraints
    "SketchConstraint",
    "CONSTRAINT_KIND",
    "ANGLE_DIRECTION",
    "ANGLE_LINE_DIRECTION",
    # constraint factories
    "constrain",
    "coincident", "horizontal", "vertical", "parallel", "perpendicular",
    "tangent", "concentric", "collinear", "equal", "coradial", "at_midpoint",
    "symmetric", "fixed", "horizontal_points", "vertical_points",
    "merge_points", "at_pierce", "at_intersect", "use_edge", "offset_edge",
    "distance", "horizontal_distance", "vertical_distance", "angle",
    "radius", "diameter", "arc_length",
    # free-function entity factories
    "ORIGIN",
    "sketch_point",
    "sketch_line",
    "sketch_circle",
    "sketch_arc",
    "sketch_rectangle",
    "sketch_polygon",
    "sketch_linear_pattern",
    "sketch_circular_pattern",
    "sketch_ellipse",
    "sketch_elliptical_arc",
    "sketch_parabola",
    "sketch_spline",
    # private helpers (kept for cross-module use within the client)
    "_is_sketch_entity",
    "_CompositeSketchEntity",
    "CONSTRAINED_STATUS",
]
