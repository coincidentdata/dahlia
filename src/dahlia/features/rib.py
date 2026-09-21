from __future__ import annotations

import math
from typing import Literal

from pydantic import Field, model_validator

from ..sketch import Sketch
from ._base import DefinitionValue, _Base

RibDirection = Literal["ParallelToSketch", "NormalToSketch"]
RibExtension = Literal["Linear", "Natural"]


class Rib(_Base):
    type: Literal["Rib"] = "Rib"
    name: str | None = None
    sketch: Sketch
    thickness: float = Field(gt=0, allow_inf_nan=False)
    two_sided: bool = True
    reverse_thickness: bool = False
    flipped: bool = False
    direction: RibDirection = "ParallelToSketch"
    extension: RibExtension = "Linear"
    reference_segment: int = Field(default=0, ge=0)
    draft_angle: float = Field(default=0, ge=0, lt=math.pi / 2, allow_inf_nan=False)
    draft_outward: bool = False
    draft_from_wall: bool = False
    body: DefinitionValue | None = None

    @model_validator(mode="after")
    def _options(self):
        if self.two_sided and self.reverse_thickness:
            raise ValueError("reverse_thickness requires a single-sided rib")
        if self.direction == "ParallelToSketch" and self.extension != "Linear":
            raise ValueError("Natural extension requires NormalToSketch")
        if not self.draft_angle and (self.draft_outward or self.draft_from_wall):
            raise ValueError("draft options require a positive draft_angle")
        return self


def rib(
    *,
    sketch: Sketch,
    thickness: float,
    two_sided: bool = True,
    reverse_thickness: bool = False,
    flipped: bool = False,
    direction: RibDirection = "ParallelToSketch",
    extension: RibExtension = "Linear",
    reference_segment: int = 0,
    draft_angle: float = 0,
    draft_outward: bool = False,
    draft_from_wall: bool = False,
    body: DefinitionValue | None = None,
    name: str | None = None,
) -> Rib:
    return Rib(
        sketch=sketch,
        thickness=thickness,
        two_sided=two_sided,
        reverse_thickness=reverse_thickness,
        flipped=flipped,
        direction=direction,
        extension=extension,
        reference_segment=reference_segment,
        draft_angle=draft_angle,
        draft_outward=draft_outward,
        draft_from_wall=draft_from_wall,
        body=body,
        name=name,
    )
