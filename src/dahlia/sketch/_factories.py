"""Free-function sketch-entity factories.

Use these to build entity values declaratively inside a sketch literal:

    s = sketch(plane=FRONT, name="Sketch2", entities=[
        sketch_line((-0.05, 0.05), (0.05, 0.05)),
        sketch_line((0.05, 0.05), (0.05, -0.05)),
        sketch_point((0.0, 0.0)),
    ])

For the imperative case of appending to an already-open sketch, the matching
`s.add_*` method on the Sketch builder does the same thing.
"""
from __future__ import annotations

from typing import Optional

from ..types import (
    Point2D,
    SketchEntityId,
    SketchPointDefinition,
    SketchLineDefinition,
    SketchCircleDefinition,
    SketchArcDefinition,
    SketchEllipseDefinition,
    SketchEllipticalArcDefinition,
    SketchParabolaDefinition,
    SketchSplineDefinition,
)
from .composites import SketchRectangle, SketchPolygon
from .composites import SketchLinearPattern, SketchCircularPattern
from .entities import _pending_id, _to_p2, _to_pt


def sketch_point(p: tuple[float, float] | Point2D, *,
                 sketch_name: Optional[str] = None,
                 id: Optional[int] = None,
                 construction: bool = False) -> SketchPointDefinition:
    """Construct a free-standing SketchPoint.

    For a freshly-authored point (the common case) pass just the coordinate —
    the id is a placeholder the plugin back-fills on Add. Pass ``sketch_name``
    + ``id`` together to reference an *existing* point (e.g. the part origin, or
    a SolidWorks-assigned point) as a constraint ref. See ``ORIGIN`` for the
    part origin."""
    if (sketch_name is None) != (id is None):
        raise ValueError(
            "sketch_point: pass both sketch_name and id (to reference an "
            "existing point) or neither (to author a new one)."
        )
    eid = (SketchEntityId(sketch_name=sketch_name, entity_kind="point", id=id)
           if sketch_name is not None else _pending_id())
    return SketchPointDefinition(id=eid, p=_to_p2(p), construction=construction)


def sketch_line(start: tuple[float, float] | Point2D,
                end: tuple[float, float] | Point2D, *,
                construction: bool = False) -> SketchLineDefinition:
    return SketchLineDefinition(
        id=_pending_id(),
        start=_to_pt(start), end=_to_pt(end),
        construction=construction,
    )


def sketch_circle(center: tuple[float, float] | Point2D, *,
                  radius: float,
                  construction: bool = False) -> SketchCircleDefinition:
    return SketchCircleDefinition(
        id=_pending_id(),
        center=_to_pt(center), radius=radius,
        construction=construction,
    )


def sketch_arc(center: tuple[float, float] | Point2D,
               start: tuple[float, float] | Point2D,
               end: tuple[float, float] | Point2D, *,
               clockwise: bool = False,
               construction: bool = False) -> SketchArcDefinition:
    return SketchArcDefinition(
        id=_pending_id(),
        center=_to_pt(center), start=_to_pt(start), end=_to_pt(end),
        clockwise=clockwise, construction=construction,
    )


def sketch_rectangle(p1: tuple[float, float] | Point2D,
                     p2: tuple[float, float] | Point2D, *,
                     construction: bool = False) -> SketchRectangle:
    return SketchRectangle(p1=_to_p2(p1), p2=_to_p2(p2), construction=construction)


def sketch_polygon(center: tuple[float, float] | Point2D, *,
                   first_vertex: tuple[float, float] | Point2D,
                   sides: int,
                   inscribed: bool = True,
                   construction: bool = False) -> SketchPolygon:
    return SketchPolygon(
        center=_to_p2(center), first_vertex=_to_p2(first_vertex),
        sides=sides, inscribed=inscribed, construction=construction,
    )


