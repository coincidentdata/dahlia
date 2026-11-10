"""Math helpers. Free functions, short names."""
from __future__ import annotations

import math
from typing import Any, Union

from .types import Point2D


def feature_ref(feat: Union[dict, str, Any]) -> dict:
    """Wrap a feature-name (or the dict returned by ``target.add_feature``)
    as a FeatureDefinition for use in fields like ``Sweep.path`` or
    ``LinearPattern.seeds``.

    Accepts:
      - a string feature name (e.g. ``'Curve2'``)
      - a dict from ``target.add_feature(...)`` (reads ``['name']``)
      - a feature pydantic instance with a populated ``.name`` attribute
        (works after ``target.add_feature`` back-fills it)

    Returns ``{'kind': 'feature', 'name': <name>}`` — the on-wire shape
    the SolidWorks plugin resolves via ``FindFeatureByName``. Used in
    generated transcripts so cross-feature paths survive verify replay:

        curve_1 = target.add_feature(projected_curve(sketch=s2, ...))
        target.add_feature(sweep(profile=s3, path=feature_ref(curve_1), ...))
    """
    if isinstance(feat, str):
        return {"kind": "feature", "name": feat}
    if isinstance(feat, dict):
        name = feat.get("name")
        if isinstance(name, str):
            return {"kind": "feature", "name": name}
        raise ValueError(
            f"feature_ref: dict has no string 'name' key (keys={list(feat)})")
    name = getattr(feat, "name", None)
    if isinstance(name, str):
        return {"kind": "feature", "name": name}
    raise TypeError(
        f"feature_ref: expected str / dict / feature model with .name; got {feat!r}")


def linspace(a: float, b: float, n: int) -> list[float]:
    """n evenly spaced points from a to b inclusive."""
    if n < 1:
        raise ValueError("linspace n must be >= 1")
    if n == 1:
        return [a]
    step = (b - a) / (n - 1)
    return [a + i * step for i in range(n)]


def midpoint(a: tuple[float, float] | Point2D,
             b: tuple[float, float] | Point2D) -> Point2D:
    ax, ay = (a.x, a.y) if isinstance(a, Point2D) else a
    bx, by = (b.x, b.y) if isinstance(b, Point2D) else b
    return Point2D(x=(ax + bx) / 2.0, y=(ay + by) / 2.0)


def circle_points(center: tuple[float, float] | Point2D,
                  radius: float, n: int, *,
                  start_angle: float = 0.0) -> list[Point2D]:
    """n points evenly spaced on a circle. start_angle in radians."""
    if n < 1:
        raise ValueError("circle_points n must be >= 1")
    if radius <= 0:
        raise ValueError("circle_points radius must be > 0")
    cx, cy = (center.x, center.y) if isinstance(center, Point2D) else center
    out: list[Point2D] = []
    for i in range(n):
        theta = start_angle + 2.0 * math.pi * i / n
        out.append(Point2D(x=cx + radius * math.cos(theta),
                           y=cy + radius * math.sin(theta)))
    return out


def offset_points(points: list[Point2D] | list[tuple[float, float]],
                  distance: float) -> list[Point2D]:
    """Offset a polyline by `distance` along the local normal (left side
    for CCW). Endpoints offset by their adjacent segment's normal; interior
    points by the average of the two adjacent normals.

    Naive implementation; intended for simple convex contours.
    """
    pts = [p if isinstance(p, Point2D) else Point2D(x=p[0], y=p[1])
           for p in points]
    if len(pts) < 2:
        raise ValueError("offset_points needs at least 2 points")

    def _normal(a: Point2D, b: Point2D) -> tuple[float, float]:
        dx, dy = b.x - a.x, b.y - a.y
        n = math.hypot(dx, dy)
        if n == 0:
            return (0.0, 0.0)
        # left normal of (dx, dy) is (-dy, dx)
        return (-dy / n, dx / n)

    out: list[Point2D] = []
    for i, p in enumerate(pts):
        if i == 0:
            nx, ny = _normal(pts[0], pts[1])
        elif i == len(pts) - 1:
            nx, ny = _normal(pts[-2], pts[-1])
        else:
            ax, ay = _normal(pts[i - 1], pts[i])
            bx, by = _normal(pts[i], pts[i + 1])
            nx, ny = (ax + bx) / 2.0, (ay + by) / 2.0
            mag = math.hypot(nx, ny)
            if mag != 0:
                nx, ny = nx / mag, ny / mag
        out.append(Point2D(x=p.x + distance * nx, y=p.y + distance * ny))
    return out


def contour(*segments: Any) -> dict:
    """Wrap sketch segments as a SketchContourDefinition for `contours=`.

    `contour(s1.entities[0], s1.entities[1])` instead of the wire shape
    `{"kind": "sketch_contour", "segments": [...]}`. A contour is the unordered
    SET of its segments — SW's ISketchContour has no GetID() pair, so the segment
    set is the stable handle.
    """
    if not segments:
        raise ValueError("contour() needs at least one sketch segment")
    return {"kind": "sketch_contour", "segments": list(segments)}
