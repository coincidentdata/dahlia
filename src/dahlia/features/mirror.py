from __future__ import annotations

from typing import Literal

from pydantic import Field, field_validator, model_validator

from ..helpers import feature_ref
from ._base import DefinitionValue, _Base


class Mirror(_Base):
    type: Literal["Mirror"] = "Mirror"
    name: str | None = None
    plane: DefinitionValue
    seeds: list[DefinitionValue] = Field(min_length=1)
    secondary_plane: DefinitionValue | None = None
    mirror_seed_only: bool = False
    geometry_pattern: bool = False
    merge: bool = True
    knit_surfaces: bool = False
    propagate_visual_properties: bool = True
    scope: Literal["AllBodies", "AutoSelect", "SelectedBodies"] = "AllBodies"
    feature_scope: list[DefinitionValue] = Field(default_factory=list)

    @field_validator("plane", "secondary_plane", mode="before")
    @classmethod
    def _plane_reference(cls, value):
        return feature_ref(value) if isinstance(value, str) else value

    @field_validator("seeds", "feature_scope", mode="before")
    @classmethod
    def _references(cls, values):
        return [feature_ref(v) if isinstance(v, str) else v for v in values]

    @model_validator(mode="after")
    def _options(self):
        if self.mirror_seed_only and self.secondary_plane is None:
            raise ValueError("mirror_seed_only requires secondary_plane")
        if self.scope == "SelectedBodies" and not self.feature_scope:
            raise ValueError("SelectedBodies requires feature_scope")
        if self.scope == "AllBodies" and self.feature_scope:
            raise ValueError("feature_scope requires AutoSelect or SelectedBodies scope")
        return self


def mirror(
    *, plane: DefinitionValue, seeds: list[DefinitionValue],
    secondary_plane: DefinitionValue | None = None,
    mirror_seed_only: bool = False, geometry_pattern: bool = False,
    merge: bool = True, knit_surfaces: bool = False,
    propagate_visual_properties: bool = True,
    scope: Literal["AllBodies", "AutoSelect", "SelectedBodies"] = "AllBodies",
    feature_scope: list[DefinitionValue] | None = None,
    name: str | None = None,
) -> Mirror:
    return Mirror(
        plane=plane, seeds=seeds, secondary_plane=secondary_plane,
        mirror_seed_only=mirror_seed_only, geometry_pattern=geometry_pattern,
        merge=merge, knit_surfaces=knit_surfaces,
        propagate_visual_properties=propagate_visual_properties,
        scope=scope, feature_scope=feature_scope or [], name=name,
    )
