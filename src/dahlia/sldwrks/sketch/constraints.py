"""Sketch constraint model + the geometric/dimensional kind literal."""
from __future__ import annotations

from typing import Annotated, Any, Literal, Optional, Union

from pydantic import Field, TypeAdapter, field_validator

from ..types import _Base, Echo


# -- constraints ------------------------------------------------------------

# Geometric / relation kinds covering the full `swSketchRelations_e` set so
# the wire round-trips every constraint SolidWorks can emit. Distance subtypes
# (HorizontalDistance / VerticalDistance / Ordinate / ChamferDimension) and
# ArcLength are dimensional and live alongside Distance / Angle / Radius /
# Diameter.
_GENERAL_KIND = Literal[
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
    "Radius",
    "Diameter",
    "ArcLength",
    "Scalar",
    "DoubleAngular",
    "DoubleDistance",
    "AngularOrdinate",
]
CONSTRAINT_KIND = Literal[_GENERAL_KIND, "Angle"]


ANGLE_LINE_DIRECTION = Literal["None", "TowardsStart", "TowardsEnd"]
ANGLE_DIRECTION = Literal["None", "Right", "Up", "Left", "Down"]


class _Constraint(_Base):
    """`refs` are Definition JSON objects. On Inspect the C# emits each as a typed Definition
    flavor (usually a sketch-entity one); on Add it resolves each SketchEntityId against the
    per-Add map of created entities, falling through to `DefinitionResolver` for cross-sketch refs."""
    refs: list[Any]
    value: Optional[float] = None
    # Annotation placement in sketch-plane coords; the Add replays it.
    echo_text_location: Optional[tuple[float, float]] = Echo(default=None)
    # The dimension's SolidWorks name ("D1@Sketch2@Part.Part"), the handle edit_dimension /
    # rename_dimension resolve through. SW assigns it at creation.
    echo_dim_name: Optional[str] = Echo(default=None)

    @field_validator("refs")
    @classmethod
    def _refs_normalized(cls, v: list[Any]) -> list[Any]:
        out: list[Any] = []
        for i, ref in enumerate(v):
            if hasattr(ref, "model_dump") and callable(ref.model_dump):
                ref = ref.model_dump(mode="json")
            if not isinstance(ref, dict) or "kind" not in ref:
                raise ValueError(
                    f"constraint refs[{i}]: expected a Definition (model or dict with 'kind' "
                    f"discriminator); got {ref!r}. A None ref usually means an upstream `probe()` "
                    f"missed (ray returned None) — use a ray that hits the intended entity.")
            out.append(ref)
        return out


class GeneralConstraint(_Constraint):
    kind: _GENERAL_KIND


class AngleConstraint(_Constraint):
    """Angle dimension. Two lines meet in four equivalent angles; these options pick which one
    SolidWorks measures."""
    kind: Literal["Angle"] = "Angle"
    line_directions: list[ANGLE_LINE_DIRECTION] = Field(default_factory=list)
    angle_direction: ANGLE_DIRECTION = "None"
    clockwise: bool = False
    use_opp_ext_line: bool = False


SketchConstraint = Annotated[Union[GeneralConstraint, AngleConstraint], Field(discriminator="kind")]
_SKETCH_CONSTRAINT = TypeAdapter(SketchConstraint)


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

def constrain(kind: CONSTRAINT_KIND, *refs: Any, value: Optional[float] = None,
              text_location: Optional[tuple[float, float]] = None) -> SketchConstraint:
    """Generic constraint factory for any ``CONSTRAINT_KIND`` — the escape hatch for kinds
    without a named factory below."""
    return _SKETCH_CONSTRAINT.validate_python(
        {"kind": kind, "refs": list(refs), "value": value, "echo_text_location": text_location})


def _geometric(kind: _GENERAL_KIND):
    def factory(*refs: Any) -> GeneralConstraint:
        return GeneralConstraint(kind=kind, refs=list(refs))
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
             text_location: Optional[tuple[float, float]] = None) -> GeneralConstraint:
    return GeneralConstraint(kind="Distance", refs=[a, b], value=value,
                            echo_text_location=text_location)


def horizontal_distance(a: Any, b: Any, value: float, *,
                        text_location: Optional[tuple[float, float]] = None) -> GeneralConstraint:
    return GeneralConstraint(kind="HorizontalDistance", refs=[a, b], value=value,
                            echo_text_location=text_location)


def vertical_distance(a: Any, b: Any, value: float, *,
                      text_location: Optional[tuple[float, float]] = None) -> GeneralConstraint:
    return GeneralConstraint(kind="VerticalDistance", refs=[a, b], value=value,
                            echo_text_location=text_location)


def angle(*refs: Any, value: float, text_location: Optional[tuple[float, float]] = None,
          line_directions: Optional[list[ANGLE_LINE_DIRECTION]] = None, angle_direction: ANGLE_DIRECTION = "None",
          clockwise: bool = False, use_opp_ext_line: bool = False) -> AngleConstraint:
    return AngleConstraint(refs=list(refs), value=value, echo_text_location=text_location,
                           line_directions=line_directions or [], angle_direction=angle_direction,
                           clockwise=clockwise, use_opp_ext_line=use_opp_ext_line)


def radius(entity: Any, value: float, *,
           text_location: Optional[tuple[float, float]] = None) -> GeneralConstraint:
    return GeneralConstraint(kind="Radius", refs=[entity], value=value,
                            echo_text_location=text_location)


def diameter(entity: Any, value: float, *,
             text_location: Optional[tuple[float, float]] = None) -> GeneralConstraint:
    return GeneralConstraint(kind="Diameter", refs=[entity], value=value,
                            echo_text_location=text_location)


def arc_length(entity: Any, value: float, *,
               text_location: Optional[tuple[float, float]] = None) -> GeneralConstraint:
    return GeneralConstraint(kind="ArcLength", refs=[entity], value=value,
                            echo_text_location=text_location)
