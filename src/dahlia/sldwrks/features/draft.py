from __future__ import annotations

import math
from typing import Annotated, Literal, Union

from pydantic import Field, TypeAdapter, field_validator

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


class _Draft(_Base):
    type: Literal["Draft"] = "Draft"
    name: str | None = None
    angle: float = Field(gt=0, lt=math.pi / 2, allow_inf_nan=False)
    reversed: bool = False
    propagation: DraftPropagation = "None"


class NeutralPlaneDraft(_Draft):
    kind: Literal["NeutralPlane"] = "NeutralPlane"
    neutral_plane: DefinitionValue
    faces: list[DefinitionValue] = Field(min_length=1)

    @field_validator("neutral_plane", mode="before")
    @classmethod
    def _reference(cls, value):
        return feature_ref(value) if isinstance(value, str) else value


class _PullDraft(_Draft):
    # The pull direction: one reference, or two vertices.
    direction: DefinitionValue | list[DefinitionValue]
    parting_lines: list[DraftEdge] = Field(min_length=1)

    @field_validator("direction", mode="before")
    @classmethod
    def _reference(cls, value):
        return feature_ref(value) if isinstance(value, str) else value

    @field_validator("direction")
    @classmethod
    def _vertex_pair(cls, value):
        if isinstance(value, list) and len(value) != 2:
            raise ValueError("a direction list must contain exactly two vertices")
        return value


class PartingLineDraft(_PullDraft):
    kind: Literal["PartingLine"] = "PartingLine"
    allow_reduced_angle: bool = False


class StepDraft(_PullDraft):
    kind: Literal["Step"] = "Step"
    step_type: DraftStep = "Tapered"


Draft = Annotated[Union[NeutralPlaneDraft, PartingLineDraft, StepDraft], Field(discriminator="kind")]
_DRAFT = TypeAdapter(Draft)


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
    step_type: DraftStep | None = None,
    allow_reduced_angle: bool | None = None,
    name: str | None = None,
) -> Draft:
    """A draft of `kind`; pass only that kind's options (NeutralPlane: neutral_plane + faces;
    PartingLine: direction + parting_lines + allow_reduced_angle; Step: direction + parting_lines
    + step_type)."""
    options = {"neutral_plane": neutral_plane, "faces": faces, "direction": direction, "parting_lines": parting_lines,
               "step_type": step_type, "allow_reduced_angle": allow_reduced_angle}
    return _DRAFT.validate_python({"kind": kind, "angle": angle, "reversed": reversed, "propagation": propagation,
                                   "name": name, **{k: v for k, v in options.items() if v is not None}})
