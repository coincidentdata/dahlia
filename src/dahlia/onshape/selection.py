"""Geometry references for Onshape features: a reference is a geometry definition (the shared
`dahlia.sldwrks.types` shapes) plus `native_id`, the deterministic id Onshape gives the entity.
Features take references in their query lists; each becomes an id query. Every lookup is one
FeatureScript request, evaluated by the helpers in selection.fs."""
import copy
import json
from pathlib import Path

from pydantic import BaseModel, TypeAdapter

from ..sldwrks.types import Definition, Ray
from .transport import OnshapeError

_DEFINITION = TypeAdapter(Definition)
_ENTITY_TYPES = {"face": "FACE", "edge": "EDGE", "vertex": "VERTEX", "body": "BODY"}
_DEFAULT_PROBE_RADIUS = 1e-4


def _bt_type(node: dict) -> str:
    # Type names may arrive package-qualified and carry a serialization version suffix that
    # changes without notice; compare on the bare name.
    return node.get("btType", "").rsplit(".", 1)[-1].split("-", 1)[0]


def _unwrap(value):
    if isinstance(value, list):
        return [_unwrap(item) for item in value]
    if not isinstance(value, dict):
        return value
    if "message" in value and "typeName" in value:
        return _unwrap({"btType": value["typeName"], **value["message"]})
    kind = _bt_type(value)
    if kind == "BTFSValueMap":
        return {_unwrap(item["key"]): _unwrap(item["value"]) for item in value["value"]}
    if kind.startswith("BTFSValue") and "value" in value:
        return _unwrap(value["value"])
    raise OnshapeError(f"Unsupported FeatureScript result type {kind!r}")


def _evaluate(file, body: str, *, rollback: int = -1):
    snapshot = file._snapshot or file._refresh()
    path = file._path.replace(f"/w/{file.workspace_id}/", f"/m/{snapshot['sourceMicroversion']}/")
    helpers = Path(__file__).with_name("selection.fs").read_text(encoding="utf-8")
    script = f"function(context is Context, queries) {{\n{helpers}\n{body}\n}}"
    result = file.transport.request("POST", path + "/featurescript", params={"rollbackBarIndex": rollback},
                                    body={"queries": {}, "script": script})
    if result.get("result") is None:
        notices = result.get("notices") or []
        errors = [n.get("message") for n in notices if n.get("level") == "ERROR"] or notices
        raise OnshapeError(f"Onshape geometry evaluation failed: {errors}")
    return _unwrap(result["result"])


def _references(entries) -> list[dict]:
    return [_DEFINITION.validate_python({**entry["definition"], "native_id": entry["id"]}).model_dump(exclude_none=True)
            for entry in entries]


def _entity_type(kind: str) -> str:
    if kind not in _ENTITY_TYPES:
        raise ValueError(f"Onshape selects {sorted(_ENTITY_TYPES)}, not {kind!r}")
    return _ENTITY_TYPES[kind]


def _category(definition: dict) -> str:
    kind = definition["kind"]
    return _entity_type(kind if kind in ("body", "vertex") else kind.rsplit("_", 1)[-1])


def _id_query(native_id: str) -> dict:
    return {"btType": "BTMIndividualQuery-138", "deterministicIds": [native_id]}


def _query_lists(value):
    if isinstance(value, dict):
        if isinstance(value.get("queries"), list):
            yield value
        for item in value.values():
            yield from _query_lists(item)
    elif isinstance(value, list):
        for item in value:
            yield from _query_lists(item)


def _resolve_script(definitions) -> str:
    def geometry(definition):
        return {k: v for k, v in definition.items() if not k.startswith("echo_") and k != "native_id"}
    return "return [" + ",".join(
        f"dahliaEntry(dahliaResolve({json.dumps(geometry(d), sort_keys=True)}, EntityType.{_category(d)}))"
        for d in definitions) + "];"


def write_references(file, feature: dict) -> dict:
    """Replace every reference in the feature's query lists with an id query, resolving the ones
    without `native_id` in one request. Native Onshape queries pass through."""
    pending = []
    for parameter in list(_query_lists(feature)):
        queries = []
        for query in parameter["queries"]:
            if isinstance(query, BaseModel):
                query = query.model_dump(exclude_none=True)
            if isinstance(query, dict) and "btType" in query:
                queries.append(query)
                continue
            if not isinstance(query, dict) or "kind" not in query:
                raise TypeError(f"A query is an Onshape query or a geometry reference, not {query!r}")
            reference = _DEFINITION.validate_python(query).model_dump(exclude_none=True)
            if reference.get("native_id"):
                queries.append(_id_query(reference["native_id"]))
            else:
                queries.append(slot := {})
                pending.append((slot, reference))
        parameter["queries"] = queries
    if pending:
        resolved = _references(_evaluate(file, _resolve_script([reference for _, reference in pending])))
        for (slot, _), reference in zip(pending, resolved, strict=True):
            slot.update(_id_query(reference["native_id"]))
    return feature


def read_references(file, feature: dict, rollback: int) -> dict:
    """The feature with each id query replaced by the references it selects, captured where the
    feature sits in the tree (one request when it has any). Queries that capture nothing, or that
    have no ids, stay native."""
    feature = copy.deepcopy(feature)
    parameters = list(_query_lists(feature))
    ids = sorted({i for p in parameters for q in p["queries"] for i in q.get("deterministicIds") or []})
    if not ids:
        return feature
    captured = _evaluate(file, "return [" + ",".join(f"dahliaCaptured(qTransient({json.dumps(i)}))" for i in ids) + "];",
                         rollback=rollback)
    by_id = {i: _references(entries) for i, entries in zip(ids, captured, strict=True)}
    for parameter in parameters:
        queries = []
        for query in parameter["queries"]:
            found = [by_id.get(i, []) for i in query.get("deterministicIds") or []]
            queries.extend([r for refs in found for r in refs] if found and all(len(r) == 1 for r in found) else [query])
        parameter["queries"] = queries
    return feature


def probe(file, ray: Ray, kind: str) -> list[dict]:
    category = _entity_type(kind)
    o, d = ray.origin, ray.direction
    radius = _DEFAULT_PROBE_RADIUS if ray.radius is None else ray.radius
    return _references(_evaluate(file, f"return dahliaProbe(vector({o.x!r}, {o.y!r}, {o.z!r}) * meter, "
                                       f"normalize(vector({d.x!r}, {d.y!r}, {d.z!r})), EntityType.{category}, {radius!r} * meter);"))


def select(file, definition) -> dict:
    value = definition.model_dump(exclude_none=True) if isinstance(definition, BaseModel) else definition
    value = _DEFINITION.validate_python(value).model_dump(exclude_none=True)
    return _references(_evaluate(file, _resolve_script([value])))[0]


def created_by(file, feature_id: str, kind: str) -> list[dict]:
    category = _entity_type(kind)
    return _references(_evaluate(file, f"return dahliaEntries(qCreatedBy(makeId({json.dumps(feature_id)}), EntityType.{category}));"))


def bodies(file) -> list[dict]:
    return _references(_evaluate(file, "return dahliaEntries(dahliaCandidates(EntityType.BODY));"))


def probe_region(file, sketch_id: str, x: float, y: float) -> dict:
    return _references(_evaluate(file, f"return dahliaProbeRegion(makeId({json.dumps(sketch_id)}), {x!r}, {y!r});"))[0]
