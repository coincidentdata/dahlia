"""Loft feature model + lowercase factory.

A Loft (a.k.a. Blend) blends a sequence of section profiles into a continuous
surface or solid. The full SolidWorks Loft dialog exposes a wide option set;
this model surfaces every option that maps to a stable ILoftFeatureData field:

  - profiles            : ordered list of section profiles. Each is either
                          a Sketch reference (``{"type": "Sketch", "name": ...}``)
                          or a feature name or a typed/raw Definition pointing
                          at a face / edge / vertex / reference axis. Minimum of two.
  - guide_curves        : optional curves that shape the loft between profiles.
  - centerline          : optional centerline (loft follows this path).
  - start_/end_constraint: start- and end-section constraints, one shape per mode
                          (see `LoftConstraint`); a mode carries only the options it uses.
  - guide_influence     : "NextGuide", "NextSharp", "NextEdge", or "NextGlobal".
  - guide_tangency_types: per-guide tangency tag, one per entry in guide_curves.
  - merge / close / merge_tangent_faces / advanced_smoothing : standard flags.
  - thin                : optional walls around the profiles; TwoDirections takes a second thickness.
  - feature_scope       : optional list of bodies to scope the merge into.
"""
from __future__ import annotations

from typing import Annotated, Literal, Optional, Union

from pydantic import Field, model_validator

from ..helpers import feature_ref
from ..types import BodyDefinition, Echo
from ._base import _Base, DefinitionValue, named


LoftProfile = DefinitionValue


LoftTangency = Literal[
    "None",
    "NormalToProfile",
    "DirectionVector",
    "TangentToFace",
    "CurvatureToFace",
]

LoftGuideInfluence = Literal["NextGuide", "NextSharp", "NextEdge", "NextGlobal"]

class _LoftThinOneSided(_Base):
    wall_type: Literal["OneDirection", "OppositeDirection", "MidPlane"] = "OneDirection"
    thickness: float = Field(gt=0)


class _LoftThinTwoSided(_Base):
    wall_type: Literal["TwoDirections"] = "TwoDirections"
    thickness: float = Field(gt=0)
    thickness2: float = Field(gt=0)


# Walls around the profiles instead of a solid.
LoftThin = Annotated[Union[_LoftThinOneSided, _LoftThinTwoSided], Field(discriminator="wall_type")]


class _LoftCenterline(_Base):
    curve: DefinitionValue
    # Cross sections interpolated along the curve. SW stores a whole number but exposes a double;
    # omitting it leaks a part-dependent default and subtly changes the surface.
    sections: Optional[float] = None


class _LoftDraft(_Base):
    angle: float = Field(gt=0)  # radians
    reverse: bool = False


class _LoftConstraintNone(_Base):
    tangency: Literal["None"] = "None"


class _LoftConstraintSet(_Base):
    tangency: LoftTangency
    tangent_length: float = 1.0
    reverse_tangent: bool = False
    # Read-only in SolidWorks: dialog-built lofts report True, API-built ones False.
    echo_apply_to_all: Optional[bool] = Echo(default=None)


class _LoftConstraintNormalToProfile(_LoftConstraintSet):
    tangency: Literal["NormalToProfile"] = "NormalToProfile"
    draft: Optional[_LoftDraft] = None


class _LoftConstraintDirectionVector(_LoftConstraintNormalToProfile):
    tangency: Literal["DirectionVector"] = "DirectionVector"  # type: ignore[assignment]
    direction: DefinitionValue


class _LoftConstraintFace(_LoftConstraintSet):
    tangency: Literal["TangentToFace", "CurvatureToFace"]
    # The adjacent face. SolidWorks infers it from the profile; Onshape needs it explicitly.
    face: Optional[DefinitionValue] = None


LoftConstraint = named(Annotated[
    Union[_LoftConstraintNone, _LoftConstraintNormalToProfile, _LoftConstraintDirectionVector, _LoftConstraintFace],
    Field(discriminator="tangency"),
], "tangency")


