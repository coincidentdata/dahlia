"""RefPlane / RefAxis (reference geometry) feature models + lowercase factories."""
from __future__ import annotations

import math
from typing import Literal, Optional

from pydantic import field_validator, model_validator

from ._base import _Base, DefinitionValue, Echo, In


# -- RefPlane / RefAxis (reference geometry) -------------------------------
#
# Reference planes and axes are FeatureManager features in their own right.
# Default planes (Top/Front/Right) and the default Origin already exist on
# every part — these factories build *new* references. Once created, the
# plugin returns the feature in the standard `FeatureDefinition` shape and the
# model uses it like any other plane/axis reference.

# "Tangent" is a real RefPlane constraint kind — bit 5 in the InsertRefPlane
# constraint int (see solidworks-quirks.md "InsertRefPlane packs constraint
# kind + flip flags").
RefPlaneConstraint = Literal[
    "Coincident", "Parallel", "Perpendicular",
    "Distance", "Angle", "Tangent", "MidPlane", "ProjectOnto",
]


# Per-reference modifier bits the InsertRefPlane constraint int can carry
# alongside the kind. Bit 9 = OriginOnCurve, bit 11 = ProjectToNearestLocation.
# Per slot a model can set neither, one, or both. See solidworks-quirks.md
# "InsertRefPlane packs constraint kind + flip flags" for the full bit layout.
RefPlaneModifier = Literal["OriginOnCurve", "ProjectToNearestLocation"]


# SolidWorks distinguishes RefPlane variants via `IRefPlaneFeatureData.Type2`
# (a `swRefPlaneType_e` int). The constraint-array form (`ConstraintBase`) is
# the only one `FeatureManager.InsertRefPlane` builds; the other subtypes are
# specialized creation paths preserved for round-trip fidelity on Inspect.
# The wire literal mirrors the SolidWorks enum names verbatim so the
# discriminator round-trips through the C# handler.
#
# Authoring constraint:
# - `ConstraintBase` is the only kind that can be CREATED via this handler.
# - Other subtypes (LinePoint / ThreePoint / LineLine / Distance / Parallel /
#   Angle / Normal / OnSurface / SWStandard) round-trip on Inspect only.
#   `Add` throws when the subtype is not `ConstraintBase`. SWStandard is the
#   default-plane subtype (Top/Front/Right Plane) and is never the Add path —
#   default planes already exist on every part.
RefPlaneSubtype = Literal[
    "ConstraintBase",
    "LinePoint",
    "ThreePoint",
    "LineLine",
    "Distance",
    "Parallel",
    "Angle",
    "Normal",
    "OnSurface",
    "SWStandard",
]


