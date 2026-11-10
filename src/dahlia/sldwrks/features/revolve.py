"""Revolve / CutRevolve feature models + lowercase factories."""
from __future__ import annotations

import math
from typing import Annotated, Literal, Optional, Union

from pydantic import Field

from ..sketch import Sketch
from ..types import BodyDefinition
from ._base import _Base, ContourRef, DefinitionValue

# Radians. The slack admits a full turn written to 13 significant figures (6.28318530718 > 2π).
_Angle = Annotated[float, Field(gt=0, le=2 * math.pi + 1e-9)]


class _OneDirection(_Base):
    extent: Literal["OneDirection"] = "OneDirection"
    angle: _Angle = 2 * math.pi


class _MidPlane(_Base):
    extent: Literal["MidPlane"] = "MidPlane"
    angle: _Angle  # split evenly about the sketch plane


class _TwoDirections(_Base):
    extent: Literal["TwoDirections"] = "TwoDirections"
    angle: _Angle
    angle_b: _Angle


RevolveExtent = Annotated[Union[_OneDirection, _MidPlane, _TwoDirections], Field(discriminator="extent")]


class _Revolve(_Base):
    # SolidWorks-assigned feature name, back-filled on the Add result.
    name: Optional[str] = None
    sketch: Sketch
    axis: DefinitionValue
    extent: RevolveExtent = Field(default_factory=_OneDirection)
    reversed: bool = False
    # Explicit sketch contour / region selection. Empty list = the whole sketch.
    contours: list[ContourRef] = Field(default_factory=list)
    # None = AutoSelect; a list narrows the affected bodies.
    feature_scope: Optional[list[BodyDefinition]] = None


class Revolve(_Revolve):
    type: Literal["Revolve"] = "Revolve"
    merge: bool = True


class CutRevolve(_Revolve):
    # No flip_side_to_cut: IRevolveFeatureData2 has no getter for it, so it cannot round-trip.
    type: Literal["CutRevolve"] = "CutRevolve"


def _extent(angle: Optional[float], extent: object) -> object:
    if angle is not None and extent is not None:
        raise ValueError("pass angle (a one-direction revolve) or extent, not both")
    return extent if extent is not None else {"extent": "OneDirection", "angle": 2 * math.pi if angle is None else angle}


def revolve(*, sketch: Sketch, axis: DefinitionValue, angle: Optional[float] = None,
            extent: Optional[RevolveExtent] = None, reversed: bool = False, merge: bool = True,
            contours: Optional[list[ContourRef]] = None,
            feature_scope: Optional[list[BodyDefinition]] = None,
            name: Optional[str] = None) -> Revolve:
    return Revolve(sketch=sketch, axis=axis, extent=_extent(angle, extent), reversed=reversed, merge=merge,
                   contours=contours or [], feature_scope=feature_scope, name=name)


def cut_revolve(*, sketch: Sketch, axis: DefinitionValue, angle: Optional[float] = None,
                extent: Optional[RevolveExtent] = None, reversed: bool = False,
                contours: Optional[list[ContourRef]] = None,
                feature_scope: Optional[list[BodyDefinition]] = None,
                name: Optional[str] = None) -> CutRevolve:
    return CutRevolve(sketch=sketch, axis=axis, extent=_extent(angle, extent), reversed=reversed,
                      contours=contours or [], feature_scope=feature_scope, name=name)


__all__ = ["Revolve", "CutRevolve", "RevolveExtent", "revolve", "cut_revolve"]