class _Loft(_Base):
    # Inspect/Add-result only — SolidWorks-assigned feature name.
    name: Optional[str] = None

    # Each profile is a Sketch model or a Definition dict (face / edge /
    # vertex / reference axis). Minimum 2.
    profiles: list[LoftProfile]

    guide_curves: list[DefinitionValue] = Field(default_factory=list)
    centerline: Optional[_LoftCenterline] = None

    start_constraint: LoftConstraint = Field(default_factory=_LoftConstraintNone)
    end_constraint: LoftConstraint = Field(default_factory=_LoftConstraintNone)

    # Guide curve handling.
    guide_influence: LoftGuideInfluence = "NextGuide"
    # Per-guide tangency tag (one entry per guide_curves item). Empty list means
    # all guides default to "None".
    guide_tangency_types: list[LoftTangency] = Field(default_factory=list)

    # Standard options.
    close: bool = False
    merge_tangent_faces: bool = False
    advanced_smoothing: bool = False

    thin: Optional[LoftThin] = None

    # User-narrowed feature scope (selection mark 8). None = AutoSelect.
    feature_scope: Optional[list[BodyDefinition]] = None

    # Inspect-only echo: the produced solid-body Definitions after Add.
    echo_bodies: Optional[list[dict]] = Echo(default=None)

    @model_validator(mode="after")
    def _check_profiles(self) -> "_Loft":
        if len(self.profiles) < 2:
            raise ValueError(
                f"Loft: requires at least 2 profiles, got {len(self.profiles)}"
            )
        self.profiles = [
            feature_ref(profile) if isinstance(profile, str) else profile
            for profile in self.profiles
        ]
        if self.guide_tangency_types and len(self.guide_tangency_types) != len(self.guide_curves):
            raise ValueError(
                "Loft: guide_tangency_types must have one entry per guide_curves "
                f"(got {len(self.guide_tangency_types)} tags for {len(self.guide_curves)} guides)"
            )
        return self


class Loft(_Loft):
    type: Literal["Loft"] = "Loft"
    merge: bool = True


class CutLoft(_Loft):
    type: Literal["CutLoft"] = "CutLoft"


def loft(*, profiles: list[LoftProfile], guide_curves: Optional[list[DefinitionValue]] = None,
         centerline: Optional[_LoftCenterline] = None, start_constraint: LoftConstraint = "None",
         end_constraint: LoftConstraint = "None", guide_influence: LoftGuideInfluence = "NextGuide",
         guide_tangency_types: Optional[list[LoftTangency]] = None, close: bool = False, merge: bool = True,
         merge_tangent_faces: bool = False, advanced_smoothing: bool = False, thin: Optional[LoftThin] = None,
         feature_scope: Optional[list[BodyDefinition]] = None, name: Optional[str] = None) -> Loft:
    return Loft(profiles=profiles, guide_curves=guide_curves or [], centerline=centerline,
                start_constraint=start_constraint, end_constraint=end_constraint, guide_influence=guide_influence,
                guide_tangency_types=guide_tangency_types or [], close=close, merge=merge,
                merge_tangent_faces=merge_tangent_faces, advanced_smoothing=advanced_smoothing, thin=thin,
                feature_scope=feature_scope, name=name)


def cut_loft(*, profiles: list[LoftProfile], guide_curves: Optional[list[DefinitionValue]] = None,
             centerline: Optional[_LoftCenterline] = None, start_constraint: LoftConstraint = "None",
             end_constraint: LoftConstraint = "None", guide_influence: LoftGuideInfluence = "NextGuide",
             guide_tangency_types: Optional[list[LoftTangency]] = None, close: bool = False,
             merge_tangent_faces: bool = False, advanced_smoothing: bool = False, thin: Optional[LoftThin] = None,
             feature_scope: Optional[list[BodyDefinition]] = None, name: Optional[str] = None) -> CutLoft:
    return CutLoft(profiles=profiles, guide_curves=guide_curves or [], centerline=centerline,
                   start_constraint=start_constraint, end_constraint=end_constraint, guide_influence=guide_influence,
                   guide_tangency_types=guide_tangency_types or [], close=close,
                   merge_tangent_faces=merge_tangent_faces, advanced_smoothing=advanced_smoothing, thin=thin,
                   feature_scope=feature_scope, name=name)


__all__ = ["Loft", "loft", "CutLoft", "cut_loft", "LoftConstraint", "LoftTangency", "LoftGuideInfluence",
           "LoftThin"]
