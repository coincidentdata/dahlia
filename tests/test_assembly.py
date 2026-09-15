import math
from unittest.mock import Mock

import pytest
from pydantic import TypeAdapter, ValidationError

from dahlia import File, Feature, FRONT, RIGHT, MM, DEG, mates, ray, transform
from dahlia.assembly import Component, ComponentInput
from dahlia.types import Definition


def test_axis_angle_has_the_declared_local_to_assembly_convention():
    placement = transform((3 * MM, 4 * MM, 5 * MM), axis=(0, 0, 2), angle=90 * DEG)
    rotated = tuple(sum(row[i] * (1, 0, 0)[i] for i in range(3)) for row in placement.rotation)
    assert rotated == pytest.approx((0, 1, 0))
    assert placement.translation.x == 3 * MM
    assert transform(rotation=placement.rotation).rotation == placement.rotation


@pytest.mark.parametrize("kwargs", [
    {"axis": (0, 0, 0), "angle": 1},
    {"axis": (0, 1, 0)},
    {"angle": 1},
    {"translation": (math.inf, 0, 0)},
    {"rotation": ((1, 0, 0), (0, 1, 0), (0, 0, -1))},
    {"rotation": ((2, 0, 0), (0, 1, 0), (0, 0, 1))},
    {"rotation": ((1, 0, 0),)},
    {"rotation": ((1, 0, 0), (0, 1, 0), (0, 0, 1)), "axis": (0, 1, 0), "angle": 1},
])
def test_invalid_rigid_placements_fail_before_native_calls(kwargs):
    with pytest.raises(ValueError):
        transform(**kwargs)


def test_repeated_instances_have_distinct_references_and_round_trip():
    assembly = File("Fixture.SLDASM", _test_transport=Mock())
    first = assembly.component(("Bolt-1",))
    second = assembly.component(("Bolt-2",))
    a, b = first.ref(FRONT), second.ref(FRONT)
    assert a != b
    assert a.entity == b.entity
    assert TypeAdapter(Definition).validate_json(a.model_dump_json()) == a
    assert TypeAdapter(Definition).validate_json(first.ref().model_dump_json()) == first.ref()
    assert first.ref("Front Plane") == a


def test_component_handles_follow_assembly_save_as():
    transport = Mock()
    transport.call.side_effect = [{"FileName": "Saved.SLDASM", "path": "C:/Saved.SLDASM"}, None]
    assembly = File("Assembly1", _test_transport=transport)
    part = assembly.component(("Part-1",))
    assembly.save("C:/Saved.SLDASM")
    part.probe(ray((0, 0, 1), (0, 0, -1)), "face")
    assert transport.call.call_args.kwargs["file"] == "Saved.SLDASM"
    assert transport.call.call_args.kwargs["component"] == ["Part-1"]


def test_cross_assembly_component_edit_is_rejected():
    first = File("First.SLDASM", _test_transport=Mock())
    second = File("Second.SLDASM", _test_transport=Mock())
    with pytest.raises(ValueError, match="different assembly"):
        first.inspect_component(second.component(("Part-1",)))
    first._transport().call.assert_not_called()


def test_add_component_uses_assigned_occurrence_path():
    transport = Mock()
    transport.call.return_value = {"path": ["Part-8"]}
    assembly = File("Assembly1", _test_transport=transport)
    part = assembly.add_component("part.SLDPRT", fixed=True, transform=transform((0, 0, 2 * MM)))
    assert isinstance(part, Component)
    assert part.path == ("Part-8",)
    assert transport.call.call_args.kwargs["component"]["fixed"] is True


@pytest.mark.parametrize('color', [(1, 0, .5, .2), (0, 0, 0, 0), (1, 1, 1, 1), None])
def test_component_color_is_preserved_at_add_edit_and_clear(color):
    transport = Mock()
    transport.call.return_value = {'path': ['Part-8']}
    assembly = File('Assembly1', _test_transport=transport)
    part = assembly.add_component('part.SLDPRT', color=color)
    assert transport.call.call_args.kwargs['component']['color'] == color
    assembly.inspect_component = Mock(return_value=ComponentInput(source='part.SLDPRT').model_dump())
    assembly.edit_component(part, color=color)
    assert transport.call.call_args.kwargs['changes'] == {'color': color}


