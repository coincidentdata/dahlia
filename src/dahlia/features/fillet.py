"""Fillet feature model + lowercase factories."""
from __future__ import annotations

from typing import Literal, Optional

from pydantic import Field, field_validator, model_validator

from ..types import Echo, Point3D
from ._base import _Base, DefinitionValue


# -- Fillet ----------------------------------------------------------------
#
# Fillet carries three variants in a single class with an explicit `kind`
# discriminator (the top-level `Feature` union already uses `type="Fillet"`, so
# the inner discriminator goes on `kind` instead — same convention as Chamfer).
# Maps to SolidWorks FilletType: ConstantRadius / Face / FullRound.
#
#   kind="ConstantRadius" (default):
#     - `edges` is a list of edge / loop / face / feature Definitions.
#     - `radius` is the primary fillet radius (RadiusA).
#     - `radius_b` + `symmetric=False` enables AsymmetricFillet (radius applies to
#       the second side); both fields are no-ops when symmetric=True.
#     - `profile` selects the cross-section type. `conic_value` is the rho ratio
#       (0.05..0.95, unitless) for ConicRho / ConicRhoZeroChamfer, or a radius
#       (meters) for ConicRadius. Ignored for Circular / CurvatureContinuous.
#     - `round_corners` toggles SolidWorks' "Round corners" option on intersecting
#       fillets.
#     - `overflow` maps to swFilletOverflowType_e (Default / KeepEdge / KeepSurface).
#
#   kind="Face":
#     - `face_set_a` and `face_set_b` are the two sets of faces the fillet runs
#       between. `help_point` is an optional 3D point in model space disambiguating
#       which side of the face pair the fillet sits on (written via
#       SimpleFilletFeatureData2.HelpPoint after creation).
#     - `face_type` selects the per-variant shape:
#         * "Radius"     ⇒ `radius` (+ `radius_b` / `symmetric` for asymmetric;
#                          `profile` + `conic_value` for conic / curvature-continuous)
#         * "ChordWidth" ⇒ `chord_width` is the constant-width value (meters);
#                          `profile` + `conic_value` per Radius
#         * "HoldLines"  ⇒ `hold_lines` is the list of edges the fillet conforms
#                          to; `profile` selects the cross-section continuity
#     - `edges` is unused for Face fillets.
#
#   kind="FullRound":
#     - `face_set_a`, `face_set_b`, `face_set_c` are the three sets of faces
#       (set B is the "center" / blended set: swFullRoundFilletCenterSet).
#     - No radius — SolidWorks computes the full-round geometry from the three
#       face sets. `tangent_propagation` is the only shared option.

# Profile (cross-section) enum: maps to SolidWorks swFeatureFilletProfileType_e
# + the orthogonal `CurvatureContinuous` flag (kept as a profile value here
# for symmetry).
_FilletProfile = Literal[
    "Circular",
    "ConicRho",
    "ConicRadius",
    "ConicRhoZeroChamfer",
    "CurvatureContinuous",
]


