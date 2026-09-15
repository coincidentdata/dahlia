from __future__ import annotations

from typing import Literal

from ._base import Alignment, _AlignedPair, _entities


class Parallel(_AlignedPair):
    type: Literal["MateParallel"] = "MateParallel"


def parallel(a, b, *, alignment: Alignment = "closest", pick_points=None,
             name=None, suppressed=False) -> Parallel:
    return Parallel(entities=_entities(a, b), alignment=alignment,
                    pick_points=pick_points, name=name, suppressed=suppressed)
