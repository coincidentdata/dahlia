"""Sweep / CutSweep feature models + lowercase factories."""
from __future__ import annotations

from typing import Annotated, Literal, Optional, Union

from pydantic import BeforeValidator, Field, model_validator

from ..sketch import Sketch
from ..types import BodyDefinition, Echo
from ._base import _Base, DefinitionValue


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


def _twist_from_name(v: object) -> object:
    """Accept `twist="FollowPath"` for the variants that carry no other data.

    The `{"twist": ...}` dict is the wire's discriminator envelope, not something
    an author should have to spell out. `Specified` and `Direction` carry an angle
    or a direction, so a bare name for those fails validation on the missing
    field, which is the right error to get.
    """
    return {"twist": v} if isinstance(v, str) else v


# Same wrapping as EndCondition: a BeforeValidator beside the discriminator is
# skipped under Optional[...]. Sweep.twist is not Optional today, so the inner form
# happened to work — this does not depend on that staying true.
SweepTwist = Annotated[
    Annotated[
        Union[_SweepTwistNone, _SweepTwistSpecified, _SweepTwistDirection],
        Field(discriminator="twist"),
    ],
    BeforeValidator(_twist_from_name),
]


# Start / end profile tangency. Wire literals map to swTangencyType_e:
# "None" -> swTangencyNone (default),
# "NormalToProfile" -> swTangencyNormalToProfile.
SweepTangency = Literal["None", "NormalToProfile"]


class Sweep(_Base):
    type: Literal["Sweep"] = "Sweep"
    # Inspect/Add-result only — SolidWorks-assigned feature name. See Extrude.name.
    name: Optional[str] = None
    # Required iff profile_type == "Sketch"; None otherwise (Inspect omits it).
    profile: Optional[Sketch] = None
    path: DefinitionValue  # sketch entity or solid-geometry edge/curve
    guide_curves: list[DefinitionValue] = Field(default_factory=list)
    twist: SweepTwist = Field(default_factory=_SweepTwistNone)
    align_with_end_faces: bool = False
    merge_tangent_faces: bool = False
    merge: bool = True
    thin_feature: bool = False
    thin_thickness: Optional[float] = None  # required iff thin_feature

    # Three profile types are defined: Sketch, Circular (diameter-only, no
    # sketch), Solid (body sweep). The handler throws NotSupportedException
    # for non-Sketch until an authoring case lands.
    profile_type: Literal["Sketch", "Circular", "Solid"] = "Sketch"
    circular_diameter: Optional[float] = None      # required iff profile_type=="Circular"
    solid_body: Optional[BodyDefinition] = None    # required iff profile_type=="Solid"

    # Start / end tangency.
    start_tangency: SweepTangency = "None"
    end_tangency: SweepTangency = "None"

    # User-narrowed feature scope (selection mark 8). None means AutoSelect
    # (SolidWorks picks affected bodies); a list means explicit body scope.
    # Mirrors Revolve.feature_scope.
    feature_scope: Optional[list[BodyDefinition]] = None
    # Sketch-sweep direction. Maps to `SweepFeatureData.Direction`. For
    # multi-segment sketch profiles, `DirectionA` vs `DirectionB` produces
    # distinct geometries (sweep along the path's start-to-end vs end-to-start
    # direction). `None` lets SW collapse to Both at create time.
    direction: Literal["None", "DirectionA", "Both", "DirectionB"] = "None"

    # Inspect-only echo: the produced solid-body Definitions after Add.
    echo_bodies: Optional[list[dict]] = Echo(default=None)

    @model_validator(mode="after")
    def _check_profile_type(self) -> "Sweep":
        if self.profile_type == "Circular":
            if self.circular_diameter is None or self.circular_diameter <= 0:
                raise ValueError(
                    "Sweep: profile_type='Circular' requires circular_diameter > 0")
            if self.solid_body is not None:
                raise ValueError(
                    "Sweep: profile_type='Circular' must not set solid_body")
            if self.profile is not None:
                raise ValueError(
                    "Sweep: profile_type='Circular' must not set profile sketch")
        elif self.profile_type == "Solid":
            if self.solid_body is None:
                raise ValueError(
                    "Sweep: profile_type='Solid' requires solid_body")
            if self.circular_diameter is not None:
                raise ValueError(
                    "Sweep: profile_type='Solid' must not set circular_diameter")
        else:  # "Sketch"
            if self.profile is None:
                raise ValueError(
                    "Sweep: profile_type='Sketch' requires profile sketch")
            if self.circular_diameter is not None:
                raise ValueError(
                    "Sweep: profile_type='Sketch' must not set circular_diameter")
            if self.solid_body is not None:
                raise ValueError(
                    "Sweep: profile_type='Sketch' must not set solid_body")
        return self


