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
  - start_/end_tangency : start- and end-section constraints (None, Normal to
                          profile, Direction vector, Tangent to face,
                          Curvature to face). Direction / tangency values
                          carry an associated tangent_length and reverse_tangent
                          flag; draft-angle constraints carry a draft_angle
                          and draft_reverse flag. Apply-to-all constraints are
                          unavailable because their native setters are unimplemented.
  - guide_influence     : "NextGuide", "NextSharp", "NextEdge", or "NextGlobal".
  - guide_tangency_types: per-guide tangency tag, one per entry in guide_curves.
  - merge / close / merge_tangent_faces / advanced_smoothing : standard flags.
  - thin_feature + thin_wall_type + thin_thickness / thin_thickness2 : thin loft.
  - feature_scope       : optional list of bodies to scope the merge into.
"""
from __future__ import annotations

from typing import Literal, Optional

from pydantic import Field, model_validator

from ..helpers import feature_ref
from ..types import BodyDefinition, Echo
from ._base import _Base, DefinitionValue


LoftProfile = DefinitionValue


LoftTangency = Literal[
    "None",
    "NormalToProfile",
    "DirectionVector",
    "TangentToFace",
    "CurvatureToFace",
]

LoftGuideInfluence = Literal["NextGuide", "NextSharp", "NextEdge", "NextGlobal"]

LoftThinWallType = Literal[
    "OneDirection",
    "OppositeDirection",
    "MidPlane",
    "TwoDirections",
]


class _Loft(_Base):
    # Inspect/Add-result only — SolidWorks-assigned feature name.
    name: Optional[str] = None

    # Each profile is a Sketch model or a Definition dict (face / edge /
    # vertex / reference axis). Minimum 2.
    profiles: list[LoftProfile]

    guide_curves: list[DefinitionValue] = Field(default_factory=list)
    centerline: Optional[DefinitionValue] = None

    # Start section constraint.
    start_tangency: LoftTangency = "None"
    # Required iff start_tangency == "DirectionVector"; otherwise None.
    start_direction: Optional[DefinitionValue] = None
    start_tangent_length: float = 1.0
    start_reverse_tangent: bool = False
    start_draft_angle: float = 0.0       # radians (only meaningful with start_tangency=DirectionVector)
    start_draft_reverse: bool = False
    start_apply_to_all: bool = False

    # End section constraint.
    end_tangency: LoftTangency = "None"
    end_direction: Optional[DefinitionValue] = None
    end_tangent_length: float = 1.0
    end_reverse_tangent: bool = False
    end_draft_angle: float = 0.0
    end_draft_reverse: bool = False
    end_apply_to_all: bool = False

    # Guide curve handling.
    guide_influence: LoftGuideInfluence = "NextGuide"
    # Per-guide tangency tag (one entry per guide_curves item). Empty list means
    # all guides default to "None".
    guide_tangency_types: list[LoftTangency] = Field(default_factory=list)

    # Standard options.
    close: bool = False
    merge_tangent_faces: bool = False
    advanced_smoothing: bool = False

    # Thin feature options create walls around the profiles. SolidWorks defaults
    # to OneDirection; TwoDirections requires thin_thickness2 too.
    thin_feature: bool = False
    thin_wall_type: LoftThinWallType = "OneDirection"
    thin_thickness: Optional[float] = None
    thin_thickness2: Optional[float] = None

    # User-narrowed feature scope (selection mark 8). None = AutoSelect.
    feature_scope: Optional[list[BodyDefinition]] = None

    # Centerline cross-section sample count ("Number of sections" slider in
    # the SW Loft dialog, only meaningful when a centerline is present).
    # Controls how many cross-sections SW interpolates along the centerline
    # — drives the tessellation of the lofted surface. Omitting leaks SW's
    # part-dependent default and produces a subtly different body shape on
    # rebuild. SW stores this as an integer but the API exposes a double.
    number_of_sections: Optional[float] = None

    # Inspect-only echo: the produced solid-body Definitions after Add.
    echo_bodies: Optional[list[dict]] = Echo(default=None)

    @model_validator(mode="after")
    def _check_profiles(self) -> "_Loft":
        if self.start_apply_to_all or self.end_apply_to_all:
            raise ValueError("Loft: apply-to-all constraint setters are not implemented by SolidWorks")
        if len(self.profiles) < 2:
            raise ValueError(
                f"Loft: requires at least 2 profiles, got {len(self.profiles)}"
            )
        self.profiles = [
            feature_ref(profile) if isinstance(profile, str) else profile
            for profile in self.profiles
        ]
        if self.start_tangency == "DirectionVector" and self.start_direction is None:
            raise ValueError(
                "Loft: start_tangency='DirectionVector' requires start_direction"
            )
        if self.end_tangency == "DirectionVector" and self.end_direction is None:
            raise ValueError(
                "Loft: end_tangency='DirectionVector' requires end_direction"
            )
        if self.guide_tangency_types and len(self.guide_tangency_types) != len(self.guide_curves):
            raise ValueError(
                "Loft: guide_tangency_types must have one entry per guide_curves "
                f"(got {len(self.guide_tangency_types)} tags for {len(self.guide_curves)} guides)"
            )
        if self.thin_feature:
            if self.thin_thickness is None or self.thin_thickness <= 0:
                raise ValueError(
                    "Loft: thin_feature=True requires thin_thickness > 0"
                )
            if self.thin_wall_type == "TwoDirections":
                if self.thin_thickness2 is None or self.thin_thickness2 <= 0:
                    raise ValueError(
                        "Loft: thin_wall_type='TwoDirections' requires thin_thickness2 > 0"
                    )
        return self


class Loft(_Loft):
    type: Literal["Loft"] = "Loft"
    merge: bool = True


class CutLoft(_Loft):
    type: Literal["CutLoft"] = "CutLoft"


def loft(*, profiles: list[LoftProfile],
         guide_curves: Optional[list[DefinitionValue]] = None,
         centerline: Optional[DefinitionValue] = None,
         start_tangency: str = "None",
         start_direction: Optional[DefinitionValue] = None,
         start_tangent_length: float = 1.0,
         start_reverse_tangent: bool = False,
         start_draft_angle: float = 0.0,
         start_draft_reverse: bool = False,
         start_apply_to_all: bool = False,
         end_tangency: str = "None",
         end_direction: Optional[DefinitionValue] = None,
         end_tangent_length: float = 1.0,
         end_reverse_tangent: bool = False,
         end_draft_angle: float = 0.0,
         end_draft_reverse: bool = False,
         end_apply_to_all: bool = False,
         guide_influence: str = "NextGuide",
         guide_tangency_types: Optional[list[str]] = None,
         close: bool = False,
         merge: bool = True,
         merge_tangent_faces: bool = False,
         advanced_smoothing: bool = False,
         thin_feature: bool = False,
         thin_wall_type: str = "OneDirection",
         thin_thickness: Optional[float] = None,
         thin_thickness2: Optional[float] = None,
         feature_scope: Optional[list[BodyDefinition]] = None,
         number_of_sections: Optional[float] = None,
         name: Optional[str] = None) -> Loft:
    return Loft(
        profiles=profiles,
        guide_curves=guide_curves or [],
        centerline=centerline,
        start_tangency=start_tangency,  # type: ignore[arg-type]
        start_direction=start_direction,
        start_tangent_length=start_tangent_length,
        start_reverse_tangent=start_reverse_tangent,
        start_draft_angle=start_draft_angle,
        start_draft_reverse=start_draft_reverse,
        start_apply_to_all=start_apply_to_all,
        end_tangency=end_tangency,  # type: ignore[arg-type]
        end_direction=end_direction,
        end_tangent_length=end_tangent_length,
        end_reverse_tangent=end_reverse_tangent,
        end_draft_angle=end_draft_angle,
        end_draft_reverse=end_draft_reverse,
        end_apply_to_all=end_apply_to_all,
        guide_influence=guide_influence,  # type: ignore[arg-type]
        guide_tangency_types=guide_tangency_types or [],  # type: ignore[arg-type]
        close=close, merge=merge, merge_tangent_faces=merge_tangent_faces,
        advanced_smoothing=advanced_smoothing,
        thin_feature=thin_feature,
        thin_wall_type=thin_wall_type,  # type: ignore[arg-type]
        thin_thickness=thin_thickness,
        thin_thickness2=thin_thickness2,
        feature_scope=feature_scope,
        number_of_sections=number_of_sections,
        name=name,
    )


def cut_loft(
    *, profiles: list[LoftProfile], guide_curves: Optional[list[DefinitionValue]] = None,
    centerline: Optional[DefinitionValue] = None,
    start_tangency: LoftTangency = "None", start_direction: Optional[DefinitionValue] = None,
    start_tangent_length: float = 1.0, start_reverse_tangent: bool = False,
    start_draft_angle: float = 0.0, start_draft_reverse: bool = False,
    start_apply_to_all: bool = False,
    end_tangency: LoftTangency = "None", end_direction: Optional[DefinitionValue] = None,
    end_tangent_length: float = 1.0, end_reverse_tangent: bool = False,
    end_draft_angle: float = 0.0, end_draft_reverse: bool = False,
    end_apply_to_all: bool = False, guide_influence: LoftGuideInfluence = "NextGuide",
    guide_tangency_types: Optional[list[LoftTangency]] = None,
    close: bool = False, merge_tangent_faces: bool = False, advanced_smoothing: bool = False,
    thin_feature: bool = False, thin_wall_type: LoftThinWallType = "OneDirection",
    thin_thickness: Optional[float] = None, thin_thickness2: Optional[float] = None,
    feature_scope: Optional[list[BodyDefinition]] = None,
    number_of_sections: Optional[float] = None, name: Optional[str] = None,
) -> CutLoft:
    return CutLoft(
        profiles=profiles, guide_curves=guide_curves or [], centerline=centerline,
        start_tangency=start_tangency, start_direction=start_direction,
        start_tangent_length=start_tangent_length, start_reverse_tangent=start_reverse_tangent,
        start_draft_angle=start_draft_angle, start_draft_reverse=start_draft_reverse,
        start_apply_to_all=start_apply_to_all,
        end_tangency=end_tangency, end_direction=end_direction,
        end_tangent_length=end_tangent_length, end_reverse_tangent=end_reverse_tangent,
        end_draft_angle=end_draft_angle, end_draft_reverse=end_draft_reverse,
        end_apply_to_all=end_apply_to_all, guide_influence=guide_influence,
        guide_tangency_types=guide_tangency_types or [], close=close,
        merge_tangent_faces=merge_tangent_faces, advanced_smoothing=advanced_smoothing,
        thin_feature=thin_feature, thin_wall_type=thin_wall_type,
        thin_thickness=thin_thickness, thin_thickness2=thin_thickness2,
        feature_scope=feature_scope, number_of_sections=number_of_sections, name=name,
    )


__all__ = ["Loft", "loft", "CutLoft", "cut_loft", "LoftTangency", "LoftGuideInfluence", "LoftThinWallType"]
