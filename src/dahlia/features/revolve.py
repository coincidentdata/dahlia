"""Revolve / CutRevolve feature models + lowercase factories."""
from __future__ import annotations

import math
from typing import Literal, Optional

from pydantic import Field, model_validator

from ..sketch import Sketch
from ..types import BodyDefinition
from ._base import _Base, ContourRef, DefinitionValue


# -- Revolve / CutRevolve --------------------------------------------------

class Revolve(_Base):
    type: Literal["Revolve"] = "Revolve"
    # Inspect/Add-result only — SolidWorks-assigned feature name. See Extrude.name.
    name: Optional[str] = None
    sketch: Sketch
    axis: DefinitionValue
    angle: float = 2 * math.pi  # radians; full revolve. Wire is radians.
    # When set, this is a two-direction (`swRevolveTypeTwoDirection`) revolve
    # — `angle` is direction-1 and `angle_b` is direction-2. None means single
    # direction or mid-plane (see `mid_plane`).
    angle_b: Optional[float] = None  # radians; second direction's sweep
    # When true, the revolve is mid-plane (`swRevolveTypeMidPlane` — full
    # `angle` symmetrically split about the sketch). Mutually exclusive with
    # `angle_b` (a revolve can't be both two-direction and mid-plane).
    mid_plane: bool = False
    reversed: bool = False
    merge: bool = True
    # Explicit sketch contour / region selection. Empty list = revolve the
    # whole sketch profile (the common case). When non-empty, the plugin
    # re-selects each contour at mark 0 instead of the parent sketch.
    contours: list[ContourRef] = Field(default_factory=list)
    # User-narrowed feature scope. None = AutoSelect (default); a list of
    # BodyDefinitions narrows the boss merge to those bodies. Empty collapses
    # to None — mirrors Sweep.feature_scope.
    feature_scope: Optional[list[BodyDefinition]] = None

    @model_validator(mode="after")
    def _angle_b_excludes_mid_plane(self) -> "Revolve":
        # mid-plane is its own swRevolveType_e value; can't combine with
        # two-direction. The handler routes one or the other but never both.
        if self.mid_plane and self.angle_b is not None:
            raise ValueError("Revolve: mid_plane and angle_b are mutually exclusive")
        if self.angle_b is not None and self.angle_b <= 0:
            raise ValueError("Revolve: angle_b must be > 0 when set")
        return self


class CutRevolve(_Base):
    type: Literal["CutRevolve"] = "CutRevolve"
    # Inspect/Add-result only — SolidWorks-assigned feature name. See Extrude.name.
    name: Optional[str] = None
    sketch: Sketch
    axis: DefinitionValue
    angle: float = 2 * math.pi  # radians; full revolve. Wire is radians.
    # See Revolve.angle_b. Two-direction (`swRevolveTypeTwoDirection`) when set.
    angle_b: Optional[float] = None  # radians; second direction's sweep
    # See Revolve.mid_plane. Mid-plane revolve (`swRevolveTypeMidPlane`) when
    # true. Mutually exclusive with `angle_b`.
    mid_plane: bool = False
    reversed: bool = False
    # `flip_side_to_cut` is intentionally absent. SolidWorks' IRevolveFeatureData2
    # exposes no getter for this field, so it can't round-trip. Authoring-side
    # control over which side of the axis is removed is implicit in the sketch
    # geometry — not separately representable on the wire.
    # Explicit sketch contour / region selection. Empty list = whole sketch.
    contours: list[ContourRef] = Field(default_factory=list)
    # User-narrowed feature scope. None = AutoSelect; a list narrows the cut
    # to those bodies only. Empty list collapses to None.
    feature_scope: Optional[list[BodyDefinition]] = None

    @model_validator(mode="after")
    def _angle_b_excludes_mid_plane(self) -> "CutRevolve":
        if self.mid_plane and self.angle_b is not None:
            raise ValueError("CutRevolve: mid_plane and angle_b are mutually exclusive")
        if self.angle_b is not None and self.angle_b <= 0:
            raise ValueError("CutRevolve: angle_b must be > 0 when set")
        return self


# -- factories -------------------------------------------------------------

def revolve(*, sketch: Sketch, axis: DefinitionValue,
            angle: float = 2 * math.pi,
            angle_b: Optional[float] = None,
            mid_plane: bool = False,
            reversed: bool = False,
            merge: bool = True,
            contours: Optional[list[ContourRef]] = None,
            feature_scope: Optional[list[BodyDefinition]] = None,
            name: Optional[str] = None) -> Revolve:
    return Revolve(sketch=sketch, axis=axis, angle=angle,
                   angle_b=angle_b, mid_plane=mid_plane,
                   reversed=reversed, merge=merge,
                   contours=contours or [],
                   feature_scope=feature_scope, name=name)


def cut_revolve(*, sketch: Sketch, axis: DefinitionValue,
                angle: float = 2 * math.pi,
                angle_b: Optional[float] = None,
                mid_plane: bool = False,
                reversed: bool = False,
                contours: Optional[list[ContourRef]] = None,
                feature_scope: Optional[list[BodyDefinition]] = None,
                name: Optional[str] = None) -> CutRevolve:
    # `flip_side_to_cut` is intentionally absent — IRevolveFeatureData2 exposes
    # no getter for it. Authoring the cut side is implicit in the sketch geometry.
    return CutRevolve(sketch=sketch, axis=axis, angle=angle,
                      angle_b=angle_b, mid_plane=mid_plane,
                      reversed=reversed,
                      contours=contours or [],
                      feature_scope=feature_scope, name=name)


__all__ = ["Revolve", "CutRevolve", "revolve", "cut_revolve"]
