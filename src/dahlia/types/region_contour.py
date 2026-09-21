"""SketchContourDefinition, RegionDefinition, FeatureDefinition.

These three Definition flavors aggregate other Definitions:
- SketchContourDefinition: list of geometry-bearing sketch-entity flavors.
- RegionDefinition: list of arbitrary Definitions (the bordering edges).
- FeatureDefinition: a by-name reference to a FeatureManager tree node.
"""
from __future__ import annotations

from typing import TYPE_CHECKING, Literal, Optional

from pydantic import model_validator

from ._base import Echo
from ._definition_base import _DefinitionBase
from .primitives import Point2D
from .sketch_def import SketchEntityDefinitionFlavor

if TYPE_CHECKING:
    from . import Definition  # noqa: F401


class FeatureDefinition(_DefinitionBase):
    """Reference to a feature by name in the FeatureManager tree.

    Used for the default planes (`TOP`, `FRONT`, `RIGHT`) and for user-created
    features referenced by name rather than by their geometry.
    """
    kind: Literal["feature"] = "feature"
    name: str


class SketchContourDefinition(_DefinitionBase):
    """Sketch contour as the unordered set of its constituent segments.

    SW quirk: ISketchContour has no GetID() pair (unlike ISketchSegment /
    ISketchPoint), so the segment-id set is the stable handle. Each segment
    is one of the 8 geometry-bearing sketch-entity flavors (NOT a bare
    SketchEntityId — contours hold geometry to disambiguate when SW has
    reissued ids on a round-tripped target).
    """
    kind: Literal["sketch_contour"] = "sketch_contour"
    segments: list[SketchEntityDefinitionFlavor]

    @model_validator(mode="after")
    def _segments_nonempty(self) -> "SketchContourDefinition":
        if not self.segments:
            raise ValueError(
                "SketchContourDefinition.segments must contain at least one segment"
            )
        return self


class RegionDefinition(_DefinitionBase):
    """Sketch region as the unordered set of its bordering edges.

    SW quirk: ISketchRegion exposes GetEdges() but no GetSketchSegments(); resolver
    matches by set-equality on the serialized edge definitions.

    Author manually via `f.probe_region(sketch_name, (x, y))` — pick any 2D
    point in sketch-plane coords inside the region you want, and the plugin
    returns this full Definition (sparing you from emitting `edges` by hand).

    `echo_interior_point` is Inspect-only — a 2D sketch-plane point known to
    lie inside the region (computed from the loop tessellation). The runner's
    transcript emitter uses it to issue `target.probe_region(sketch_name,
    (x, y))` calls so the round-trip authoring path matches what an LLM would
    write, exercising the probe_region helper end-to-end.
    """
    kind: Literal["region"] = "region"
    edges: list["Definition"]
    echo_interior_point: Optional[Point2D] = Echo(default=None)

    @model_validator(mode="after")
    def _edges_nonempty(self) -> "RegionDefinition":
        if not self.edges:
            raise ValueError(
                "RegionDefinition.edges must contain at least one edge"
            )
        return self
