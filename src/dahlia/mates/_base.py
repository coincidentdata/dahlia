from __future__ import annotations

import math
from typing import Annotated, Literal

from pydantic import BeforeValidator, field_validator

from ..assembly import Component
from ..features._base import DefinitionValue
from ..helpers import feature_ref
from ..types import Echo, Point3D, _Base

Alignment = Literal["aligned", "opposed", "closest"]
_Reference = Annotated[DefinitionValue, BeforeValidator(
    lambda value: feature_ref(value) if isinstance(value, str) else value)]


class Mate(_Base):
    type: str
    name: str | None = None
    suppressed: bool = False
    echo_status: str | None = Echo()
    echo_dimension: str | None = Echo()


class _Pair(Mate):
    entities: tuple[_Reference, _Reference]


class _PickedPair(_Pair):
    pick_points: tuple[Point3D, Point3D] | None = None

    @field_validator("pick_points")
    @classmethod
    def _finite_points(cls, points):
        if points is not None and any(not math.isfinite(value)
                for point in points for value in (point.x, point.y, point.z)):
            raise ValueError("pick_points must contain finite coordinates")
        return points


class _AlignedPair(_PickedPair):
    alignment: Alignment = "closest"


class _DimensionPair(_Pair):
    alignment: Alignment = "closest"
    flipped: bool = False
    limits: tuple[float, float] | None = None

    def _check_limits(self, value):
        if self.limits is not None:
            low, high = self.limits
            if not all(math.isfinite(v) for v in self.limits) or not 0 <= low <= value <= high:
                raise ValueError("limits must be finite, nonnegative, and contain the initial value")
        return self


def _entities(a, b):
    return tuple(value.ref() if isinstance(value, Component) else value for value in (a, b))
