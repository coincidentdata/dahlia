from __future__ import annotations

from typing import Literal

from pydantic import Field

from ._base import DefinitionValue, _Base


class ShellWall(_Base):
    face: DefinitionValue
    thickness: float = Field(gt=0, allow_inf_nan=False)


def shell_wall(face: DefinitionValue, thickness: float) -> ShellWall:
    return ShellWall(face=face, thickness=thickness)


class Shell(_Base):
    type: Literal["Shell"] = "Shell"
    name: str | None = None
    thickness: float = Field(gt=0, allow_inf_nan=False)
    faces: list[DefinitionValue] = Field(default_factory=list)
    outward: bool = False
    face_thicknesses: list[ShellWall] = Field(default_factory=list)


def shell(
    *,
    thickness: float,
    faces: list[DefinitionValue] | None = None,
    outward: bool = False,
    face_thicknesses: list[ShellWall | dict] | None = None,
    name: str | None = None,
) -> Shell:
    return Shell(
        thickness=thickness,
        faces=faces or [],
        outward=outward,
        face_thicknesses=face_thicknesses or [],
        name=name,
    )
