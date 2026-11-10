"""UPPERCASE constants the model writes inline.

These are bare values (numbers or small instances), NOT enum.Enum members.
The point is token-cheap and unambiguous: `f.probe(r, FACE)`.

Wire convention: SolidWorks stores everything in meters (length) and radians
(angles) internally. JSON across the wire follows that — meters and radians,
always. The constants below are float multipliers so the model can write the
units it thinks in:

    extrude(depth=10*MM)        # -> depth=0.01 on the wire
    revolve(angle=90*DEG)       # -> angle=1.5708 on the wire

`f.set_units(MM)` is a separate concern: it changes the file's *display*
units, not the wire. The unit's `.name` attribute is what gets sent.
"""
from __future__ import annotations

import math

from .types import FeatureDefinition


# Entity types passed to f.probe().
FACE: str = "face"
EDGE: str = "edge"
VERTEX: str = "vertex"
BODY: str = "body"
PLANE: str = "plane"

# Default reference planes. These are real features on every part — the
# FeatureManager tree contains "Top Plane" / "Front Plane" / "Right Plane"
# from the moment a part is created. The model writes `sketch(plane=TOP)`
# and the plugin resolves the FeatureDefinition by name. The same JSON shape
# works for any user-created RefPlane the model wants to reference by name.
TOP = FeatureDefinition(name="Top Plane")
FRONT = FeatureDefinition(name="Front Plane")
RIGHT = FeatureDefinition(name="Right Plane")


class _Unit(float):
    """Float multiplier (meters or radians) with a display name attached.

    `MM` evaluates as `0.001` for arithmetic (`depth=10*MM` -> `0.01`) and
    carries `MM.name == "mm"` for use with `f.set_units(MM)`.
    """
    name: str

    def __new__(cls, factor: float, name: str) -> "_Unit":
        instance = super().__new__(cls, factor)
        instance.name = name
        return instance

    def __repr__(self) -> str:
        return f"{self.name}({float(self)})"


# Length multipliers. Wire is meters.
MM = _Unit(0.001, "mm")
CM = _Unit(0.01, "cm")
M = _Unit(1.0, "m")
INCH = _Unit(0.0254, "inch")

# Angle multipliers. Wire is radians.
DEG = _Unit(math.pi / 180, "deg")
RAD = _Unit(1.0, "rad")
