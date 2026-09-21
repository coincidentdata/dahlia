"""Helix feature model + lowercase factory."""
from __future__ import annotations

import math
from typing import Literal, Optional

from pydantic import field_validator, model_validator

from ..sketch import Sketch
from ..types import Echo
from ._base import _Base


# -- Helix -----------------------------------------------------------------
#
# A helix is built from an axis sketch (one circle defines the axis + start
# radius) plus parameters. `defined_by` selects which two of (pitch, height,
# revolutions) drive the curve; the third is computed.

class Helix(_Base):
    type: Literal["Helix"] = "Helix"
    # Inspect/Add-result only — SolidWorks-assigned feature name. See Extrude.name.
    name: Optional[str] = None
    sketch: Sketch  # axis sketch — exactly one circle, on the plane normal to the axis
    defined_by: Literal["PitchAndRevolution", "HeightAndRevolution",
                        "HeightAndPitch", "Spiral"] = "PitchAndRevolution"
    pitch: float = 0.0           # meters, ignored when defined_by selects out
    height: float = 0.0          # meters
    revolutions: float = 0.0     # turns (unitless)
    start_angle: float = 0.0     # radians, where the helix begins
    clockwise: bool = False
    reversed: bool = False
    taper_angle: float = 0.0     # radians; positive = taper inward, negative = outward
    taper_outward: bool = False  # explicit outward flag

    # Variable-pitch helix: per-segment (pitch_in_meters, revolutions_in_turns).
    # Forward-compat schema; the C# handler throws NotSupportedException on
    # Inspect/Add when this is non-None. SolidWorks quirk: `IHelixFeatureData`
    # segment iteration corrupts the underlying ReferenceCurve until part
    # reload. See `solidworks-quirks.md` Helix section.
    #
    # When set: must be non-empty; every pitch > 0; per-segment revolutions
    # sum equals `revolutions`. The top-level `revolutions` field stays the
    # source of truth for total turns so consumers that ignore `segments`
    # still see consistent geometry.
    segments: Optional[list[tuple[float, float]]] = None

    # Inspect-only echo: 4×3 matrix (X, Y, Z=normal, origin) lifted from the
    # helix's axis-sketch `ModelToSketchTransform`. Same purpose as the
    # matching field on `Sketch` (the axis sketch is face-authorable and
    # inherits the rebuild-drift risk from solidworks-quirks.md "Sketch
    # planes").
    echo_computed_axes: Optional[list[list[float]]] = Echo(default=None)

    @field_validator("segments")
    @classmethod
    def _segments_well_formed(
        cls, v: Optional[list[tuple[float, float]]]
    ) -> Optional[list[tuple[float, float]]]:
        if v is None:
            return v
        if len(v) == 0:
            raise ValueError(
                "Helix.segments: must be non-empty when set "
                "(use None for constant-pitch helices)"
            )
        for i, (pitch, _revs) in enumerate(v):
            if pitch <= 0:
                raise ValueError(
                    f"Helix.segments[{i}].pitch: must be > 0 (got {pitch})"
                )
        return v

    @model_validator(mode="after")
    def _segments_revolutions_sum(self) -> "Helix":
        if self.segments is None:
            return self
        total = math.fsum(revs for _pitch, revs in self.segments)
        # Tight-but-not-bit-exact: 1e-9 turns is well below any modeling case
        # but absorbs ULP drift from float-summed user-typed splits.
        if abs(total - self.revolutions) > 1e-9:
            raise ValueError(
                f"Helix.segments: per-segment revolutions sum to {total} "
                f"but `revolutions` is {self.revolutions}; they must match"
            )
        return self


# -- factory ---------------------------------------------------------------

def helix(*, sketch: Sketch,
          defined_by: str = "PitchAndRevolution",
          pitch: float = 0.0, height: float = 0.0, revolutions: float = 0.0,
          start_angle: float = 0.0, clockwise: bool = False,
          reversed: bool = False, taper_angle: float = 0.0,
          taper_outward: bool = False,
          segments: Optional[list[tuple[float, float]]] = None,
          name: Optional[str] = None) -> Helix:
    return Helix(sketch=sketch, defined_by=defined_by,  # type: ignore[arg-type]
                 pitch=pitch, height=height, revolutions=revolutions,
                 start_angle=start_angle, clockwise=clockwise,
                 reversed=reversed, taper_angle=taper_angle,
                 taper_outward=taper_outward, segments=segments, name=name)


__all__ = ["Helix", "helix"]
