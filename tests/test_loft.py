import json
from unittest.mock import Mock

import pytest
from pydantic import ValidationError

from dahlia import File, TOP, loft, cut_loft, sketch
from dahlia.types import FeatureDefinition, PlanarFaceDefinition


@pytest.fixture(params=[loft, cut_loft])
def factory(request):
    return request.param


def test_named_profiles_use_feature_definitions_on_wire(factory):
    transport = Mock()
    File("part", _test_transport=transport).add_feature(
        factory(profiles=["Sketch2", "Sketch1"])
    )
    assert transport.call.call_args.kwargs["feature"]["profiles"] == [
        {"kind": "feature", "name": "Sketch2"},
        {"kind": "feature", "name": "Sketch1"},
    ]


def test_typed_profiles_preserve_geometry_on_wire(factory):
    face = PlanarFaceDefinition(on_face_point=(0, 0, 0), normal=(0, 0, 1))
    end = FeatureDefinition(name="Sketch2")
    feature = factory(profiles=[face, end])
    payload = json.loads(feature.model_dump_json())
    assert payload["profiles"] == [face.model_dump(mode="json"), end.model_dump()]
    assert type(feature).model_validate(payload).model_dump(mode="json") == payload


def test_sketch_profiles_and_centerline_keep_assigned_names(factory):
    start, end, centerline = [sketch(plane=TOP) for _ in range(3)]
    feature = factory(
        profiles=[start, end], centerline=centerline, number_of_sections=1
    )
    for item, name in zip((start, end, centerline), ("Sketch1", "Sketch2", "Sketch3")):
        item.name = name
    payload = feature.model_dump()
    assert [profile["name"] for profile in payload["profiles"]] == [
        "Sketch1",
        "Sketch2",
    ]
    assert all(profile["type"] == "Sketch" for profile in payload["profiles"])
    assert payload["centerline"]["name"] == "Sketch3"
    assert payload["centerline"]["type"] == "Sketch"
    assert payload["number_of_sections"] == 1


def test_loft_requires_two_profiles(factory):
    with pytest.raises(ValidationError, match="at least 2 profiles"):
        factory(profiles=["Sketch1"])


@pytest.mark.parametrize("field", ["start_apply_to_all", "end_apply_to_all"])
def test_unimplemented_constraints_are_rejected(factory, field):
    with pytest.raises(ValidationError, match="apply-to-all"):
        factory(profiles=["Sketch1", "Sketch2"], **{field: True})
