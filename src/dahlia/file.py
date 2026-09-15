"""File handle: the model's per-file API.

Every dispatching method (``add_feature``, ``view``, ``inspect_feature``,
etc.) routes through the process-wide transport singleton owned by
``session``. Callers MUST invoke ``dahlia.connect()``
before any dispatch; otherwise a ``RuntimeError`` is raised.

Pure construction (``Sketch``/``Extrude``/etc. and pydantic validation)
remains import-safe and connect-free.
"""
from __future__ import annotations

import base64
from pathlib import Path
from typing import Any, Optional, Union

from pydantic import BaseModel, ConfigDict, TypeAdapter

from .constants import _Unit
from .features import Feature
from .types import Ray

_FEATURE_ADAPTER = TypeAdapter(Feature, config=ConfigDict(title="Feature"))


class File:
    """Handle to a part or assembly file open in SolidWorks.

    Constructed by ``open_file`` / ``create_file`` / ``list_files`` /
    ``get_active_file``. Direct construction is allowed (e.g. to wrap a
    name returned by some other path) but every method call still requires
    ``connect()`` to have been called first.

    The ``_test_transport`` keyword-only argument is a tests-only escape
    hatch: when set, dispatch goes through the supplied transport instead
    of consulting the singleton. The leading underscore signals "do not
    use in user code" and the keyword-only form prevents accidental
    positional use. Production callers must never pass it.
    """

    def __init__(self, name: str, *, _test_transport: Optional[Any] = None) -> None:
        self.name = name
        self._test_transport = _test_transport

    # internal --------------------------------------------------------

    def _transport(self) -> Any:
        """Return the transport this file should dispatch through.

        Resolves lazily so the singleton remains the single source of
        truth: a later ``connect()`` / ``disconnect()`` is honored on
        the very next call.
        """
        if self._test_transport is not None:
            return self._test_transport
        # Lazy import to dodge the session <-> file circular.
        from .session import _require_transport
        return _require_transport()

    # mutation -----------------------------------------------------------

    def add_component(self, source, *, configuration=None, transform=None,
                      fixed=False, visible=True, suppressed=False, color=None):
        from .assembly import Component, ComponentInput
        payload = ComponentInput(
            source=str(Path(source).resolve()), configuration=configuration, fixed=fixed,
            visible=visible, suppressed=suppressed, color=color,
            **({"transform": transform} if transform is not None else {}),
        )
        result = self._transport().call("AddComponent", file=self.name, component=payload.model_dump())
        return Component(self, result["path"])

    def component(self, path):
        from .assembly import Component
        return Component(self, path)

    def components(self) -> list[dict]:
        return self._transport().call("GetComponents", file=self.name)

    def inspect_component(self, component) -> dict:
        from .assembly import component_path
        return self._transport().call(
            "InspectComponent", file=self.name, component=component_path(self, component))

    def edit_component(self, component, **changes) -> dict:
        from .assembly import ComponentInput, component_path
        if not changes:
            raise ValueError("edit_component requires changes")
        if "source" in changes:
            raise ValueError("component replacement is not supported by edit_component")
        if "transform" in changes:
            raise ValueError("use translate_component or rotate_component for movement; transforms are only accepted at insertion")
        if "configuration" in changes and not changes["configuration"]:
            raise ValueError("configuration must name an existing configuration")
        current = self.inspect_component(component)
        inputs = {k: current[k] for k in ComponentInput.model_fields
                  if not (k == "transform" and current[k] is None)}
        inputs.update(changes)
        validated = ComponentInput.model_validate(inputs).model_dump()
        return self._transport().call(
            "EditComponent", file=self.name, component=component_path(self, component),
            changes={k: validated[k] for k in changes},
        )

    def translate_component(self, component, delta) -> dict:
        """Request an assembly-frame displacement in meters, subject to mates.

        Returns the actual component inspection plus moved (net pose changed).
        The solver may alter other free degrees of freedom. Errors do not roll back.
        """
        from .assembly import component_path, transform
        displacement = transform(translation=delta).translation
        return self._transport().call(
            "TranslateComponent", file=self.name, component=component_path(self, component),
            delta=displacement.model_dump(),
        )

    def rotate_component(self, component, *, axis, angle) -> dict:
        """Request a right-handed rotation in radians about the assembly origin.

        Axis direction is in the assembly frame. Returns actual inspection plus
        moved (net pose changed), subject to mates; errors do not roll back.
        """
        from .assembly import component_path, transform
        from .types import Direction
        direction = Direction.model_validate(axis)
        transform(axis=tuple(direction.model_dump().values()), angle=angle)
        return self._transport().call(
            "RotateComponent", file=self.name, component=component_path(self, component),
            axis=direction.model_dump(), angle=angle,
        )

    def delete_component(self, component) -> dict:
        from .assembly import component_path
        return self._transport().call(
            "DeleteComponent", file=self.name, component=component_path(self, component))

    def rebuild(self) -> dict:
        return self._transport().call("Rebuild", file=self.name)

    def add_feature(self, feature: Union[Feature, BaseModel]) -> dict:
        """Add a feature. Returns the feature JSON in the single format
        (same shape as inspect_feature).

        Sketch features are added in three phases:

        1. ``add_feature(sketch)`` — primitives + self-contained composites
           (Polygon, Rectangle). Phase-2 composites (LinearPattern,
           CircularPattern) and constraints must come in separate calls so
           their references can be back-filled to target-side ids first.
        2. ``add_sketch_entities(sketch)`` — phase-2 composites whose seeds
           reference entities created in phase 1.
        3. ``add_constraints(sketch)`` — constraints over both phase-1 and
           phase-2 entities.

        Raises ``ValueError`` if a Sketch arrives with phase-2 entities or
        non-empty ``constraints`` — the user must split into the explicit
        three-call flow above.

        The response back-fills both the top-level entity ``id`` and each
        entity's child SketchPoints (``line.start``, ``arc.center/start/end``,
        ``ellipse.center/major_axis_end/minor_axis_end``, ``elliptical_arc.*``,
        ``parabola.focal/apex/start/end``) so downstream constraint refs and
        feature refs (``extrude(direction=line)``) carry target-side ids.
        """
        # Lazy import to dodge sketch <-> file circular at module load.
        from .sketch import Sketch
        if isinstance(feature, Sketch):
            if feature.constraints:
                raise ValueError(
                    "add_feature(sketch): sketch.constraints is non-empty. "
                    "Apply constraints via add_constraints(sketch) AFTER all "
                    "phase-2 entities are added (add_sketch_entities) so the "
                    "constraint refs serialize with target-side ids."
                )
            phase2 = [e for e in feature.entities if _is_phase_two_entity(e)]
            if phase2:
                phase2_types = sorted({getattr(e, "type", "?") for e in phase2})
                raise ValueError(
                    f"add_feature(sketch): {len(phase2)} phase-2 composite(s) "
                    f"({', '.join(phase2_types)}) reference other sketch entities "
                    "by id and must come in via add_sketch_entities AFTER the "
                    "AddSketch response back-fills their seeds' target-side ids."
                )
        body = feature.model_dump() if isinstance(feature, BaseModel) else feature
        # Convert any sketch-entity instances embedded in the feature payload to
        # the wrapped Definition form. We do this at the wire boundary so the
        # rest of the model code can hand around plain entity objects.
        body = _wrap_sketch_entity_refs(feature, body) if isinstance(feature, BaseModel) else body
        result = self._transport().call("AddFeature", file=self.name, feature=body)
        # Back-fill `name` (e.g. 'Sketch3') on the in-memory feature so any
        # later code that reads it sees the SolidWorks-assigned identifier.
        if isinstance(feature, BaseModel) and isinstance(result, dict):
            assigned = result.get("name")
            if assigned and hasattr(feature, "name") and getattr(feature, "name") is None:
                try:
                    feature.name = assigned  # type: ignore[attr-defined]
                except Exception:
                    pass
            # Per-entity id back-fill for Sketch features.
            _backfill_sketch_entity_ids(feature, result)
        return result

    def edit_feature(
        self,
        name: str,
        changes: Optional[Union[dict, BaseModel]] = None,
        **field_changes: Any,
    ) -> dict:
        """Edit the existing feature called ``name`` IN PLACE and return the
        updated feature (same JSON shape ``inspect_feature`` returns).

        Specify ONLY the fields to change — as keyword args (or a dict). Every
        other field, and the feature's edge/face selections, is preserved; the
        feature keeps its name so downstream name-refs / probes stay valid::

            f.edit_feature("Fillet1", radius=0.003)
            f.edit_feature("Chamfer1", distance=0.0005)
            f.edit_feature("CirPattern1", count=16)
            f.edit_feature("Boss-Extrude1",
                           end_condition={"end_condition": "Blind", "distance": 0.005})

        Field names match what ``inspect_feature`` reports for that kind. Echo
        fields recompute from the rebuilt geometry, so never pass them.

        Editable kinds: Chamfer, Fillet, Extrude/CutExtrude, Revolve/CutRevolve,
        Sweep/CutSweep, Helix, Loft/CutLoft, Shell, Draft, Rib,
        LinearPattern, CircularPattern, Mirror, Combine,
        RefPlane (offset distance/angle only), and the eight standard mate families. Other kinds raise a clear
        "not supported" error.
        """
        overrides: dict = {}
        if isinstance(changes, BaseModel):
            overrides.update(changes.model_dump())
        elif isinstance(changes, dict):
            overrides.update(changes)
        elif changes is not None:
            raise TypeError(
                f"edit_feature: changes must be a dict or model, got {type(changes).__name__}")
        overrides.update(field_changes)
        if not overrides:
            raise ValueError(
                'edit_feature: no changes given — pass fields as keyword args, '
                'e.g. f.edit_feature("Fillet1", radius=0.003)')

        payload = self.inspect(name)
        if overrides.get("type", payload["type"]) != payload["type"]:
            raise ValueError("edit_feature cannot change the feature type")
        if overrides.get("name") is None:
            overrides.pop("name", None)
        if overrides.get("name", name) != name:
            raise ValueError("use rename_feature to change a feature's name")
        payload.update(overrides)
        model = _FEATURE_ADAPTER.validate_python(payload)
        payload = _wrap_sketch_entity_refs(model, model.model_dump(include=model.model_fields_set))
        return self._transport().call(
            "EditFeature", file=self.name, name=name, feature=payload)

    def edit_entity(self, sketch_name: str, entity: Union[dict, BaseModel]) -> dict:
        """Edit ONE existing primitive sketch entity in place and return the
        re-inspected sketch.

        ``entity`` is a single entity from ``inspect(sketch_name)["entities"]``
        with its geometry modified — e.g. take a Circle entry, change its
        ``radius`` or ``center``, and pass it back::

            sk = f.inspect("Sketch1")
            circ = next(e for e in sk["entities"] if e["type"] == "Circle")
            circ["radius"] = 0.03
            f.edit_entity("Sketch1", circ)

        The entity keeps its id (its defining points are moved, not recreated),
        so constraints that reference it stay valid. Supported kinds: Line,
        Circle, Arc, Point. Composites (Polygon, Linear/Circular pattern) have
        no single id and are rejected — edit their member primitives instead.
        Best on undimensioned geometry; on a fully-constrained DOF the solver
        overrides a raw coordinate change.
        """
        body = entity.model_dump() if isinstance(entity, BaseModel) else entity
        return self._transport().call(
            "EditSketchEntity", file=self.name, sketch_name=sketch_name, entity=body)

    def delete_entity(self, sketch_name: str, entity: Union[dict, BaseModel]) -> dict:
        """Delete ONE existing primitive sketch entity and return the re-inspected
        sketch. ``entity`` is an entry from ``inspect(sketch_name)["entities"]`` —
        only its ``id`` is used. Deleting the entity drops its (unshared) child
        points and any relations referencing it; a downstream feature that consumed
        it will then fail on rebuild (that's the point of deleting). Supported:
        Line/Arc/Circle/Point/Ellipse/Spline/etc. Composites (Polygon, Linear/
        Circular pattern) have no single id and are rejected — delete members
        individually.
        """
        body = entity.model_dump() if isinstance(entity, BaseModel) else entity
        return self._transport().call(
            "DeleteSketchEntity", file=self.name, sketch_name=sketch_name, entity=body)

    def edit_dimension(
        self,
        name: str,
        value: Optional[float] = None,
        new_name: Optional[str] = None,
    ) -> dict:
        """Edit a driving dimension in place: change its value, rename it, or both.
        Sketch dimensions return the owning sketch's re-inspected payload.
        Other feature dimensions return ``{"name": full_name, "value": value}``.

        ``name`` is the dimension's SolidWorks name. For sketches, use the
        ``echo_dim_name`` from an inspected dimension constraint. Feature
        dimensions use their native names; do not infer their roles from D1/D2
        numbering across feature kinds::

            sk = f.inspect("Sketch1")
            dim = next(c for c in sk["constraints"] if c.get("echo_dim_name"))
            f.edit_dimension(dim["echo_dim_name"], value=0.05)          # change value
            f.edit_dimension("D1@Sketch1", new_name="width")            # rename
            f.edit_dimension("D1@Sketch1", value=0.05, new_name="width")

        ``value`` is in meters / radians (the wire unit). ``new_name`` sets the
        dimension's SHORT name (the part before ``@``); its full name becomes
        ``"<new_name>@<owner>"``. Driving a dimension that SolidWorks treats as
        driven (or an out-of-range value) raises a clear error.
        """
        if value is None and not new_name:
            raise ValueError(
                'edit_dimension: nothing to change — pass value and/or new_name, '
                'e.g. f.edit_dimension("D1@Sketch1", value=0.05)')
        return self._transport().call(
            "EditDimension", file=self.name, dim_name=name,
            value=value, new_name=new_name)

    def delete_constraint(self, sketch_name: str, constraint: Union[dict, BaseModel]) -> dict:
        """Delete ONE existing sketch constraint and return the re-inspected sketch.

        ``constraint`` is a single entry from ``inspect(sketch_name)["constraints"]``.
        A dimension is matched by its unique name (``echo_dim_name``); a geometric
        relation (Coincident, Tangent, Horizontal, …) is matched by its ``kind`` plus
        the set of sketch entities it references::

            sk = f.inspect("Sketch1")
            dim = next(c for c in sk["constraints"] if c.get("echo_dim_name"))
            f.delete_constraint("Sketch1", dim)            # delete a dimension
            rel = next(c for c in sk["constraints"] if c["kind"] == "Coincident")
            f.delete_constraint("Sketch1", rel)            # delete a relation

        Deleting a dimension frees the DOF it drove; deleting a relation frees the
        coupling — the sketch re-solves on rebuild. A relation referencing only body
        topology (no sketch entity) can't be matched this way; edit the sketch directly.
        """
        body = constraint.model_dump() if isinstance(constraint, BaseModel) else constraint
        return self._transport().call(
            "DeleteConstraint", file=self.name, sketch_name=sketch_name, constraint=body)

    def rename_feature(self, name: str, new_name: str) -> dict:
        """Rename a feature in place (any kind — Boss-Extrude, Fillet, Sketch, Plane, …).

        The feature name is the durable handle downstream refs and probes resolve
        through, so this is metadata-only: no rebuild, no geometry change::

            f.rename_feature("Boss-Extrude1", "MainBody")

        Returns ``{"old_name": ..., "new_name": ...}``. Raises on a missing feature
        or a blank new name.
        """
        if not new_name or not new_name.strip():
            raise ValueError("rename_feature: new_name must be a non-empty string")
        return self._transport().call(
            "RenameFeature", file=self.name, name=name, new_name=new_name)

    def add_sketch_entities(
        self,
        sketch: Any,
        entities: Optional[list[Any]] = None,
    ) -> dict:
        """Add phase-2 composite entities (LinearPattern, CircularPattern) to
        an existing sketch.

        ``sketch`` is either a :class:`Sketch` model (``.name`` and
        ``.entities`` attributes are read) or a sketch feature name string.
        ``entities`` overrides the list pulled from a ``Sketch`` arg.

        Each composite's ``seeds`` (and any other entity ref it carries) must
        already point at target-side ``SketchEntityId`` triplets — the caller
        back-fills these from the AddSketch response before invoking this.
        """
        from .sketch import Sketch
        if isinstance(sketch, Sketch):
            sketch_name = sketch.name
            if sketch_name is None:
                raise ValueError(
                    "add_sketch_entities: sketch has no name — call add_feature first "
                    "so the SolidWorks-assigned name is back-filled."
                )
            if entities is None:
                entities = [e for e in sketch.entities if _is_phase_two_entity(e)]
        elif isinstance(sketch, str):
            sketch_name = sketch
            if entities is None:
                raise ValueError(
                    "add_sketch_entities: when sketch is a name string, entities "
                    "must be provided explicitly."
                )
        else:
            raise TypeError(
                f"add_sketch_entities: sketch must be a Sketch model or a name string; "
                f"got {type(sketch).__name__}"
            )

        if not entities:
            return {"name": sketch_name, "entities": []}

        wire = [
            e.model_dump(mode="json") if isinstance(e, BaseModel) else e
            for e in entities
        ]
        return self._transport().call(
            "AddSketchEntities",
            file=self.name,
            sketch_name=sketch_name,
            entities=wire,
        )

    def add_constraints(
        self,
        sketch: Any,
        constraints: Optional[list[Any]] = None,
    ) -> dict:
        """Apply constraints to an existing sketch.

        ``sketch`` is either a :class:`Sketch` model (the ``.name`` and
        ``.constraints`` attributes are read) or a sketch feature name string.
        ``constraints`` overrides the list pulled from a ``Sketch`` arg; pass
        a list directly when working with a name-string sketch.

        Each constraint ref must be a Definition that resolves on the C# side
        through ``DefinitionResolver`` — for same-sketch refs this means the
        embedded ``SketchEntityId`` triplet must point at a real, target-side
        entity in the named sketch (the ids back-filled by ``add_feature``).
        """
        from .sketch import Sketch
        if isinstance(sketch, Sketch):
            sketch_name = sketch.name
            if sketch_name is None:
                raise ValueError(
                    "add_constraints: sketch has no name — call add_feature first "
                    "so the SolidWorks-assigned name is back-filled."
                )
            if constraints is None:
                constraints = list(sketch.constraints)
        elif isinstance(sketch, str):
            sketch_name = sketch
            if constraints is None:
                raise ValueError(
                    "add_constraints: when sketch is a name string, constraints "
                    "must be provided explicitly."
                )
        else:
            raise TypeError(
                f"add_constraints: sketch must be a Sketch model or a name string; "
                f"got {type(sketch).__name__}"
            )

        if not constraints:
            return {"name": sketch_name, "count": 0}

        wire = [
            c.model_dump(mode="json") if isinstance(c, BaseModel) else c
            for c in constraints
        ]
        # Validate: a None ref usually means a `probe()` call upstream
        # missed and the None propagated through a variable into the
        # constraint's refs list. The C# side would reject this with a
        # generic "ref is not a valid Definition" — surface here instead
        # with the offending constraint index + kind so you can find the
        # specific upstream probe that needs investigation.
        for i, c in enumerate(wire):
            if not isinstance(c, dict):
                continue
            refs = c.get("refs") or []
            for j, r in enumerate(refs):
                if r is None:
                    raise ValueError(
                        f"add_constraints: constraint[{i}] (kind={c.get('kind')!r}) "
                        f"has None ref at refs[{j}]. This usually means a "
                        f"`probe()` call upstream returned None (ray missed) "
                        f"and the None propagated into the constraint refs. "
                        f"Use a ray that hits the intended entity, or skip "
                        f"the constraint if the entity isn't expected to exist."
                    )
        return self._transport().call(
            "AddConstraints",
            file=self.name,
            sketch_name=sketch_name,
            constraints=wire,
        )

    def delete_feature(self, index: int | str) -> dict:
        if isinstance(index, str):
            index = self._index_of(index)
        return self._transport().call("DeleteFeature", file=self.name, index=index)

    def set_rollback(self, index: int) -> dict:
        return self._transport().call("SetRollback", file=self.name, index=index)

    def set_units(self, units: str | _Unit) -> dict:
        return self._transport().call(
            "SetUnits", file=self.name, units=units.name if isinstance(units, _Unit) else units)

    def set_bodies(self, body_ids: list[str]) -> dict:
        return self._transport().call("SetBodies", file=self.name, body_ids=body_ids)

    def edit_split(
        self,
        feature_name: str,
        consume_marked_bodies: bool,
        marked_bodies: list[dict] | None = None,
        *,
        select_all: bool = False,
        marked_body_rays: list | None = None,
    ) -> dict:
        """Re-commit a Split feature with new ``consume_marked_bodies`` +
        ``marked_bodies`` settings.

        ``target.add_feature(Split)`` always commits a benign initial Split
        (every PreSplitBody2 candidate selected, consume=False). To apply an
        author's actual choices, call this AFTER Add with the target-side
        body Definitions (typically obtained via ``target.probe(ray, "body")``).

        ``select_all=True`` is the common "keep all bodies" case: every
        PreSplitBody2 fragment is marked and ``marked_bodies`` is ignored (it
        may be ``None``/empty). The plugin re-marks every candidate from the
        flag alone.

        ``marked_body_rays`` marks an explicit SUBSET by pointing at each
        fragment: one ray per fragment, aimed at it and away from its siblings.
        Prefer it over ``marked_bodies``. The fragments are transient — they
        exist only inside PreSplitBody2 — so no durable handle to one survives
        to this call, but a ray does not need one: it names a place, and the
        plugin re-fires it against its own candidates. It is also the only form
        an author can write; nobody types a centroid and a surface area to say
        "that fragment". When rays are supplied the plugin ignores
        ``marked_bodies`` entirely.

        Implementation deletes the existing Split and re-commits with the new
        settings; the feature name is preserved. Expected to be called right
        after Add so the position shift from delete + re-add is benign.
        """
        return self._transport().call(
            "EditSplit", file=self.name,
            feature_name=feature_name,
            consume_marked_bodies=consume_marked_bodies,
            marked_bodies=list(marked_bodies or []),
            select_all=select_all,
            marked_body_rays=[
                r.model_dump() if hasattr(r, "model_dump") else r
                for r in (marked_body_rays or [])
            ],
        )

    def get_discard_probes(self, feature_name: str) -> list[dict]:
        """Return ``[{body, ray}, ...]`` pairs identifying the fragments the
        named Cut-Extrude / Cut-Revolve / Cut-Sweep originally discarded.
        Empty list for single-body cuts.

        Each pair carries a ``BodyDefinition`` (mass-props identity of the
        discarded fragment, captured during the cut's notify callback) plus
        a probe ``Ray`` aimed at that fragment. Round-trip use: pipe each ray
        through ``target.probe(ray, BODY, expected=body)`` to obtain a
        target-side body Definition, then pass those to
        ``target.drop_bodies(name, [...])``.

        Moved off Inspect because the underlying ``IModifyDefinition2``
        rebuild drifts surviving bodies' mass-props by ~1e-7.
        """
        return self._transport().call(
            "GetDiscardProbes", file=self.name,
            feature_name=feature_name,
        )

    def drop_bodies(self, feature_name: str, bodies: list[dict]) -> dict:
        """Drop post-cut fragments from an already-committed Cut* feature.

        ``feature_name`` names the Cut-Extrude / Cut-Revolve / Cut-Sweep feature.
        ``bodies`` is a list of ``BodyDefinition`` dicts — typically what
        ``target.probe(ray, BODY)`` returns after the Cut* committed (the cut
        keeps all candidate fragments, so each kept fragment is selectable by
        a ray). The plugin synthesizes a probe ray for each body, re-fires the
        cut, and ray-resolves the candidates inside the notify callback so the
        targeted fragments are excluded from the new keep-list.

        Throws (via wire error) on a body def that does not resolve to a live
        body in the current part state, or on two probes that resolve to the
        same fragment (cluster below disambiguation margin).
        """
        return self._transport().call(
            "DropBodies", file=self.name,
            feature_name=feature_name, bodies=list(bodies),
        )

    # selection / inspection --------------------------------------------

    def probe(
        self,
        ray_value: "Ray | dict",
        entity_type: str,
        on_body: Optional[Union[dict, BaseModel]] = None,
    ) -> Optional[dict]:
        """Fire a ray and return the Definition of whatever entity it hits,
        or ``None`` on a miss. The author-facing way to pick a face / edge /
        vertex / body / plane out of the live part by geometry.

        Accepts either a typed ``Ray`` model or a raw ``{origin, direction}``
        dict.

        ``on_body`` scopes the hit to one body — "the face there, on THAT body".
        Coincident geometry otherwise makes a ray ambiguous: a boss added with
        ``merge=False`` shares its underside with the plate's topside, so a ray
        through that point hits two faces at the same location and which one
        SolidWorks returns first is not stable across rebuilds. Pass the owning
        body (itself found with a ``BODY`` probe) to pin it down::

            plate = target.probe(ray(..., ...), BODY)
            face  = target.probe(ray(..., ...), FACE, on_body=plate)

        Coincident geometry is ordered deterministically rather than left to
        SolidWorks' enumeration: among entities at the same point, the hit whose
        outward normal opposes the ray wins — a ray strikes the side of a surface it
        can see. So the two faces of a non-merged interface are both reachable, by
        aiming from opposite sides, and replaying a ray always binds the same entity.

        When that tie cannot be decided (the coincident hits are not planar faces, or
        their normals match too), the returned Definition carries
        ``echo_probe_ambiguous: True``. That is the case where replay may bind the
        other entity, and the fix is to scope it with ``on_body``.

        On miss, logs a WARNING with the caller's filename:line so
        downstream "ref is None" failures can be traced back to which
        probe in the script missed. The round-trip runner's generation
        pipeline picks rays via :meth:`generate_probe` against a known
        source Definition; if the captured target Def doesn't match, the
        runner validates via :meth:`approx_match_def` and retries
        generation.
        """
        ray_dict = (
            ray_value.model_dump() if isinstance(ray_value, Ray) else ray_value
        )
        scope = on_body.model_dump() if isinstance(on_body, BaseModel) else on_body
        result = self._transport().call(
            "Probe", file=self.name,
            ray=ray_dict, entity_type=entity_type, on_body=scope,
        )
        if result is None:
            import logging
            import traceback
            caller = "<unknown>"
            for frame in traceback.extract_stack()[:-1][::-1]:
                # Skip internal frames; report the first frame outside this
                # module so the user sees their script line.
                if "dahlia" not in frame.filename:
                    caller = f"{frame.filename}:{frame.lineno}"
                    break
            logging.getLogger("dahlia").warning(
                "probe(%s) missed at %s — ray=%r",
                entity_type, caller, ray_dict,
            )
        return result

    def approx_match_def(self, a: dict, b: dict) -> bool:
        """True iff two Definitions describe the same entity within Approx
        tolerance (same kind, identity fields within tolerance, sample
        fields like UV-witness ``on_face_point`` and non-deterministic
        ``axis_direction`` ignored). Mirrors C# ``DefinitionMatch.Approx``.

        Used by the round-trip runner's generation pipeline: after
        ``probe(ray, kind)`` returns a captured target Def, the runner
        validates it against the source-side Def with this helper. On
        mismatch the runner regenerates the ray (different
        ``start_attempt``) and tries again. Author scripts don't need
        this — they use ``probe`` and trust the ray.
        """
        return bool(self._transport().call(
            "ApproxMatchDef", file=self.name, a=a, b=b,
        ))

    def probe_region(self, sketch_name: str, point: tuple[float, float]) -> dict:
        """Find the sketch region containing the given 2D point and return its
        canonical ``RegionDefinition``. ``point`` is `(x, y)` in sketch-plane
        coords (meters).

        LLM-friendly authoring helper for region selection — same pattern as
        ``f.probe(ray, FACE)``. The caller picks any 2D point inside the
        region they want; the plugin runs point-in-region containment, picks
        the unique containing region, and returns the canonical Definition the
        caller can pass into ``cut_extrude``, ``cut_revolve``, etc.

        Raises if no region contains the point, or if multiple do (the point
        sits on / near a shared boundary — nudge it deeper into the region).
        """
        x, y = point
        return self._transport().call(
            "ProbeRegion", file=self.name,
            sketch_name=sketch_name, x=float(x), y=float(y),
        )

    def generate_probe(
        self,
        definition: dict,
        entity_type: str,
        start_attempt: int = 0,
    ) -> dict:
        """Synthesize a Ray that, when fired through ``probe(ray, entity_type)``,
        will hit the live entity identified by ``definition``. Returns the
        Ray dict (``{origin, direction}``).

        Used by the round-trip runner to swap source-captured Definitions for
        target-captured ones: source generates a probe ray for a captured Def,
        target.probe(ray) re-captures the corresponding entity using target's
        own geometry, and the resolver downstream sees a Definition whose
        tolerances match the body that produced it.

        ``start_attempt`` skips past the first N cardinal/sample attempts so
        the runner can REGENERATE a probe when target's approx-match rejects
        the first one. Useful for body refs where source and target have
        nearly-coplanar stacked bodies that SW's `SelectByRay` picks in
        different orders due to sub-µm rebuild drift.

        Raises (via wire error) on either side of the failure mode the test
        exists to surface: the source Definition does not resolve to a live
        entity, OR no candidate ray verifies on source within the retry
        budget at this offset. Both are real signals — there is no fallback
        path.
        """
        return self._transport().call(
            "GenerateProbe", file=self.name,
            definition=definition, entity_type=entity_type,
            start_attempt=start_attempt,
        )

    def view(self) -> dict:
        return self._transport().call("ViewFile", file=self.name)

    def inspect_feature(self, index: int) -> dict:
        return self._transport().call("InspectFeature", file=self.name, index=index)

    def inspect(self, name: str) -> dict:
        """Inspect a feature by NAME (same JSON as ``inspect_feature(index)``)."""
        return self.inspect_feature(self._index_of(name))

    def feature_names(self) -> list[str]:
        """Every feature's name, in tree order (datum planes, sketches, solids…)."""
        return [feat["name"] for feat in self.view()["features"]]

    def _index_of(self, name: str) -> int:
        feats = self.view()["features"]
        for feat in feats:
            if feat["name"] == name:
                return feat["index"]
        raise ValueError(
            f"no feature named {name!r}; have {[f['name'] for f in feats]}")

    # output -------------------------------------------------------------

    def get_image(self, orientation: str = "Isometric") -> bytes:
        result = self._transport().call("GetImage", file=self.name, orientation=orientation)
        if isinstance(result, (bytes, bytearray)):
            return bytes(result)
        if isinstance(result, dict) and isinstance(result.get("ImageBase64"), str):
            return base64.b64decode(result["ImageBase64"], validate=True)
        raise TypeError(f"GetImage returned an invalid response: {type(result).__name__}")

    def save(self, path: Optional[str] = None) -> dict:
        """Save the part, or export it when ``path`` carries a neutral-format
        extension. ``SaveAs3`` picks the exporter from the extension, so
        ``f.save("part.step")`` / ``f.save("part.stl")`` export STEP / STL.
        Updates this handle after a native SaveAs. Returns the current document's
        ``{"FileName": <name>, "path": <native path>}``."""
        if path is None:
            result = self._transport().call("SaveFile", file=self.name)
        else:
            result = self._transport().call("SaveFileAs", file=self.name, path=str(Path(path).resolve()))
        self.name = result["FileName"]
        return result

    def close(self) -> dict:
        return self._transport().call("CloseFile", file=self.name)


