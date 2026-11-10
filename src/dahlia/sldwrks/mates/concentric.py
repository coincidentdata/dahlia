from __future__ import annotations

from typing import Literal

from ._base import Alignment, _AlignedPair, _entities


class Concentric(_AlignedPair):
    type: Literal["MateConcentric"] = "MateConcentric"
    lock_rotation: bool = False


def concentric(a, b, *, alignment: Alignment = "closest", lock_rotation=False,
               pick_points=None, name=None, suppressed=False) -> Concentric:
    """Align centers or axes; use coincident for two straight reference axes."""
    return Concentric(entities=_entities(a, b), alignment=alignment,
                      lock_rotation=lock_rotation, pick_points=pick_points,
                      name=name, suppressed=suppressed)
