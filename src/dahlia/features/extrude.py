"""Extrude / CutExtrude feature models + lowercase factories."""
from __future__ import annotations

from typing import Literal, Optional

from pydantic import Field, model_validator

from ..sketch import Sketch
from ..types import BodyDefinition
from ._base import _Base, ContourRef, DefinitionValue
from ._end_conditions import (
    EndCondition,
    _ExtrudeStart,
    _ExtrudeStartSketch,
)


# -- Extrude ---------------------------------------------------------------

class Extrude(_Base):
    type: Literal["Extrude"] = "Extrude"
    # Inspect/Add-result only — SolidWorks-assigned feature name (e.g. "Boss-Extrude1").
    # The C# Add path back-fills this on the response so the model can reference the
    # feature later (DeleteFeature, SetRollback, FeatureDefinition lookups).
    name: Optional[str] = None
    sketch: Sketch  # the sketch feature itself (the plugin resolves to its name)
    depth: float
    reversed: bool = False
    both_directions: bool = False
    merge: bool = True
    draft_angle: float = 0.0
    # Per-direction draft. None = "same as draft_angle". Set independently when
    # both_directions is set and direction-B's draft differs.
    draft_angle_b: Optional[float] = None
    # Optional override for the extrude axis. None = sketch-plane normal.
    # Accepts an Edge / Feature (RefAxis) / SketchSegment Definition (mirrors
    # Revolve.axis dispatch).
    direction: Optional[DefinitionValue] = None
    # Direction-2 axis override (slot 2 of SetDirectionReference). None when
    # the second extrude direction shares slot-1's axis (the common case).
    # Only meaningful when both_directions is True.
    direction_b: Optional[DefinitionValue] = None
    # Explicit sketch contour / region selection. Empty list = whole sketch.
    # Mirrors Revolve.contours.
    contours: list[ContourRef] = Field(default_factory=list)
    # Extrude-start condition. Default is _ExtrudeStartSketch (sketch plane).
    start: _ExtrudeStart = Field(default_factory=_ExtrudeStartSketch)
    # End-condition discriminated unions, parallel to Sweep.twist. None on the
    # wire = use the top-level `depth` field (handler defaults to Blind).
    # When set, the discriminated shape carries the per-kind sibling fields
    # (distance / end / surface / reversed / translate_surface) and the C#
    # ExtrudeEndCondition.Translate path consumes it directly.
    end_condition: Optional[EndCondition] = None
    end_condition_b: Optional[EndCondition] = None
    # User-narrowed feature scope. None = AutoSelect (SolidWorks picks the
    # affected bodies); a list of BodyDefinitions narrows the boss merge to
    # those bodies only. An empty list is the literal "narrowed-to-no-bodies"
    # scope — the C# handler preserves it as a real explicit-scope call rather
    # than collapsing to AutoSelect.
    feature_scope: Optional[list[BodyDefinition]] = None

    @model_validator(mode="after")
    def _depth_positive(self) -> "Extrude":
        # `depth` is the Blind-extrude distance. When `end_condition` is set
        # the C# handler reads the per-kind sibling fields (distance / end /
        # surface) and ignores `depth` entirely — Inspect emits `depth=0` in
        # that case (see ExtrudeHandler `Inspect`). Only require `depth > 0`
        # when no end_condition is present.
        if self.end_condition is None and self.depth <= 0:
            raise ValueError("depth must be > 0 when end_condition is not set")
        return self

    @model_validator(mode="after")
    def _check_draft_angle_b(self) -> "Extrude":
        # draft_angle_b only makes sense when both_directions is set. Silently
        # honoring it for single-direction extrudes would write to `DraftAngle2`
        # against an unused direction — fail loudly instead.
        if self.draft_angle_b is not None and not self.both_directions:
            raise ValueError(
                "Extrude: draft_angle_b requires both_directions=True"
            )
        return self

    @model_validator(mode="after")
    def _check_end_condition_b(self) -> "Extrude":
        # `end_condition_b` only makes sense when both_directions is set; the C#
        # handler ignores it otherwise, but pydantic should reject early so the
        # error surface is clean.
        if self.end_condition_b is not None and not self.both_directions:
            raise ValueError(
                "Extrude: end_condition_b requires both_directions=True"
            )
        return self

    @model_validator(mode="after")
    def _check_direction_b(self) -> "Extrude":
        # `direction_b` (slot-2 axis override) only makes sense when
        # both_directions is set. Honoring it for single-direction extrudes
        # would write to SetDirectionReference's slot 2 against an unused
        # direction — fail loudly instead.
        if self.direction_b is not None and not self.both_directions:
            raise ValueError(
                "Extrude: direction_b requires both_directions=True"
            )
        return self


# -- CutExtrude ------------------------------------------------------------

