"""The top-level `Sketch` feature class + the lowercase `sketch()` factory.

A Sketch is a top-level feature (it appears in the SolidWorks FeatureManager
tree), so `f.add_feature(sketch_instance)` works directly.

Builder methods mutate the sketch and return the entity (so the model can
capture a name or pass the entity into a constraint).

Sketch-entity classes (SketchPoint, SketchLine, ...) are the polymorphic
Definition flavors registered on `Definition` via the `kind` discriminator
("sketch_line", "sketch_circle", ...). The model authors them via `s.add_*`,
captures the returned entity, and passes it directly wherever a Definition
is accepted (e.g. `extrude(direction=line, ...)`) — the entity IS the
Definition, no separate wrapping step.
"""
from __future__ import annotations

from typing import Annotated, Any, Literal, Optional, Union

from pydantic import Field

from ..types import (
    _Base,
    Definition,
    Echo,
    Point2D,
    SketchEntityDefinitionFlavor,
    SketchPointDefinition,
    SketchLineDefinition,
    SketchCircleDefinition,
    SketchArcDefinition,
    SketchEllipseDefinition,
    SketchEllipticalArcDefinition,
    SketchParabolaDefinition,
    SketchSplineDefinition,
    _SketchEntityDefinitionBase as _SketchEntityFlavorBase,
)
from .composites import (
    SketchRectangle,
    SketchPolygon,
    SketchLinearPattern,
    SketchCircularPattern,
    SketchOffsetEntity,
)
from .constraints import CONSTRAINT_KIND, SketchConstraint
from .entities import _is_sketch_entity, _pending_id, _to_p2, _to_pt


# Sketch.entities holds either a Definition-flavored entity (the 8 polymorphic
# `kind`-discriminated flavors) OR a composite (the 5 `type`-discriminated
# children that don't round-trip yet). The two sub-unions use different
# discriminator field names, so the outer Union is non-discriminated; pydantic
# matches each dict to whichever shape fits (composites have a `type` field;
# flavors have a `kind` field).
SKETCH_ENTITY = Union[
    SketchEntityDefinitionFlavor,
    Annotated[
        Union[
            SketchRectangle,
            SketchPolygon,
            SketchLinearPattern,
            SketchCircularPattern,
            SketchOffsetEntity,
        ],
        Field(discriminator="type"),
    ],
]


# -- the Sketch feature -----------------------------------------------------

