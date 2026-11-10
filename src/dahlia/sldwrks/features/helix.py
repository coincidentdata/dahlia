"""Helix feature model + lowercase factory."""
from __future__ import annotations

from typing import Annotated, Literal, Optional, Union

from pydantic import Field, model_validator

from ..sketch import Sketch
from ..types import Echo
from ._base import _Base


# A helix is built from an axis sketch (one circle defines the axis + start radius) plus the
# two values its `defined_by` mode names; SolidWorks computes the third.

class _HelixPitchAndRevolution(_Base):
    defined_by: Literal["PitchAndRevolution"] = "PitchAndRevolution"
    pitch: float = Field(gt=0)        # meters
    revolutions: float = Field(gt=0)  # turns


class _HelixHeightAndRevolution(_Base):
    defined_by: Literal["HeightAndRevolution"] = "HeightAndRevolution"
    height: float = Field(gt=0)
    revolutions: float = Field(gt=0)


class _HelixHeightAndPitch(_Base):
    defined_by: Literal["HeightAndPitch"] = "HeightAndPitch"
    height: float = Field(gt=0)
    pitch: float = Field(gt=0)


class _HelixSpiral(_Base):
    defined_by: Literal["Spiral"] = "Spiral"
    pitch: float = Field(gt=0)
    revolutions: float = Field(gt=0)


HelixDefinition = Annotated[
    Union[_HelixPitchAndRevolution, _HelixHeightAndRevolution, _HelixHeightAndPitch, _HelixSpiral],
    Field(discriminator="defined_by"),
]


class _HelixTaper(_Base):
    angle: float = Field(gt=0)  # radians
    outward: bool = False


class Helix(_Base):
    type: Literal["Helix"] = "Helix"
    # SolidWorks-assigned feature name, back-filled on the Add result.
    name: Optional[str] = None
    sketch: Sketch  # axis sketch — exactly one circle, on the plane normal to the axis
    defined_by: HelixDefinition
    start_angle: float = 0.0     # radians, where the helix begins
    clockwise: bool = False
    reversed: bool = False
    taper: Optional[_HelixTaper] = None

    # 4×3 matrix (X, Y, Z=normal, origin) from the axis sketch's ModelToSketchTransform; the
    # axis sketch is face-authorable, so it carries the same rebuild-drift risk as Sketch.
    echo_computed_axes: Optional[list[list[float]]] = Echo(default=None)

    @model_validator(mode="after")
    def _spiral_is_flat(self) -> "Helix":
        if self.defined_by.defined_by == "Spiral" and self.taper is not None:
            raise ValueError("a spiral has no taper")
        return self


def helix(*, sketch: Sketch, defined_by: HelixDefinition, start_angle: float = 0.0,
          clockwise: bool = False, reversed: bool = False, taper: Optional[_HelixTaper] = None,
          name: Optional[str] = None) -> Helix:
    return Helix(sketch=sketch, defined_by=defined_by, start_angle=start_angle, clockwise=clockwise,
                 reversed=reversed, taper=taper, name=name)


__all__ = ["Helix", "HelixDefinition", "helix"]
