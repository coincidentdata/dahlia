"""ProjectedCurve feature models + lowercase factory.

SolidWorks' two projection types: `SketchOnFaces` projects a sketch onto faces;
`SketchOnSketch` intersects the projections of two sketches.
"""
from __future__ import annotations

from typing import Annotated, Literal, Optional, Union

from pydantic import Field, TypeAdapter

from ..sketch import Sketch
from ._base import _Base, DefinitionValue


class _ProjectedCurve(_Base):
    type: Literal["ProjectedCurve"] = "ProjectedCurve"
    name: Optional[str] = None
    sketch: Sketch  # the sketch to project


class SketchOnFaces(_ProjectedCurve):
    projection_type: Literal["SketchOnFaces"] = "SketchOnFaces"
    faces: list[DefinitionValue] = Field(min_length=1)
    projection_direction: Optional[DefinitionValue] = None  # None = normal to the sketch plane
    bidirectional: bool = False
    reverse: bool = False


class SketchOnSketch(_ProjectedCurve):
    projection_type: Literal["SketchOnSketch"] = "SketchOnSketch"
    second_sketch: Sketch


ProjectedCurve = Annotated[Union[SketchOnFaces, SketchOnSketch], Field(discriminator="projection_type")]
_PROJECTED_CURVE = TypeAdapter(ProjectedCurve)


def projected_curve(*, sketch: Sketch, projection_type: Literal["SketchOnFaces", "SketchOnSketch"] = "SketchOnFaces",
                    faces: Optional[list[DefinitionValue]] = None, projection_direction: Optional[DefinitionValue] = None,
                    bidirectional: Optional[bool] = None, reverse: Optional[bool] = None,
                    second_sketch: Optional[Sketch] = None, name: Optional[str] = None) -> ProjectedCurve:
    """A projected curve of `projection_type`; pass only that type's options (SketchOnFaces: faces,
    projection_direction, bidirectional, reverse; SketchOnSketch: second_sketch)."""
    options = {"faces": faces, "projection_direction": projection_direction, "bidirectional": bidirectional,
               "reverse": reverse, "second_sketch": second_sketch}
    return _PROJECTED_CURVE.validate_python({"projection_type": projection_type, "sketch": sketch, "name": name,
                                             **{k: v for k, v in options.items() if v is not None}})


__all__ = ["ProjectedCurve", "SketchOnFaces", "SketchOnSketch", "projected_curve"]
