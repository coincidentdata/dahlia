import copy
import re
from urllib.parse import quote, urlsplit

from ..sldwrks.types import Ray
from . import selection
from .transport import OnshapeError


# INFO is a regenerated feature with a notice (a split that left some targets untouched).
HEALTHY = {"OK", "INFO"}


class PartStudio:
    """One Part Studio. Features are Onshape's own JSON (`BTMFeature-134`, `BTMSketch-151`), as the
    REST API reads and writes them; dahlia adds only geometry references (see `selection`)."""

    def __init__(self, transport, document_id: str, workspace_id: str, element_id: str):
        for identity in (document_id, workspace_id, element_id):
            if not re.fullmatch(r"[0-9a-f]{24}", identity):
                raise ValueError("Onshape document, workspace, and element IDs must be 24 hex characters")
        self.transport = transport
        self.document_id = document_id
        self.workspace_id = workspace_id
        self.element_id = element_id
        self._snapshot = None

    @property
    def url(self) -> str:
        return f"{self.transport.base_url}/documents/{self.document_id}/w/{self.workspace_id}/e/{self.element_id}"

    @property
    def _path(self) -> str:
        return f"/api/v9/partstudios/d/{self.document_id}/w/{self.workspace_id}/e/{self.element_id}"

    @classmethod
    def from_url(cls, transport, url: str):
        parsed = urlsplit(url)
        if f"{parsed.scheme}://{parsed.netloc}" != transport.base_url:
            raise ValueError("Document URL must belong to the connected Onshape origin")
        match = re.fullmatch(r"/documents/([0-9a-f]{24})/w/([0-9a-f]{24})/e/([0-9a-f]{24})/?", parsed.path)
        if not match:
            raise ValueError("Use an Onshape Part Studio workspace URL (/documents/.../w/.../e/...)")
        return cls(transport, *match.groups())

    def _refresh(self) -> dict:
        self._snapshot = None
        result = self.transport.request("GET", self._path + "/features", params={
            "rollbackBarIndex": -1, "includeGeometryIds": True, "noSketchGeometry": False})
        if not result.get("isComplete", True):
            raise OnshapeError("Onshape returned an incomplete feature list")
        self._snapshot = result
        return result

    def _features(self) -> list[dict]:
        return (self._snapshot or self._refresh())["features"]

    def _named(self, name: str) -> dict:
        matches = [feature for feature in self._features() if feature["name"] == name]
        if len(matches) != 1:
            raise ValueError(f"Expected one feature named {name!r}, found {len(matches)}")
        return matches[0]

    def view(self) -> dict:
        """Cheap listing of the feature tree with native types and regeneration states."""
        snapshot = self._refresh()
        states = snapshot.get("featureStates", {})
        return {"kind": "part", "url": self.url, "features": [
            {"name": f["name"], "feature_type": f["featureType"],
             "status": states.get(f["featureId"], {}).get("featureStatus", "OK"), "suppressed": bool(f.get("suppressed"))}
            for f in snapshot["features"]]}

    def inspect(self) -> list[dict]:
        """Every feature as Onshape's JSON, each id query replaced by the references it selects;
        one request per feature that has any."""
        features = self._refresh()["features"]
        return [selection.read_references(self, feature, index) for index, feature in enumerate(features)]

    def inspect_feature(self, name: str) -> dict:
        features = self._refresh()["features"]
        feature = self._named(name)
        return selection.read_references(self, feature, features.index(feature))

    def add_feature(self, feature: dict) -> dict:
        """Write one feature in Onshape's JSON; references in its query lists become id queries.
        Returns the feature as Onshape stored it."""
        if any(f["name"] == feature.get("name") for f in self._features()):
            raise ValueError(f"Feature name {feature['name']!r} already exists")
        return self._write(feature)

    def edit_feature(self, name: str, feature: dict) -> dict:
        """Replace the named feature, e.g. with its edited `inspect_feature` result."""
        self._refresh()
        return self._write(feature, self._named(name)["featureId"])

    def _write(self, feature: dict, feature_id: str | None = None) -> dict:
        feature = selection.write_references(self, copy.deepcopy(feature))
        snapshot = self._snapshot or self._refresh()
        path = self._path + "/features"
        if feature_id is not None:
            feature["featureId"] = feature_id
            path += "/featureid/" + quote(feature_id, safe="")
        self._snapshot = None
        result = self.transport.request("POST", path, body={
            "btType": "BTFeatureDefinitionCall-1406", "feature": feature, "rejectMicroversionSkew": True,
            "sourceMicroversion": snapshot["sourceMicroversion"], "serializationVersion": snapshot["serializationVersion"]})
        state = result.get("featureState", {})
        if result.get("microversionSkew") or state.get("featureStatus") not in HEALTHY or state.get("inactive"):
            raise OnshapeError(f"Onshape did not confirm a healthy feature; it may already exist. Inspect before retrying: {state}")
        if feature_id is None:
            # Sequential writes chain on the returned microversion instead of rereading the tree.
            native = result["feature"]
            self._snapshot = {**snapshot, "features": [*snapshot["features"], native],
                              "featureStates": {**snapshot.get("featureStates", {}), native["featureId"]: state},
                              "sourceMicroversion": result["sourceMicroversion"]}
        return result["feature"]

    def delete_feature(self, name: str):
        self._refresh()
        identity = self._named(name)["featureId"]
        self._snapshot = None
        return self.transport.request("DELETE", self._path + "/features/featureid/" + quote(identity, safe=""))

    def set_rollback(self, index: int) -> dict:
        """Move the rollback bar: `index` features stay active, -1 restores everything."""
        self._snapshot = None
        return self.transport.request("POST", self._path + "/features/rollback", body={"rollbackIndex": index})

    def probe(self, ray_value, entity_type: str) -> dict | None:
        """The first face, edge, vertex or body along a ray, as a reference, or None on a miss."""
        hits = selection.probe(self, ray_value if isinstance(ray_value, Ray) else Ray.model_validate(ray_value), entity_type)
        return hits[0] if hits else None

    def select(self, definition) -> dict:
        """The reference to the one entity a geometry definition describes."""
        return selection.select(self, definition)

    def created_by(self, name: str, entity_type: str) -> list[dict]:
        """References to the faces, edges, vertices or bodies a named feature created."""
        return selection.created_by(self, self._named(name)["featureId"], entity_type)

    def bodies(self) -> list[dict]:
        return selection.bodies(self)

    def probe_region(self, sketch_name: str, point) -> dict:
        """The region of a sketch containing a 2D point (sketch coordinates, meters)."""
        return selection.probe_region(self, self._named(sketch_name)["featureId"], float(point[0]), float(point[1]))

    def get_image(self, orientation: str = "Isometric", *, width: int = 800, height: int = 600) -> bytes:
        """PNG shaded view of the Part Studio."""
        import base64
        views = {"Isometric": "isometric", "Front": "front", "Back": "back", "Top": "top", "Bottom": "bottom",
                 "Left": "left", "Right": "right"}
        if orientation not in views:
            raise ValueError(f"orientation must be one of {sorted(views)}")
        result = self.transport.request("GET", self._path + "/shadedviews", params={
            "viewMatrix": views[orientation], "outputWidth": width, "outputHeight": height, "pixelSize": 0})
        images = result.get("images") or []
        if len(images) != 1:
            raise OnshapeError(f"Onshape returned {len(images)} shaded views")
        return base64.b64decode(images[0])

    def export_step(self, *, poll_seconds: float = 2.0, attempts: int = 30) -> bytes:
        """Translate the Part Studio to STEP and download it (one request per poll)."""
        import time
        job = self.transport.request("POST", self._path + "/translations", body={
            "formatName": "STEP", "storeInDocument": False, "notifyUser": False, "flattenAssemblies": False,
            "yAxisIsUp": False, "includeExportIds": False})
        for _ in range(attempts):
            state = job.get("requestState")
            if state == "DONE":
                ids = job.get("resultExternalDataIds") or []
                if len(ids) != 1:
                    raise OnshapeError(f"Onshape translation produced {len(ids)} files")
                return self.transport.download(f"/api/v9/documents/d/{self.document_id}/externaldata/{ids[0]}")
            if state not in {"ACTIVE", "PENDING"}:
                raise OnshapeError(f"Onshape translation failed: {job.get('failureReason') or state}")
            time.sleep(poll_seconds)
            job = self.transport.request("GET", f"/api/v9/translations/{job['id']}")
        raise OnshapeError(f"Onshape translation still running after {attempts} polls")

    def save(self, path=None) -> dict:
        """Onshape autosaves; a path with a .step extension exports instead."""
        if path is None:
            return {"url": self.url}
        if not str(path).lower().endswith(".step"):
            raise NotImplementedError("Onshape export supports STEP only")
        data = self.export_step()
        with open(path, "wb") as out:
            out.write(data)
        return {"path": str(path), "bytes": len(data)}

    def close(self) -> dict:
        self._snapshot = None
        return {"url": self.url}
