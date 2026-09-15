"""ProjectedCurve feature model + lowercase factory.

A projected curve produces a 3D curve in one of two flavors, discriminated by
which target field is populated:
  - "Sketch on Faces":  pass `faces` (non-empty list). The sketch is projected
                        normal to its plane onto each face; the trimmed
                        projection becomes the 3D curve.
  - "Sketch on Sketch": pass `second_sketch`. The two sketches' implicit
                        extrusions intersect to form the 3D curve.

Exactly one of `faces` / `second_sketch` must be set (validated client-side
and re-validated on the C# side).
"""
from __future__ import annotations

from typing import Literal, Optional

from pydantic import model_validator

from ..sketch import Sketch
from ._base import _Base, DefinitionValue


class ProjectedCurve(_Base):
    type: Literal["ProjectedCurve"] = "ProjectedCurve"
    # Inspect/Add-result only — SolidWorks-assigned feature name.
    name: Optional[str] = None

    # The sketch being projected. Required.
    sketch: Sketch

    # Sketch-on-Faces target. Populate when projecting onto faces; leave empty
    # when using `second_sketch`.
    faces: list[DefinitionValue] = []

    # Sketch-on-Sketch target. Populate when projecting onto another sketch;
    # leave None when using `faces`.
    second_sketch: Optional[Sketch] = None

    # Optional reference that overrides the default projection direction
    # (sketch-plane normal). Typically a planar face, ref-plane, ref-axis,
    # or linear edge. SW selects this on mark 8.
    projection_direction: Optional[DefinitionValue] = None

    # SW's "Bidirectional" toggle — project in both normal directions instead
    # of only the sketch's positive normal.
    bidirectional: bool = False
    # SW's "Reverse" toggle — flip the projection direction.
    reverse: bool = False

    @model_validator(mode="after")
    def _check_target(self) -> "ProjectedCurve":
        if bool(self.faces) == bool(self.second_sketch is not None):
            raise ValueError(
                "ProjectedCurve: exactly one of `faces` (non-empty) or "
                "`second_sketch` must be provided"
            )
        return self


def projected_curve(*, sketch: Sketch,
                    faces: Optional[list[DefinitionValue]] = None,
                    second_sketch: Optional[Sketch] = None,
                    projection_direction: Optional[DefinitionValue] = None,
                    bidirectional: bool = False,
                    reverse: bool = False,
                    name: Optional[str] = None) -> ProjectedCurve:
    return ProjectedCurve(
        sketch=sketch,
        faces=faces or [],
        second_sketch=second_sketch,
        projection_direction=projection_direction,
        bidirectional=bidirectional,
        reverse=reverse,
        name=name,
    )


__all__ = ["ProjectedCurve", "projected_curve"]
