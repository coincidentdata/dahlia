"""Face Definition flavors.

Face Definitions identify a live face by analytic surface params (where they
exist) plus parent body and an `on_face_point` — an XYZ captured to lie on
the face's trimmed extent. Resolver matches via `face.GetClosestPointOn(p) ≈ p`
which is invariant under SW's in-plane UV reparameterization (the failure mode
that defeats centroid-equality / UV-corner-equality on chained-Boolean'd faces).
"""
from __future__ import annotations

from typing import Literal, Optional

from ._definition_base import _DefinitionBase
from .primitives import Direction, Point3D
from .vertex_body import BodyDefinition


class PlanarFaceDefinition(_DefinitionBase):
    kind: Literal["planar_face"] = "planar_face"
    on_face_point: Point3D
    normal: Direction
    parent_body: Optional[BodyDefinition] = None


class CylindricalFaceDefinition(_DefinitionBase):
    kind: Literal["cylindrical_face"] = "cylindrical_face"
    axis_origin: Point3D
    axis_direction: Direction
    radius: float
    on_face_point: Point3D
    parent_body: Optional[BodyDefinition] = None


class ConicalFaceDefinition(_DefinitionBase):
    kind: Literal["conical_face"] = "conical_face"
    apex: Point3D
    axis_direction: Direction
    half_angle: float
    on_face_point: Point3D
    parent_body: Optional[BodyDefinition] = None


class SphericalFaceDefinition(_DefinitionBase):
    kind: Literal["spherical_face"] = "spherical_face"
    center: Point3D
    radius: float
    on_face_point: Point3D
    parent_body: Optional[BodyDefinition] = None


class ToroidalFaceDefinition(_DefinitionBase):
    kind: Literal["toroidal_face"] = "toroidal_face"
    center: Point3D
    axis_direction: Direction
    major_radius: float
    minor_radius: float
    on_face_point: Point3D
    parent_body: Optional[BodyDefinition] = None


class BSurfaceFaceDefinition(_DefinitionBase):
    """Catch-all for non-analytic faces — sweeps, lofts, blends, revolved
    profiles, BSurface. No analytic params; identity carried by an N-point
    on-trim sample. The plugin captures 9 points snapped via
    `Face2.GetClosestPointOn`, resolver checks every sample lies on the
    candidate face within tolerance.
    """
    kind: Literal["bsurface_face"] = "bsurface_face"
    on_face_points: list[Point3D]
    parent_body: Optional[BodyDefinition] = None
