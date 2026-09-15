"""Sketch-entity Definition flavors.

One Definition flavor per addressable SolidWorks sketch entity (SketchSegment
or SketchPoint with an int[2] GetID()). Mirrors C#
`Sldworks.Core.Handlers.Sketches.Entities.{Line,Circle,Arc,Point,Ellipse,
EllipticalArc,Parabola,Spline}` — each subclass of `SketchEntityDefinition`
is registered as a JsonDerivedType on `Definition` so the wire shape is a
Definition with `kind` discriminator + `id` triplet + `construction` flag +
per-flavor geometry. Constraint refs always carry a full Definition (or a
bare SketchEntityId); the plugin resolves them by id through a per-Add map.
"""
from __future__ import annotations

from typing import Annotated, Literal, Optional, Union

from pydantic import Field, model_validator

from ._base import Echo
from ._definition_base import _DefinitionBase
from .primitives import Point2D


class SketchEntityId(_DefinitionBase):
    """Stable handle for a sketch entity (segment or point) across rebuilds.

    Mirrors C# `SketchEntityId : Definition` registered as `"sketch_entity_id"`
    — bare-id Definition flavor used wherever the lookup matters but no
    geometry is needed (sweep paths, pattern axes, constraint refs that don't
    need geometry). The geometry-carrying Line/Circle/etc. flavors below embed
    one as their `id` and forward resolution through it.

    SW quirk: SketchSegment.GetID() returns int[2] that is unique only within
    ONE COM subtype's namespace — a SketchLine and a SketchArc in the same
    sketch can both report [0, 1], and SketchPoint shares the lookup space too.
    `entity_kind` ("line" | "arc" | "ellipse" | "spline" | "parabola" | "point")
    selects the namespace; together with `sketch_name` and `id` (the int[2]
    packed into a long: `(id0 << 32) | (uint)id1`) the triplet names exactly
    one live entity. The wire field is `entity_kind` (not `kind`) to avoid
    colliding with the polymorphic Definition discriminator.
    """
    kind: Literal["sketch_entity_id"] = "sketch_entity_id"
    sketch_name: str
    entity_kind: Literal["line", "arc", "ellipse", "spline", "parabola", "point"]
    id: int


CONSTRAINED_STATUS = Literal["UnderConstrained", "Constrained", "OverConstrained"]


class _SketchEntityDefinitionBase(_DefinitionBase):
    """Common shape for sketch-entity Definition flavors.

    `id` is the (sketch_name, entity_kind, id) triplet that uniquely identifies this
    entity in the live model. `construction` round-trips
    `SketchSegment.ConstructionGeometry` (always False on standalone Points).

    `echo_constrained_status` is Inspect-only echo — `swConstrainedStatus_e`
    mirror. Currently not emitted by the C# Inspect path; kept None on
    round-trip.
    """
    id: SketchEntityId
    construction: bool = False
    echo_constrained_status: Optional[CONSTRAINED_STATUS] = Echo(default=None)


class SketchPointDefinition(_SketchEntityDefinitionBase):
    """Sketch point. Mirrors C# `Point : SketchEntityDefinition` (kind = "sketch_point").

    Used both as a top-level entity (standalone user / mid / virtual-sharp
    points) and as the embedded child-point shape inside every segment
    Definition (Line.start, Circle.center, Parabola.apex, ...) — so segment
    endpoints carry their per-sketch SketchPoint id alongside the 2D coord.
    """
    kind: Literal["sketch_point"] = "sketch_point"
    p: Point2D


class SketchLineDefinition(_SketchEntityDefinitionBase):
    """Sketch line. Mirrors C# `Line : SketchEntityDefinition` (kind = "sketch_line")."""
    kind: Literal["sketch_line"] = "sketch_line"
    start: SketchPointDefinition
    end: SketchPointDefinition


