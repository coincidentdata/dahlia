"""Primitive geometry types and Definition flavors.

A Definition is the JSON-shaped opaque value the model passes around to identify
faces, edges, vertices on bodies, or sketch entities. Two flavors:

- Solid-geometry definitions carry geometry (centroid, normal, axis, control
  points). The plugin re-resolves them by geometric matching at use time.
- Sketch-entity definitions carry the SolidWorks `entity.GetID()` triplet
  (sketch_name, entity_kind, id) PLUS the full per-entity geometric payload
  (center, radius, start, end, etc.). The plugin resolves by id; the geometry
  rides along for descriptiveness/inspection.

Discrimination is via the `kind` field literal (snake_case literals matching
the C# `[JsonDerivedType]` registrations in `Sldworks.Core.Definitions.Definition`).
"""
from __future__ import annotations

from typing import Annotated, Union

from pydantic import Field

from ._base import _Base, Echo, In
from ._definition_base import _DefinitionBase
from .primitives import (
    BoundingBox,
    Direction,
    Plane,
    Point2D,
    Point3D,
    Ray,
    point,
    ray,
)
from .face import (
    BSurfaceFaceDefinition,
    ConicalFaceDefinition,
    CylindricalFaceDefinition,
    PlanarFaceDefinition,
    SphericalFaceDefinition,
    ToroidalFaceDefinition,
)
from .edge import (
    CircularEdgeDefinition,
    EllipticalEdgeDefinition,
    LineEdgeDefinition,
    SplineEdgeDefinition,
)
from .vertex_body import BodyDefinition, BodyProbe, VertexDefinition
from .sketch_def import (
    CONSTRAINED_STATUS,
    SketchArcDefinition,
    SketchCircleDefinition,
    SketchEllipseDefinition,
    SketchEllipticalArcDefinition,
    SketchEntityDefinition,
    SketchEntityDefinitionFlavor,
    SketchEntityId,
    SketchLineDefinition,
    SketchParabolaDefinition,
    SketchPointDefinition,
    SketchSplineDefinition,
    _SketchEntityDefinitionBase,
)
from .region_contour import (
    FeatureDefinition,
    RegionDefinition,
    SketchContourDefinition,
)
from .ref import (
    ComponentDefinition, ComponentEntityDefinition,
    RefAxisDefinition, RefPlaneDefinition, TempAxisDefinition,
)


# Discriminated union over every concrete Definition flavor. Constructed here
# (rather than per-module) because the union has to enumerate every leaf class —
# building it earlier would force a circular import. Plane.definition and
# RegionDefinition.edges both reference this; the model_rebuild() calls below
# resolve those forward refs once the alias is in scope.
Definition = Annotated[
    Union[
        PlanarFaceDefinition,
        CylindricalFaceDefinition,
        ConicalFaceDefinition,
        SphericalFaceDefinition,
        ToroidalFaceDefinition,
        BSurfaceFaceDefinition,
        LineEdgeDefinition,
        CircularEdgeDefinition,
        EllipticalEdgeDefinition,
        SplineEdgeDefinition,
        VertexDefinition,
        BodyDefinition,
        SketchEntityId,
        SketchLineDefinition,
        SketchCircleDefinition,
        SketchArcDefinition,
        SketchPointDefinition,
        SketchEllipseDefinition,
        SketchEllipticalArcDefinition,
        SketchParabolaDefinition,
        SketchSplineDefinition,
        SketchContourDefinition,
        RegionDefinition,
        FeatureDefinition,
        RefAxisDefinition,
        TempAxisDefinition,
        RefPlaneDefinition,
        ComponentDefinition,
        ComponentEntityDefinition,
    ],
    Field(discriminator="kind"),
]


# Resolve forward refs. `Plane.definition` and `RegionDefinition.edges` both
# reference the `Definition` union by string — rebuild now that it's bound in
# this module's namespace.
Plane.model_rebuild(_types_namespace={"Definition": Definition})
RegionDefinition.model_rebuild(_types_namespace={"Definition": Definition})
ComponentEntityDefinition.model_rebuild(_types_namespace={"Definition": Definition})


__all__ = [
    "ComponentDefinition",
    "ComponentEntityDefinition",
    # base
    "_Base",
    "_DefinitionBase",
    "Echo",
    "In",
    # primitives
    "Point2D",
    "Point3D",
    "Direction",
    "Plane",
    "Ray",
    "BoundingBox",
    "ray",
    "point",
    # face flavors
    "PlanarFaceDefinition",
    "CylindricalFaceDefinition",
    "ConicalFaceDefinition",
    "SphericalFaceDefinition",
    "ToroidalFaceDefinition",
    "BSurfaceFaceDefinition",
    # edge flavors
    "LineEdgeDefinition",
    "CircularEdgeDefinition",
    "EllipticalEdgeDefinition",
    "SplineEdgeDefinition",
    # vertex + body
    "VertexDefinition",
    "BodyDefinition",
    "BodyProbe",
    # sketch entities
    "SketchEntityId",
    "CONSTRAINED_STATUS",
    "_SketchEntityDefinitionBase",
    "SketchPointDefinition",
    "SketchLineDefinition",
    "SketchCircleDefinition",
    "SketchArcDefinition",
    "SketchEllipseDefinition",
    "SketchEllipticalArcDefinition",
    "SketchParabolaDefinition",
    "SketchSplineDefinition",
    "SketchEntityDefinitionFlavor",
    "SketchEntityDefinition",
    # region/contour/feature
    "SketchContourDefinition",
    "RegionDefinition",
    "FeatureDefinition",
    # ref geometry
    "RefAxisDefinition",
    "TempAxisDefinition",
    "RefPlaneDefinition",
    # the union itself
    "Definition",
]
