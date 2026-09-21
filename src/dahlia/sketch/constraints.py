"""Sketch constraint model + the geometric/dimensional kind literal."""
from __future__ import annotations

from typing import Any, Literal, Optional

from pydantic import Field, field_validator

from ..types import _Base, Echo


# -- constraints ------------------------------------------------------------

# Geometric / relation kinds covering the full `swSketchRelations_e` set so
# the wire round-trips every constraint SolidWorks can emit. Distance subtypes
# (HorizontalDistance / VerticalDistance / Ordinate / ChamferDimension) and
# ArcLength are dimensional and live alongside Distance / Angle / Radius /
# Diameter.
CONSTRAINT_KIND = Literal[
    # Geometric (simple)
    "Horizontal",
    "Vertical",
    "Coincident",
    "Tangent",
    "Parallel",
    "Perpendicular",
    "Equal",
    "Concentric",
    "Midpoint",
    "Symmetric",
    "Fixed",
    # Geometric (extended)
    "HorizontalPoints",
    "VerticalPoints",
    "Collinear",
    "Coradial",
    "AtPierce",
    "AtIntersect",
    "MergePoints",
    "AlongX",
    "AlongY",
    "AlongZ",
    "OffsetEdge",
    "UseEdge",
    "FixedSlot",
    # Note: structural relations Patterned (sw=57) and SketchOffset (sw=47) are
    # not constraints — they back composite sketch entities. The C# read path
    # (SketchConstraint.Read) skips them; the C# write path has no enum entry
    # for them. They never appear on the wire as constraint kinds, so they're
    # not in this literal.
    # Arc-angle family
    "ArcAngle90",
    "ArcAngle180",
    "ArcAngle270",
    "ArcAngleTop",
    "ArcAngleBottom",
    "ArcAngleLeft",
    "ArcAngleRight",
    # Ellipse-angle family
    "EllipseAngle90",
    "EllipseAngle180",
    "EllipseAngle270",
    "EllipseAngleTop",
    "EllipseAngleBottom",
    "EllipseAngleLeft",
    "EllipseAngleRight",
    # Geometric (additional kinds).
    "DoubleDistanceRelation",
    "Angle3Points",
    "Normal",
    "NormalPoints",
    "AlongXPoints",
    "AlongYPoints",
    "AlongZPoints",
    "ParallelYZ",
    "ParallelXZ",
    "Intersection",
    "FitSpline",
    "EqualCurvature",
    "EqualTangent",
    "TangentFace",
    "BlockFixedLock",
    "BlockNormalLock",
    "BlockRotateLock",
    "SameSlot",
    "RadialOffset",
    "PlanarOffset",
    "ConicRho",
    "C3Touch",
    "DoubleAngle",
    "SameCurveLength",
    # Dimensional
    "Distance",
    "HorizontalDistance",
    "VerticalDistance",
    "Ordinate",
    "HorizontalOrdinate",
    "VerticalOrdinate",
    "ChamferDimension",
    "Angle",
    "Radius",
    "Diameter",
    "ArcLength",
    "Scalar",
    "DoubleAngular",
    "DoubleDistance",
    "AngularOrdinate",
]


ANGLE_LINE_DIRECTION = Literal["None", "TowardsStart", "TowardsEnd"]
ANGLE_DIRECTION = Literal["None", "Right", "Up", "Left", "Down"]


class SketchConstraint(_Base):
    """Generic sketch constraint. Most kinds (geometric relations + simple
    dimensional ones) round-trip with `kind`, `refs`, optional `value`.

    `refs` is a list of `Definition` JSON objects (uniform with every other
    ref site in the codebase). On Inspect the C# emits each ref as a typed
    Definition flavor — most often one of the geometry-bearing sketch-entity
    flavors (sketch_line, sketch_circle, sketch_point, ...). On Add the C#
    handler reads each ref via `Definition.FromJson` and resolves the
    SketchEntityId triplet against an in-flight per-Add map (built as entities
    are created), falling through to `DefinitionResolver` for cross-sketch refs.
    """
    kind: CONSTRAINT_KIND
    refs: list[Any]
    value: Optional[float] = None
    # Inspect-only echo: annotation placement (2D coords in sketch-plane space)
    # captured for the dimension's display label. Not consumed on Add.
    echo_text_location: Optional[tuple[float, float]] = Echo(default=None)
    # Inspect-only echo: the dimension's SolidWorks name ("D1@Sketch2@Part.Part") —
    # the durable handle edit_dimension/rename_dimension resolve through. Not
    # consumed on Add (SW assigns names at creation).
    echo_dim_name: Optional[str] = Echo(default=None)
    line_directions: list[ANGLE_LINE_DIRECTION] = Field(default_factory=list)
    angle_direction: ANGLE_DIRECTION = "None"
    clockwise: bool = False
    use_opp_ext_line: bool = False

    @field_validator("refs")
    @classmethod
    def _refs_normalized(cls, v: list[Any]) -> list[Any]:
        """Validate and normalize each ref entry. Each ref must be a Definition
        — either a pydantic model instance or a dict with a `kind` discriminator.
        """
        out: list[Any] = []
        for i, ref in enumerate(v):
            if hasattr(ref, "model_dump") and callable(ref.model_dump):
                d = ref.model_dump(mode="json")
                if not isinstance(d, dict) or "kind" not in d:
                    raise ValueError(
                        f"SketchConstraint.refs[{i}]: model object did not dump to a "
                        f"Definition-shaped dict; got {d!r}"
                    )
                out.append(d)
                continue
            if isinstance(ref, dict):
                if "kind" not in ref:
                    raise ValueError(
                        f"SketchConstraint.refs[{i}]: dict ref is missing the 'kind' "
                        f"discriminator field — every constraint ref must be a "
                        f"Definition JSON object"
                    )
                out.append(ref)
                continue
            raise ValueError(
                f"SketchConstraint.refs[{i}]: expected a Definition (model or dict "
                f"with 'kind' discriminator); got {ref!r}. A None ref usually means "
                f"an upstream `probe()` missed (ray returned None) — use a ray that "
                f"hits the intended entity."
            )
        return out


