"""LinearPattern / CircularPattern feature models + lowercase factories."""
from __future__ import annotations

from typing import Annotated, Literal, Optional, Union

from pydantic import Field, field_validator, model_validator

from ..types import Echo
from ._base import _Base, DefinitionValue


# -- LinearPattern end-condition -------------------------------------------
#
# Each axis carries a discriminated end-condition: either the simple
# `SpacingAndInstances` (spacing+count default) or `UpToReference` — an
# end-reference entity, optional start-reference (the "use seed" toggle in
# the UI), an offset distance with a reverse flag, and a spacing/count toggle.
# Map to:
#
#   * `_LinearPatternSpacingAndInstances` → `swPatternEndCondition_SpacingAndInstances`
#     with `D{1,2}Spacing` + `D{1,2}TotalInstances`. The top-level
#     `spacing_a` / `count_a` (and `_b`) populate this shape.
#   * `_LinearPatternUpToReference` → `swPatternEndCondition_UpToReference` with
#     `D{1,2}EndReference` + (optional `D{1,2}EndSeedReference` via
#     `D{1,2}EndUseSeedReference`) + `D{1,2}EndRefOffset` +
#     `D{1,2}EndRefReverseOffset` + `D{1,2}EndUseSpacing` + the spacing-or-count
#     value.
#
# The top-level `spacing_a` / `count_a` / `spacing_b` / `count_b` fields and
# the discriminated `end_condition_a` / `end_condition_b` per-axis fields
# coexist; the validator enforces "exactly one shape per axis" so populating
# both forms is an error. When the discriminated form is set, the top-level
# fields must remain at their no-op defaults.

class _LinearPatternSpacingAndInstances(_Base):
    end_condition: Literal["SpacingAndInstances"] = "SpacingAndInstances"
    spacing: float
    count: int

    @field_validator("count")
    @classmethod
    def _count_min(cls, v: int) -> int:
        if v < 1:
            raise ValueError("LinearPattern end_condition.count must be >= 1")
        return v


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


# -- LinearPattern ---------------------------------------------------------

class LinearPattern(_Base):
    type: Literal["LinearPattern"] = "LinearPattern"
    # Inspect/Add-result only — SolidWorks-assigned feature name. See Extrude.name.
    name: Optional[str] = None
    seeds: list[DefinitionValue]  # features or bodies to repeat
    direction_a: DefinitionValue  # edge / axis
    spacing_a: float = 0.0
    count_a: int = 1
    direction_b: Optional[DefinitionValue] = None
    spacing_b: Optional[float] = None
    count_b: Optional[int] = None
    # Per-axis reverse flag mapping to FlipDir1 / FlipDir2 of
    # `FeatureLinearPattern5` (and `D1ReverseDirection` / `D2ReverseDirection`
    # on the feature-data on Inspect). Required so UI-authored reversal
    # round-trips.
    reversed_a: bool = False
    reversed_b: bool = False
    # Per-axis discriminated end-condition. None = use the simple
    # spacing/count shape from `spacing_a` / `count_a` (or `_b`). When set, the
    # discriminated shape wins and the simple-shape fields on that axis must be
    # at their no-op defaults (the validator enforces this).
    end_condition_a: Optional[_LinearPatternEndCondition] = None
    end_condition_b: Optional[_LinearPatternEndCondition] = None
    deleted: list[int] = Field(default_factory=list)
    # SolidWorks pattern flags. Defaults match the SolidWorks UI defaults (all
    # false).
    geometry_pattern: bool = False
    vary_sketch: bool = False
    pattern_seed_only: bool = False  # 2D-only flag; only honored when count_b is set

    # Inspect-only echo: post-rebuild instance bookkeeping captured by the C#
    # handler so callers can introspect what SW produced. Forward-compat —
    # LinearPatternHandler.Inspect does not currently emit these.
    echo_instances: Optional[list] = Echo(default=None)
    echo_instance_states: Optional[list] = Echo(default=None)

    @field_validator("count_a")
    @classmethod
    def _count_a_min(cls, v: int) -> int:
        if v < 1:
            raise ValueError("count_a must be >= 1")
        return v

    @model_validator(mode="after")
    def _check_axis_a_shape(self) -> "LinearPattern":
        # Axis A specifies exactly one shape. When the discriminated
        # `end_condition_a` is set, the top-level spacing_a/count_a fields must
        # stay at their no-op defaults (0.0 / 1) so there's no ambiguity about
        # which shape wins. Without the discriminated form, the top-level
        # fields drive A — and spacing must be > 0 when count > 1.
        if self.end_condition_a is not None:
            if self.spacing_a != 0.0 or self.count_a != 1:
                raise ValueError(
                    "LinearPattern: when end_condition_a is set, leave spacing_a / "
                    "count_a at defaults (0.0 / 1) — the discriminated shape carries "
                    "those values"
                )
        else:
            if self.count_a > 1 and self.spacing_a <= 0.0:
                raise ValueError(
                    "LinearPattern: spacing_a must be > 0 when count_a > 1"
                )
        return self

    @model_validator(mode="after")
    def _check_2d_consistency(self) -> "LinearPattern":
        # direction_b / spacing_b / count_b are an all-or-nothing trio. Mixing
        # partial state would silently downgrade to 1D and lose user intent.
        # C# also enforces this; the validator here surfaces the error
        # client-side first.
        #
        # When `end_condition_b` is set, spacing_b / count_b stay unset (the
        # discriminated shape carries those). `direction_b` is still required
        # because the axis is the entity, not the end-condition.
        if self.end_condition_b is not None:
            if self.spacing_b is not None or self.count_b is not None:
                raise ValueError(
                    "LinearPattern: when end_condition_b is set, leave spacing_b / "
                    "count_b unset — the discriminated shape carries those"
                )
            if self.direction_b is None:
                raise ValueError(
                    "LinearPattern: end_condition_b requires direction_b"
                )
            return self
        secondary = (self.direction_b, self.spacing_b, self.count_b)
        any_set = any(v is not None for v in secondary)
        all_set = all(v is not None for v in secondary)
        if any_set and not all_set:
            raise ValueError(
                "LinearPattern: direction_b, spacing_b, count_b must all be set "
                "together for a 2D pattern, or all None for a 1D pattern"
            )
        if all_set and self.count_b is not None and self.count_b < 1:
            raise ValueError("count_b must be >= 1")
        return self


