"""Sketch-entity name aliases + the helpers used to author them.

The 8 polymorphic sketch-entity Definition flavors live in `types.py`; this
module re-exports them under the short `Sketch*` builder names the model
uses, and provides the `_to_p2` / `_to_pt` / `_pending_id` helpers + the
`_is_sketch_entity` runtime check.
"""
from __future__ import annotations

from typing import Any

from ..types import (
    Point2D,
    SketchEntityId,
    SketchPointDefinition,
    SketchLineDefinition,
    SketchCircleDefinition,
    SketchArcDefinition,
    SketchEllipseDefinition,
    SketchEllipticalArcDefinition,
    SketchParabolaDefinition,
    SketchSplineDefinition,
    _SketchEntityDefinitionBase as _SketchEntityFlavorBase,
)


# -- entity name aliases ---------------------------------------------------
#
# The model-facing API uses short names (SketchPoint / SketchLine / ...) that
# map directly to the typed Definition flavors in `types.py`. The entity IS
# the Definition — no separate wrapping step at the wire boundary.

SketchPoint = SketchPointDefinition
SketchLine = SketchLineDefinition
SketchCircle = SketchCircleDefinition
SketchArc = SketchArcDefinition
SketchEllipse = SketchEllipseDefinition
SketchEllipticalArc = SketchEllipticalArcDefinition
SketchParabola = SketchParabolaDefinition
SketchSpline = SketchSplineDefinition


# -- runtime entity-check helpers ------------------------------------------
#
# Both the flavor classes (sharing _SketchEntityDefinitionBase) and composites
# (declared in `composites.py`) need to be in scope for the isinstance check.
# We register the composite base lazily — `composites.py` calls
# `_register_composite_base()` at import time to plug it in.

_COMPOSITE_BASE: type | None = None


def _register_composite_base(cls: type) -> None:
    """Called by `composites.py` to register the `_CompositeSketchEntity`
    base so `_is_sketch_entity` sees both flavor entities and composites."""
    global _COMPOSITE_BASE
    _COMPOSITE_BASE = cls


def _is_sketch_entity(x: Any) -> bool:
    """True for either a Definition-flavor sketch entity (Line/Circle/...) or
    a composite (Rectangle/Polygon/...). Used at the wire boundary to detect
    entity refs."""
    if isinstance(x, _SketchEntityFlavorBase):
        return True
    if _COMPOSITE_BASE is not None and isinstance(x, _COMPOSITE_BASE):
        return True
    return False


# -- coord/id helpers ------------------------------------------------------

def _to_p2(p: tuple[float, float] | Point2D) -> Point2D:
    if isinstance(p, Point2D):
        return p
    x, y = p
    return Point2D(x=x, y=y)


def _to_pt(p: "tuple[float, float] | Point2D | SketchPointDefinition") -> SketchPointDefinition:
    """Wrap a tuple/Point2D as a placeholder SketchPointDefinition with a
    sentinel id. Used for segment-endpoint fields (Line.start, Circle.center,
    Parabola.apex, ...) so the wire shape carries the per-sketch SketchPoint id
    alongside the 2D coord — the plugin back-fills the real id on Add."""
    if isinstance(p, SketchPointDefinition):
        return p
    return SketchPointDefinition(id=_pending_id(), p=_to_p2(p))


def _pending_id() -> SketchEntityId:
    """Placeholder SketchEntityId for fresh-author entities. The plugin issues
    real ids on Add and back-fills via `_backfill_sketch_entity_ids`. Until
    then the entity carries a sentinel triplet (sketch_name="", entity_kind=
    "point", id=0) — the C# Add path doesn't read this on author (the (sketch,
    position) mapping is by index), and the back-fill overwrites it on
    response with the real (kind, id) the new entity got.
    """
    return SketchEntityId(sketch_name="", entity_kind="point", id=0)
