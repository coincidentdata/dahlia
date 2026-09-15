"""End-condition and Extrude start-condition discriminated unions.

Shared between Extrude / CutExtrude. Kept here (rather than inlined in
`extrude.py`) so the discriminated unions are importable without dragging in
the full extrude class, and so the discriminator types stay in one place.
"""
from __future__ import annotations

from typing import Annotated, Literal, Union

from pydantic import BeforeValidator, Field, field_validator

from ._base import _Base, DefinitionValue


# -- shared end-condition shapes -------------------------------------------

class _EndConditionDistance(_Base):
    end_condition: Literal["Blind", "MidPlane"] = "Blind"
    distance: float

    @field_validator("distance")
    @classmethod
    def _distance_positive(cls, v: float) -> float:
        if v <= 0:
            raise ValueError("distance must be > 0")
        return v


# `UpToNext` is body-aware (next solid body face stops the extrude); `ThroughNext`
# is surface-aware (next surface, possibly mid-body). Both are no-param like
# `ThroughAll`. The two must stay distinct — collapsing them silently changes
# geometry on multi-body parts.
# SolidWorks quirk: see solidworks-quirks.md (extrude end-conditions).
class _EndConditionThrough(_Base):
    end_condition: Literal["ThroughAll", "ThroughNext", "UpToNext"] = "ThroughAll"


# SolidWorks' `swEndCondUpToVertex` accepts both vertex and edge targets, so
# `UpToEdgeOrVertex` and `UpToVertex` collapse to a single kind. `UpToVertex`
# is accepted as an input-only alias; the C# handler normalizes it on parse
# and emits `UpToEdgeOrVertex` on inspect.
class _EndConditionUpTo(_Base):
    end_condition: Literal["UpToEdgeOrVertex", "UpToVertex", "UpToSurface", "UpToBody"] = "UpToSurface"
    end: DefinitionValue


# OffFrom (= `swEndCondOffsetFromSurface`): extrude terminates at a fixed offset
# from a reference surface. Carries `distance` (offset, signed by `reversed`),
# `surface` (the offset-from reference), and `translate_surface` (whether SW
# translates the surface to the end face vs treats it as a fixed datum).
class _EndConditionOffFrom(_Base):
    end_condition: Literal["OffFrom"] = "OffFrom"
    distance: float
    surface: DefinitionValue
    reversed: bool = False
    translate_surface: bool = False

    @field_validator("distance")
    @classmethod
    def _distance_positive(cls, v: float) -> float:
        if v <= 0:
            raise ValueError("OffFrom distance must be > 0")
        return v


def _end_condition_from_name(v: object) -> object:
    """Accept `end_condition="ThroughAll"` for the variants that carry no other data.

    `_EndConditionThrough` is nothing but its discriminator, so the `{"end_condition":
    ...}` envelope is pure wire noise there. Blind/MidPlane need a distance and UpTo*
    needs an end, so a bare name for those fails on the missing field — the right error.
    """
    return {"end_condition": v} if isinstance(v, str) else v


# The BeforeValidator WRAPS the discriminated union rather than sitting beside the
# discriminator in one Annotated. Inside, it is silently skipped once the field is
# `Optional[EndCondition]` — which is every use of it — and a bare name fails with
# "Input should be a valid dictionary".
EndCondition = Annotated[
    Annotated[
        Union[_EndConditionDistance, _EndConditionThrough, _EndConditionUpTo,
              _EndConditionOffFrom],
        Field(discriminator="end_condition"),
    ],
    BeforeValidator(_end_condition_from_name),
]


# -- Extrude start-condition shapes ----------------------------------------
#
# Maps to swStartCondition_e: Sketch=0, Surface=1, Vertex=2, Offset=3.
# Collapsing these to "Sketch" silently loses authored geometry (a UI-authored
# extrude that starts from a surface would round-trip as if from the sketch
# plane). Default is Sketch.
class _ExtrudeStartSketch(_Base):
    type: Literal["Sketch"] = "Sketch"


class _ExtrudeStartSurface(_Base):
    type: Literal["Surface"] = "Surface"
    reference: DefinitionValue  # face / surface body the extrude starts from


class _ExtrudeStartVertex(_Base):
    type: Literal["Vertex"] = "Vertex"
    reference: DefinitionValue  # vertex the extrude starts from


class _ExtrudeStartOffset(_Base):
    type: Literal["Offset"] = "Offset"
    offset: float                 # meters, signed; reversed below flips direction
    reversed: bool = False


_ExtrudeStart = Annotated[
    Union[_ExtrudeStartSketch, _ExtrudeStartSurface,
          _ExtrudeStartVertex, _ExtrudeStartOffset],
    Field(discriminator="type"),
]


__all__ = [
    "_EndConditionDistance",
    "_EndConditionThrough",
    "_EndConditionUpTo",
    "_EndConditionOffFrom",
    "EndCondition",
    "_ExtrudeStartSketch",
    "_ExtrudeStartSurface",
    "_ExtrudeStartVertex",
    "_ExtrudeStartOffset",
    "_ExtrudeStart",
]
