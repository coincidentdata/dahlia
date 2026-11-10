"""LinearPattern / CircularPattern feature models + lowercase factories."""
from __future__ import annotations

from typing import Annotated, Literal, Optional, Union

from pydantic import Field, model_validator

from ..types import Echo
from ._base import _Base, DefinitionValue


# -- LinearPattern ---------------------------------------------------------
#
# Each direction ends either after a spacing + instance count, or `UpToReference`: up to an
# end-reference entity, optionally measured from a start ("seed") reference, with an offset and
# either a spacing or an instance count.

class _LinearPatternSpacingAndInstances(_Base):
    end_condition: Literal["SpacingAndInstances"] = "SpacingAndInstances"
    spacing: float
    count: int

    @model_validator(mode="after")
    def _check(self) -> "_LinearPatternSpacingAndInstances":
        if self.count < 1:
            raise ValueError("LinearPattern end_condition.count must be >= 1")
        if self.count > 1 and self.spacing <= 0:
            raise ValueError("LinearPattern end_condition.spacing must be > 0 when count > 1")
        return self


class _LinearPatternUpToReference(_Base):
    end_condition: Literal["UpToReference"] = "UpToReference"
    end_reference: DefinitionValue
    # `D1EndUseSeedReference = true` iff a Start reference is provided. `None`
    # means "no seed reference" (default UI behavior); a value means "use this
    # entity as the seed reference for the up-to-reference distance".
    start_reference: Optional[DefinitionValue] = None
    offset: float = 0.0
    offset_reversed: bool = False
    # Spacing-or-count toggle:
    #   * `mode == "Spacing"`   ⇒ `D{1,2}EndUseSpacing = true` and `value` is the
    #     per-instance distance (meters). SW computes count from the reference.
    #   * `mode == "Instances"` ⇒ `D{1,2}EndUseSpacing = false` and `value` is the
    #     integer total-instances count. (The wire carries it as float for
    #     symmetry with the Spacing branch; the handler casts to int.)
    mode: Literal["Spacing", "Instances"] = "Instances"
    value: float

    @model_validator(mode="after")
    def _check_value(self) -> "_LinearPatternUpToReference":
        if self.mode == "Instances":
            iv = int(self.value)
            if iv < 1 or iv != self.value:
                raise ValueError(
                    "LinearPattern UpToReference.value must be a positive integer "
                    "when mode='Instances'"
                )
        elif self.value <= 0:
            raise ValueError(
                "LinearPattern UpToReference.value must be > 0 when mode='Spacing'"
            )
        return self


_LinearPatternEndCondition = Annotated[
    Union[_LinearPatternSpacingAndInstances, _LinearPatternUpToReference],
    Field(discriminator="end_condition"),
]


class _LinearPatternDirection(_Base):
    direction: DefinitionValue  # edge / axis
    end_condition: _LinearPatternEndCondition
    reversed: bool = False


class _LinearPatternDirection2(_LinearPatternDirection):
    # Repeat only the seeds along direction 2, not their direction-1 copies.
    seed_only: bool = False


class LinearPattern(_Base):
    type: Literal["LinearPattern"] = "LinearPattern"
    # SolidWorks-assigned feature name, back-filled on the Add result.
    name: Optional[str] = None
    seeds: list[DefinitionValue]  # features or bodies to repeat
    direction1: _LinearPatternDirection
    direction2: Optional[_LinearPatternDirection2] = None
    deleted: list[int] = Field(default_factory=list)
    geometry_pattern: bool = False
    vary_sketch: bool = False

    # Post-rebuild instance bookkeeping. Not emitted by LinearPatternHandler.Inspect yet.
    echo_instances: Optional[list] = Echo(default=None)
    echo_instance_states: Optional[list] = Echo(default=None)


# -- CircularPattern -------------------------------------------------------

class _CircularStep(_Base):
    spacing: Literal["Step"] = "Step"
    angle: float = Field(gt=0)  # between neighbouring instances


class _CircularEqual(_Base):
    spacing: Literal["Equal"] = "Equal"
    total_angle: float = Field(gt=0)  # spread evenly over all instances


class _CircularPatternDirection(_Base):
    count: int = Field(ge=1)
    spacing: Annotated[Union[_CircularStep, _CircularEqual], Field(discriminator="spacing")]


class CircularPattern(_Base):
    type: Literal["CircularPattern"] = "CircularPattern"
    # SolidWorks-assigned feature name, back-filled on the Add result.
    name: Optional[str] = None
    seeds: list[DefinitionValue]
    axis: DefinitionValue
    direction1: _CircularPatternDirection
    # Turns about the same axis, the other way round.
    direction2: Optional[_CircularPatternDirection] = None
    deleted: list[int] = Field(default_factory=list)
    reversed: bool = False
    geometry_pattern: bool = False
    vary_sketch: bool = False

    # Post-rebuild instance bookkeeping. Not emitted by CircularPatternHandler.Inspect yet.
    echo_instances: Optional[list] = Echo(default=None)
    echo_instance_states: Optional[list] = Echo(default=None)


# -- factories -------------------------------------------------------------

def linear_pattern(*, seeds: list[DefinitionValue], direction1: _LinearPatternDirection,
                   direction2: Optional[_LinearPatternDirection2] = None,
                   deleted: Optional[list[int]] = None, geometry_pattern: bool = False,
                   vary_sketch: bool = False, name: Optional[str] = None) -> LinearPattern:
    return LinearPattern(seeds=seeds, direction1=direction1, direction2=direction2, deleted=deleted or [],
                         geometry_pattern=geometry_pattern, vary_sketch=vary_sketch, name=name)


def circular_pattern(*, seeds: list[DefinitionValue], axis: DefinitionValue,
                     direction1: _CircularPatternDirection,
                     direction2: Optional[_CircularPatternDirection] = None,
                     deleted: Optional[list[int]] = None, reversed: bool = False,
                     geometry_pattern: bool = False, vary_sketch: bool = False,
                     name: Optional[str] = None) -> CircularPattern:
    return CircularPattern(seeds=seeds, axis=axis, direction1=direction1, direction2=direction2,
                           deleted=deleted or [], reversed=reversed, geometry_pattern=geometry_pattern,
                           vary_sketch=vary_sketch, name=name)


__all__ = [
    "_LinearPatternSpacingAndInstances",
    "_LinearPatternUpToReference",
    "_LinearPatternEndCondition",
    "LinearPattern",
    "CircularPattern",
    "linear_pattern",
    "circular_pattern",
]
