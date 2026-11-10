"""Sweep / CutSweep feature models + lowercase factories."""
from __future__ import annotations

from typing import Annotated, Literal, Optional, Union

from pydantic import Field

from ..sketch import Sketch
from ..types import BodyDefinition, Echo
from ._base import _Base, DefinitionValue, named


# -- Sweep / CutSweep ------------------------------------------------------
#
# Sweep takes a profile sketch swept along a path. Path can be a sketch
# (a SketchEntityDefinition or a whole Sketch's first segment) or a model
# edge / curve (any solid-geometry Definition). Guide curves are optional
# auxiliary curves that constrain the sweep cross-section.

class _SweepTwistNone(_Base):
    twist: Literal["None", "FollowPath", "KeepNormalConstant", "MinimumTwist",
                   "TangentAdjacentFaces"] = "FollowPath"


class _SweepTwistSpecified(_Base):
    twist: Literal["Specified"] = "Specified"
    angle: float  # radians, total twist along the path (D1 direction)
    # Two-direction sweeps (Direction=Both) carry a separate D2 twist angle.
    # None means single-direction or zero direction-B angle — the handler
    # hard-codes 0.0 in that case. Wire is radians.
    angle_b: Optional[float] = None
    # `swTwistControlNormalConstantTwistAlongPath` is "Specified twist +
    # KeepNormalConstant orientation"; collapsing to plain Specified would
    # silently drop the orientation flag and produce a different rotation
    # along the path. Mapping:
    #   keep_normal=False -> swTwistControlConstantTwistAlongPath
    #   keep_normal=True  -> swTwistControlNormalConstantTwistAlongPath
    keep_normal: bool = False


class _SweepTwistDirection(_Base):
    twist: Literal["Direction"] = "Direction"
    direction: DefinitionValue  # axis / linear edge defining the twist vector
    # No `flipped` field. The path-alignment flip state isn't reliably readable
    # (GetPathAlignmentDirectionVector's out-param is a swSelectType_e
    # entity-kind discriminator, not a flip flag) and there's no setter on
    # SweepFeatureData to author it either, so the wire omits the field.


SweepTwist = named(Annotated[
    Union[_SweepTwistNone, _SweepTwistSpecified, _SweepTwistDirection], Field(discriminator="twist"),
], "twist")


# Start / end profile tangency. Wire literals map to swTangencyType_e:
# "None" -> swTangencyNone (default),
# "NormalToProfile" -> swTangencyNormalToProfile.
SweepTangency = Literal["None", "NormalToProfile"]


class _CircularProfile(_Base):
    type: Literal["Circular"] = "Circular"
    diameter: float = Field(gt=0)


# A profile sketch, or a circle of `diameter` swept with no sketch.
SweepProfile = Annotated[Union[Sketch, _CircularProfile], Field(discriminator="type")]


class _SweepThin(_Base):
    thickness: float = Field(gt=0)


class _Sweep(_Base):
    # SolidWorks-assigned feature name, back-filled on the Add result.
    name: Optional[str] = None
    profile: SweepProfile
    path: DefinitionValue  # sketch entity or solid-geometry edge/curve
    guide_curves: list[DefinitionValue] = Field(default_factory=list)
    twist: SweepTwist = Field(default_factory=_SweepTwistNone)
    align_with_end_faces: bool = False
    merge_tangent_faces: bool = False
    thin: Optional[_SweepThin] = None
    start_tangency: SweepTangency = "None"
    end_tangency: SweepTangency = "None"
    # None = AutoSelect; a list narrows the affected bodies (selection mark 8).
    feature_scope: Optional[list[BodyDefinition]] = None
    # Maps to SweepFeatureData.Direction. For multi-segment sketch profiles DirectionA vs
    # DirectionB sweep the path start-to-end vs end-to-start; "None" lets SW pick Both.
    direction: Literal["None", "DirectionA", "Both", "DirectionB"] = "None"
    # The produced solid-body Definitions after Add.
    echo_bodies: Optional[list[dict]] = Echo(default=None)


class Sweep(_Sweep):
    type: Literal["Sweep"] = "Sweep"
    merge: bool = True


class CutSweep(_Sweep):
    type: Literal["CutSweep"] = "CutSweep"


def sweep(*, profile: SweepProfile, path: DefinitionValue, guide_curves: Optional[list[DefinitionValue]] = None,
          twist: Optional[SweepTwist] = None, align_with_end_faces: bool = False,
          merge_tangent_faces: bool = False, merge: bool = True, thin: Optional[_SweepThin] = None,
          start_tangency: SweepTangency = "None", end_tangency: SweepTangency = "None",
          feature_scope: Optional[list[BodyDefinition]] = None, direction: str = "None",
          name: Optional[str] = None) -> Sweep:
    return Sweep(profile=profile, path=path, guide_curves=guide_curves or [], twist=twist or _SweepTwistNone(),
                 align_with_end_faces=align_with_end_faces, merge_tangent_faces=merge_tangent_faces, merge=merge,
                 thin=thin, start_tangency=start_tangency, end_tangency=end_tangency, feature_scope=feature_scope,
                 direction=direction, name=name)  # type: ignore[arg-type]


def cut_sweep(*, profile: SweepProfile, path: DefinitionValue, guide_curves: Optional[list[DefinitionValue]] = None,
              twist: Optional[SweepTwist] = None, align_with_end_faces: bool = False,
              merge_tangent_faces: bool = False, thin: Optional[_SweepThin] = None,
              start_tangency: SweepTangency = "None", end_tangency: SweepTangency = "None",
              feature_scope: Optional[list[BodyDefinition]] = None, direction: str = "None",
              name: Optional[str] = None) -> CutSweep:
    return CutSweep(profile=profile, path=path, guide_curves=guide_curves or [], twist=twist or _SweepTwistNone(),
                    align_with_end_faces=align_with_end_faces, merge_tangent_faces=merge_tangent_faces, thin=thin,
                    start_tangency=start_tangency, end_tangency=end_tangency, feature_scope=feature_scope,
                    direction=direction, name=name)  # type: ignore[arg-type]


__all__ = [
    "_SweepTwistNone",
    "_SweepTwistSpecified",
    "_SweepTwistDirection",
    "SweepTwist",
    "SweepTangency",
    "Sweep",
    "CutSweep",
    "sweep",
    "cut_sweep",
]