class CutExtrude(_Base):
    type: Literal["CutExtrude"] = "CutExtrude"
    # Inspect/Add-result only — SolidWorks-assigned feature name. See Extrude.name.
    name: Optional[str] = None
    sketch: Sketch
    depth: float
    reversed: bool = False
    both_directions: bool = False
    flip_side_to_cut: bool = False
    draft_angle: float = 0.0
    # Per-direction draft. None = "same as draft_angle".
    draft_angle_b: Optional[float] = None
    # Optional override for the extrude axis. None = sketch-plane normal.
    direction: Optional[DefinitionValue] = None
    # Direction-2 axis override (slot 2). None when slot-2 shares slot-1's axis.
    # Only meaningful when both_directions is True.
    direction_b: Optional[DefinitionValue] = None
    # Explicit sketch contour / region selection. Empty list = whole sketch.
    contours: list[ContourRef] = Field(default_factory=list)
    # Extrude-start condition. Default is _ExtrudeStartSketch.
    start: _ExtrudeStart = Field(default_factory=_ExtrudeStartSketch)
    # End-condition discriminated unions; see Extrude.end_condition. None on
    # the wire = use the top-level `depth` field (handler defaults to Blind).
    end_condition: Optional[EndCondition] = None
    end_condition_b: Optional[EndCondition] = None
    # User-narrowed feature scope. None = AutoSelect; a list narrows the cut
    # to those bodies only. Empty list = literal "narrowed-to-no-bodies"
    # scope (the C# handler preserves it, no auto-fallback).
    feature_scope: Optional[list[BodyDefinition]] = None

    @model_validator(mode="after")
    def _depth_positive(self) -> "CutExtrude":
        # See Extrude._depth_positive — `depth` is irrelevant when
        # `end_condition` carries the real distance/surface payload.
        if self.end_condition is None and self.depth <= 0:
            raise ValueError("depth must be > 0 when end_condition is not set")
        return self

    @model_validator(mode="after")
    def _check_draft_angle_b(self) -> "CutExtrude":
        # draft_angle_b only makes sense when both_directions is set.
        if self.draft_angle_b is not None and not self.both_directions:
            raise ValueError(
                "CutExtrude: draft_angle_b requires both_directions=True"
            )
        return self

    @model_validator(mode="after")
    def _check_end_condition_b(self) -> "CutExtrude":
        # `end_condition_b` only makes sense when both_directions is set.
        if self.end_condition_b is not None and not self.both_directions:
            raise ValueError(
                "CutExtrude: end_condition_b requires both_directions=True"
            )
        return self

    @model_validator(mode="after")
    def _check_direction_b(self) -> "CutExtrude":
        # `direction_b` (slot-2 axis override) only makes sense when
        # both_directions is set. Mirrors Extrude._check_direction_b.
        if self.direction_b is not None and not self.both_directions:
            raise ValueError(
                "CutExtrude: direction_b requires both_directions=True"
            )
        return self


# -- factories -------------------------------------------------------------

def extrude(*, sketch: Sketch, depth: float = 0.0, reversed: bool = False,
            both_directions: bool = False, merge: bool = True,
            draft_angle: float = 0.0,
            draft_angle_b: Optional[float] = None,
            direction: Optional[DefinitionValue] = None,
            direction_b: Optional[DefinitionValue] = None,
            contours: Optional[list[ContourRef]] = None,
            start: Optional[_ExtrudeStart] = None,
            end_condition: Optional[EndCondition] = None,
            end_condition_b: Optional[EndCondition] = None,
            feature_scope: Optional[list[BodyDefinition]] = None,
            name: Optional[str] = None) -> Extrude:
    return Extrude(
        sketch=sketch, depth=depth, reversed=reversed,
        both_directions=both_directions, merge=merge,
        draft_angle=draft_angle, draft_angle_b=draft_angle_b,
        direction=direction, direction_b=direction_b,
        contours=contours or [],
        start=start or _ExtrudeStartSketch(),
        end_condition=end_condition,
        end_condition_b=end_condition_b,
        feature_scope=feature_scope,
        name=name,
    )


def cut_extrude(*, sketch: Sketch, depth: float = 0.0, reversed: bool = False,
                both_directions: bool = False, flip_side_to_cut: bool = False,
                draft_angle: float = 0.0,
                draft_angle_b: Optional[float] = None,
                direction: Optional[DefinitionValue] = None,
                direction_b: Optional[DefinitionValue] = None,
                contours: Optional[list[ContourRef]] = None,
                start: Optional[_ExtrudeStart] = None,
                end_condition: Optional[EndCondition] = None,
                end_condition_b: Optional[EndCondition] = None,
                feature_scope: Optional[list[BodyDefinition]] = None,
                name: Optional[str] = None) -> CutExtrude:
    return CutExtrude(
        sketch=sketch, depth=depth, reversed=reversed,
        both_directions=both_directions,
        flip_side_to_cut=flip_side_to_cut, draft_angle=draft_angle,
        draft_angle_b=draft_angle_b, direction=direction,
        direction_b=direction_b,
        contours=contours or [],
        start=start or _ExtrudeStartSketch(),
        end_condition=end_condition,
        end_condition_b=end_condition_b,
        feature_scope=feature_scope,
        name=name,
    )


__all__ = ["Extrude", "CutExtrude", "extrude", "cut_extrude"]
