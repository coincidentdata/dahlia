"""Extrude / CutExtrude feature models + lowercase factories."""
from __future__ import annotations

from typing import Annotated, Literal, Optional

from pydantic import Field, model_validator

from ..sketch import Sketch
from ..types import BodyDefinition
from ._base import _Base, ContourRef, DefinitionValue, named
from ._end_conditions import (
    EndCondition,
    _ExtrudeStart,
    _ExtrudeStartSketch,
)


class _ExtrudeDirection(_Base):
    end_condition: EndCondition
    draft_angle: float = 0.0  # radians; negative drafts outward
    # Extrusion axis override (edge / reference axis / sketch segment). None = sketch-plane normal.
    axis: Optional[DefinitionValue] = None


# `direction1="ThroughAll"` for an end condition with no other options.
ExtrudeDirection = named(_ExtrudeDirection, "end_condition")


class _Extrude(_Base):
    # SolidWorks-assigned feature name, back-filled on the Add result.
    name: Optional[str] = None
    sketch: Sketch  # the sketch feature itself (the plugin resolves to its name)
    direction1: ExtrudeDirection
    direction2: Optional[ExtrudeDirection] = None
    reversed: bool = False
    # Explicit sketch contour / region selection. Empty list = whole sketch.
    contours: list[ContourRef] = Field(default_factory=list)
    start: _ExtrudeStart = Field(default_factory=_ExtrudeStartSketch)
    # None = AutoSelect. A list narrows the affected bodies; an empty list is a real
    # "no bodies" scope, not AutoSelect.
    feature_scope: Optional[list[BodyDefinition]] = None

    @model_validator(mode="after")
    def _mid_plane_is_one_sided(self) -> "_Extrude":
        if self.direction2 is not None and "MidPlane" in (
                self.direction1.end_condition.end_condition, self.direction2.end_condition.end_condition):
            raise ValueError("a MidPlane extrude has no second direction")
        return self


class Extrude(_Extrude):
    type: Literal["Extrude"] = "Extrude"
    merge: bool = True


class CutExtrude(_Extrude):
    type: Literal["CutExtrude"] = "CutExtrude"
    flip_side_to_cut: bool = False


def _first_direction(depth: Optional[float], direction1: object) -> object:
    if (depth is None) == (direction1 is None):
        raise ValueError("pass depth (a Blind extrude) or direction1, not both")
    return direction1 if depth is None else {"end_condition": {"end_condition": "Blind", "distance": depth}}


def extrude(*, sketch: Sketch, depth: Optional[float] = None,
            direction1: Optional[ExtrudeDirection] = None,
            direction2: Optional[ExtrudeDirection] = None,
            reversed: bool = False, merge: bool = True,
            contours: Optional[list[ContourRef]] = None,
            start: Optional[_ExtrudeStart] = None,
            feature_scope: Optional[list[BodyDefinition]] = None,
            name: Optional[str] = None) -> Extrude:
    return Extrude(
        sketch=sketch, direction1=_first_direction(depth, direction1), direction2=direction2,
        reversed=reversed, merge=merge, contours=contours or [],
        start=start or _ExtrudeStartSketch(), feature_scope=feature_scope, name=name,
    )


def cut_extrude(*, sketch: Sketch, depth: Optional[float] = None,
                direction1: Optional[ExtrudeDirection] = None,
                direction2: Optional[ExtrudeDirection] = None,
                reversed: bool = False, flip_side_to_cut: bool = False,
                contours: Optional[list[ContourRef]] = None,
                start: Optional[_ExtrudeStart] = None,
                feature_scope: Optional[list[BodyDefinition]] = None,
                name: Optional[str] = None) -> CutExtrude:
    return CutExtrude(
        sketch=sketch, direction1=_first_direction(depth, direction1), direction2=direction2,
        reversed=reversed, flip_side_to_cut=flip_side_to_cut, contours=contours or [],
        start=start or _ExtrudeStartSketch(), feature_scope=feature_scope, name=name,
    )


__all__ = ["Extrude", "CutExtrude", "ExtrudeDirection", "extrude", "cut_extrude"]