class RefPlane(_Base):
    type: Literal["RefPlane"] = "RefPlane"
    # Subtype discriminator. Inspect emits whatever Type2 the SW data exposes;
    # Add throws on anything other than ConstraintBase because `InsertRefPlane`
    # only builds the constraint-array form.
    subtype: RefPlaneSubtype = "ConstraintBase"
    # Empty by default for non-constraint-base subtypes. ConstraintBase requires
    # at least one entry; the validator enforces.
    references: list[DefinitionValue] = []
    constraints: list[RefPlaneConstraint] = []          # parallel array; same length as references
    # Per-reference modifiers. Outer list parallel to references, inner list a
    # slot's modifiers (a slot can carry neither, one, or both). None means no
    # modifiers anywhere. Maps to constraint-int bits 9 / 11.
    modifiers: Optional[list[list[RefPlaneModifier]]] = None
    distance: Optional[float] = None                   # required when any constraint is "Distance"
    angle: Optional[float] = None                      # radians; required when any constraint is "Angle"
    # `expected_normal` (a quantized unit direction) is the model-facing,
    # low-cardinality orientation input — it IS diffed. A from-scratch model can
    # state it alone; the Add reverses the plane iff the live normal faces away.
    expected_normal: Optional[list[float]] = In(default=None)
    # --- informational replay inputs (role="echo": NOT diffed) ---------------
    # These three carry the full captured frame so a recorded transcript replays
    # deterministically: when `expected_axes` is present the Add brute-forces the
    # (per-reference flip × flip_normal) combos until the live axes match it,
    # origin + normal WITH SIGN. That canonicalizes the frame across rebuilds so
    # downstream bodies don't mirror. They are emitted + consumed by the Add but
    # NEVER compared (their in-plane basis / chosen combo drift build-to-build),
    # and a from-scratch model may omit them entirely (falls back to expected_normal).
    flips: Optional[list[bool]] = Echo(default=None)
    expected_axes: Optional[list[list[float]]] = Echo(default=None)
    flip_normal: bool = Echo(default=False)
    name: Optional[str] = None

    @field_validator("references")
    @classmethod
    def _references_in_range(cls, v: list[DefinitionValue]) -> list[DefinitionValue]:
        # 0 is allowed for non-constraint-base subtypes (validated downstream by
        # `_check_constraint_alignment` once the subtype is known). ConstraintBase
        # plus references-out-of-1..3 is rejected there.
        if len(v) > 3:
            raise ValueError("RefPlane: at most 3 references")
        return v

    @field_validator("angle")
    @classmethod
    def _angle_range(cls, v: Optional[float]) -> Optional[float]:
        # SolidWorks accepts (-2π, 2π) but values outside (0, π) are nearly always
        # an authoring bug (forgetting * DEG). The C# handler will pass anything
        # through; the bound here is the user-facing guard.
        if v is None:
            return v
        if not -2 * math.pi < v < 2 * math.pi:
            raise ValueError("RefPlane: angle must be between -2π and 2π radians")
        return v

    @field_validator("expected_axes")
    @classmethod
    def _expected_axes_shape(cls, v: Optional[list[list[float]]]) -> Optional[list[list[float]]]:
        # `expected_axes` is a 4×3 matrix with row layout (X, Y, Z, origin).
        # Informational replay input; validate shape so a malformed frame surfaces early.
        if v is None:
            return v
        if len(v) != 4:
            raise ValueError("RefPlane: expected_axes must have exactly 4 rows (X, Y, Z, origin)")
        for i, row in enumerate(v):
            if len(row) != 3:
                raise ValueError(
                    f"RefPlane: expected_axes[{i}] must have exactly 3 entries; got {len(row)}")
        return v

    @field_validator("expected_normal")
    @classmethod
    def _expected_normal_shape(cls, v: Optional[list[float]]) -> Optional[list[float]]:
        # A direction (3 components). Need not be normalized — the Add only takes
        # its sign against the live plane normal — but it must be exactly 3 long.
        if v is None:
            return v
        if len(v) != 3:
            raise ValueError(f"RefPlane: expected_normal must have exactly 3 entries; got {len(v)}")
        return v

    @model_validator(mode="after")
    def _check_constraint_alignment(self) -> "RefPlane":
        # ConstraintBase is the only subtype with a fully-defined Add-path
        # invariant. The other subtypes are Inspect-only — their references and
        # constraints arrays may be empty / partially populated depending on what
        # Type2 the IRefPlaneFeatureData reports.
        if self.subtype == "ConstraintBase":
            if not 1 <= len(self.references) <= 3:
                raise ValueError(
                    "RefPlane[ConstraintBase]: references must have 1 to 3 entries")
            if len(self.constraints) != len(self.references):
                raise ValueError(
                    "RefPlane[ConstraintBase]: constraints length must match references length")
            if "Distance" in self.constraints and self.distance is None:
                raise ValueError(
                    "RefPlane[ConstraintBase]: 'distance' is required when any constraint is 'Distance'")
            if "Angle" in self.constraints and self.angle is None:
                raise ValueError(
                    "RefPlane[ConstraintBase]: 'angle' is required when any constraint is 'Angle'")
            if self.flips is not None and len(self.flips) != len(self.references):
                raise ValueError(
                    "RefPlane[ConstraintBase]: flips length must match references length")
            if self.modifiers is not None and len(self.modifiers) != len(self.references):
                raise ValueError(
                    "RefPlane[ConstraintBase]: modifiers length must match references length")
        else:
            # Non-constraint-base subtypes — Inspect-only. Reject the constraint-shape
            # fields with a clear cause so a model trying to author them gets the
            # right error early (rather than at the C# wire boundary).
            if self.constraints:
                raise ValueError(
                    f"RefPlane[{self.subtype}]: constraints are only valid on the ConstraintBase subtype")
            if self.flips is not None:
                raise ValueError(
                    f"RefPlane[{self.subtype}]: flips are only valid on the ConstraintBase subtype")
            if self.modifiers is not None:
                raise ValueError(
                    f"RefPlane[{self.subtype}]: modifiers are only valid on the ConstraintBase subtype")
        return self


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

def ref_plane(*, references: list[DefinitionValue],
              constraints: list[str],
              expected_normal: Optional[list[float]] = None,
              flips: Optional[list[bool]] = None,
              modifiers: Optional[list[list[str]]] = None,
              distance: Optional[float] = None,
              angle: Optional[float] = None,
              flip_normal: bool = False,
              expected_axes: Optional[list[list[float]]] = None,
              subtype: str = "ConstraintBase",
              name: Optional[str] = None) -> RefPlane:
    # `expected_normal` is the model-facing orientation input; `flips` /
    # `flip_normal` / `expected_axes` are informational replay inputs a recorded
    # transcript carries (a from-scratch caller passes just expected_normal).
    return RefPlane(subtype=subtype,  # type: ignore[arg-type]
                    references=references,
                    constraints=constraints,  # type: ignore[arg-type]
                    expected_normal=expected_normal,
                    flips=flips,
                    modifiers=modifiers,  # type: ignore[arg-type]
                    distance=distance, angle=angle,
                    flip_normal=flip_normal,
                    expected_axes=expected_axes, name=name)


def ref_axis(*, references: list[DefinitionValue],
             axis_type: str,
             name: Optional[str] = None) -> RefAxis:
    return RefAxis(references=references,
                   axis_type=axis_type,  # type: ignore[arg-type]
                   name=name)


__all__ = [
    "RefPlaneConstraint",
    "RefPlaneModifier",
    "RefPlaneSubtype",
    "RefPlane",
    "RefAxis",
    "ref_plane",
    "ref_axis",
]