@pytest.mark.parametrize('color', [(1, 0, 0), (1, 0, 0, 1, 1), (-.1, 0, 0, 1),
    (0, 1.1, 0, 1), (0, 0, 0, math.nan), (math.inf, 0, 0, 1)])
def test_invalid_component_colors_fail_before_mutation(color):
    transport = Mock()
    assembly = File('Assembly1', _test_transport=transport)
    with pytest.raises(ValidationError):
        assembly.add_component('part.SLDPRT', color=color)
    assembly.inspect_component = Mock(return_value=ComponentInput(source='part.SLDPRT').model_dump())
    with pytest.raises(ValidationError):
        assembly.edit_component(['Part-1'], color=color)
    transport.call.assert_not_called()


def test_movement_sends_relative_requests_and_returns_actual_state():
    transport = Mock()
    actual = {"transform": transform((.002, 0, 0)).model_dump(), "moved": True}
    transport.call.return_value = actual
    assembly = File("Assembly1", _test_transport=transport)
    part = assembly.component(["Part-1"])
    assert assembly.translate_component(part, delta=(5 * MM, 0, 0)) is actual
    transport.call.assert_called_once_with(
        "TranslateComponent", file="Assembly1", component=["Part-1"], delta={"x": .005, "y": 0., "z": 0.})
    transport.reset_mock()
    actual["moved"] = False
    assert assembly.rotate_component(part, axis=(0, 0, -2), angle=270 * DEG) is actual
    transport.call.assert_called_once_with(
        "RotateComponent", file="Assembly1", component=["Part-1"], axis={"x": 0., "y": 0., "z": -2.}, angle=270 * DEG)


@pytest.mark.parametrize("method,kwargs", [
    ("translate_component", {"delta": (math.inf, 0, 0)}),
    ("translate_component", {"delta": (1,)}),
    ("rotate_component", {"axis": (0, 0, 0), "angle": 1}),
    ("rotate_component", {"axis": (0, 0, 1), "angle": math.nan}),
    ("rotate_component", {"axis": (math.inf, 0, 1), "angle": 1}),
    ("edit_component", {"transform": transform()}),
])
def test_invalid_movement_fails_before_native_calls(method, kwargs):
    transport = Mock()
    assembly = File("Assembly1", _test_transport=transport)
    with pytest.raises(ValueError):
        getattr(assembly, method)(assembly.component(["Part-1"]), **kwargs)
    transport.call.assert_not_called()


@pytest.mark.parametrize("method,kwargs", [
    ("translate_component", {"delta": (0, 0, 1)}),
    ("rotate_component", {"axis": (0, 0, 1), "angle": 1}),
])
def test_movement_preserves_assembly_ownership(method, kwargs):
    first, second = File("First", _test_transport=Mock()), File("Second", _test_transport=Mock())
    with pytest.raises(ValueError, match="different assembly"):
        getattr(first, method)(second.component(["Part-1"]), **kwargs)
    first._transport().call.assert_not_called()


@pytest.mark.parametrize("mate", [
    mates.coincident(FRONT, FRONT, alignment="opposed", pick_points=((0, 0, 0), (0, 0, 0))),
    mates.concentric(FRONT, FRONT, lock_rotation=True),
    mates.parallel(FRONT, FRONT),
    mates.perpendicular(FRONT, FRONT),
    mates.tangent(FRONT, FRONT),
    mates.lock(FRONT, FRONT),
    mates.distance(FRONT, FRONT, distance=5 * MM, limits=(0, 10 * MM), flipped=True),
    mates.angle(FRONT, FRONT, angle=30 * DEG, limits=(0, 90 * DEG), reference=FRONT),
])
def test_mate_payloads_use_the_shared_feature_union(mate):
    restored = TypeAdapter(Feature).validate_json(mate.model_dump_json())
    assert isinstance(restored, mates.Mate)
    assert restored.model_dump(mode="json") == mate.model_dump(mode="json")


@pytest.mark.parametrize("factory,dimension", [
    (mates.distance, "distance"), (mates.angle, "angle"),
])
@pytest.mark.parametrize("value,options", [
    (-1, {}), (math.nan, {}), (math.inf, {}),
    (2, {"limits": (3, 4)}),
    (2, {"limits": (0, 1)}),
    (2, {"limits": (-1, 3)}),
    (2, {"limits": (3, 0)}),
    (2, {"limits": (0, math.inf)}),
    (2, {"limits": (math.nan, 3)}),
    (2, {"alignment": "swMateAlignALIGNED"}),
])
def test_bad_mate_dimensions_and_native_enum_names_are_rejected(factory, dimension, value, options):
    with pytest.raises(ValidationError):
        factory(FRONT, FRONT, **{dimension: value}, **options)


