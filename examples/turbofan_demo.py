from __future__ import annotations

import argparse
import math
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from uuid import uuid4

from dahlia import (
    FRONT, RIGHT, TOP, connect, create_file, feature_ref, loft, mates,
    ref_axis, ref_plane, revolve, sketch, circular_pattern,
    extrude, transform, cut_revolve,
)


@dataclass(frozen=True)
class Parameters:
    fan_blades: int = 20
    core_blades: int = 24
    turbine_blades: int = 28


@dataclass(frozen=True)
class Stage:
    name: str
    stations: tuple[float, ...]
    root: float
    tip: float
    spool: str | None = None


COLORS = {
    "Nacelle": (0.72, 0.76, 0.78, 0.10),
    "IntakeLip": (0.64, 0.72, 0.76, 1.0),
    "shaft": (0.13, 0.18, 0.22, 1.0),
    "CoreHousing": (0.68, 0.72, 0.74, 0.12),
    "Combustor": (0.43, 0.45, 0.44, 1.0),
    "CoreNozzle": (0.54, 0.57, 0.59, 0.30),
    "frame": (0.36, 0.41, 0.44, 1.0),
    "stator": (0.42, 0.49, 0.53, 1.0),
    "hot_stator": (0.39, 0.34, 0.28, 1.0),
    "Fan": (0.025, 0.38, 0.44, 1.0),
    "compressor": (0.58, 0.67, 0.72, 1.0),
    "turbine": (0.55, 0.40, 0.26, 1.0),
}


CORE_CONTOUR = (
    (-0.22, 0.55), (-0.36, 0.57), (-0.77, 0.545), (-0.90, 0.50),
    (-1.20, 0.455), (-1.55, 0.390), (-1.86, 0.370), (-2.02, 0.450),
    (-2.38, 0.445), (-2.50, 0.405), (-2.85, 0.404), (-3.20, 0.448),
    (-3.62, 0.483), (-3.76, 0.478),
)


ROTORS = (
    Stage("Fan", (0.00,), 0.220, 1.005, "low"),
    Stage("Booster", (-0.49, -0.69), 0.190, 0.495, "low"),
    Stage("CompressorFront", (-0.94, -1.10, -1.26), 0.210, 0.410, "high"),
    Stage("CompressorRear", (-1.42, -1.58, -1.74), 0.240, 0.345, "high"),
    Stage("HighPressureTurbine", (-2.58, -2.79), 0.220, 0.340, "high"),
    Stage("LowPressureTurbine", (-3.00, -3.20, -3.40, -3.60), 0.200, 0.375, "low"),
)


def core_radius(station):
    for (front, front_radius), (rear, rear_radius) in zip(CORE_CONTOUR, CORE_CONTOUR[1:]):
        if rear <= station <= front:
            fraction = (station - front) / (rear - front)
            return front_radius + fraction * (rear_radius - front_radius)
    raise ValueError(f"Station outside core casing: {station}")


STATORS = (
    Stage("FanOutletGuide", (-0.27,), 0.560, 1.025),
    *(Stage(name, stations, root, min(core_radius(station) for station in stations) - 0.026)
      for name, stations, root in (
          ("BoosterGuide", (-0.59, -0.81), 0.195),
          ("CompressorStatorFront", (-1.02, -1.18, -1.34), 0.215),
          ("CompressorStatorRear", (-1.50, -1.66), 0.240),
          ("HighPressureGuide", (-2.48, -2.685, -2.895), 0.225),
          ("LowPressureGuide", (-3.10, -3.30, -3.50), 0.200),
      )),
)


def polygon(part, name, points, plane=FRONT):
    profile = sketch(plane=plane, name=name)
    for start, end in zip(points, points[1:] + points[:1]):
        profile.add_line(start, end)
    part.add_feature(profile)
    return profile


def meridian(part, name, points, *, merge=True):
    profile = polygon(part, name + "Profile", points)
    return part.add_feature(revolve(
        sketch=profile, axis=feature_ref("EngineAxis"), merge=merge, name=name,
    ))


def engine_axis(part):
    part.add_feature(ref_axis(
        references=[FRONT, TOP], axis_type="TwoPlanes", name="EngineAxis",
    ))


