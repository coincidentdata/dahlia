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
from .extrude import Extrude, CutExtrude, ExtrudeDirection, extrude, cut_extrude
from .revolve import Revolve, CutRevolve, RevolveExtent, revolve, cut_revolve
from .fillet import Fillet, ConstantRadiusFillet, FaceFillet, FullRoundFillet, FilletProfile, fillet, face_fillet, full_round_fillet
from .chamfer import Chamfer, EdgeChamfer, VertexChamfer, ChamferSize, chamfer
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
from .helix import Helix, HelixDefinition, helix
from .loft import Loft, loft, CutLoft, cut_loft, LoftConstraint, LoftTangency, LoftGuideInfluence, LoftThin
from .mirror import Mirror, FeatureMirror, BodyMirror, MirrorScope, mirror
from .shell import Shell, shell, ShellWall, shell_wall
from .draft import Draft, NeutralPlaneDraft, PartingLineDraft, StepDraft, draft, DraftEdge, draft_edge
from .rib import Rib, RibSides, RibDirection, rib
from .projected_curve import ProjectedCurve, SketchOnFaces, SketchOnSketch, projected_curve
from .combine_split import Combine, Split, combine, split
from .refplane_axis import (
    PlaneReference,
    RefPlane,
    ConstraintRefPlane,
    LegacyRefPlane,
    LegacyDistanceRefPlane,
    LegacyAngleRefPlane,
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
    "CutExtrude", "cut_extrude", "ExtrudeDirection",
    "Revolve", "revolve",
    "CutRevolve", "cut_revolve", "RevolveExtent",
    "Fillet", "fillet", "ConstantRadiusFillet", "FaceFillet", "FullRoundFillet", "FilletProfile",
    "Chamfer", "chamfer", "EdgeChamfer", "VertexChamfer", "ChamferSize",
    "LinearPattern", "linear_pattern",
    "CircularPattern", "circular_pattern",
    "Mirror", "mirror", "FeatureMirror", "BodyMirror", "MirrorScope",
    "Shell", "shell", "ShellWall", "shell_wall",
    "Draft", "draft", "DraftEdge", "draft_edge", "NeutralPlaneDraft", "PartingLineDraft", "StepDraft",
    "Rib", "rib", "RibSides", "RibDirection",
    "Sweep", "sweep",
    "CutSweep", "cut_sweep",
    "Helix", "helix", "HelixDefinition",
    "Loft", "loft", "CutLoft", "cut_loft",
    "LoftConstraint", "LoftTangency", "LoftGuideInfluence", "LoftThin",
    "ProjectedCurve", "SketchOnFaces", "SketchOnSketch", "projected_curve",
    "Combine", "combine",
    "Split", "split",
    "RefPlane", "ref_plane", "PlaneReference", "ConstraintRefPlane", "LegacyRefPlane", "LegacyDistanceRefPlane",
    "LegacyAngleRefPlane",
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
    "Feature",
]
