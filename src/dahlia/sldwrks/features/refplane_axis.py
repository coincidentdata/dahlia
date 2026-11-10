"""RefPlane / RefAxis (reference geometry) feature models + lowercase factories."""
from __future__ import annotations

import math
from typing import Annotated, Literal, Optional, Union

from pydantic import Field, field_validator

from ._base import _Base, DefinitionValue, Echo, In


# -- RefPlane ----------------------------------------------------------------
#
# Mirrors SolidWorks' plane PropertyManager: up to three references, each with its own
# constraint (IRefPlaneFeatureData.Reference[i] / Constraint[i] / AngleOrDistance[i]).
# Default planes (Top/Front/Right) already exist on every part.

def _unset(value: bool) -> bool:
    return not value  # dumped only when set, like Inspect emits them


class _PlaneReference(_Base):
    reference: DefinitionValue
    # Which side the plane lands on (tangent side, angle tilt, offset side). A replay input,
    # not diffed: the solver picks the side, and Add searches flips until the frame matches.
    flip: bool = Echo(default=False, exclude_if=_unset)


class _PlaneRelation(_PlaneReference):
    constraint: Literal["Coincident", "Parallel", "Tangent", "MidPlane"]


class _PlanePerpendicular(_PlaneReference):
    constraint: Literal["Perpendicular"] = "Perpendicular"
    origin_on_curve: bool = Field(default=False, exclude_if=_unset)


class _PlaneProjectOnto(_PlaneReference):
    constraint: Literal["ProjectOnto"] = "ProjectOnto"
    project_to_nearest_location: bool = Field(default=False, exclude_if=_unset)  # else along the sketch normal


class _PlaneDistance(_PlaneReference):
    constraint: Literal["Distance"] = "Distance"
    distance: float


class _PlaneAngle(_PlaneReference):
    constraint: Literal["Angle"] = "Angle"
    angle: float = Field(gt=-2 * math.pi, lt=2 * math.pi)  # radians


PlaneReference = Annotated[
    Union[_PlaneRelation, _PlanePerpendicular, _PlaneProjectOnto, _PlaneDistance, _PlaneAngle],
    Field(discriminator="constraint"),
]

_Vec3 = Annotated[list[float], Field(min_length=3, max_length=3)]


class _RefPlane(_Base):
    type: Literal["RefPlane"] = "RefPlane"
    name: Optional[str] = None
    # Unit normal; Add flips the plane iff its solved normal faces away. Diffed.
    expected_normal: Optional[_Vec3] = In(default=None)
    # Replay inputs, not diffed: the captured frame (X, Y, Z, origin) that Add reproduces by
    # searching per-reference flips, and the plane's flip-normal state.
    expected_axes: Optional[Annotated[list[_Vec3], Field(min_length=4, max_length=4)]] = Echo(default=None)
    flip_normal: bool = Echo(default=False)


class ConstraintRefPlane(_RefPlane):
    subtype: Literal["ConstraintBase"] = "ConstraintBase"
    references: list[PlaneReference] = Field(min_length=1, max_length=3)


class _LegacyRefPlane(_RefPlane):
    """A plane from an older SolidWorks construction (IRefPlaneFeatureData.Type2 other than
    ConstraintBase, e.g. the default planes). Inspect and edit only; InsertRefPlane can't build one."""
    references: list[DefinitionValue] = Field(default_factory=list)


class LegacyRefPlane(_LegacyRefPlane):
    subtype: Literal["LinePoint", "ThreePoint", "LineLine", "Parallel", "Normal", "OnSurface", "SWStandard"]


class LegacyDistanceRefPlane(_LegacyRefPlane):
    subtype: Literal["Distance"] = "Distance"
    distance: float


class LegacyAngleRefPlane(_LegacyRefPlane):
    subtype: Literal["Angle"] = "Angle"
    angle: float


RefPlane = Annotated[
    Union[ConstraintRefPlane, LegacyRefPlane, LegacyDistanceRefPlane, LegacyAngleRefPlane],
    Field(discriminator="subtype"),
]


class RefAxis(_Base):
    type: Literal["RefAxis"] = "RefAxis"
    references: list[DefinitionValue]                  # 1..2 depending on axis_type
    axis_type: Literal[
        "TwoPlanes", "TwoPoints", "Edge", "Cylindrical", "PointFace"
    ]
    name: Optional[str] = None

    @field_validator("references")
    @classmethod
    def _references_in_range(cls, v: list[DefinitionValue]) -> list[DefinitionValue]:
        if not 1 <= len(v) <= 2:
            raise ValueError("RefAxis: references must have 1 or 2 entries")
        return v


# -- factories -------------------------------------------------------------

def ref_plane(*, references: list[PlaneReference], expected_normal: Optional[list[float]] = None,
              expected_axes: Optional[list[list[float]]] = None, flip_normal: bool = False,
              name: Optional[str] = None) -> ConstraintRefPlane:
    """A plane from up to three references, each a dict like the PropertyManager's reference
    boxes: `{"reference": FRONT, "constraint": "Distance", "distance": 0.01}`."""
    return ConstraintRefPlane(references=references, expected_normal=expected_normal,
                              expected_axes=expected_axes, flip_normal=flip_normal, name=name)


def ref_axis(*, references: list[DefinitionValue],
             axis_type: str,
             name: Optional[str] = None) -> RefAxis:
    return RefAxis(references=references,
                   axis_type=axis_type,  # type: ignore[arg-type]
                   name=name)


__all__ = [
    "PlaneReference",
    "RefPlane",
    "ConstraintRefPlane",
    "LegacyRefPlane",
    "LegacyDistanceRefPlane",
    "LegacyAngleRefPlane",
    "RefAxis",
    "ref_plane",
    "ref_axis",
]