def blade_stage(part, stage, count):
    root, tip = stage.root, stage.tip
    stator = stage.spool is None
    fan = stage.name == "Fan"
    outlet = stage.name == "FanOutletGuide"
    if fan:
        tip += 0.050
    if stator:
        meridian(part, "InnerShroud", [(-0.019, root - 0.022), (0.019, root - 0.022),
                                       (0.019, root), (-0.019, root)])
    else:
        bore = 0.052 if stage.spool == "low" else 0.100
        meridian(part, "RotorDisk", [(-0.035, bore), (0.035, bore),
                                    (0.035, root - 0.012), (0.022, root),
                                    (-0.022, root), (-0.035, root - 0.012)])
    profiles = []
    for index, fraction in enumerate((0.0, 0.35, 0.72, 1.0)):
        start_radius = root * (0.98 if stator else 0.94)
        radius = start_radius + (tip - start_radius) * fraction
        plane_name = f"BladeSectionPlane{index + 1}"
        part.add_feature(ref_plane(
            references=[TOP], constraints=["Distance"], distance=radius,
            expected_normal=[0, 1, 0], name=plane_name,
        ))
        if fan:
            chord = 0.065 + 0.245 * fraction
            twist = math.radians(65 + 14 * fraction)
            axial_sweep = -0.030 * fraction ** 1.5
            sweep = 0.20 * fraction ** 2
            thickness = 0.028 - 0.016 * fraction
        elif outlet:
            chord = 0.09 + 0.025 * fraction
            twist = math.radians(-62 - 12 * fraction)
            axial_sweep = 0
            sweep = -0.035 * fraction ** 1.3
            thickness = 0.014 - 0.005 * fraction
        elif stator:
            chord = 0.055 + 0.015 * fraction
            twist = math.radians(-55 - 20 * fraction)
            axial_sweep = 0
            sweep = -0.018 * fraction ** 1.3
            thickness = 0.009 * (1 - 0.3 * fraction)
        else:
            chord = 0.075 + 0.010 * fraction
            twist = math.radians(30 + 40 * fraction)
            axial_sweep = -0.015 * fraction
            sweep = 0.022 * fraction ** 1.3
            thickness = (0.015 if stage.stations[0] < -2.4 else 0.012) * (1 - 0.35 * fraction)
        section = []
        for along, across in ((-0.5, 0), (-0.40, 0.48), (-0.16, 0.70), (0.16, 0.46),
                              (0.5, 0), (0.20, -0.16), (-0.12, -0.22), (-0.40, -0.14)):
            x = axial_sweep + along * chord * math.cos(twist) - across * thickness * math.sin(twist)
            z = sweep + along * chord * math.sin(twist) + across * thickness * math.cos(twist)
            section.append((x, -z))
        profiles.append(polygon(part, f"BladeSection{index + 1}", section, feature_ref(plane_name)))
    blade = part.add_feature(loft(profiles=profiles, name="SweptTwistedBlade"))
    part.add_feature(circular_pattern(
        seeds=[feature_ref(blade)], axis=feature_ref("EngineAxis"),
        total_angle=math.tau, count=count, geometry_pattern=True, name="BladeRow",
    ))
    if fan:
        trim = polygon(part, "CylindricalTipEnvelope", [(-0.20, stage.tip), (0.20, stage.tip),
            (0.20, stage.tip + 0.30), (-0.20, stage.tip + 0.30)])
        part.add_feature(cut_revolve(sketch=trim, axis=feature_ref("EngineAxis"), name="UniformFanTipClearance"))
    if stator:
        meridian(part, "OuterShroud", [(-0.019, tip - 0.010), (0.019, tip - 0.010),
                                       (0.019, tip + 0.018), (-0.019, tip + 0.018)])


def nacelle(part):
    profile = sketch(plane=FRONT, name="BarrelNacelleProfile")
    outer = [(0.055, 1.055), (-0.65, 1.10), (-2.30, 1.01), (-3.20, 0.72)]
    inner = [(x, radius - 0.025) for x, radius in outer]
    knots = [0, 0, 0, 0, 1, 1, 1, 1]
    profile.add_spline(outer, knots, generic=True)
    profile.add_line(outer[-1], inner[-1])
    profile.add_spline(inner[::-1], knots, generic=True)
    profile.add_line(inner[0], outer[0])
    part.add_feature(profile)
    part.add_feature(revolve(sketch=profile, axis=feature_ref("EngineAxis"), name="CompleteNacelle"))


