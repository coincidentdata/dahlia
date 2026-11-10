"""Common discriminator base for every *Definition flavor."""
from __future__ import annotations

from typing import Optional

from ._base import _Base, Echo


class _DefinitionBase(_Base):
    """Common shape for definitions; kind is the discriminator literal."""
    pass


class _SolidGeometryDefinition(_DefinitionBase):
    # Backend-scoped identity captured on inspect (an Onshape deterministic id). A backend
    # that recognizes its own id may use it instead of geometric resolution; ids are not
    # portable across documents or CAD systems.
    native_id: Optional[str] = Echo(None)
