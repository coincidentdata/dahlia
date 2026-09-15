"""Feature pydantic models + lowercase factory functions.

Same shape on add and inspect, with read-only computed fields appended on
inspect (marked here on the model).

Per-kind classes / factories live in their respective submodules; this
`__init__` re-exports the public surface.
"""
from __future__ import annotations

from typing import Annotated, Union

from pydantic import Field

# Re-export Sketch / sketch so callers can import them from this package.
from ..sketch import Sketch, sketch, _is_sketch_entity  # noqa: F401  (re-export)

from ._base import _Base, DefinitionValue, ContourRef  # noqa: F401  (re-export)
from ._end_conditions import (
    _EndConditionDistance,
    _EndConditionThrough,
    _EndConditionUpTo,
    _EndConditionOffFrom,
    EndCondition,
    _ExtrudeStartSketch,
    _ExtrudeStartSurface,
    _ExtrudeStartVertex,
    _ExtrudeStartOffset,
    _ExtrudeStart,
)
from .extrude import Extrude, CutExtrude, extrude, cut_extrude
from .revolve import Revolve, CutRevolve, revolve, cut_revolve
from .fillet import Fillet, fillet, face_fillet, full_round_fillet
from .chamfer import Chamfer, chamfer
from .pattern import (
    _LinearPatternSpacingAndInstances,
    _LinearPatternUpToReference,
    _LinearPatternEndCondition,
    LinearPattern,
    CircularPattern,
    linear_pattern,
    circular_pattern,
)
from .sweep import (
    _SweepTwistNone,
    _SweepTwistSpecified,
    _SweepTwistDirection,
    SweepTwist,
    SweepTangency,
    Sweep,
    CutSweep,
    sweep,
    cut_sweep,
)
from .helix import Helix, helix
from .loft import Loft, loft, CutLoft, cut_loft, LoftTangency, LoftGuideInfluence, LoftThinWallType
from .mirror import Mirror, mirror
from .shell import Shell, shell, ShellWall, shell_wall
from .draft import Draft, draft, DraftEdge, draft_edge
from .rib import Rib, rib
from .projected_curve import ProjectedCurve, projected_curve
from .combine_split import Combine, Split, combine, split
from .refplane_axis import (
    RefPlaneConstraint,
    RefPlaneModifier,
    RefPlaneSubtype,
    RefPlane,
    RefAxis,
    ref_plane,
    ref_axis,
)


# Annotated union for AddFeature wire shape. Must reference every concrete
# feature class — defined here at the end of __init__ so all the imports are
# resolved before the Union is constructed.
from .. import mates

Feature = Annotated[
    Union[
        mates.Coincident, mates.Concentric, mates.Parallel, mates.Perpendicular,
        mates.Tangent, mates.Lock, mates.Distance, mates.Angle,
        Sketch,
        Extrude,
        CutExtrude,
        Revolve,
        CutRevolve,
        Fillet,
        Chamfer,
        LinearPattern,
        CircularPattern,
        Mirror,
        Shell,
        Draft,
        Rib,
        Sweep,
        CutSweep,
        Helix,
        Loft,
        CutLoft,
        ProjectedCurve,
        Combine,
        Split,
        RefPlane,
        RefAxis,
    ],
    Field(discriminator="type"),
]


__all__ = [
    "Sketch", "sketch",
    "Extrude", "extrude",
    "CutExtrude", "cut_extrude",
    "Revolve", "revolve",
    "CutRevolve", "cut_revolve",
    "Fillet", "fillet",
    "Chamfer", "chamfer",
    "LinearPattern", "linear_pattern",
    "CircularPattern", "circular_pattern",
    "Mirror", "mirror",
    "Shell", "shell", "ShellWall", "shell_wall",
    "Draft", "draft", "DraftEdge", "draft_edge",
    "Rib", "rib",
    "Sweep", "sweep",
    "CutSweep", "cut_sweep",
    "Helix", "helix",
    "Loft", "loft", "CutLoft", "cut_loft",
    "LoftTangency", "LoftGuideInfluence", "LoftThinWallType",
    "ProjectedCurve", "projected_curve",
    "Combine", "combine",
    "Split", "split",
    "RefPlane", "ref_plane",
    "RefAxis", "ref_axis",
    "face_fillet", "full_round_fillet",
    "SweepTwist",
    "SweepTangency",
    "_SweepTwistNone",
    "_SweepTwistSpecified",
    "_SweepTwistDirection",
    "_LinearPatternSpacingAndInstances",
    "_LinearPatternUpToReference",
    "_LinearPatternEndCondition",
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
    "RefPlaneConstraint",
    "RefPlaneModifier",
    "RefPlaneSubtype",
    "Feature",
]