# -- CircularPattern -------------------------------------------------------

class CircularPattern(_Base):
    type: Literal["CircularPattern"] = "CircularPattern"
    # Inspect/Add-result only — SolidWorks-assigned feature name. See Extrude.name.
    name: Optional[str] = None
    seeds: list[DefinitionValue]
    axis: DefinitionValue
    # Step-vs-total-angle mode is mutually exclusive. `angle_step` is the
    # per-instance step angle (`EqualSpacing=false`); `total_angle` is the full
    # sweep across all instances (`EqualSpacing=true`). Exactly one must be set.
    angle_step: Optional[float] = None
    total_angle: Optional[float] = None
    count: int
    # Second direction. Mirrors the step-vs-total dispatch on the second axis.
    # All-or-nothing trio: when any direction-B field is set, both
    # `direction_b` + `count_b` + exactly one of (`angle_step_b`,
    # `total_angle_b`) are required.
    direction_b: Optional[DefinitionValue] = None
    angle_step_b: Optional[float] = None
    total_angle_b: Optional[float] = None
    count_b: Optional[int] = None
    deleted: list[int] = Field(default_factory=list)
    reversed: bool = False
    # SolidWorks pattern flags.
    geometry_pattern: bool = False
    vary_sketch: bool = False

    # Inspect-only echo: see LinearPattern.echo_instances. Not currently
    # emitted by CircularPatternHandler.Inspect; declared for symmetry +
    # forward-compat.
    echo_instances: Optional[list] = Echo(default=None)
    echo_instance_states: Optional[list] = Echo(default=None)

    @field_validator("count")
    @classmethod
    def _count_min(cls, v: int) -> int:
        if v < 1:
            raise ValueError("count must be >= 1")
        return v

    @model_validator(mode="after")
    def _check_angle_mode(self) -> "CircularPattern":
        # Exactly one of `angle_step` / `total_angle` for direction A. Setting
        # both is ambiguous; setting neither leaves the SolidWorks slot
        # uninitialized — the handler would write a default which is data loss.
        a_step = self.angle_step is not None
        a_total = self.total_angle is not None
        if a_step == a_total:
            raise ValueError(
                "CircularPattern: exactly one of angle_step / total_angle must be set"
            )
        if a_step and self.angle_step is not None and self.angle_step <= 0:
            raise ValueError("CircularPattern: angle_step must be > 0")
        if a_total and self.total_angle is not None and self.total_angle <= 0:
            raise ValueError("CircularPattern: total_angle must be > 0")
        return self

    @model_validator(mode="after")
    def _check_2d_consistency(self) -> "CircularPattern":
        # All-or-nothing for direction B. `direction_b` set requires exactly
        # one of (angle_step_b, total_angle_b) plus count_b.
        b_dir = self.direction_b is not None
        b_count = self.count_b is not None
        b_step = self.angle_step_b is not None
        b_total = self.total_angle_b is not None
        any_b = b_dir or b_count or b_step or b_total
        if not any_b:
            return self
        if not b_dir or not b_count:
            raise ValueError(
                "CircularPattern: when any direction-B field is set, both "
                "direction_b and count_b are required"
            )
        if b_step == b_total:
            raise ValueError(
                "CircularPattern: when direction_b is set, exactly one of "
                "angle_step_b / total_angle_b must be set"
            )
        if self.count_b is not None and self.count_b < 1:
            raise ValueError("CircularPattern: count_b must be >= 1")
        if b_step and self.angle_step_b is not None and self.angle_step_b <= 0:
            raise ValueError("CircularPattern: angle_step_b must be > 0")
        if b_total and self.total_angle_b is not None and self.total_angle_b <= 0:
            raise ValueError("CircularPattern: total_angle_b must be > 0")
        return self


