from __future__ import annotations

from typing import Literal

from ._base import _Pair, _entities


class Lock(_Pair):
    type: Literal["MateLock"] = "MateLock"


def lock(a, b, *, name=None, suppressed=False) -> Lock:
    return Lock(entities=_entities(a, b), name=name, suppressed=suppressed)
