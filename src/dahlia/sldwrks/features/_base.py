"""Shared base + value-alias imports for the `features` package.

`_Base` is the pydantic BaseModel base defined in `..types`; feature modules
import it from here so the package has a single canonical place for the
shared aliases (DefinitionValue, ContourRef) that every feature shape uses.
"""
from __future__ import annotations

from typing import Annotated, Any, Union

from pydantic import BaseModel as _BaseModel
from pydantic import BeforeValidator, SerializeAsAny

from ..types import (
    _Base,
    Echo,
    In,
    SketchContourDefinition,
)


# A Definition value, in the wire shape the model passes around. Accepts:
#   - a raw dict (e.g. f.probe(...) returns this);
#   - a string name ('Edge1', 'Axis1' for hand-authored references);
#   - a sketch-entity Definition instance (Line/Circle/...) — the entity IS
#     the Definition, so the model can write `extrude(direction=line)` after
#     capturing `line = s.add_line(...)` and it serializes as its own
#     Definition flavor at the wire boundary.
#
# `SerializeAsAny` is what makes that last case actually work. Pydantic v2
# serializes by the DECLARED type, so a sketch-entity instance assigned to a
# plain `_BaseModel` field serializes as `{}` — the reference silently vanishes
# from the wire and the feature is built against nothing. Duck-typed
# serialization keeps the concrete flavor's fields.
DefinitionValue = Union[dict, str, SerializeAsAny[_BaseModel]]


# A contour selection on Extrude / CutExtrude / Revolve / CutRevolve. The wire
# shape is a `SketchContourDefinition` (a list of constituent SketchSegment
# definitions); a raw dict is also accepted for round-trip from the wire.
# `SketchEntityDefinition` / single-segment dicts are not in the design but
# the C# resolver tolerates them, so the type stays loose to avoid breaking
# manual JSON authoring.
ContourRef = Union[SketchContourDefinition, dict]


def named(union: Any, key: str) -> Any:
    """`union`, discriminated on `key`, that also takes a bare mode name for the modes with no
    other options: `twist="FollowPath"` for `{"twist": "FollowPath"}`. A mode that needs a value
    fails on the missing field, which is the right error. The validator wraps the union rather
    than sitting beside its discriminator: beside it, pydantic skips it under Optional[...]."""
    return Annotated[union, BeforeValidator(lambda v: {key: v} if isinstance(v, str) else v)]


__all__ = ["_Base", "DefinitionValue", "ContourRef", "Echo", "In", "named"]