def sketch_linear_pattern(*,
                          seeds: list[SketchEntityId],
                          angle_a: float,
                          spacing_a: float,
                          count_a: int,
                          angle_b: float = 0.0,
                          spacing_b: float = 0.0,
                          count_b: int = 1,
                          deleted: Optional[list[list[bool]]] = None,
                          construction: bool = False) -> SketchLinearPattern:
    """Construct a phase-2 sketch linear pattern for add_sketch_entities(...)."""
    return SketchLinearPattern(
        seeds=seeds,
        angle_a=angle_a,
        spacing_a=spacing_a,
        count_a=count_a,
        angle_b=angle_b,
        spacing_b=spacing_b,
        count_b=count_b,
        deleted=deleted or [],
        construction=construction,
    )


def sketch_circular_pattern(*,
                            seeds: list[SketchEntityId],
                            center: tuple[float, float] | Point2D,
                            angle_step: float,
                            count: int,
                            deleted: Optional[list[list[bool]]] = None,
                            construction: bool = False) -> SketchCircularPattern:
    """Construct a phase-2 sketch circular pattern for add_sketch_entities(...)."""
    return SketchCircularPattern(
        seeds=seeds,
        center=_to_p2(center),
        angle_step=angle_step,
        count=count,
        deleted=deleted or [],
        construction=construction,
    )


def sketch_ellipse(center: tuple[float, float] | Point2D,
                   major_axis_end: tuple[float, float] | Point2D,
                   minor_axis_end: tuple[float, float] | Point2D, *,
                   construction: bool = False) -> SketchEllipseDefinition:
    """Construct a free-standing SketchEllipse. Use `s.add_ellipse(...)` to
    append directly to a sketch; use this when building entity values to embed
    in a Sketch literal."""
    return SketchEllipseDefinition(
        id=_pending_id(),
        center=_to_pt(center),
        major_axis_end=_to_pt(major_axis_end),
        minor_axis_end=_to_pt(minor_axis_end),
        construction=construction,
    )


def sketch_elliptical_arc(center: tuple[float, float] | Point2D,
                          major_axis_end: tuple[float, float] | Point2D,
                          minor_axis_end: tuple[float, float] | Point2D,
                          start: tuple[float, float] | Point2D,
                          end: tuple[float, float] | Point2D, *,
                          clockwise: bool = False,
                          construction: bool = False) -> SketchEllipticalArcDefinition:
    return SketchEllipticalArcDefinition(
        id=_pending_id(),
        center=_to_pt(center),
        major_axis_end=_to_pt(major_axis_end),
        minor_axis_end=_to_pt(minor_axis_end),
        start=_to_pt(start), end=_to_pt(end),
        clockwise=clockwise, construction=construction,
    )


def sketch_parabola(focal: tuple[float, float] | Point2D,
                    apex: tuple[float, float] | Point2D,
                    start: tuple[float, float] | Point2D,
                    end: tuple[float, float] | Point2D, *,
                    construction: bool = False) -> SketchParabolaDefinition:
    return SketchParabolaDefinition(
        id=_pending_id(),
        focal=_to_pt(focal), apex=_to_pt(apex),
        start=_to_pt(start), end=_to_pt(end),
        construction=construction,
    )


def sketch_spline(control_points: list[tuple[float, float] | Point2D],
                  knots: list[float], *,
                  order: int = 4,
                  periodic: bool = False,
                  generic: bool = False,
                  construction: bool = False) -> SketchSplineDefinition:
    return SketchSplineDefinition(
        id=_pending_id(),
        control_points=[_to_p2(p) for p in control_points],
        knots=list(knots), order=order,
        periodic=periodic, generic=generic, construction=construction,
    )


# The part origin, as a constraint ref. SolidWorks exposes the origin as a
# stable sketch point in the well-known "Origin" sketch (id 1), so constraints
# anchoring geometry to it (e.g. `coincident(ORIGIN, line.start)`) reference it
# directly rather than re-deriving it per sketch.
ORIGIN: SketchPointDefinition = sketch_point((0.0, 0.0), sketch_name="Origin", id=1)
