from __future__ import annotations

from typing import Literal

from pydantic import Field, model_validator

from ._base import Alignment, _DimensionPair, _entities


class Distance(_DimensionPair):
    type: Literal["MateDistance"] = "MateDistance"
    distance: float = Field(ge=0, allow_inf_nan=False)

    @model_validator(mode="after")
    def _limits(self):
        return self._check_limits(self.distance)


def distance(a, b, *, distance: float, limits=None, alignment: Alignment = "closest",
             flipped=False, name=None, suppressed=False) -> Distance:
    return Distance(entities=_entities(a, b), distance=distance, limits=limits,
                    alignment=alignment, flipped=flipped, name=name, suppressed=suppressed)