class Fillet(_Base):
    type: Literal["Fillet"] = "Fillet"
    # Inspect/Add-result only — SolidWorks-assigned feature name. See Extrude.name.
    name: Optional[str] = None
    kind: Literal["ConstantRadius", "Face", "FullRound"] = "ConstantRadius"

    # Constant-radius / face-radius / face-chord-width selections.
    edges: list[DefinitionValue] = Field(default_factory=list)

    # Shared.
    tangent_propagation: bool = True

    # Constant-radius and Face(Radius) carrier; also the chord_width value when
    # Face.face_type=="ChordWidth" lives on `chord_width` instead.
    radius: Optional[float] = None
    radius_b: Optional[float] = None  # second side for asymmetric (Constant or Face/Radius)
    symmetric: bool = True             # False ⇒ SolidWorks AsymmetricFillet=true

    # Cross-section. `conic_value` is the rho ratio (unitless 0.05..0.95) for
    # ConicRho / ConicRhoZeroChamfer, or a meters radius for ConicRadius.
    # Ignored for Circular / CurvatureContinuous.
    profile: _FilletProfile = "Circular"
    conic_value: Optional[float] = None

    # ConstantRadius-only options.
    round_corners: bool = False
    overflow: Literal["Default", "KeepEdge", "KeepSurface"] = "Default"

    # Face-fillet fields (kind=="Face").
    face_set_a: Optional[list[DefinitionValue]] = None
    face_set_b: Optional[list[DefinitionValue]] = None
    help_point: Optional[Point3D] = None
    face_type: Optional[Literal["Radius", "ChordWidth", "HoldLines"]] = None
    chord_width: Optional[float] = None
    hold_lines: Optional[list[DefinitionValue]] = None

    # FullRound-only field (kind=="FullRound") — face_set_a / face_set_b reused
    # for sets 1 and the center set; face_set_c is the third set.
    face_set_c: Optional[list[DefinitionValue]] = None

    # Inspect-only echo: faces SW reports as affected by the fillet operation.
    echo_affected_faces: Optional[list[dict]] = Echo(default=None)

    @field_validator("radius")
    @classmethod
    def _radius_positive(cls, v: Optional[float]) -> Optional[float]:
        if v is not None and v <= 0:
            raise ValueError("radius must be > 0")
        return v

    @field_validator("radius_b")
    @classmethod
    def _radius_b_positive(cls, v: Optional[float]) -> Optional[float]:
        if v is not None and v <= 0:
            raise ValueError("radius_b must be > 0")
        return v

    @field_validator("chord_width")
    @classmethod
    def _chord_width_positive(cls, v: Optional[float]) -> Optional[float]:
        if v is not None and v <= 0:
            raise ValueError("chord_width must be > 0")
        return v

    @model_validator(mode="after")
    def _check_variant(self) -> "Fillet":
        if self.kind == "ConstantRadius":
            if self.radius is None:
                raise ValueError("Fillet[ConstantRadius]: radius is required")
            if self.face_set_a is not None or self.face_set_b is not None or self.face_set_c is not None:
                raise ValueError("Fillet[ConstantRadius]: face_set_* fields belong to Face / FullRound variants")
            if self.face_type is not None or self.help_point is not None or self.hold_lines is not None:
                raise ValueError("Fillet[ConstantRadius]: face_type / help_point / hold_lines belong to Face variant")
            if self.chord_width is not None:
                raise ValueError("Fillet[ConstantRadius]: chord_width belongs to Face[ChordWidth] variant")
        elif self.kind == "Face":
            if not self.face_set_a or not self.face_set_b:
                raise ValueError("Fillet[Face]: face_set_a and face_set_b are required and non-empty")
            if self.face_set_c is not None:
                raise ValueError("Fillet[Face]: face_set_c belongs to FullRound variant")
            ft = self.face_type or "Radius"
            if ft == "Radius":
                if self.radius is None:
                    raise ValueError("Fillet[Face/Radius]: radius is required")
                if self.chord_width is not None or self.hold_lines is not None:
                    raise ValueError("Fillet[Face/Radius]: chord_width / hold_lines do not apply")
            elif ft == "ChordWidth":
                if self.chord_width is None:
                    raise ValueError("Fillet[Face/ChordWidth]: chord_width is required")
                if self.radius is not None or self.radius_b is not None or self.hold_lines is not None:
                    raise ValueError("Fillet[Face/ChordWidth]: radius / radius_b / hold_lines do not apply")
            elif ft == "HoldLines":
                if not self.hold_lines:
                    raise ValueError("Fillet[Face/HoldLines]: hold_lines is required and non-empty")
                if self.radius is not None or self.radius_b is not None or self.chord_width is not None:
                    raise ValueError("Fillet[Face/HoldLines]: radius / radius_b / chord_width do not apply")
            self.face_type = ft  # normalize default
        elif self.kind == "FullRound":
            if not self.face_set_a or not self.face_set_b or not self.face_set_c:
                raise ValueError("Fillet[FullRound]: face_set_a, face_set_b, face_set_c are required and non-empty")
            if self.radius is not None or self.radius_b is not None or self.chord_width is not None:
                raise ValueError("Fillet[FullRound]: radius / radius_b / chord_width do not apply")
            if self.face_type is not None or self.help_point is not None or self.hold_lines is not None:
                raise ValueError("Fillet[FullRound]: face_type / help_point / hold_lines belong to Face variant")
            if self.profile != "Circular":
                # FullRound has no per-section profile knob in the SW API surface
                # we expose; reject rather than silently dropping.
                raise ValueError("Fillet[FullRound]: profile must be 'Circular' (no cross-section selector)")
            if self.round_corners:
                raise ValueError("Fillet[FullRound]: round_corners is ConstantRadius-only")
            if self.overflow != "Default":
                raise ValueError("Fillet[FullRound]: overflow is ConstantRadius-only")
        if not self.symmetric and self.radius_b is None:
            raise ValueError("Fillet: symmetric=False requires radius_b to be set")
        return self


