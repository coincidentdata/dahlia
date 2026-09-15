"""Base pydantic model for all dahlia types.

Defines the shared `_Base` model class plus two `Field()` helpers — `In` and
`Echo` — that tag each field with `role="input"` or `role="echo"` metadata.
The role metadata is the single source of truth for which fields are
author-writable on the wire vs. read-only echo fields that SolidWorks fills
in on Inspect / Add response. The diff harness and the wire-shape inventory
generator both read from this metadata; consumers like
`sldworks_tests/diff/tolerance.py::ECHO_FIELD_NAMES` build a flat set of
echo-field names by walking every pydantic class registered here.
"""
from __future__ import annotations

from typing import Any

from pydantic import BaseModel, ConfigDict, Field


class _Base(BaseModel):
    """Base model: forbid extra fields, freeze nothing (builders mutate)."""
    model_config = ConfigDict(extra="forbid", populate_by_name=True)


# `_MISSING` sentinel distinguishes "field has no default" (required) from
# "field defaults to None" — pydantic uses Ellipsis (...) for required.
_MISSING: Any = ...


def In(default: Any = _MISSING, **kwargs: Any) -> Any:
    """Mark a field as wire-input (author-writable). Equivalent to a regular
    `Field(...)` plus the `role="input"` metadata. Pass `default=...` for
    required fields (mirrors pydantic) or any other value for an optional
    default. Use `default_factory=...` for mutable defaults (lists, dicts)."""
    extra = dict(kwargs.pop("json_schema_extra", {}))
    extra.setdefault("role", "input")
    if "default_factory" in kwargs:
        return Field(json_schema_extra=extra, **kwargs)
    return Field(default, json_schema_extra=extra, **kwargs)


def Echo(default: Any = None, **kwargs: Any) -> Any:
    """Mark a field as wire-echo (Inspect-only, never author-writable on the
    Add path). Same shape as `In` but with `role="echo"` metadata so the diff
    harness and inventory generator can pick it up."""
    extra = dict(kwargs.pop("json_schema_extra", {}))
    extra.setdefault("role", "echo")
    if "default_factory" in kwargs:
        return Field(json_schema_extra=extra, **kwargs)
    return Field(default, json_schema_extra=extra, **kwargs)