class Sketch(_Base):
    """Top-level Sketch feature."""
    type: Literal["Sketch"] = "Sketch"
    plane: Union[Definition, dict] = Field(default_factory=lambda: {"kind": "feature", "name": "Top Plane"})
    entities: list[SKETCH_ENTITY] = Field(default_factory=list)
    constraints: list[SketchConstraint] = Field(default_factory=list)

    name: Optional[str] = None  # SolidWorks-assigned (e.g. 'Sketch3'); model may pre-set.

    # Inspect-only echo: 4×3 matrix lifted from the live
    # `Sketch.ModelToSketchTransform`. The runtime compensator outside the
    # C# core uses this to detect when a face-authored sketch's local frame
    # flipped on rebuild and flip the dependent feature's `reversed` flag
    # accordingly.
    echo_computed_axes: Optional[list[list[float]]] = Echo(default=None)

    # mutating builders --------------------------------------------------

    def add_point(self, p: tuple[float, float] | Point2D, *,
                  construction: bool = False) -> SketchPointDefinition:
        e = SketchPointDefinition(
            id=_pending_id(), p=_to_p2(p),
            construction=construction,
        )
        self.entities.append(e)
        return e

    def add_line(self, start: tuple[float, float] | Point2D,
                 end: tuple[float, float] | Point2D, *,
                 construction: bool = False) -> SketchLineDefinition:
        e = SketchLineDefinition(
            id=_pending_id(),
            start=_to_pt(start), end=_to_pt(end),
            construction=construction,
        )
        self.entities.append(e)
        return e

    def add_circle(self, center: tuple[float, float] | Point2D, *,
                   radius: float,
                   construction: bool = False) -> SketchCircleDefinition:
        e = SketchCircleDefinition(
            id=_pending_id(),
            center=_to_pt(center), radius=radius,
            construction=construction,
        )
        self.entities.append(e)
        return e

    def add_arc(self, center: tuple[float, float] | Point2D,
                start: tuple[float, float] | Point2D,
                end: tuple[float, float] | Point2D, *,
                clockwise: bool = False,
                construction: bool = False) -> SketchArcDefinition:
        e = SketchArcDefinition(
            id=_pending_id(),
            center=_to_pt(center), start=_to_pt(start),
            end=_to_pt(end), clockwise=clockwise,
            construction=construction,
        )
        self.entities.append(e)
        return e

    def add_rectangle(self, p1: tuple[float, float] | Point2D,
                      p2: tuple[float, float] | Point2D, *,
                      construction: bool = False) -> SketchRectangle:
        e = SketchRectangle(p1=_to_p2(p1), p2=_to_p2(p2),
                            construction=construction)
        self.entities.append(e)
        return e

    def add_polygon(self, center: tuple[float, float] | Point2D, *,
                    first_vertex: tuple[float, float] | Point2D,
                    sides: int,
                    inscribed: bool = True,
                    construction: bool = False) -> SketchPolygon:
        """Regular polygon defined by `center`, one corner (`first_vertex`),
        `sides`, and `inscribed`. `first_vertex` sets both size and rotation;
        `inscribed` only picks the construction-circle style."""
        e = SketchPolygon(
            center=_to_p2(center),
            first_vertex=_to_p2(first_vertex),
            sides=sides, inscribed=inscribed, construction=construction,
        )
        self.entities.append(e)
        return e

    def add_ellipse(self, center: tuple[float, float] | Point2D,
                    major_axis_end: tuple[float, float] | Point2D,
                    minor_axis_end: tuple[float, float] | Point2D, *,
                    construction: bool = False) -> SketchEllipseDefinition:
        e = SketchEllipseDefinition(
            id=_pending_id(),
            center=_to_pt(center),
            major_axis_end=_to_pt(major_axis_end),
            minor_axis_end=_to_pt(minor_axis_end),
            construction=construction,
        )
        self.entities.append(e)
        return e

    def add_elliptical_arc(self, center: tuple[float, float] | Point2D,
                           major_axis_end: tuple[float, float] | Point2D,
                           minor_axis_end: tuple[float, float] | Point2D,
                           start: tuple[float, float] | Point2D,
                           end: tuple[float, float] | Point2D, *,
                           clockwise: bool = False,
                           construction: bool = False) -> SketchEllipticalArcDefinition:
        e = SketchEllipticalArcDefinition(
            id=_pending_id(),
            center=_to_pt(center),
            major_axis_end=_to_pt(major_axis_end),
            minor_axis_end=_to_pt(minor_axis_end),
            start=_to_pt(start), end=_to_pt(end),
            clockwise=clockwise, construction=construction,
        )
        self.entities.append(e)
        return e

    def add_parabola(self, focal: tuple[float, float] | Point2D,
                     apex: tuple[float, float] | Point2D,
                     start: tuple[float, float] | Point2D,
                     end: tuple[float, float] | Point2D, *,
                     construction: bool = False) -> SketchParabolaDefinition:
        e = SketchParabolaDefinition(
            id=_pending_id(),
            focal=_to_pt(focal), apex=_to_pt(apex),
            start=_to_pt(start), end=_to_pt(end),
            construction=construction,
        )
        self.entities.append(e)
        return e

    def add_spline(self, control_points: list[tuple[float, float] | Point2D],
                   knots: list[float], *,
                   order: int = 4,
                   periodic: bool = False,
                   generic: bool = False,
                   construction: bool = False) -> SketchSplineDefinition:
        e = SketchSplineDefinition(
            id=_pending_id(),
            control_points=[_to_p2(p) for p in control_points],
            knots=list(knots), order=order,
            periodic=periodic, generic=generic, construction=construction,
        )
        self.entities.append(e)
        return e

    def add_constraint(self, kind: CONSTRAINT_KIND,
                       refs: list[Any], *,
                       value: Optional[float] = None) -> SketchConstraint:
        """Append a constraint. Each ref must be a Definition.

        Accepted ref shapes:

        - A sketch-entity instance (Line/Circle/...) already in this sketch's
          `entities` list — the entity IS its Definition; serialized via
          `model_dump`. Composites (Rectangle/Polygon/Pattern) are not
          Definition flavors and are rejected here.
        - A `Definition` model instance or Definition-shaped dict — passed through.

        For sub-entity refs (line endpoints, arc center) pass the corresponding
        SketchPointDefinition directly — `line.start`, `arc.center`, etc. — those
        are full Definitions and resolve through the same per-Add map the parent
        segment uses.
        """
        normalized: list[Any] = []
        for r in refs:
            if isinstance(r, _SketchEntityFlavorBase):
                normalized.append(r.model_dump(mode="json"))
                continue
            if _is_sketch_entity(r):
                # Composite (Rectangle/Polygon/Pattern) — no Definition flavor yet.
                raise ValueError(
                    f"add_constraint: composite entity {type(r).__name__} is not a "
                    "Definition flavor and can't be referenced directly. Pass one of "
                    "its constituent line/arc/etc. Definitions instead."
                )
            if hasattr(r, "model_dump") and callable(r.model_dump):
                d = r.model_dump(mode="json")
                if isinstance(d, dict) and "kind" in d:
                    normalized.append(d)
                    continue
            if isinstance(r, dict) and "kind" in r:
                normalized.append(r)
                continue
            raise TypeError(
                f"add_constraint: ref must be a sketch-entity instance or a Definition "
                f"(model or dict with 'kind' discriminator); got {r!r}"
            )
        c = SketchConstraint(kind=kind, refs=normalized, value=value)
        self.constraints.append(c)
        return c


