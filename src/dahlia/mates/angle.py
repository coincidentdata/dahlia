from __future__ import annotations

from typing import Literal

from pydantic import Field, model_validator

from ._base import Alignment, _DimensionPair, _Reference, _entities


class Angle(_DimensionPair):
    type: Literal["MateAngle"] = "MateAngle"
    angle: float = Field(ge=0, allow_inf_nan=False)
    reference: _Reference | None = None

    @model_validator(mode="after")
    def _limits(self):
        return self._check_limits(self.angle)


def angle(a, b, *, angle: float, limits=None, alignment: Alignment = "closest",
          flipped=False, reference=None, name=None, suppressed=False) -> Angle:
    return Angle(entities=_entities(a, b), angle=angle, limits=limits,
                 alignment=alignment, flipped=flipped, reference=reference,
                 name=name, suppressed=suppressed)
