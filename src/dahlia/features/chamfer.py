"""Chamfer feature model + lowercase factory."""
from __future__ import annotations

from typing import Literal, Optional

from pydantic import Field, model_validator

from ..types import Echo
from ._base import _Base, DefinitionValue


# -- Chamfer ---------------------------------------------------------------
#
# Chamfer carries two flavors in a single class with an explicit `kind`
# discriminator (the top-level `Feature` union already uses `type="Chamfer"`,
# so the inner discriminator goes on `kind` instead).
#
#   kind="Edge" (default):
#     - `edges` is a list of edge / loop / face Definitions.
#     - Mode is implied by which optional field is set:
#         * `angle` set                       → AngleDistance
#         * `distance_b` set, `angle` null   → DistanceDistance (asymmetric)
#         * both null                        → EqualDistance (distance_b = distance)
#     - `flipped` only meaningful for AngleDistance (maps to
#       swFeatureChamferFlipDirection).
#
#   kind="Vertex":
#     - `vertices` is a list of vertex Definitions (singleton in practice;
#       SolidWorks vertex chamfer is one vertex with 3 incident edges).
#     - `distance_a` / `distance_b_vertex` / `distance_c` are the per-edge
#       distances along the three edges meeting at the vertex.
#     - `tangent_propagation` is inapplicable; `flipped` is inapplicable.
#
# `keep_features` is shared and defaults to True (the SolidWorks UI default
# — chamfer KEEPS features the chamfer would otherwise consume).

class Chamfer(_Base):
    type: Literal["Chamfer"] = "Chamfer"
    # Inspect/Add-result only — SolidWorks-assigned feature name. See Extrude.name.
    name: Optional[str] = None
    # Inner discriminator. Default "Edge" matches the SolidWorks UI default
    # (callers that don't set `kind` get the Edge variant).
    kind: Literal["Edge", "Vertex"] = "Edge"

    # Edge-variant fields (kind="Edge")
    # Empty list when kind="Vertex".
    edges: list[DefinitionValue] = Field(default_factory=list)
    distance: Optional[float] = None  # required for Edge; ignored for Vertex
    distance_b: Optional[float] = None  # for asymmetric distance-distance (Edge only)
    angle: Optional[float] = None       # if set, AngleDistance mode (Edge only)
    tangent_propagation: bool = True
    # Only meaningful for Edge+AngleDistance, but the wire schema carries it
    # on the Chamfer class for symmetry. Maps to the SolidWorks options bit
    # swFeatureChamferFlipDirection.
    flipped: bool = False

    # Vertex-variant fields (kind="Vertex")
    # Vertex defs (vertex Definitions, not edges). Empty list when kind="Edge".
    vertices: list[DefinitionValue] = Field(default_factory=list)
    distance_a: Optional[float] = None       # per-edge distance for vertex chamfer
    distance_b_vertex: Optional[float] = None  # second per-edge distance (renamed to
                                              # avoid clash with edge `distance_b`)
    distance_c: Optional[float] = None       # third per-edge distance

    # Defaults to True. Maps to swFeatureChamferKeepFeature.
    keep_features: bool = True

    # Inspect-only echo: faces SW reports as affected by the chamfer operation.
    echo_affected_faces: Optional[list[dict]] = Echo(default=None)

    @model_validator(mode="after")
    def _check_kind_fields(self) -> "Chamfer":
        # Per-kind invariants. Surfacing the error here gives a clean
        # Python-side message before the C# wire boundary rejects an
        # under-specified payload.
        if self.kind == "Edge":
            if not self.edges:
                raise ValueError("Chamfer[Edge]: edges must be non-empty")
            if self.distance is None or self.distance <= 0:
                raise ValueError("Chamfer[Edge]: distance must be > 0")
            if self.vertices:
                raise ValueError(
                    "Chamfer[Edge]: vertices must be empty (use kind='Vertex')")
            if (self.distance_a is not None or
                self.distance_b_vertex is not None or
                self.distance_c is not None):
                raise ValueError(
                    "Chamfer[Edge]: distance_a / distance_b_vertex / distance_c "
                    "are only valid for kind='Vertex'")
            if self.angle is not None and self.distance_b is not None:
                # Same invariant the C# handler enforces — they're separate modes.
                raise ValueError(
                    "Chamfer[Edge]: 'angle' and 'distance_b' are mutually "
                    "exclusive — pick one mode")
            # `flipped` only honored by AngleDistance (Edge + angle set). The C#
            # handler silently ignores it for Distance / EqualDistance edge modes —
            # reject here so the wire matches behavior.
            if self.flipped and self.angle is None:
                raise ValueError(
                    "Chamfer[Edge]: 'flipped' is only valid in AngleDistance mode "
                    "(set 'angle' alongside 'distance')")
        else:  # kind == "Vertex"
            if not self.vertices:
                raise ValueError("Chamfer[Vertex]: vertices must be non-empty")
            for label, val in (
                ("distance_a", self.distance_a),
                ("distance_b_vertex", self.distance_b_vertex),
                ("distance_c", self.distance_c),
            ):
                if val is None or val <= 0:
                    raise ValueError(f"Chamfer[Vertex]: {label} must be > 0")
            if self.edges:
                raise ValueError(
                    "Chamfer[Vertex]: edges must be empty (use kind='Edge')")
            if (self.distance is not None or
                self.distance_b is not None or
                self.angle is not None):
                raise ValueError(
                    "Chamfer[Vertex]: distance / distance_b / angle are only "
                    "valid for kind='Edge'")
            if self.flipped:
                raise ValueError(
                    "Chamfer[Vertex]: 'flipped' is only valid for kind='Edge'")
            # `tangent_propagation` is meaningless for vertex chamfers — the C#
            # handler ignores it. Pin to the default (True) for Vertex; reject
            # any non-default value so the wire shape stays honest.
            if self.tangent_propagation is not True:
                raise ValueError(
                    "Chamfer[Vertex]: 'tangent_propagation' is only valid for "
                    "kind='Edge' (vertex chamfers have no tangent edges to propagate)")
        return self


# -- factories -------------------------------------------------------------

def chamfer(*, edges: Optional[list[DefinitionValue]] = None,
            distance: Optional[float] = None,
            distance_b: Optional[float] = None,
            angle: Optional[float] = None,
            tangent_propagation: bool = True,
            flipped: bool = False,
            keep_features: bool = True,
            kind: str = "Edge",
            vertices: Optional[list[DefinitionValue]] = None,
            distance_a: Optional[float] = None,
            distance_b_vertex: Optional[float] = None,
            distance_c: Optional[float] = None,
            name: Optional[str] = None) -> Chamfer:
    return Chamfer(
        kind=kind,  # type: ignore[arg-type]
        edges=edges or [],
        distance=distance, distance_b=distance_b, angle=angle,
        tangent_propagation=tangent_propagation,
        flipped=flipped,
        keep_features=keep_features,
        vertices=vertices or [],
        distance_a=distance_a,
        distance_b_vertex=distance_b_vertex,
        distance_c=distance_c,
        name=name,
    )


__all__ = ["Chamfer", "chamfer"]
