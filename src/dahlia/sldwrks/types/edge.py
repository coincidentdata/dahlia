"""Edge Definition flavors: Line, Circular, Elliptical, Spline."""
from __future__ import annotations

from typing import Literal, Optional

from ._definition_base import _SolidGeometryDefinition
from .primitives import Direction, Point3D


class LineEdgeDefinition(_SolidGeometryDefinition):
    kind: Literal["line_edge"] = "line_edge"
    start: Point3D
    end: Point3D


class CircularEdgeDefinition(_SolidGeometryDefinition):
    # SW quirk: closed-curve edges report null start/end vertices, so start/end are
    # None for a full circle and present for an arc. Arcs are orientation-agnostic.
    kind: Literal["circular_edge"] = "circular_edge"
    center: Point3D
    axis_direction: Direction
    radius: float
    start: Optional[Point3D] = None
    end: Optional[Point3D] = None


class EllipticalEdgeDefinition(_SolidGeometryDefinition):
    # SW quirk: closed-curve edges report null start/end vertices, so start/end are
    # None for a full ellipse and present for an arc. Arcs are orientation-agnostic.
    kind: Literal["elliptical_edge"] = "elliptical_edge"
    center: Point3D
    major_axis: Direction
    minor_axis: Direction
    major_radius: float
    minor_radius: float
    start: Optional[Point3D] = None
    end: Optional[Point3D] = None


class SplineEdgeDefinition(_SolidGeometryDefinition):
    kind: Literal["spline_edge"] = "spline_edge"
    control_points: list[Point3D]
    knots: list[float]
    degree: int = 3
    periodic: bool = False