class SketchCircleDefinition(_SketchEntityDefinitionBase):
    """Sketch circle. Mirrors C# `Circle : SketchEntityDefinition` (kind = "sketch_circle").

    SW quirk: SketchArc covers BOTH circles and arcs; SW disambiguates via
    SketchArc.IsCircle() != 0 at capture time and routes here.
    """
    kind: Literal["sketch_circle"] = "sketch_circle"
    center: SketchPointDefinition
    radius: float

    @model_validator(mode="after")
    def _radius_positive(self) -> "SketchCircleDefinition":
        if self.radius <= 0:
            raise ValueError("SketchCircleDefinition: radius must be > 0")
        return self


class SketchArcDefinition(_SketchEntityDefinitionBase):
    """Sketch arc. Mirrors C# `Arc : SketchEntityDefinition` (kind = "sketch_arc").

    `clockwise` is the CW/CCW direction passed straight through to SW's
    `SketchManager.CreateArc` (-1 / +1). A major/minor flag would degenerate at
    the semi-circle (start, end, center collinear) — both directions yield the
    same major-vs-minor classification — so the wire carries direction directly.
    """
    kind: Literal["sketch_arc"] = "sketch_arc"
    center: SketchPointDefinition
    start: SketchPointDefinition
    end: SketchPointDefinition
    clockwise: bool = False


class SketchEllipseDefinition(_SketchEntityDefinitionBase):
    """Full sketch ellipse. Mirrors C# `Ellipse : SketchEntityDefinition`
    (kind = "sketch_ellipse"). Point-end form: center + major-axis end +
    minor-axis end — matches `SketchManager.CreateEllipse` arg shape and
    preserves axis orientation (recovering (major, minor) magnitudes loses
    rotation in the sketch plane).

    `echo_major_b_id` / `echo_minor_b_id` are Inspect-only echoes — the
    integer ids of the opposite-side axis handle SketchPoints SW
    auto-creates alongside the primary major / minor endpoints. Forward-
    compat: not currently emitted by Ellipse.Parse.
    """
    kind: Literal["sketch_ellipse"] = "sketch_ellipse"
    center: SketchPointDefinition
    major_axis_end: SketchPointDefinition
    minor_axis_end: SketchPointDefinition
    echo_major_b_id: Optional[int] = Echo(default=None)
    echo_minor_b_id: Optional[int] = Echo(default=None)

    @model_validator(mode="after")
    def _axes_nondegenerate(self) -> "SketchEllipseDefinition":
        if (self.major_axis_end.p.x == self.center.p.x and
                self.major_axis_end.p.y == self.center.p.y):
            raise ValueError("ellipse: major_axis_end coincides with center")
        if (self.minor_axis_end.p.x == self.center.p.x and
                self.minor_axis_end.p.y == self.center.p.y):
            raise ValueError("ellipse: minor_axis_end coincides with center")
        return self


class SketchEllipticalArcDefinition(_SketchEntityDefinitionBase):
    """Elliptical arc. Mirrors C# `EllipticalArc : SketchEntityDefinition`
    (kind = "sketch_elliptical_arc"). Full ellipse parameters + start/end
    points + clockwise direction; SW's CreateEllipticalArc takes 16 doubles
    + a +1/-1 short, and `clockwise=true` maps to -1.

    `echo_major_b_id` / `echo_minor_b_id`: see SketchEllipseDefinition.
    """
    kind: Literal["sketch_elliptical_arc"] = "sketch_elliptical_arc"
    center: SketchPointDefinition
    major_axis_end: SketchPointDefinition
    minor_axis_end: SketchPointDefinition
    start: SketchPointDefinition
    end: SketchPointDefinition
    clockwise: bool = False
    echo_major_b_id: Optional[int] = Echo(default=None)
    echo_minor_b_id: Optional[int] = Echo(default=None)

    @model_validator(mode="after")
    def _axes_nondegenerate(self) -> "SketchEllipticalArcDefinition":
        if (self.major_axis_end.p.x == self.center.p.x and
                self.major_axis_end.p.y == self.center.p.y):
            raise ValueError("elliptical arc: major_axis_end coincides with center")
        if (self.minor_axis_end.p.x == self.center.p.x and
                self.minor_axis_end.p.y == self.center.p.y):
            raise ValueError("elliptical arc: minor_axis_end coincides with center")
        if self.start.p.x == self.end.p.x and self.start.p.y == self.end.p.y:
            raise ValueError("elliptical arc: start and end coincide")
        return self


