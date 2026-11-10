from __future__ import annotations

from typing import Literal

from ._base import Alignment, _AlignedPair, _entities


class Tangent(_AlignedPair):
    type: Literal["MateTangent"] = "MateTangent"


def tangent(a, b, *, alignment: Alignment = "closest", pick_points=None,
            name=None, suppressed=False) -> Tangent:
    return Tangent(entities=_entities(a, b), alignment=alignment,
                   pick_points=pick_points, name=name, suppressed=suppressed)
