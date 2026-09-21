"""Reference geometry Definition flavors: RefAxis, TempAxis, RefPlane."""
from __future__ import annotations

from typing import Literal, Optional

from pydantic import Field

from ._base import Echo
from ._definition_base import _DefinitionBase
from .primitives import Direction, Point3D


class ComponentDefinition(_DefinitionBase):
    kind: Literal["component"] = "component"
    path: tuple[str, ...] = Field(min_length=1)


class ComponentEntityDefinition(_DefinitionBase):
    kind: Literal["component_entity"] = "component_entity"
    component: tuple[str, ...] = Field(min_length=1)
    entity: "Definition"


class RefAxisDefinition(_DefinitionBase):
    # A RefAxis is a FEATURE — resolved by NAME (no geometry match). start/end are the
    # displayed-extent endpoints kept ONLY to show the user which axis this is: echo
    # (drift across rebuilds, so not diffed; the emitter strips them from the reference).
    kind: Literal["ref_axis"] = "ref_axis"
    name: str
    echo_start: Optional[Point3D] = Echo(default=None)
    echo_end: Optional[Point3D] = Echo(default=None)


class TempAxisDefinition(_DefinitionBase):
    # Implicit axis of a curved face (cylinder / cone / torus). Identity rides
    # on (start, end) — the displayed-extent endpoints from GetRefAxisParams,
    # sorted lex. Resolver SelectByID2("AXIS") at the midpoint to acquire the
    # live RefAxis proxy.
    kind: Literal["temp_axis"] = "temp_axis"
    start: Point3D
    end: Point3D


class RefPlaneDefinition(_DefinitionBase):
    # A RefPlane is a FEATURE (default planes too) — resolved by NAME (no geometry match).
    # x/y/z/origin are the plane basis kept ONLY to show the user which plane this is:
    # echo (the X/Y basis drifts across rebuilds, so not diffed; the emitter strips them).
    kind: Literal["ref_plane"] = "ref_plane"
    name: str
    echo_x: Optional[Direction] = Echo(default=None)
    echo_y: Optional[Direction] = Echo(default=None)
    echo_z: Optional[Direction] = Echo(default=None)
    echo_origin: Optional[Point3D] = Echo(default=None)
