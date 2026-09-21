from __future__ import annotations

from typing import Literal

from ._base import _PickedPair, _entities


class Perpendicular(_PickedPair):
    type: Literal["MatePerpendicular"] = "MatePerpendicular"


def perpendicular(a, b, *, pick_points=None, name=None, suppressed=False) -> Perpendicular:
    return Perpendicular(entities=_entities(a, b), pick_points=pick_points,
                         name=name, suppressed=suppressed)