# -- helpers ---------------------------------------------------------------


# Composite type discriminators that reference OTHER sketch entities by id.
# These can't be added until the referenced entities have target-side ids — the
# wire flow handles them via the separate `add_sketch_entities` call after
# `add_feature` returns and back-fills phase-1 ids.
_PHASE_TWO_TYPES: frozenset[str] = frozenset({"LinearPattern", "CircularPattern"})


def _is_phase_two_entity(entity: Any) -> bool:
    """True for sketch composites that reference other entities by id and must
    flow through ``add_sketch_entities`` rather than ``add_feature``."""
    if isinstance(entity, BaseModel):
        return getattr(entity, "type", None) in _PHASE_TWO_TYPES
    if isinstance(entity, dict):
        return entity.get("type") in _PHASE_TWO_TYPES
    return False


def _wrap_sketch_entity_refs(feature: BaseModel, body: dict) -> dict:
    """Walk the dumped feature payload and ensure every field that holds a
    sketch-entity instance (Line / Circle / etc.) is on the wire as its
    Definition JSON.

    The entity IS the Definition — `model.model_dump()` on the parent feature
    already serializes nested entity instances into Definition JSON
    (kind/id/construction/geometry), so the wrap step is mostly a no-op.
    Acts as a safety pass for authoring paths that may have stuffed a raw
    entity into a `dict`-typed field where pydantic doesn't auto-dump it.

    Sketch features themselves are skipped: a sketch's `entities` list holds
    entities authored INSIDE the sketch — they're already serialized as their
    own Definition flavors via the Sketch.entities discriminated union.
    """
    # Lazy import to dodge the sketch <-> file circular at module load.
    from .sketch import _is_sketch_entity, Sketch

    if isinstance(feature, Sketch):
        return body

    out = dict(body)  # shallow copy; we only overwrite slots that need it
    for field_name in feature.__class__.model_fields.keys():
        attr = getattr(feature, field_name, None)
        if attr is None:
            continue
        if _is_sketch_entity(attr) and isinstance(attr, BaseModel):
            out[field_name] = attr.model_dump(mode="json")
        elif isinstance(attr, list) and attr and any(_is_sketch_entity(x) for x in attr):
            out[field_name] = [
                x.model_dump(mode="json") if isinstance(x, BaseModel) else x
                for x in attr
            ]
    return out


