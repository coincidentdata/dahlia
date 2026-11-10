"""Mirror feature models + lowercase factory.

`Mirror` is keyed by what it mirrors: `Features` (feature or face seeds), which can scope the
result to bodies, or `Bodies` (body seeds), which merge or knit. Either can mirror again across
a `secondary` plane.
"""
from __future__ import annotations

from typing import Annotated, Literal, Optional, Union

from pydantic import Field, TypeAdapter, field_validator

from ..helpers import feature_ref
from ._base import DefinitionValue, _Base, named


def _ref(value):
    return feature_ref(value) if isinstance(value, str) else value


class _MirrorSecondary(_Base):
    plane: DefinitionValue
    seed_only: bool = False  # mirror only the seeds across it, not their first-plane copies

    _plane = field_validator("plane", mode="before")(classmethod(lambda cls, v: _ref(v)))


class _ScopeDefault(_Base):
    scope: Literal["AllBodies", "AutoSelect"] = "AllBodies"


class _ScopeSelected(_Base):
    scope: Literal["SelectedBodies"] = "SelectedBodies"
    bodies: list[DefinitionValue] = Field(min_length=1)


MirrorScope = named(Annotated[Union[_ScopeDefault, _ScopeSelected], Field(discriminator="scope")], "scope")


class _Mirror(_Base):
    type: Literal["Mirror"] = "Mirror"
    name: Optional[str] = None
    plane: DefinitionValue
    seeds: list[DefinitionValue] = Field(min_length=1)
    secondary: Optional[_MirrorSecondary] = None
    propagate_visual_properties: bool = True

    _plane = field_validator("plane", mode="before")(classmethod(lambda cls, v: _ref(v)))
    _seeds = field_validator("seeds", mode="before")(classmethod(lambda cls, v: [_ref(x) for x in v]))


class FeatureMirror(_Mirror):
    kind: Literal["Features"] = "Features"  # feature or face seeds
    geometry_pattern: bool = False
    scope: MirrorScope = Field(default_factory=_ScopeDefault)


class BodyMirror(_Mirror):
    kind: Literal["Bodies"] = "Bodies"
    merge: bool = True
    knit_surfaces: bool = False


Mirror = Annotated[Union[FeatureMirror, BodyMirror], Field(discriminator="kind")]
_MIRROR = TypeAdapter(Mirror)


def mirror(*, plane: DefinitionValue, seeds: list[DefinitionValue], kind: Literal["Features", "Bodies"] = "Features",
           secondary: Optional[_MirrorSecondary] = None, propagate_visual_properties: bool = True,
           geometry_pattern: Optional[bool] = None, scope: Optional[MirrorScope] = None,
           merge: Optional[bool] = None, knit_surfaces: Optional[bool] = None, name: Optional[str] = None) -> Mirror:
    """A mirror of `kind`; pass only that kind's options (Features: geometry_pattern + scope;
    Bodies: merge + knit_surfaces)."""
    options = {"geometry_pattern": geometry_pattern, "scope": scope, "merge": merge, "knit_surfaces": knit_surfaces}
    return _MIRROR.validate_python({"kind": kind, "plane": plane, "seeds": seeds, "secondary": secondary,
                                    "propagate_visual_properties": propagate_visual_properties, "name": name,
                                    **{k: v for k, v in options.items() if v is not None}})


__all__ = ["Mirror", "FeatureMirror", "BodyMirror", "MirrorScope", "mirror"]
