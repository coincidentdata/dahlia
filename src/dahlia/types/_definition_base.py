"""Common discriminator base for every *Definition flavor."""
from __future__ import annotations

from ._base import _Base


class _DefinitionBase(_Base):
    """Common shape for definitions; kind is the discriminator literal."""
    pass
