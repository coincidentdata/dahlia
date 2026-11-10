"""Chamfer feature models + lowercase factory.

`Chamfer` is the union of an edge chamfer and a vertex chamfer, keyed by `kind`. An edge chamfer's
`size` is one of equal distances, two distances, or an angle and a distance; a vertex chamfer
sets back each of the three edges meeting at its single vertex.
"""
from __future__ import annotations

import math
from typing import Annotated, Literal, Optional, Union

from pydantic import Field, TypeAdapter

from ..types import Echo
from ._base import _Base, DefinitionValue

_Distance = Annotated[float, Field(gt=0)]


class _EqualDistance(_Base):
    mode: Literal["EqualDistance"] = "EqualDistance"
    distance: _Distance


class _DistanceDistance(_Base):
    mode: Literal["DistanceDistance"] = "DistanceDistance"
    distance: _Distance
    distance_b: _Distance


class _AngleDistance(_Base):
    mode: Literal["AngleDistance"] = "AngleDistance"
    distance: _Distance
    angle: float = Field(gt=0, lt=math.pi / 2)
    flipped: bool = False  # measure the distance along the other face


ChamferSize = Annotated[Union[_EqualDistance, _DistanceDistance, _AngleDistance], Field(discriminator="mode")]


class _Chamfer(_Base):
    type: Literal["Chamfer"] = "Chamfer"
    # SolidWorks-assigned feature name, back-filled on the Add result.
    name: Optional[str] = None
    keep_features: bool = True  # keep features the chamfer would otherwise consume
    # Faces SolidWorks reports as affected by the chamfer.
    echo_affected_faces: Optional[list[dict]] = Echo(default=None)


class EdgeChamfer(_Chamfer):
    kind: Literal["Edge"] = "Edge"
    edges: list[DefinitionValue] = Field(min_length=1)  # edges, loops or faces
    size: ChamferSize
    tangent_propagation: bool = True


class VertexChamfer(_Chamfer):
    kind: Literal["Vertex"] = "Vertex"
    vertices: list[DefinitionValue] = Field(min_length=1, max_length=1)
    distance_a: _Distance
    distance_b: _Distance
    distance_c: _Distance


Chamfer = Annotated[Union[EdgeChamfer, VertexChamfer], Field(discriminator="kind")]
_CHAMFER = TypeAdapter(Chamfer)


def chamfer(*, kind: Literal["Edge", "Vertex"] = "Edge", edges: Optional[list[DefinitionValue]] = None,
            size: Optional[ChamferSize] = None, tangent_propagation: Optional[bool] = None,
            vertices: Optional[list[DefinitionValue]] = None, distance_a: Optional[float] = None,
            distance_b: Optional[float] = None, distance_c: Optional[float] = None,
            keep_features: bool = True, name: Optional[str] = None) -> Chamfer:
    """A chamfer of `kind`; pass only that kind's options (Edge: edges + size + tangent_propagation;
    Vertex: vertices + distance_a/b/c)."""
    options = {"edges": edges, "size": size, "tangent_propagation": tangent_propagation, "vertices": vertices,
               "distance_a": distance_a, "distance_b": distance_b, "distance_c": distance_c}
    return _CHAMFER.validate_python({"kind": kind, "keep_features": keep_features, "name": name,
                                     **{k: v for k, v in options.items() if v is not None}})


__all__ = ["Chamfer", "EdgeChamfer", "VertexChamfer", "ChamferSize", "chamfer"]
