"""Combine / Split (body boolean) feature models + lowercase factories."""
from __future__ import annotations

from typing import Literal, Optional

from pydantic import Field, field_validator, model_validator

from ..types import Ray, BodyDefinition, Echo
from ._base import _Base, DefinitionValue


# -- Combine / Split (body booleans) ---------------------------------------
#
# Combine adds, subtracts, or intersects solid bodies. Subtract distinguishes
# `main_body` (kept, with tools cut out) from `bodies` (the tools). Add and
# Common (intersection) treat every body equally and leave `main_body` unset.
#
# Split slices solids by trim surfaces (faces, planes, surface bodies). The
# Add is two-phase to give a stable commit story:
#   Phase 1 — `add_feature(Split)`: commits the Split with EVERY PreSplitBody2
#     candidate marked and consume=False. SW rejects an empty `Bodies` arg,
#     so this is the only commit shape guaranteed to succeed regardless of
#     the author's actual intent.
#   Phase 2 — `target.edit_split(name, consume_marked_bodies, marked_bodies)`:
#     re-commits the feature with the real consume flag and the real marked
#     subset (resolved via probe-swap onto target's PreSplitBody2 candidates).
# Both `consume_marked_bodies` and `marked_bodies` are normal input fields on
# the Split model — the C# Add ignores them, the runner shuttles them via
# `edit_split` post-Add.

class Combine(_Base):
    type: Literal["Combine"] = "Combine"
    operation: Literal["Add", "Subtract", "Common"]
    bodies: list[DefinitionValue]
    # Required only for Subtract; the body to keep that the tools are cut FROM.
    main_body: Optional[DefinitionValue] = None
    name: Optional[str] = None
    # Add-result only echo — list of solid-body Definitions present in the
    # part AFTER the combine ran. The C# CombineHandler.Add emits this so
    # callers can pick up the result body identities without a separate
    # Inspect call. Inspect itself does not emit it.
    echo_bodies_result: Optional[list[BodyDefinition]] = Echo(default=None)

    @field_validator("bodies")
    @classmethod
    def _bodies_nonempty(cls, v: list[DefinitionValue]) -> list[DefinitionValue]:
        if not v:
            raise ValueError("Combine: 'bodies' must contain at least one body")
        return v

    @model_validator(mode="after")
    def _check_main_body(self) -> "Combine":
        # Subtract distinguishes a kept main body from the tools cut out of it;
        # Add and Common treat every body equally and have no main_body slot.
        # The C# handler enforces this at parse time too — the pydantic check
        # surfaces the error client-side first.
        if self.operation == "Subtract" and self.main_body is None:
            raise ValueError(
                "Combine: 'main_body' is required when operation is 'Subtract'")
        if self.operation in ("Add", "Common") and self.main_body is not None:
            raise ValueError(
                f"Combine: 'main_body' must be None when operation is '{self.operation}'")
        return self


class Split(_Base):
    type: Literal["Split"] = "Split"
    trim_surfaces: list[DefinitionValue]
    consume_marked_bodies: bool = False
    marked_bodies: list[BodyDefinition] = Field(default_factory=list)
    # One ray per marked fragment, aimed at it and away from its siblings. The
    # authorable selector: split fragments are transient, so a ray naming a place
    # is the only handle that survives to a rebuild. Takes precedence over
    # `marked_bodies` when present.
    marked_body_rays: list[Ray] = Field(default_factory=list)
    name: Optional[str] = None
    echo_bodies_result: Optional[list[BodyDefinition]] = Echo(default=None)

    @field_validator("trim_surfaces")
    @classmethod
    def _trim_nonempty(cls, v: list[DefinitionValue]) -> list[DefinitionValue]:
        if not v:
            raise ValueError("Split: 'trim_surfaces' must contain at least one entry")
        return v


# -- factories -------------------------------------------------------------

def combine(*, operation: str, bodies: list[DefinitionValue],
            main_body: Optional[DefinitionValue] = None,
            name: Optional[str] = None) -> Combine:
    return Combine(operation=operation,  # type: ignore[arg-type]
                   bodies=bodies, main_body=main_body, name=name)


def split(*, trim_surfaces: list[DefinitionValue],
          marked_bodies: Optional[list[BodyDefinition]] = None,
          marked_body_rays: Optional[list[Ray]] = None,
          consume_marked_bodies: bool = False,
          name: Optional[str] = None) -> Split:
    return Split(trim_surfaces=trim_surfaces,
                 marked_bodies=marked_bodies or [],
                 marked_body_rays=marked_body_rays or [],
                 consume_marked_bodies=consume_marked_bodies, name=name)


__all__ = ["Combine", "Split", "combine", "split"]
