import json

import pytest
from pydantic import TypeAdapter, ValidationError

from dahlia import Feature, Mirror, RIGHT, TOP, mirror
from dahlia.types import PlanarFaceDefinition


def test_mirror_names_and_typed_planes_survive_wire_roundtrip():
    face = PlanarFaceDefinition(on_face_point=(0, 0, 0), normal=(0, 0, 1))
    feature = mirror(plane=RIGHT, secondary_plane=face, seeds=["Boss1"], mirror_seed_only=True)
    payload = json.loads(feature.model_dump_json())
    assert payload["seeds"] == [{"kind": "feature", "name": "Boss1"}]
    assert payload["secondary_plane"] == face.model_dump(mode="json")
    assert isinstance(TypeAdapter(Feature).validate_python(payload), Mirror)
    assert Mirror.model_validate(payload).model_dump() == feature.model_dump()


@pytest.mark.parametrize("options, message", [
    ({"seeds": []}, "at least 1"),
    ({"mirror_seed_only": True}, "requires secondary_plane"),
    ({"scope": "SelectedBodies"}, "requires feature_scope"),
    ({"feature_scope": [{"kind": "body"}]}, "requires AutoSelect or SelectedBodies"),
    ({"scope": "unsupported"}, "Input should be"),
])
def test_invalid_mirror_options_fail_before_transport(options, message):
    with pytest.raises(ValidationError, match=message):
        mirror(**{"plane": TOP, "seeds": ["Cut1"], **options})