# -- factories -------------------------------------------------------------

def linear_pattern(*, seeds: list[DefinitionValue],
                   direction_a: DefinitionValue,
                   spacing_a: float = 0.0, count_a: int = 1,
                   direction_b: Optional[DefinitionValue] = None,
                   spacing_b: Optional[float] = None,
                   count_b: Optional[int] = None,
                   reversed_a: bool = False,
                   reversed_b: bool = False,
                   end_condition_a: Optional[_LinearPatternEndCondition] = None,
                   end_condition_b: Optional[_LinearPatternEndCondition] = None,
                   deleted: Optional[list[int]] = None,
                   geometry_pattern: bool = False,
                   vary_sketch: bool = False,
                   pattern_seed_only: bool = False,
                   name: Optional[str] = None) -> LinearPattern:
    return LinearPattern(
        seeds=seeds, direction_a=direction_a,
        spacing_a=spacing_a, count_a=count_a,
        direction_b=direction_b, spacing_b=spacing_b, count_b=count_b,
        reversed_a=reversed_a, reversed_b=reversed_b,
        end_condition_a=end_condition_a,
        end_condition_b=end_condition_b,
        deleted=deleted or [],
        geometry_pattern=geometry_pattern,
        vary_sketch=vary_sketch,
        pattern_seed_only=pattern_seed_only,
        name=name,
    )


def circular_pattern(*, seeds: list[DefinitionValue], axis: DefinitionValue,
                     angle_step: Optional[float] = None,
                     total_angle: Optional[float] = None,
                     count: int,
                     direction_b: Optional[DefinitionValue] = None,
                     angle_step_b: Optional[float] = None,
                     total_angle_b: Optional[float] = None,
                     count_b: Optional[int] = None,
                     deleted: Optional[list[int]] = None,
                     reversed: bool = False,
                     geometry_pattern: bool = False,
                     vary_sketch: bool = False,
                     name: Optional[str] = None) -> CircularPattern:
    return CircularPattern(seeds=seeds, axis=axis, angle_step=angle_step,
                           total_angle=total_angle,
                           count=count, deleted=deleted or [],
                           direction_b=direction_b,
                           angle_step_b=angle_step_b,
                           total_angle_b=total_angle_b,
                           count_b=count_b,
                           reversed=reversed,
                           geometry_pattern=geometry_pattern,
                           vary_sketch=vary_sketch,
                           name=name)


__all__ = [
    "_LinearPatternSpacingAndInstances",
    "_LinearPatternUpToReference",
    "_LinearPatternEndCondition",
    "LinearPattern",
    "CircularPattern",
    "linear_pattern",
    "circular_pattern",
]