@pytest.mark.parametrize("factory", [
    mates.coincident, mates.concentric, mates.parallel, mates.perpendicular, mates.tangent,
])
@pytest.mark.parametrize("value", [math.nan, math.inf, -math.inf])
def test_nonfinite_mate_pick_points_are_rejected(factory, value):
    with pytest.raises(ValidationError, match="finite coordinates"):
        factory(FRONT, FRONT, pick_points=((0, 0, 0), (0, value, 0)))


def test_mates_preserve_occurrence_qualification_at_transport_boundary():
    transport = Mock()
    transport.call.return_value = {"name": "Axis"}
    assembly = File("Assembly1", _test_transport=transport)
    first, second = assembly.component(("Part-1",)), assembly.component(("Part-2",))
    assembly.add_feature(mates.concentric(first.ref(FRONT), second.ref(FRONT), name="Axis"))
    payload = transport.call.call_args.kwargs["feature"]
    assert tuple(payload["entities"][0]["component"]) == ("Part-1",)
    assert tuple(payload["entities"][1]["component"]) == ("Part-2",)
    assert payload["entities"][0]["entity"]["name"] == "Front Plane"
    assert mates.lock(first, second).entities[0].kind == "component"


@pytest.mark.parametrize("changes", [
    {"distnace": .008}, {"lock_rotation": True}, {"distance": -1},
    {"limits": (.006, .01)}, {"entities": [FRONT]},
    {"type": "MateConcentric"}, {"name": "Renamed"},
])
def test_invalid_mate_edits_fail_before_mutation(changes):
    transport = Mock()
    assembly = File("Assembly1", _test_transport=transport)
    assembly.inspect = Mock(return_value=mates.distance(
        FRONT, FRONT, distance=.005, name="Offset").model_dump(mode="json"))
    with pytest.raises(ValueError):
        assembly.edit_feature("Offset", changes)
    transport.call.assert_not_called()


def test_mate_edit_validates_and_preserves_existing_references():
    transport = Mock()
    assembly = File("Assembly1", _test_transport=transport)
    first, second = assembly.component(["A-1"]), assembly.component(["B-1"])
    original = mates.distance(first.ref(FRONT), second.ref(FRONT),
                              distance=.005, alignment="opposed", name="Offset").model_dump()
    assembly.inspect = Mock(return_value=original)
    assembly.edit_feature("Offset", distance=.008)
    payload = transport.call.call_args.kwargs["feature"]
    assert payload["distance"] == .008
    assert payload["alignment"] == "opposed"
    assert payload["entities"] == original["entities"]


def test_mate_reference_edits_keep_nested_definition_discriminators():
    transport = Mock()
    assembly = File("Assembly1", _test_transport=transport)
    first, second = assembly.component(["A-1"]), assembly.component(["B-1"])
    assembly.inspect = Mock(return_value=mates.coincident(
        first.ref(FRONT), second.ref(FRONT), name="Contact").model_dump())
    references = [first.ref(RIGHT), second.ref(FRONT)]
    assembly.edit_feature("Contact", entities=references)
    payload = transport.call.call_args.kwargs["feature"]
    assert list(payload["entities"]) == [ref.model_dump() for ref in references]


def test_mate_names_use_the_existing_feature_definition_format():
    value = mates.angle("Front Plane", "OffsetPlane", angle=.5, reference="AssemblyAxis")
    payload = value.model_dump()
    assert payload["entities"][1] == {"kind": "feature", "name": "OffsetPlane"}
    assert payload["reference"] == {"kind": "feature", "name": "AssemblyAxis"}


def test_editing_with_an_unnamed_model_preserves_the_feature_name():
    transport = Mock()
    assembly = File("Assembly1", _test_transport=transport)
    assembly.inspect = Mock(return_value=mates.distance(
        FRONT, FRONT, distance=.005, name="Offset").model_dump(mode="json"))
    assembly.edit_feature("Offset", mates.distance(FRONT, FRONT, distance=.008))
    assert transport.call.call_args.kwargs["feature"]["name"] == "Offset"
