from __future__ import annotations

import math
from typing import Annotated, TYPE_CHECKING

from pydantic import Field, model_validator

from .types import (
    ComponentDefinition, ComponentEntityDefinition, Definition, FeatureDefinition,
    Point3D, Ray, _Base,
)

if TYPE_CHECKING:
    from .file import File


class Transform(_Base):
    """Rigid placement in the receiving File's assembly frame, including nested occurrences."""

    translation: Point3D = Point3D(x=0, y=0, z=0)
    rotation: tuple[tuple[float, float, float], ...] = (
        (1, 0, 0), (0, 1, 0), (0, 0, 1),
    )

    @model_validator(mode="after")
    def _rigid(self) -> Transform:
        rows = self.rotation
        values = [*self.translation.model_dump().values(), *(v for row in rows for v in row)]
        if len(rows) != 3 or not all(math.isfinite(v) for v in values):
            raise ValueError("transform requires a finite translation and 3x3 rotation")
        for i in range(3):
            for j in range(3):
                if abs(sum(rows[i][k] * rows[j][k] for k in range(3)) - (i == j)) > 1e-9:
                    raise ValueError("rotation must be orthonormal")
        a, b, c = rows
        determinant = (a[0] * (b[1] * c[2] - b[2] * c[1])
                       - a[1] * (b[0] * c[2] - b[2] * c[0])
                       + a[2] * (b[0] * c[1] - b[1] * c[0]))
        if abs(determinant - 1) > 1e-9:
            raise ValueError("rotation cannot scale or reflect geometry")
        return self


def transform(translation=(0, 0, 0), *, rotation=None, axis=None, angle=None) -> Transform:
    """Rotate about the local origin, then translate; angle is in radians."""
    if rotation is not None and (axis is not None or angle is not None):
        raise ValueError("provide rotation or axis/angle, not both")
    if (axis is None) != (angle is None):
        raise ValueError("axis and angle must be provided together")
    if axis is not None:
        x, y, z = axis
        length = math.sqrt(x*x + y*y + z*z)
        if not math.isfinite(length) or length == 0 or not math.isfinite(angle):
            raise ValueError("axis must be finite and nonzero; angle must be finite")
        x, y, z = x / length, y / length, z / length
        c, s = math.cos(angle), math.sin(angle)
        d = 1 - c
        rotation = (
            (c + x*x*d, x*y*d - z*s, x*z*d + y*s),
            (y*x*d + z*s, c + y*y*d, y*z*d - x*s),
            (z*x*d - y*s, z*y*d + x*s, c + z*z*d),
        )
    return Transform(translation=translation, **({"rotation": rotation} if rotation is not None else {}))


_ColorChannel = Annotated[float, Field(ge=0, le=1, allow_inf_nan=False)]


class ComponentInput(_Base):
    source: str = Field(min_length=1)
    configuration: str | None = None
    transform: Transform = Field(default_factory=Transform)
    fixed: bool = False
    visible: bool = True
    suppressed: bool = False
    color: tuple[_ColorChannel, _ColorChannel, _ColorChannel, _ColorChannel] | None = None


class Component:
    """One occurrence in an assembly, re-resolved by path on every operation."""

    def __init__(self, assembly: File, path: tuple[str, ...] | list[str]):
        self.assembly = assembly
        self.path = ComponentDefinition(path=path).path

    def __repr__(self) -> str:
        return f"Component({self.assembly.name!r}, {self.path!r})"

    def ref(self, entity: Definition | dict | str | None = None):
        if entity is None:
            return ComponentDefinition(path=self.path)
        if isinstance(entity, str):
            entity = FeatureDefinition(name=entity)
        return ComponentEntityDefinition(component=self.path, entity=entity)

    def probe(self, ray: Ray | dict, entity_type: str, on_body=None) -> dict | None:
        return self.assembly._transport().call(
            "ProbeComponent", file=self.assembly.name, component=list(self.path),
            ray=ray.model_dump() if isinstance(ray, Ray) else ray,
            entity_type=entity_type,
            on_body=on_body.model_dump() if isinstance(on_body, _Base) else on_body,
        )

    def generate_probe(self, definition: Definition | dict, entity_type: str, start_attempt=0) -> dict:
        """Generate a local ray for a local geometry Definition in this occurrence's configuration."""
        return self.assembly._transport().call(
            "GenerateComponentProbe", file=self.assembly.name, component=list(self.path),
            definition=definition.model_dump() if isinstance(definition, _Base) else definition,
            entity_type=entity_type, start_attempt=start_attempt,
        )


def component_path(assembly: File, component: Component | tuple[str, ...] | list[str]) -> list[str]:
    if isinstance(component, Component):
        if component.assembly is not assembly and component.assembly.name != assembly.name:
            raise ValueError("component belongs to a different assembly")
        return list(component.path)
    return list(ComponentDefinition(path=component).path)