# -- factories -------------------------------------------------------------

def fillet(*, edges: list[DefinitionValue], radius: float,
           tangent_propagation: bool = True,
           profile: str = "Circular",
           conic_value: Optional[float] = None,
           radius_b: Optional[float] = None,
           symmetric: bool = True,
           round_corners: bool = False,
           overflow: str = "Default",
           name: Optional[str] = None) -> Fillet:
    """Constant-radius fillet (FilletType=ConstantRadius). Edges, loops,
    faces, and feature seeds are accepted heterogeneously in `edges`.
    `radius_b` + `symmetric=False` enables asymmetric fillets; `profile` +
    `conic_value` selects the cross-section."""
    return Fillet(kind="ConstantRadius", edges=edges, radius=radius,
                  tangent_propagation=tangent_propagation,
                  profile=profile,  # type: ignore[arg-type]
                  conic_value=conic_value,
                  radius_b=radius_b, symmetric=symmetric,
                  round_corners=round_corners,
                  overflow=overflow,  # type: ignore[arg-type]
                  name=name)


def face_fillet(*, face_set_a: list[DefinitionValue],
                face_set_b: list[DefinitionValue],
                face_type: str = "Radius",
                radius: Optional[float] = None,
                radius_b: Optional[float] = None,
                symmetric: bool = True,
                chord_width: Optional[float] = None,
                hold_lines: Optional[list[DefinitionValue]] = None,
                profile: str = "Circular",
                conic_value: Optional[float] = None,
                help_point: Optional[Point3D] = None,
                tangent_propagation: bool = True,
                name: Optional[str] = None) -> Fillet:
    """Face fillet (FilletType=Face). Two face sets define the fillet; an
    optional `help_point` disambiguates which side is rounded. `face_type` picks
    the dimensioning flavor: Radius (default — a radius value), ChordWidth (a
    constant chord width), or HoldLines (the fillet conforms to selected edges)."""
    return Fillet(kind="Face",
                  face_set_a=face_set_a, face_set_b=face_set_b,
                  face_type=face_type,  # type: ignore[arg-type]
                  radius=radius, radius_b=radius_b, symmetric=symmetric,
                  chord_width=chord_width, hold_lines=hold_lines,
                  profile=profile,  # type: ignore[arg-type]
                  conic_value=conic_value,
                  help_point=help_point,
                  tangent_propagation=tangent_propagation,
                  name=name)


def full_round_fillet(*, face_set_a: list[DefinitionValue],
                      face_set_b: list[DefinitionValue],
                      face_set_c: list[DefinitionValue],
                      tangent_propagation: bool = True,
                      name: Optional[str] = None) -> Fillet:
    """Full-round fillet (FilletType=FullRound). Three face sets; SolidWorks
    computes the round geometry from the geometry alone — no radius value."""
    return Fillet(kind="FullRound",
                  face_set_a=face_set_a, face_set_b=face_set_b,
                  face_set_c=face_set_c,
                  tangent_propagation=tangent_propagation,
                  name=name)


__all__ = ["Fillet", "fillet", "face_fillet", "full_round_fillet"]