def intake_lip(part):
    profile = sketch(plane=FRONT, name="RoundedIntakeProfile")
    profile.add_circle((0.055, 1.055), radius=0.035)
    part.add_feature(profile)
    part.add_feature(revolve(sketch=profile, axis=feature_ref("EngineAxis"), name="RoundedIntakeLip"))


def core_housing(part):
    outer = list(CORE_CONTOUR)
    meridian(part, "CompleteCoreCasing", outer + [(x, r - 0.020) for x, r in outer[::-1]])
    for index, station in enumerate((-0.87, -1.86, -2.50, -3.72), start=1):
        radius = core_radius(station)
        meridian(part, f"CasingJoint{index}", [(station - 0.012, radius - 0.006),
            (station + 0.012, radius - 0.006), (station + 0.012, radius + 0.012),
            (station - 0.012, radius + 0.012)])


def curved_centerbody(part, name, points):
    profile = sketch(plane=FRONT, name=name + "Profile")
    profile.add_spline(points, [0, 0, 0, 0, 1, 1, 1, 1], generic=True)
    profile.add_line(points[-1], (points[0][0], 0))
    profile.add_line((points[0][0], 0), points[0])
    part.add_feature(profile)
    part.add_feature(revolve(sketch=profile, axis=feature_ref("EngineAxis"), name=name))


def low_pressure_shaft(part):
    meridian(part, "LowPressureShaft", [(-4.06, 0), (0.16, 0), (0.16, 0.052), (-4.06, 0.052)])
    curved_centerbody(part, "OgiveSpinner", [(0.035, 0.22), (0.12, 0.22), (0.23, 0.13), (0.28, 0)])
    curved_centerbody(part, "ExhaustCenterbody", [(-3.94, 0.18), (-4.10, 0.18), (-4.32, 0.11), (-4.50, 0)])


def high_pressure_shaft(part):
    meridian(part, "HollowHighPressureShaft", [(-2.87, 0.068), (-0.89, 0.068),
                                              (-0.89, 0.100), (-2.87, 0.100)])


def combustor(part):
    meridian(part, "AnnularDome", [(-1.89, 0.29), (-1.89, 0.32), (-1.95, 0.385),
        (-2.035, 0.405), (-2.045, 0.390), (-1.97, 0.373), (-1.918, 0.317), (-1.918, 0.29)])
    meridian(part, "InnerLiner", [(-1.90, 0.277), (-2.48, 0.277), (-2.48, 0.295), (-1.90, 0.295)])
    meridian(part, "OuterLiner", [(-2.02, 0.392), (-2.36, 0.392), (-2.47, 0.355),
                                   (-2.47, 0.372), (-2.36, 0.410), (-2.02, 0.410)])
    meridian(part, "LinerFlange", [(-2.30, 0.402), (-2.33, 0.402), (-2.33, 0.430), (-2.30, 0.430)])
    profile = sketch(plane=FRONT, name="LinerBossProfile")
    profile.add_circle((-2.15, 0.405), radius=0.012)
    part.add_feature(profile)
    boss = part.add_feature(extrude(sketch=profile, depth=0.018, both_directions=True, name="LinerBoss"))
    part.add_feature(circular_pattern(seeds=[feature_ref(boss)], axis=feature_ref("EngineAxis"),
        total_angle=math.tau, count=12, geometry_pattern=True, name="LinerBossRing"))


def frame(part, radius):
    meridian(part, "BearingHousing", [(-0.035, 0.070), (0.035, 0.070), (0.035, 0.145), (-0.035, 0.145)])
    profile = polygon(part, "SupportStrutProfile", [(-0.027, 0.135), (0.027, 0.135),
                                                   (0.020, radius), (-0.020, radius)])
    strut = part.add_feature(extrude(sketch=profile, depth=0.015, both_directions=True, name="SupportStrut"))
    part.add_feature(circular_pattern(seeds=[feature_ref(strut)], axis=feature_ref("EngineAxis"),
        total_angle=math.tau, count=4, geometry_pattern=True, name="SupportStruts"))


