"""VertexDefinition + BodyDefinition.

Kept together because BodyDefinition is the parent-body backref carried by every
face flavor, and VertexDefinition is the smallest sibling that shares no other
home; both are leaf definitions with no further composition.
"""
from __future__ import annotations

from typing import Literal

from ._base import _Base
from ._definition_base import _DefinitionBase
from .primitives import BoundingBox, Point3D, Ray


class VertexDefinition(_DefinitionBase):
    kind: Literal["vertex"] = "vertex"
    point: Point3D


class BodyDefinition(_DefinitionBase):
    # Body identity = mass-properties (centroid+volume+surface_area) + bbox.
    # Mass props compare at the looser BodyMassPropertyTol on the resolver;
    # bbox compares at the tight GeometryTolerance since extreme points are
    # stable across rebuilds.
    kind: Literal["body"] = "body"
    centroid: Point3D
    volume: float
    bbox: BoundingBox
    surface_area: float


class BodyProbe(_Base):
    """Source-built handle for a transient post-feature body — the kind of
    fragment that only exists during a PromptBodiesToKeepNotify firing on
    Cut* features. `body` is the source-captured mass-props identity; `ray`
    is a probe fired from outside the body's bbox toward its centroid,
    captured on source while the body was alive. Target re-fires the ray
    inside its own notify callback to identify the corresponding fragment
    by geometric position (robust to mass-props drift across rebuilds).
    Mirrors C# `Sldworks.Core.Definitions.BodyProbe`.
    """
    body: BodyDefinition
    ray: Ray