# -- constraint factories ---------------------------------------------------
#
# Free-function builders, mirroring the feature factories (extrude(), fillet()).
# Pass entity refs DIRECTLY — no `.model_dump()`: a factory accepts sketch-entity
# objects, their sub-points (`line.start`, `arc.center`, ...), `ORIGIN`, probe
# results (edge/face/vertex Definitions), and Definition dicts.
#
# Apply the returned constraints AFTER `add_feature` via
# `f.add_constraints(sketch, [...])` so their refs serialize with the
# back-filled target-side ids (sketch entities only get real ids on Add).

def constrain(kind: CONSTRAINT_KIND, *refs: Any,
              value: Optional[float] = None,
              line_directions: Optional[list[ANGLE_LINE_DIRECTION]] = None,
              angle_direction: ANGLE_DIRECTION = "None",
              clockwise: bool = False,
              use_opp_ext_line: bool = False,
              text_location: Optional[tuple[float, float]] = None) -> SketchConstraint:
    """Generic constraint factory for any ``CONSTRAINT_KIND`` — the escape hatch
    for kinds without a named factory below, or for setting the extended
    angle/dimension fields (``line_directions``, ``angle_direction``, ...)."""
    return SketchConstraint(
        kind=kind, refs=list(refs), value=value,
        line_directions=line_directions or [],
        angle_direction=angle_direction, clockwise=clockwise,
        use_opp_ext_line=use_opp_ext_line, echo_text_location=text_location,
    )


def _geometric(kind: CONSTRAINT_KIND):
    def factory(*refs: Any) -> SketchConstraint:
        return SketchConstraint(kind=kind, refs=list(refs))
    factory.__name__ = kind.lower()
    return factory


# Geometric relations (no measured value). Each takes the refs it relates, in
# the order SolidWorks expects (e.g. midpoint(point, line); symmetric(a, b, axis)).
coincident = _geometric("Coincident")
horizontal = _geometric("Horizontal")
vertical = _geometric("Vertical")
parallel = _geometric("Parallel")
perpendicular = _geometric("Perpendicular")
tangent = _geometric("Tangent")
concentric = _geometric("Concentric")
collinear = _geometric("Collinear")
equal = _geometric("Equal")
coradial = _geometric("Coradial")
# `at_midpoint`, not `midpoint`, to avoid shadowing the `midpoint(a, b)` math
# helper (helpers.py) that computes the geometric midpoint of two points.
at_midpoint = _geometric("Midpoint")
symmetric = _geometric("Symmetric")
fixed = _geometric("Fixed")
horizontal_points = _geometric("HorizontalPoints")
vertical_points = _geometric("VerticalPoints")
merge_points = _geometric("MergePoints")
at_pierce = _geometric("AtPierce")
at_intersect = _geometric("AtIntersect")
use_edge = _geometric("UseEdge")
offset_edge = _geometric("OffsetEdge")


# Dimensional constraints (carry a measured `value`, meters or radians on the
# wire). `text_location` places the dimension's annotation; for an Angle dim it
# is load-bearing (it selects which witness branch SolidWorks measures).
def distance(a: Any, b: Any, value: float, *,
             text_location: Optional[tuple[float, float]] = None) -> SketchConstraint:
    return SketchConstraint(kind="Distance", refs=[a, b], value=value,
                            echo_text_location=text_location)


def horizontal_distance(a: Any, b: Any, value: float, *,
                        text_location: Optional[tuple[float, float]] = None) -> SketchConstraint:
    return SketchConstraint(kind="HorizontalDistance", refs=[a, b], value=value,
                            echo_text_location=text_location)


def vertical_distance(a: Any, b: Any, value: float, *,
                      text_location: Optional[tuple[float, float]] = None) -> SketchConstraint:
    return SketchConstraint(kind="VerticalDistance", refs=[a, b], value=value,
                            echo_text_location=text_location)


def angle(*refs: Any, value: float,
          text_location: Optional[tuple[float, float]] = None) -> SketchConstraint:
    return SketchConstraint(kind="Angle", refs=list(refs), value=value,
                            echo_text_location=text_location)


def radius(entity: Any, value: float, *,
           text_location: Optional[tuple[float, float]] = None) -> SketchConstraint:
    return SketchConstraint(kind="Radius", refs=[entity], value=value,
                            echo_text_location=text_location)


def diameter(entity: Any, value: float, *,
             text_location: Optional[tuple[float, float]] = None) -> SketchConstraint:
    return SketchConstraint(kind="Diameter", refs=[entity], value=value,
                            echo_text_location=text_location)


def arc_length(entity: Any, value: float, *,
               text_location: Optional[tuple[float, float]] = None) -> SketchConstraint:
    return SketchConstraint(kind="ArcLength", refs=[entity], value=value,
                            echo_text_location=text_location)
