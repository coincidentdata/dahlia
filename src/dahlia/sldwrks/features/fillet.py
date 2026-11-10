"""Fillet feature models + lowercase factories.

`Fillet` is the union of SolidWorks' three fillet types, keyed by `kind`:

  - ConstantRadius: rounds `edges` (edges, loops, faces or feature seeds) at `radius`.
  - Face: blends between `face_set_a` and `face_set_b`, sized by `face_type`; `help_point`
    picks which side of the face pair the fillet sits on.
  - FullRound: rounds away the `face_set_b` center faces between `face_set_a` and `face_set_c`;
    SolidWorks derives the size from the faces.

A fillet with `radius_b` is asymmetric: `radius` on one side, `radius_b` on the other.
"""
from __future__ import annotations

from typing import Annotated, Literal, Optional, Union

from pydantic import Field

from ..types import Echo, Point3D
from ._base import _Base, DefinitionValue, named


class _ProfileSimple(_Base):
    profile: Literal["Circular", "CurvatureContinuous"] = "Circular"


class _ProfileConicRho(_Base):
    profile: Literal["ConicRho", "ConicRhoZeroChamfer"]
    rho: float = Field(ge=0.05, le=0.95)


class _ProfileConicRadius(_Base):
    profile: Literal["ConicRadius"] = "ConicRadius"
    radius: float = Field(gt=0)


FilletProfile = named(Annotated[
    Union[_ProfileSimple, _ProfileConicRho, _ProfileConicRadius], Field(discriminator="profile"),
], "profile")


class _Fillet(_Base):
    type: Literal["Fillet"] = "Fillet"
    # SolidWorks-assigned feature name, back-filled on the Add result.
    name: Optional[str] = None
    tangent_propagation: bool = True
    # Faces SolidWorks reports as affected by the fillet.
    echo_affected_faces: Optional[list[dict]] = Echo(default=None)


class ConstantRadiusFillet(_Fillet):
    kind: Literal["ConstantRadius"] = "ConstantRadius"
    edges: list[DefinitionValue]
    radius: float = Field(gt=0)
    radius_b: Optional[float] = Field(default=None, gt=0)
    profile: FilletProfile = Field(default_factory=_ProfileSimple)
    round_corners: bool = False
    overflow: Literal["Default", "KeepEdge", "KeepSurface"] = "Default"


class _FaceRadius(_Base):
    face_type: Literal["Radius"] = "Radius"
    radius: float = Field(gt=0)
    radius_b: Optional[float] = Field(default=None, gt=0)


class _FaceChordWidth(_Base):
    face_type: Literal["ChordWidth"] = "ChordWidth"
    chord_width: float = Field(gt=0)


class _FaceHoldLines(_Base):
    face_type: Literal["HoldLines"] = "HoldLines"
    hold_lines: list[DefinitionValue] = Field(min_length=1)  # edges the fillet conforms to


class FaceFillet(_Fillet):
    kind: Literal["Face"] = "Face"
    face_set_a: list[DefinitionValue] = Field(min_length=1)
    face_set_b: list[DefinitionValue] = Field(min_length=1)
    face_type: Annotated[Union[_FaceRadius, _FaceChordWidth, _FaceHoldLines], Field(discriminator="face_type")]
    profile: FilletProfile = Field(default_factory=_ProfileSimple)
    help_point: Optional[Point3D] = None


class FullRoundFillet(_Fillet):
    kind: Literal["FullRound"] = "FullRound"
    face_set_a: list[DefinitionValue] = Field(min_length=1)
    face_set_b: list[DefinitionValue] = Field(min_length=1)  # the center faces
    face_set_c: list[DefinitionValue] = Field(min_length=1)


Fillet = Annotated[Union[ConstantRadiusFillet, FaceFillet, FullRoundFillet], Field(discriminator="kind")]


def fillet(*, edges: list[DefinitionValue], radius: float, radius_b: Optional[float] = None,
           profile: FilletProfile = "Circular", tangent_propagation: bool = True,
           round_corners: bool = False, overflow: str = "Default",
           name: Optional[str] = None) -> ConstantRadiusFillet:
    return ConstantRadiusFillet(edges=edges, radius=radius, radius_b=radius_b, profile=profile,
                                tangent_propagation=tangent_propagation, round_corners=round_corners,
                                overflow=overflow, name=name)  # type: ignore[arg-type]


def face_fillet(*, face_set_a: list[DefinitionValue], face_set_b: list[DefinitionValue],
                face_type: Union[_FaceRadius, _FaceChordWidth, _FaceHoldLines, dict],
                profile: FilletProfile = "Circular", help_point: Optional[Point3D] = None,
                tangent_propagation: bool = True, name: Optional[str] = None) -> FaceFillet:
    return FaceFillet(face_set_a=face_set_a, face_set_b=face_set_b, face_type=face_type, profile=profile,
                      help_point=help_point, tangent_propagation=tangent_propagation, name=name)


def full_round_fillet(*, face_set_a: list[DefinitionValue], face_set_b: list[DefinitionValue],
                      face_set_c: list[DefinitionValue], tangent_propagation: bool = True,
                      name: Optional[str] = None) -> FullRoundFillet:
    return FullRoundFillet(face_set_a=face_set_a, face_set_b=face_set_b, face_set_c=face_set_c,
                           tangent_propagation=tangent_propagation, name=name)


__all__ = ["Fillet", "ConstantRadiusFillet", "FaceFillet", "FullRoundFillet", "FilletProfile",
           "fillet", "face_fillet", "full_round_fillet"]
