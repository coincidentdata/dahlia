from __future__ import annotations

import argparse
from pathlib import Path
from uuid import uuid4

from dahlia import MM, TOP, connect, create_file, extrude, sketch


def build(output: Path) -> Path:
    connect()
    name = f"Spacer_{uuid4().hex[:8]}"
    destination = output.resolve() / name
    destination.mkdir(parents=True)

    part = create_file()
    profile = sketch(plane=TOP, name="SpacerProfile")
    profile.add_circle((0, 0), radius=12 * MM)
    profile.add_circle((0, 0), radius=5 * MM)
    part.add_feature(profile)
    part.add_feature(extrude(sketch=profile, depth=8 * MM, name="Spacer"))

    part.save(str(destination / f"{name}.SLDPRT"))
    part.save(str(destination / f"{name}.step"))
    (destination / f"{name}.png").write_bytes(part.get_image())
    print(f"Saved native CAD, STEP, and PNG to {destination}")
    return destination


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Create and export a small spacer in SOLIDWORKS.")
    parser.add_argument("--output", type=Path, default=Path("outputs/quickstart"))
    build(parser.parse_args().output)
