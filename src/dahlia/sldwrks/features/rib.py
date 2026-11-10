from __future__ import annotations

import math
from typing import Annotated, Literal, Optional, Union

from pydantic import Field

from ..sketch import Sketch
from ._base import DefinitionValue, _Base, named


class _RibTwoSided(_Base):
    sides: Literal["TwoSided"] = "TwoSided"


class _RibOneSided(_Base):
    sides: Literal["OneSided"] = "OneSided"
    reverse: bool = False  # thicken to the other side of the sketch


class _RibParallel(_Base):
    direction: Literal["ParallelToSketch"] = "ParallelToSketch"


class _RibNormal(_Base):
    direction: Literal["NormalToSketch"] = "NormalToSketch"
    extension: Literal["Linear", "Natural"] = "Linear"


class _RibDraft(_Base):
    angle: float = Field(gt=0, lt=math.pi / 2, allow_inf_nan=False)
    outward: bool = False
    from_wall: bool = False


RibSides = named(Annotated[Union[_RibTwoSided, _RibOneSided], Field(discriminator="sides")], "sides")
RibDirection = named(Annotated[Union[_RibParallel, _RibNormal], Field(discriminator="direction")], "direction")


class Rib(_Base):
    type: Literal["Rib"] = "Rib"
    name: Optional[str] = None
    sketch: Sketch
    thickness: float = Field(gt=0, allow_inf_nan=False)
    sides: RibSides = Field(default_factory=_RibTwoSided)
    flipped: bool = False
    direction: RibDirection = Field(default_factory=_RibParallel)
    reference_segment: int = Field(default=0, ge=0)
    draft: Optional[_RibDraft] = None
    body: Optional[DefinitionValue] = None


def rib(*, sketch: Sketch, thickness: float, sides: RibSides = "TwoSided", flipped: bool = False,
        direction: RibDirection = "ParallelToSketch", reference_segment: int = 0,
        draft: Optional[_RibDraft] = None, body: Optional[DefinitionValue] = None,
        name: Optional[str] = None) -> Rib:
    return Rib(sketch=sketch, thickness=thickness, sides=sides, flipped=flipped, direction=direction,
               reference_segment=reference_segment, draft=draft, body=body, name=name)


__all__ = ["Rib", "RibSides", "RibDirection", "rib"]