# -- factory ----------------------------------------------------------------

_UNSET: Any = object()


def sketch(plane: Union[Definition, dict, None] = _UNSET, *,
           name: Optional[str] = None,
           entities: Optional[list[Any]] = None) -> Sketch:
    """Construct a mutable Sketch on the given plane.

    `plane` accepts a `Definition` (e.g. `TOP`/`FRONT`/`RIGHT` for default
    planes, or any face/refplane returned by `f.probe(...)`). Omitting it
    defaults to the Top Plane.

    Passing ``plane=None`` *explicitly* is an error — that almost always
    means a `probe()` call missed and produced None, which would silently
    place the sketch on Top Plane instead of the face the author meant.
    Better to fail loudly here than to let a sketch with the wrong plane
    cascade into "SketchManager.ActiveSketch is null" 50 features later
    (SolidWorks drops sketches that end up empty because every entity
    fails to land on the wrong plane).

    `name` (optional) overrides the SolidWorks-assigned sketch name. Leave
    None to let SolidWorks assign one.

    `entities` (optional) defines the sketch's geometry declaratively at
    construction using the `sketch_*` entity factories:

        sketch(plane=FRONT, entities=[
            sketch_line((-0.05, 0.05), (0.05, 0.05)),
            sketch_point((0.0, 0.0)),
        ])

    Equivalent to calling the matching `s.add_*` builders afterwards. Apply
    constraints AFTER `add_feature` via `add_constraints(s, [...])` (they
    reference entity ids the plugin only assigns on Add); a sketch carrying
    constraints is rejected by `add_feature`.
    """
    if plane is None:
        raise ValueError(
            "sketch(plane=None) — `plane` cannot be None. This usually means "
            "a `target.probe(...)` call returned None (ray missed) and "
            "propagated into the sketch. Use a ray that hits the intended "
            "face / refplane, or pass an explicit plane (e.g. `TOP` / `FRONT` / "
            "`RIGHT`)."
        )
    s = Sketch(entities=list(entities) if entities else [], constraints=[])
    if plane is not _UNSET:
        s.plane = plane
    if name is not None:
        s.name = name
    return s
