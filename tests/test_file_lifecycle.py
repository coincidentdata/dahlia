from unittest.mock import Mock
from pathlib import Path

import pytest

from dahlia import CM, INCH, M, MM, File


@pytest.mark.parametrize("units, name", [(MM, "mm"), (CM, "cm"), (M, "m"), (INCH, "inch"), ("mm", "mm"), ("IPS", "IPS")])
def test_display_units_send_names_instead_of_numeric_scale_factors(units, name):
    transport = Mock()
    file = File("part.SLDPRT", _test_transport=transport)

    file.set_units(units)

    transport.call.assert_called_once_with("SetUnits", file="part.SLDPRT", units=name)


def test_file_paths_resolve_in_python_working_directory(monkeypatch, tmp_path):
    import dahlia.session as session
    monkeypatch.chdir(tmp_path)
    transport = Mock()
    transport.call.return_value = {'FileName': 'part.SLDPRT', 'path': ['part-1']}
    monkeypatch.setattr(session, '_TRANSPORT', transport)
    file = session.open_file('part.SLDPRT')
    transport.call.assert_called_with('OpenFile', path=str(tmp_path / 'part.SLDPRT'))
    file.save('copy.SLDPRT')
    transport.call.assert_called_with('SaveFileAs', file='part.SLDPRT', path=str(tmp_path / 'copy.SLDPRT'))
    file.add_component(Path('part.SLDPRT'))
    assert transport.call.call_args.kwargs['component']['source'] == str(tmp_path / 'part.SLDPRT')


def test_save_as_updates_handle_for_subsequent_commands():
    transport = Mock()
    transport.call.side_effect = [
        {"FileName": "bracket.SLDPRT", "path": "C:/parts/bracket.SLDPRT"},
        {"body_count": 1},
    ]
    file = File("Part1", _test_transport=transport)

    file.save("C:/parts/bracket.SLDPRT")
    file.view()

    assert file.name == "bracket.SLDPRT"
    transport.call.assert_called_with("ViewFile", file="bracket.SLDPRT")


def test_neutral_export_keeps_native_name():
    transport = Mock()
    transport.call.return_value = {
        "FileName": "bracket.SLDPRT", "path": "C:/parts/bracket.SLDPRT"
    }
    file = File("bracket.SLDPRT", _test_transport=transport)

    file.save("C:/exports/final_model.step")
    file.save()

    transport.call.assert_called_with("SaveFile", file="bracket.SLDPRT")


def test_failed_save_keeps_handle_and_surfaces_error():
    transport = Mock()
    transport.call.side_effect = RuntimeError("another open document is named 'bracket.SLDPRT'")
    file = File("Part1", _test_transport=transport)

    with pytest.raises(RuntimeError, match="another open document"):
        file.save("C:/parts/bracket.SLDPRT")

    assert file.name == "Part1"


def test_default_image_orientation_matches_native_api():
    transport = Mock()
    transport.call.return_value = {"ImageBase64": "cG5n"}
    file = File("bracket.SLDPRT", _test_transport=transport)

    assert file.get_image() == b"png"
    transport.call.assert_called_with("GetImage", file="bracket.SLDPRT", orientation="Isometric")


@pytest.mark.parametrize("response", [None, {}, {"ImageBase64": None}, "png"])
def test_invalid_image_response_raises(response):
    transport = Mock()
    transport.call.return_value = response
    file = File("bracket.SLDPRT", _test_transport=transport)

    with pytest.raises(TypeError, match="GetImage returned an invalid response"):
        file.get_image()


def test_invalid_image_encoding_raises():
    transport = Mock()
    transport.call.return_value = {"ImageBase64": "!!!!"}
    file = File("bracket.SLDPRT", _test_transport=transport)

    with pytest.raises(ValueError):
        file.get_image()