def _backfill_sketch_entity_ids(feature: BaseModel, result: dict) -> None:
    """For Sketch features, copy back-fillable target fields from the response's
    `entities` list onto the in-memory pydantic entity objects (matched by index
    — the plugin walks in receipt order).

    Back-fills the top-level entity ``id`` AND every embedded child-point ``id``
    on segment endpoints (``line.start``, ``arc.center/start/end``,
    ``ellipse.center/major_axis_end/minor_axis_end``, ``elliptical_arc.*``,
    ``parabola.focal/apex/start/end``). Endpoint ids are required so subsequent
    ``add_constraints`` payloads that target endpoints (line endpoints, arc
    centers, ...) serialize with target-side ids the C# resolver can find.

    Silent no-op for non-Sketch features and when shapes don't line up.
    """
    if getattr(feature, "type", None) != "Sketch":
        return
    entities_attr = getattr(feature, "entities", None)
    if not isinstance(entities_attr, list):
        return
    response_entities = result.get("entities")
    if not isinstance(response_entities, list):
        return
    for in_mem, on_wire in zip(entities_attr, response_entities):
        if not isinstance(on_wire, dict):
            continue
        _apply_id_to(in_mem, on_wire.get("id"))
        # Endpoint points carried alongside segment flavors. Composites (Polygon
        # etc.) lack these fields and are skipped via the hasattr check.
        for field in _ENDPOINT_FIELDS:
            wire_field = on_wire.get(field)
            in_mem_field = getattr(in_mem, field, None)
            if isinstance(wire_field, dict) and in_mem_field is not None:
                _apply_id_to(in_mem_field, wire_field.get("id"))


# Embedded SketchPointDefinition fields per segment flavor. Used by the
# response-side back-fill to carry target-side endpoint ids onto the in-memory
# entity so constraint refs serialize with correct (kind, id) triplets.
_ENDPOINT_FIELDS = (
    "start", "end",                                  # line, arc, elliptical_arc, parabola
    "center",                                        # circle, arc, ellipse, elliptical_arc
    "major_axis_end", "minor_axis_end",              # ellipse, elliptical_arc
    "focal", "apex",                                 # parabola
)


def _apply_id_to(target: Any, wire_id: Any) -> None:
    """Set ``target.id`` from a wire SketchEntityId dict, best-effort."""
    if not (isinstance(wire_id, dict) and "sketch_name" in wire_id
            and "entity_kind" in wire_id and "id" in wire_id):
        return
    from .types import SketchEntityId
    try:
        target.id = SketchEntityId(
            sketch_name=wire_id["sketch_name"],
            entity_kind=wire_id["entity_kind"],
            id=wire_id["id"],
        )
    except Exception:
        pass
