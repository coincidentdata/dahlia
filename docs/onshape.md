# Onshape development preview

`dahlia.onshape` drives Onshape Part Studios with Onshape's own feature JSON,
the `BTMFeature-134` and `BTMSketch-151` documents its REST API reads and writes.
Dahlia adds geometry references: probe, select, or list an entity once, then put
the reference in any query list of a feature. There are no Dahlia feature models
for Onshape; the parameters, enums and units are Onshape's.

```python
from dahlia import onshape

session = onshape.connect()
part = session.open_file(part_studio_workspace_url)

part.add_feature({"btType": "BTMSketch-151", "featureType": "newSketch", "name": "Profile",
    "parameters": [{"btType": "BTMParameterQueryList-148", "parameterId": "sketchPlane", "queries": [
        {"btType": "BTMIndividualQuery-138", "queryString": 'query=qCreatedBy(makeId("Top"), EntityType.FACE);'}]}],
    "entities": [{"btType": "BTMSketchCurve-4", "entityId": "c1", "centerId": "c1.center",
                  "geometry": {"btType": "BTCurveGeometryCircle-115", "xCenter": 0, "yCenter": 0,
                               "radius": 0.005, "xDir": 1, "yDir": 0, "clockwise": False}}],
    "constraints": []})

region = part.probe_region("Profile", (0, 0))
part.add_feature({"btType": "BTMFeature-134", "featureType": "extrude", "name": "Boss", "parameters": [
    {"btType": "BTMParameterEnum-145", "parameterId": "bodyType", "enumName": "ExtendedToolBodyType", "value": "SOLID"},
    {"btType": "BTMParameterEnum-145", "parameterId": "operationType", "enumName": "NewBodyOperationType", "value": "NEW"},
    {"btType": "BTMParameterQueryList-148", "parameterId": "entities", "queries": [region]},
    {"btType": "BTMParameterEnum-145", "parameterId": "endBound", "enumName": "BoundingType", "value": "BLIND"},
    {"btType": "BTMParameterQuantity-147", "parameterId": "depth", "expression": "10 mm"}]})

top = next(e for e in part.created_by("Boss", onshape.EDGE) if e["center"]["z"] > 0.009)
part.add_feature({"btType": "BTMFeature-134", "featureType": "fillet", "name": "Round", "parameters": [
    {"btType": "BTMParameterQueryList-148", "parameterId": "entities", "queries": [top]},
    {"btType": "BTMParameterQuantity-147", "parameterId": "radius", "expression": "1 mm"}]})
```

A feature's parameter ids and enum values are the ones its Onshape feature
type defines. Inspect a feature made in the Onshape UI, or read
`GET /api/v9/partstudios/d/{did}/w/{wid}/e/{eid}/featurespecs`, which lists every
parameter, its type and default, and the condition under which it applies.
Parameters left out take their defaults, though a missing boolean that selects a
mode has been seen to fail regeneration with a bare `REGEN_ERROR`.

## References

A reference is a geometry definition (the shapes in `dahlia.sldwrks.types`, such
as `planar_face` or `circular_edge`) plus `native_id`, the deterministic id Onshape
gives the entity. Each lookup is one FeatureScript request:

```python
face   = part.probe(onshape.ray((0, 0, .1), (0, 0, -1)), onshape.FACE)  # first hit, or None
edge   = part.select({"kind": "circular_edge", "center": {...}, "axis_direction": {...}, "radius": .005})
edges  = part.created_by("Boss", onshape.EDGE)   # also FACE, VERTEX, BODY
solids = part.bodies()
region = part.probe_region("Profile", (x, y))    # the sketch region containing a 2D point
```

Probes and selection consider solid geometry only, not sketches or construction
planes. Edges and vertices are probed by distance from the ray (the ray's
`radius`, default 0.1 mm), faces and bodies by the first surface hit.

When a feature is written, every reference in its query lists becomes an id
query. A definition without `native_id` (hand-written, or captured in another
document) is resolved first, in one request for all of them, and must match
exactly one entity. Native Onshape queries pass through unchanged. Ids belong to
one document: drop `native_id` before using a definition elsewhere.

## Reading and editing

`inspect()` and `inspect_feature(name)` return Onshape's feature JSON with each id
query replaced by the references it selects, captured where the feature sits in
the tree. That costs one request for each feature with id queries; `view()` lists
the tree with types and regeneration states in one. A query whose geometry cannot
be captured, or that no longer resolves, stays native.

`edit_feature(name, feature)` replaces a feature, typically with its edited
`inspect_feature` result. `add_feature` returns the feature as Onshape stored it,
including its `featureId`, and refuses a name already in the tree. A write that
does not regenerate cleanly raises `OnshapeError`; the feature may still have been
written, so inspect before retrying. Sequential writes chain on the returned
microversion and reject concurrent edits.

Also on the Part Studio: `delete_feature(name)`, `set_rollback(index)`,
`get_image(orientation)`, `export_step()`, `save(path)` for a `.step` path, and
`close()`. `session.create_file(name, public=...)` creates a document in two
requests; free accounts can only create public documents.

Verified live on 2026-11-07: a native sketch, an extrude of a probed sketch
region, a fillet of an edge from `created_by`, a reference plane on a probed face,
inspection of each with their references captured, editing a feature from its
inspected JSON, resolving a definition without an id, and deleting features.

## Authentication

Authenticate either with an API key pair (`ONSHAPE_ACCESS_KEY` and
`ONSHAPE_SECRET_KEY` in the environment) or with an OAuth2 application from the
Onshape Developer Portal (`ONSHAPE_CLIENT_ID` and `ONSHAPE_CLIENT_SECRET`, plus a
one-time browser sign-in through `onshape.OAuthTokens(...).authorize()`, which
stores self-refreshing tokens in `~/.dahlia/onshape-oauth.json`). The
application must list `http://localhost:8765` as a redirect URL and request the
`OAuth2Read` and `OAuth2Write` scopes. Both paths spend the same allowance: Onshape
counts API-key calls and private OAuth applications against the owning account.
Connecting and opening a workspace URL make no requests.

## Request budget

Onshape's published allowance is **2,500 calls per user per year** on Free and
Standard plans (5,000 Professional, 10,000 Enterprise), counted for successful
responses at the company level; usage and notification emails are under
My Account > Developer. A local ledger in `~/.dahlia/onshape-requests.sqlite3`
counts HTTP attempts per UTC day (failures included) and stops a runaway loop at
1,000; a server 429 pauses further attempts for its `Retry-After`, capped at ten
minutes because the header has been observed to outlive the throttle. There are
no automatic retries, redirects, or background polls. The counter cannot measure
usage by other clients or the annual quota remaining.

Pass `budget=onshape.RequestBudget(path, limit=1000)` to choose a ledger and
limit, or set `ONSHAPE_REQUEST_LIMIT` for the default ledger. Read usage with
`session.transport.budget.used`.
