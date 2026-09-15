from __future__ import annotations

import math
from typing import Literal

from pydantic import Field, field_validator, model_validator

from ..helpers import feature_ref
from ._base import DefinitionValue, _Base


class DraftEdge(_Base):
    edge: DefinitionValue
    other_face: bool = False


def draft_edge(edge: DefinitionValue, other_face: bool = False) -> DraftEdge:
    return DraftEdge(edge=edge, other_face=other_face)


DraftKind = Literal["NeutralPlane", "PartingLine", "Step"]
DraftPropagation = Literal["None", "Tangent", "AllLoops", "InnerLoops", "OuterLoops"]
DraftStep = Literal["Tapered", "Perpendicular"]


class Draft(_Base):
    type: Literal["Draft"] = "Draft"
    name: str | None = None
    kind: DraftKind = "NeutralPlane"
    angle: float = Field(gt=0, lt=math.pi / 2, allow_inf_nan=False)
    neutral_plane: DefinitionValue | None = None
    faces: list[DefinitionValue] = Field(default_factory=list)
    direction: DefinitionValue | list[DefinitionValue] | None = None
    parting_lines: list[DraftEdge] = Field(default_factory=list)
    reversed: bool = False
    propagation: DraftPropagation = "None"
    step_type: DraftStep = "Tapered"
    allow_reduced_angle: bool = False

    @field_validator("neutral_plane", "direction", mode="before")
    @classmethod
    def _references(cls, value):
        return feature_ref(value) if isinstance(value, str) else value

    @model_validator(mode="after")
    def _options(self):
        if self.kind == "NeutralPlane":
            if self.neutral_plane is None or not self.faces:
                raise ValueError("NeutralPlane draft requires neutral_plane and faces")
            if self.direction is not None or self.parting_lines:
                raise ValueError(
                    "direction and parting_lines require PartingLine or Step draft"
                )
        else:
            if self.direction is None or not self.parting_lines:
                raise ValueError(
                    "PartingLine and Step draft require direction and parting_lines"
                )
            if self.neutral_plane is not None or self.faces:
                raise ValueError("neutral_plane and faces require NeutralPlane draft")
        if isinstance(self.direction, list) and len(self.direction) != 2:
            raise ValueError("a direction list must contain exactly two vertices")
        if self.kind != "Step" and self.step_type != "Tapered":
            raise ValueError("step_type requires Step draft")
        if self.kind != "PartingLine" and self.allow_reduced_angle:
            raise ValueError("allow_reduced_angle requires PartingLine draft")
        return self


def draft(
    *,
    angle: float,
    kind: DraftKind = "NeutralPlane",
    neutral_plane: DefinitionValue | None = None,
    faces: list[DefinitionValue] | None = None,
    direction: DefinitionValue | list[DefinitionValue] | None = None,
    parting_lines: list[DraftEdge | dict] | None = None,
    reversed: bool = False,
    propagation: DraftPropagation = "None",
    step_type: DraftStep = "Tapered",
    allow_reduced_angle: bool = False,
    name: str | None = None,
) -> Draft:
    return Draft(
        angle=angle,
        kind=kind,
        neutral_plane=neutral_plane,
        faces=faces or [],
        direction=direction,
        parting_lines=parting_lines or [],
        reversed=reversed,
        propagation=propagation,
        step_type=step_type,
        allow_reduced_angle=allow_reduced_angle,
        name=name,
    )
