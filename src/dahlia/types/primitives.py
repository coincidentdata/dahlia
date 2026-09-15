"""Primitive geometry types: Point2D, Point3D, Direction, Plane, Ray, BoundingBox.

Also hosts the `ray()` and `point()` factory helpers.
"""
from __future__ import annotations

from typing import TYPE_CHECKING, Any, Optional

from pydantic import model_validator

from ._base import _Base


class _Coordinates(_Base):
    """Base for the x/y/z value types, accepting a plain tuple or list.

    An author writes `help_point=(0.0, 0.0, 0.1)`, not
    `help_point={"x": 0.0, "y": 0.0, "z": 0.1}` — the dict is the wire shape and
    it has no business in a hand-written script. Sketch entities already take
    `(x, y)` tuples, so this makes the 3D fields consistent with them.
    """

    @model_validator(mode="before")
    @classmethod
    def _from_sequence(cls, value: Any) -> Any:
        if isinstance(value, (tuple, list)):
            names = [n for n in ("x", "y", "z") if n in cls.model_fields]
            if not 2 <= len(value) <= len(names):
                raise ValueError(
                    f"{cls.__name__}: expected {len(names)} coordinates, got {len(value)}: "
                    f"{value!r}")
            return dict(zip(names, value))
        return value

if TYPE_CHECKING:
    # Plane.definition is a forward ref to the Union over all Definition flavors.
    # `Definition` is constructed in the package `__init__.py` after every concrete
    # flavor is imported, so we only pull it in for type-checkers here.
    from . import Definition  # noqa: F401


class Point2D(_Coordinates):
    """Point in a sketch (sketch-local 2D coordinates)."""
    x: float
    y: float


class Point3D(_Coordinates):
    """Point in 3D model space."""
    x: float
    y: float
    z: float = 0.0


class Direction(_Coordinates):
    """Unit vector. Not normalized at construction; the plugin normalizes."""
    x: float
    y: float
    z: float


class Plane(_Base):
    """Wrapper for things that can stand in for a sketch plane: a planar face
    `Definition`, or a `FeatureDefinition` pointing at a RefPlane feature.

    Most callers write `sketch(plane=TOP)` (a `FeatureDefinition`) or
    `sketch(plane=face)` where `face` came from `f.probe(..., FACE)`. This
    type only exists if you need to attach extra data alongside.
    """
    definition: Optional["Definition"] = None  # forward ref resolved in package __init__


class Ray(_Base):
    """Ray for selection: origin + direction in 3D model space.

    ``radius`` is the SelectByRay cylinder radius in meters (ignored for faces,
    which SW selects by infinite line). ``None`` → the plugin's default
    (0.1 mm). Probe generation bakes a tight, clearance-sized radius here.
    """
    origin: Point3D
    direction: Direction
    radius: Optional[float] = None


class BoundingBox(_Base):
    """Axis-aligned bounding box in model space (meters). Used as an optional
    disambiguator on face Definition flavors — two faces that share analytic
    parameters (e.g. coaxial inner / outer cylinder of a thin tube) carry distinct
    bboxes, so the resolver can pick the right one. Mirrors C#
    `Sldworks.Core.Definitions.BoundingBox`. SolidWorks `Face2.GetBox()` returns
    `[xmin, ymin, zmin, xmax, ymax, zmax]` in *meters regardless of display units*.
    """
    min: Point3D
    max: Point3D


# -- factories --------------------------------------------------------------

def ray(origin: tuple[float, float, float] | Point3D,
        direction: tuple[float, float, float] | Direction,
        radius: Optional[float] = None) -> Ray:
    """Build a Ray. Accepts tuples or typed instances. ``radius`` (meters) sets
    the SelectByRay cylinder; ``None`` uses the plugin default."""
    if isinstance(origin, Point3D):
        o = origin
    else:
        ox, oy, oz = origin
        o = Point3D(x=ox, y=oy, z=oz)
    if isinstance(direction, Direction):
        d = direction
    else:
        dx, dy, dz = direction
        d = Direction(x=dx, y=dy, z=dz)
    return Ray(origin=o, direction=d, radius=radius)


def point(x: float, y: float, z: float = 0.0) -> Point3D:
    """Build a Point3D."""
    return Point3D(x=x, y=y, z=z)