class CutSweep(_Base):
    type: Literal["CutSweep"] = "CutSweep"
    # Inspect/Add-result only — SolidWorks-assigned feature name. See Extrude.name.
    name: Optional[str] = None
    # Required iff profile_type == "Sketch"; None otherwise (Inspect omits it).
    profile: Optional[Sketch] = None
    path: DefinitionValue
    guide_curves: list[DefinitionValue] = Field(default_factory=list)
    twist: SweepTwist = Field(default_factory=_SweepTwistNone)
    align_with_end_faces: bool = False
    merge_tangent_faces: bool = False
    thin_feature: bool = False
    thin_thickness: Optional[float] = None

    # See Sweep.profile_type. Solid is unsupported on the CutSweep path; the
    # schema slot is kept for symmetry but the handler throws.
    profile_type: Literal["Sketch", "Circular", "Solid"] = "Sketch"
    circular_diameter: Optional[float] = None
    solid_body: Optional[BodyDefinition] = None

    # Start / end tangency.
    start_tangency: SweepTangency = "None"
    end_tangency: SweepTangency = "None"

    # User-narrowed feature scope (selection mark 8).
    feature_scope: Optional[list[BodyDefinition]] = None
    # Sketch-sweep direction. See Sweep.direction.
    direction: Literal["None", "DirectionA", "Both", "DirectionB"] = "None"

    # Inspect-only echo: the produced solid-body Definitions after Add.
    echo_bodies: Optional[list[dict]] = Echo(default=None)

    @model_validator(mode="after")
    def _check_profile_type(self) -> "CutSweep":
        if self.profile_type == "Circular":
            if self.circular_diameter is None or self.circular_diameter <= 0:
                raise ValueError(
                    "CutSweep: profile_type='Circular' requires circular_diameter > 0")
            if self.solid_body is not None:
                raise ValueError(
                    "CutSweep: profile_type='Circular' must not set solid_body")
            if self.profile is not None:
                raise ValueError(
                    "CutSweep: profile_type='Circular' must not set profile sketch")
        elif self.profile_type == "Solid":
            if self.solid_body is None:
                raise ValueError(
                    "CutSweep: profile_type='Solid' requires solid_body")
            if self.circular_diameter is not None:
                raise ValueError(
                    "CutSweep: profile_type='Solid' must not set circular_diameter")
        else:  # "Sketch"
            if self.profile is None:
                raise ValueError(
                    "CutSweep: profile_type='Sketch' requires profile sketch")
            if self.circular_diameter is not None:
                raise ValueError(
                    "CutSweep: profile_type='Sketch' must not set circular_diameter")
            if self.solid_body is not None:
                raise ValueError(
                    "CutSweep: profile_type='Sketch' must not set solid_body")
        return self


# -- factories -------------------------------------------------------------

def sweep(*, profile: Sketch, path: DefinitionValue,
          guide_curves: Optional[list[DefinitionValue]] = None,
          twist: Optional[SweepTwist] = None,
          align_with_end_faces: bool = False,
          merge_tangent_faces: bool = False,
          merge: bool = True,
          thin_feature: bool = False,
          thin_thickness: Optional[float] = None,
          profile_type: str = "Sketch",
          circular_diameter: Optional[float] = None,
          solid_body: Optional[BodyDefinition] = None,
          start_tangency: str = "None",
          end_tangency: str = "None",
          feature_scope: Optional[list[BodyDefinition]] = None,
          direction: str = "None",
          name: Optional[str] = None) -> Sweep:
    return Sweep(
        profile=profile, path=path,
        guide_curves=guide_curves or [],
        twist=twist or _SweepTwistNone(),
        align_with_end_faces=align_with_end_faces,
        merge_tangent_faces=merge_tangent_faces, merge=merge,
        thin_feature=thin_feature, thin_thickness=thin_thickness,
        profile_type=profile_type,  # type: ignore[arg-type]
        circular_diameter=circular_diameter, solid_body=solid_body,
        start_tangency=start_tangency,  # type: ignore[arg-type]
        end_tangency=end_tangency,  # type: ignore[arg-type]
        feature_scope=feature_scope,
        direction=direction,  # type: ignore[arg-type]
        name=name,
    )


def cut_sweep(*, profile: Sketch, path: DefinitionValue,
              guide_curves: Optional[list[DefinitionValue]] = None,
              twist: Optional[SweepTwist] = None,
              align_with_end_faces: bool = False,
              merge_tangent_faces: bool = False,
              thin_feature: bool = False,
              thin_thickness: Optional[float] = None,
              profile_type: str = "Sketch",
              circular_diameter: Optional[float] = None,
              solid_body: Optional[BodyDefinition] = None,
              start_tangency: str = "None",
              end_tangency: str = "None",
              feature_scope: Optional[list[BodyDefinition]] = None,
              direction: str = "None",
              name: Optional[str] = None) -> CutSweep:
    return CutSweep(
        profile=profile, path=path,
        guide_curves=guide_curves or [],
        twist=twist or _SweepTwistNone(),
        align_with_end_faces=align_with_end_faces,
        merge_tangent_faces=merge_tangent_faces,
        thin_feature=thin_feature, thin_thickness=thin_thickness,
        profile_type=profile_type,  # type: ignore[arg-type]
        circular_diameter=circular_diameter, solid_body=solid_body,
        start_tangency=start_tangency,  # type: ignore[arg-type]
        end_tangency=end_tangency,  # type: ignore[arg-type]
        feature_scope=feature_scope,
        direction=direction,  # type: ignore[arg-type]
        name=name,
    )


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
