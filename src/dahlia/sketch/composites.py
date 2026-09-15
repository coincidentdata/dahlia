"""Composite sketch entities.

Composites (Rectangle, Polygon, LinearPattern, CircularPattern, OffsetEntity)
are NOT subclasses of any Definition flavor — they're sketch-feature children
with no single live (entity_kind, id) handle. Reconstructing them from SW's
hidden Patterned/OffsetEdge/SketchOffset relations on Inspect is not yet
wired.

The classes below let the model author them (via `s.add_rectangle` etc.) but
they DON'T round-trip — the C# SketchHandler only dispatches the 8 polymorphic
SketchEntityDefinition flavors on Add. The wire shape uses a `type`
discriminator (since they're not Definition flavors).
"""
from __future__ import annotations

from typing import Any, Literal, Optional

from pydantic import Field, field_validator

from ..types import (
    _Base,
    Definition,
    Echo,
    Point2D,
    SketchEntityId,
    SketchLineDefinition,
    SketchCircleDefinition,
)
from .entities import _register_composite_base


class _CompositeSketchEntity(_Base):
    """Base for composites that ride alongside the 8 entity Definition
    flavors but aren't themselves Definitions. Type-discriminated (not
    kind-discriminated) — they're sketch-feature children, not Definitions."""
    name: Optional[str] = None
    construction: bool = False


# Wire the composite base into the runtime entity-check helper so
# `_is_sketch_entity` returns True for composites too.
_register_composite_base(_CompositeSketchEntity)


class SketchRectangle(_CompositeSketchEntity):
    """Axis-aligned rectangle expressed by two opposite corners.

    On inspect the plugin expands this into four lines; on add the summary
    form is kept.
    """
    type: Literal["Rectangle"] = "Rectangle"
    p1: Point2D
    p2: Point2D


class SketchPolygon(_CompositeSketchEntity):
    """Regular polygon, defined by `center + first_vertex + sides + inscribed`.

    `first_vertex` is one polygon CORNER; together with `center` it fixes both
    size (|first_vertex − center|) and rotation, and is passed straight to
    SolidWorks's `CreatePolygon` reference point. `inscribed` only selects the
    construction circle (inscribed/tangent-to-edges vs circumscribed/through-
    vertices) — it does NOT move `first_vertex`. The old redundant `radius`
    scalar is gone.

    Inspect additionally fills `echo_edges` (constituent line segments) and
    `echo_circle` (the construction circle).
    """
    type: Literal["Polygon"] = "Polygon"
    center: Point2D
    first_vertex: Point2D
    sides: int
    inscribed: bool = True
    # Inspect-only echo: SW-expanded constituent line segments + construction
    # circle. Empty on input; the C# composite parser fills them on read.
    echo_edges: list[SketchLineDefinition] = Echo(default_factory=list)
    echo_circle: Optional[SketchCircleDefinition] = Echo(default=None)

    @field_validator("sides")
    @classmethod
    def _sides_min(cls, v: int) -> int:
        if v < 3:
            raise ValueError("polygon must have >= 3 sides")
        return v


class SketchLinearPattern(_CompositeSketchEntity):
    """Sketch linear pattern. Mirrors C# `SketchLinearPattern` (composite, type-
    discriminated). Patterns each seed entity into a 1D or 2D grid via SW's
    `SketchManager.CreateLinearSketchStepAndRepeat`.

    Wire shape:

    - ``seeds``: SketchEntityIds of the entities to pattern. Each seed gets
      ``count_a * count_b - 1`` instance copies (the seed itself sits at grid
      position (0, 0) and is not counted as an instance).
    - ``entities``: 2D list of typed sketch-entity Definitions; outer index is
      per seed (same order as ``seeds``), inner index walks the grid in
      SW's column-major order (a-direction varies fastest). Empty on input;
      Inspect populates with the target's instance entities.
    - ``deleted``: parallel 2D bool list. ``True`` means the user deleted that
      specific instance from the pattern; the entity at that slot is a
      synthesized def (seed translated to the grid position) rather than a
      live captured instance.
    - ``angle_a / spacing_a / count_a``: primary direction (radians + meters
      + count). Matches SW's CreateLinearSketchStepAndRepeat arg shape.
    - ``angle_b / spacing_b / count_b``: secondary direction. For a 1D pattern
      set ``count_b = 1`` (and ``angle_b = spacing_b = 0`` by convention).
    """
    type: Literal["LinearPattern"] = "LinearPattern"
    seeds: list[SketchEntityId]
    entities: list[list[Definition]] = Field(default_factory=list)
    deleted: list[list[bool]] = Field(default_factory=list)
    angle_a: float
    spacing_a: float
    count_a: int
    angle_b: float = 0.0
    spacing_b: float = 0.0
    count_b: int = 1

    @field_validator("count_a", "count_b")
    @classmethod
    def _count_min(cls, v: int) -> int:
        if v < 1:
            raise ValueError("pattern counts must be >= 1")
        return v


class SketchCircularPattern(_CompositeSketchEntity):
    """Sketch circular pattern. Mirrors C# `SketchCircularPattern`. Patterns
    each seed entity around ``center`` ``count - 1`` times with ``angle_step``
    radians between instances; ``count`` includes the seed at angle 0.

    Wire shape:

    - ``seeds``: SketchEntityIds of the entities to pattern.
    - ``entities``: 2D list of typed sketch-entity Definitions; outer index is
      per seed, inner index walks the angular instances in order.
    - ``deleted``: parallel 2D bool list (same convention as
      :class:`SketchLinearPattern`).
    - ``center``: Point2D in sketch-local coords.
    - ``angle_step``: radians between successive instances. Positive is CCW
      (the C# Add path negates internally because SW's circular pattern API
      uses positive = CW).
    - ``count``: total instance count including the seed at position 0.
    """
    type: Literal["CircularPattern"] = "CircularPattern"
    seeds: list[SketchEntityId]
    entities: list[list[Definition]] = Field(default_factory=list)
    deleted: list[list[bool]] = Field(default_factory=list)
    center: Point2D
    angle_step: float
    count: int

    @field_validator("count")
    @classmethod
    def _count_min(cls, v: int) -> int:
        if v < 1:
            raise ValueError("circular pattern count must be >= 1")
        return v


class SketchOffsetEntity(_CompositeSketchEntity):
    """Sketch offset entity.

    `seeds` carries Definitions, not bare names — internal offsets use
    sketch-entity Definitions, external offsets use solid-edge Definitions.

    `echo_sketch_entities` is Inspect-only — the SW-expanded constituent
    sketch entities the offset produced.
    """
    type: Literal["OffsetEntity"] = "OffsetEntity"
    seeds: list[Definition]
    distance: float
    bidirectional: bool = False
    cap_ends: bool = False
    make_construction: bool = False
    instances: list[str] = Field(default_factory=list)
    echo_sketch_entities: list[dict[str, Any]] = Echo(default_factory=list)