class SketchParabolaDefinition(_SketchEntityDefinitionBase):
    """Sketch parabola. Mirrors C# `Parabola : SketchEntityDefinition`
    (kind = "sketch_parabola"). Defined by focal + apex + start/end of the
    swept arc; SW's CreateParabola takes (focal, apex, start, end) in that
    order. SW quirk: generic conics use the parabola COM type but lack an
    apex — those are NOT round-trippable and the C# Parse throws on null
    apex/focal."""
    kind: Literal["sketch_parabola"] = "sketch_parabola"
    focal: SketchPointDefinition
    apex: SketchPointDefinition
    start: SketchPointDefinition
    end: SketchPointDefinition

    @model_validator(mode="after")
    def _nondegenerate(self) -> "SketchParabolaDefinition":
        if self.focal.p.x == self.apex.p.x and self.focal.p.y == self.apex.p.y:
            raise ValueError("parabola: focal coincides with apex")
        if self.start.p.x == self.end.p.x and self.start.p.y == self.end.p.y:
            raise ValueError("parabola: start and end coincide")
        return self


class SketchSplineDefinition(_SketchEntityDefinitionBase):
    """Non-rational B-spline. Mirrors C# `Spline : SketchEntityDefinition`
    (kind = "sketch_spline"). `control_points` + `knots` + `order` describe a
    B-curve of arbitrary degree. Order travels on the wire because SW returns
    the COMPACT periodic knot vector for periodic splines (`knots = cps + 1`),
    so order can't be derived from knot count alone — captured directly from
    ISplineParamData.Order on Inspect.

    Rational splines (NURBS with non-unit weights) are refused by C# Parse —
    the wire only carries non-rational splines.

    `periodic` round-trips a closed (looping) spline. `generic` controls
    whether the spline is converted to an editable form — when False the
    handler runs `swCommands_ConvertToModif` after creation so the spline
    becomes editable via SW's generic-spline UI; when True it stays rigid.

    `echo_spline_points` is Inspect-only echo (read from
    `SketchSpline.GetPoints2()`): interpolation/handle points the SW spline
    maintains alongside the control points. Re-author from control points +
    knots — never consumed on Add.
    """
    kind: Literal["sketch_spline"] = "sketch_spline"
    control_points: list[Point2D]
    knots: list[float]
    order: int
    periodic: bool = False
    generic: bool = False
    echo_spline_points: list[Point2D] = Echo(default_factory=list)

    @model_validator(mode="after")
    def _spline_invariants(self) -> "SketchSplineDefinition":
        if self.order < 2:
            raise ValueError(f"spline: order must be >= 2 (got {self.order})")
        if len(self.control_points) < self.order:
            raise ValueError(
                f"spline: need >= {self.order} control points for order {self.order} "
                f"(got {len(self.control_points)})"
            )
        # Non-periodic: knots = cps + order. Periodic: SW's compact form has knots = cps + 1.
        expected_knots = len(self.control_points) + (1 if self.periodic else self.order)
        if len(self.knots) != expected_knots:
            raise ValueError(
                f"spline: knots length ({len(self.knots)}) != expected "
                f"{expected_knots} for {len(self.control_points)} cps, order {self.order}, "
                f"{'periodic' if self.periodic else 'non-periodic'}"
            )
        return self


# Discriminated union over the 8 sketch-entity Definition flavors. Used as the
# `entities` list element type on Sketch and as the segment list element type on
# SketchContourDefinition. Does NOT include `SketchEntityId` (bare ids carry no
# geometry — contour segments and the entities list both need geometry-bearing
# flavors).
SketchEntityDefinitionFlavor = Annotated[
    Union[
        SketchLineDefinition,
        SketchCircleDefinition,
        SketchArcDefinition,
        SketchPointDefinition,
        SketchEllipseDefinition,
        SketchEllipticalArcDefinition,
        SketchParabolaDefinition,
        SketchSplineDefinition,
    ],
    Field(discriminator="kind"),
]


# Alias for the union under the shorter `SketchEntityDefinition` name.
SketchEntityDefinition = SketchEntityDefinitionFlavor
