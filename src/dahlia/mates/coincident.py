from __future__ import annotations

from typing import Literal

from ._base import Alignment, _AlignedPair, _entities


class Coincident(_AlignedPair):
    type: Literal["MateCoincident"] = "MateCoincident"


def coincident(a, b, *, alignment: Alignment = "closest", pick_points=None,
               name=None, suppressed=False) -> Coincident:
    """Make compatible entities coincident, including two reference axes."""
    return Coincident(entities=_entities(a, b), alignment=alignment,
                      pick_points=pick_points, name=name, suppressed=suppressed)