def core_nozzle(part):
    meridian(part, "CoreExhaustNozzle", [(-3.72, 0.483), (-3.82, 0.465), (-4.12, 0.300),
        (-4.12, 0.278), (-3.82, 0.443), (-3.72, 0.461)])


def assemble(paths):
    import win32com.client

    print("Assembling housings, independent spools, and blade rows", flush=True)
    assembly = create_file(kind="assembly")
    model = win32com.client.GetActiveObject("SldWorks.Application").ActiveDoc
    housing = assembly.add_component(paths["Nacelle"], fixed=True, color=COLORS["Nacelle"])
    stationary = {"Nacelle": housing}
    for name, station in (("IntakeLip", 0), ("CoreHousing", 0), ("Combustor", 0),
                          ("FrontFrame", -0.38), ("RearFrame", -3.84), ("CoreNozzle", 0)):
        color = COLORS.get(name, COLORS["frame"])
        component = assembly.add_component(paths[name], transform=transform((station, 0, 0)), color=color)
        assembly.add_feature(mates.lock(housing, component, name=f"{name}ToNacelle"))
        stationary[name] = component
        model.GraphicsRedraw2()
    shafts = {}
    for spool, name in (("low", "LowPressureShaft"), ("high", "HighPressureShaft")):
        shaft = assembly.add_component(paths[name], color=COLORS["shaft"])
        assembly.add_feature(mates.coincident(housing.ref("EngineAxis"), shaft.ref("EngineAxis"),
            alignment="aligned", name=f"{name}Centerline"))
        assembly.add_feature(mates.coincident(housing.ref(RIGHT), shaft.ref(RIGHT),
            alignment="aligned", name=f"{name}AxialLocation"))
        shafts[spool] = shaft
        model.GraphicsRedraw2()
    for stage in (*ROTORS, *STATORS):
        hot = stage.stations[0] < -2.4
        color = COLORS.get(stage.name, COLORS[("turbine" if hot else "compressor")
                           if stage.spool else ("hot_stator" if hot else "stator")])
        target = shafts[stage.spool] if stage.spool else stationary["CoreHousing"]
        for index, station in enumerate(stage.stations, start=1):
            component = assembly.add_component(paths[stage.name], transform=transform((station, 0, 0)), color=color)
            assembly.add_feature(mates.lock(target, component, name=f"{stage.name}{index}Attachment"))
            model.GraphicsRedraw2()
    assembly.rebuild()
    model.GraphicsRedraw2()
    return assembly


def build(output):
    parameters = Parameters()
    run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + "_" + uuid4().hex[:8]
    output = Path(output).resolve() / run_id
    output.mkdir(parents=True)
    print(f"Output: {output}", flush=True)
    connect()
    paths = {}

    def row_count(stage):
        if stage.name == "Fan":
            return parameters.fan_blades
        return parameters.turbine_blades if stage.stations[0] < -2.4 else parameters.core_blades

    authors = [
        ("Fan", lambda part: blade_stage(part, ROTORS[0], parameters.fan_blades)),
        ("Nacelle", nacelle), ("IntakeLip", intake_lip), ("CoreHousing", core_housing),
        ("LowPressureShaft", low_pressure_shaft), ("HighPressureShaft", high_pressure_shaft),
        ("Combustor", combustor), ("FrontFrame", lambda part: frame(part, 0.566)),
        ("RearFrame", lambda part: frame(part, 0.446)), ("CoreNozzle", core_nozzle),
        *((stage.name, lambda part, stage=stage: blade_stage(part, stage, row_count(stage)))
          for stage in (*ROTORS[1:], *STATORS)),
    ]
    for label, author in authors:
        print(f"Building {label}", flush=True)
        part = create_file()
        engine_axis(part)
        author(part)
        part.rebuild()
        path = output / f"{label}_{run_id}.SLDPRT"
        part.save(str(path))
        paths[label] = path
        part.close()

    assembly = assemble(paths)
    assembly.save(str(output / f"Turbofan_{run_id}.SLDASM"))
    print(f"Saved {len(paths)} parts and the turbofan assembly to {output}", flush=True)
    return output


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Build an intact transparent turbofan in live SOLIDWORKS.")
    parser.add_argument("--output", type=Path, default=Path("outputs/turbofan_demo"))
    build(parser.parse_args().output)
