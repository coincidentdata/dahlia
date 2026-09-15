# Using Dahlia

See the [README](../README.md#installation) for setup and the
[examples](../examples/README.md) for runnable scripts.

## Core conventions

- Call `connect()` before operations that communicate with CAD.
- Lengths are meters and angles radians. Multiply by `MM`, `INCH`, or `DEG`
  when writing dimensions; `set_units()` changes display units only.
- Construct a sketch, add its geometry, call `file.add_feature(sketch)`, then
  add features that reference it. The same sketch object receives its native name.
- Inspect with `file.view()` and `file.inspect(name)`. Use `file.probe(ray(...), FACE)`
  for a geometry definition suitable for subsequent feature selections.
- Geometry definitions are structured data. Use returned references instead of
  inventing face indices or native IDs. Feature options are typed in
  [`src/dahlia/features/`](../src/dahlia/features/) and mates in
  [`src/dahlia/mates/`](../src/dahlia/mates/).

## Assemblies

Assemblies use the same `File` and feature lifecycle. Create one with
`create_file(kind="assembly")`, insert saved dependencies with `add_component`,
and add typed `mates.coincident`, `concentric`, `parallel`, `perpendicular`,
`tangent`, `distance`, `angle`, or `lock` features. Distance and angle accept
`limits=(minimum, maximum)`. `component.ref(FRONT)` qualifies a datum;
`component.probe(ray(...), FACE)` qualifies a local geometry selection.
`transform(translation, axis=..., angle=...)` supplies a rigid placement.

Use `components()`, `inspect_component()`, `view()`, and `inspect(mate_name)` to
read the file. Component edits support fixed state, visibility, suppression,
referenced configuration, and color. An occurrence color is normalized RGBA:

```python
casing = assembly.add_component("casing.SLDPRT", color=(0.69, 0.82, 0.90, 0.12))
assembly.edit_component(casing, color=(0.69, 0.82, 0.90, 1.0))
assembly.edit_component(casing, color=None)
```

The fourth channel is opacity: zero is transparent and one is opaque. Every
channel must be finite and between zero and one. `color=None` removes the
occurrence override and inherits the source appearance. Changes apply to the
active assembly configuration and leave the source part and sibling occurrences
unchanged. Inspection returns the actual override or `None`; native RGB channels
are rounded to 8-bit values. Color and opacity survive saving and assembly replay.

Use relative movement commands for motion:

```python
result = assembly.translate_component(shaft, delta=(5 * MM, 0, 0))
result = assembly.rotate_component(shaft, axis=(0, 0, 1), angle=30 * DEG)
print(result["moved"], result["transform"])
```

Both return the actual component inspection plus `moved`, indicating a net pose
change. Mates can limit movement or change other free degrees of freedom. Vectors
use assembly coordinates; rotation is right-handed about an axis through the
assembly origin. Units are meters and radians. Fixed or blocked movement can return
`moved=False`; native failures and failed rebuilds/mates raise errors. Errors do
not roll back movement. Absolute transforms are accepted only by `add_component`.

Suppressed components remain visible in `components()`, `inspect_component()`, and
`view()` with `suppressed=True`. Geometry access and mate inspection/editing require
the referenced components to be unsuppressed; errors identify the dependency.
Use `edit_component(component, suppressed=False)` to restore it. Suppressing only
a mate leaves the component geometry available and supports normal mate inspection.
Pick points are creation hints; editing them is outside the mate contract.

`get_image()` returns PNG bytes for parts and assemblies.

## API entry points

- Connection: `connect`, `disconnect`.
- File entry points: `open_file`, `create_file`, `view_file`, `list_files`,
  `get_active_file`, `close_all_files`.
- Per-file methods on `File`: `add_feature`, `select`, `view`,
  `inspect_feature`, `edit_feature`, `edit_dimension`, `delete_feature`, `set_rollback`, `set_units`,
  `set_bodies`, `get_image`, `save`, `close`.
- Feature factories and typed options:
  [public exports](../src/dahlia/__init__.py),
  [feature modules](../src/dahlia/features/), and
  [sketch builders](../src/dahlia/sketch/).
- Sketch builder methods: `add_line`, `add_circle`, `add_arc`, `add_point`,
  `add_rectangle`, `add_polygon`, `add_constraint`, `entity(name)`.
- Math helpers: `circle_points`, `linspace`, `midpoint`, `offset_points`.
- UPPERCASE constants: `FACE`, `EDGE`, `VERTEX`, `BODY`, `PLANE`, `TOP`,
  `FRONT`, `RIGHT`, `MM`, `CM`, `M`, `INCH`, `DEG`, `RAD`.

`edit_dimension(name, value=..., new_name=...)` edits an existing driving
dimension. Sketch dimensions return the inspected sketch; other feature
dimensions return their full name and current value.
Use names captured from the model; D1/D2 numbering does not identify the same
parameter across feature types. A dimension changes a numeric value, not the
feature's face selections, direction flags, or construction mode.

## Document names and saving

Open documents must have unique names. Saving under another open document's name
raises an error, even when the destination directory differs. Name lookup also
rejects duplicates introduced outside the integration.

`f.save("part.SLDPRT")` updates `f.name` after a native SaveAs; the same handle can
be used for subsequent commands. STEP/STL exports retain the native document name.
Save responses contain `FileName` and the current native `path`. Separately created
handles to an old name must be reacquired after a rename.

`f.get_image()` defaults to `Isometric`. Explicit orientations use the native
capitalization, such as `Front`, `Top`, and `Right`.

## Feature-specific behavior and limitations

These notes cover special behavior and current limitations. For the full
feature API and parameter definitions, see the
[feature modules](../src/dahlia/features/).

### Ribs

`rib(sketch=..., thickness=...)` grows a rib parallel to its driving sketch.
Use `direction="NormalToSketch"` to grow it normal to the sketch and
`extension="Natural"` for natural profile extension. `two_sided=False` and
`reverse_thickness=True` select a single thickness side; `flipped` selects the
material direction. `draft_angle`, `draft_outward`, and `draft_from_wall`
control draft. `reference_segment` is the zero-based sketch segment used for
material/draft direction; `body` optionally selects the target body.

Edits support these settings. The driving sketch can be edited in place, but
switching to a different sketch requires a new rib.

### Drafts

`draft(angle=..., neutral_plane=..., faces=[...])` creates a neutral-plane
draft. Angles are in radians. `kind="PartingLine"` or `kind="Step"` instead
takes a `direction` reference and `parting_lines=[draft_edge(edge, other_face=True)]`.
Propagation supports `None`, `Tangent`, `AllLoops`, `InnerLoops`, and `OuterLoops`.
Step drafts support `step_type="Tapered"` or `"Perpendicular"`; parting-line drafts
support `allow_reduced_angle`. Edits preserve the feature and its draft kind.
The native data API's single draft angle is supported; two-direction parting-line
drafts are not covered.

### Shells

`shell(thickness=..., faces=[...])` removes the selected faces and hollows the
remaining solid. An empty `faces` list creates a closed shell. `outward=True`
puts the wall outside the original solid. All thicknesses are in meters.

```python
from dahlia import shell, shell_wall

f.add_feature(shell(thickness=0.002, faces=[opening],
                    face_thicknesses=[shell_wall(side, 0.004)]))
```

Edits support thickness, direction, opening selections, and existing wall
overrides. The integration currently rejects changes to the number of overrides
and closing the last opening before mutation; create a new shell for those cases.
These are implementation limits pending setter-order and intermediate-commit
checks, not proof that SolidWorks cannot perform those edits.

### Lofts

`loft` creates a boss loft and `cut_loft` removes material between at least two
ordered profiles. Pass already-added Sketch objects, feature names, or typed/raw
geometry definitions. Both share `centerline`, `guide_curves`, start/end tangency
and direction references, tangent lengths/reversal, draft angles, guide influence,
smoothing, and thin-wall settings. `feature_scope` selects affected bodies;
`merge` is available only on boss lofts.

```python
from dahlia import loft, cut_loft

f.add_feature(loft(profiles=["BaseSection", "EndSection"], name="Transition"))
f.add_feature(cut_loft(profiles=["Inlet", "Outlet"], name="Bore"))
```

Thin lofts use `thin_feature=True`, `thin_thickness`, and `thin_wall_type`:
`OneDirection`, `OppositeDirection`, `MidPlane`, or `TwoDirections`. The last
also requires `thin_thickness2`. Thin cut edits drive the native wall dimensions
because SolidWorks' feature-data thickness setter can leave geometry unchanged.
Renamed native wall dimensions are not supported. Switching between solid and
thin requires a new feature. The native apply-to-all constraint setters are
unimplemented, so `start_apply_to_all=True` and `end_apply_to_all=True` are rejected.
Inspection preserves the native apply-to-all values; replaying an existing model
that requires them fails explicitly. Replacing guide-curve references is not
verified, and clearing an existing centerline raises an error.
Cut-loft fragments are retained; the separate
`drop_bodies` workflow is currently supported by the other cut types.

### Mirrors

`mirror` accepts feature, face, or body definitions in `seeds`. Body seeds cannot
be mixed with features or faces. Both planes can be reference planes or planar
faces. `secondary_plane` creates a native two-direction mirror in one feature.

```python
from dahlia import RIGHT, TOP, mirror

f.add_feature(mirror(
    plane=RIGHT, secondary_plane=TOP, seeds=["Boss1"], name="FourBosses",
))
f.edit_feature("FourBosses", mirror_seed_only=True)
```

With perpendicular planes, the normal mode includes the seed and three copies;
`mirror_seed_only=True` includes the seed and two copies. Feature/face mirrors
support `geometry_pattern`, `scope` (`AllBodies`, `AutoSelect`, `SelectedBodies`),
and `feature_scope` body definitions. Body mirrors support `merge` and
`knit_surfaces`. Both support `propagate_visual_properties`.
SolidWorks resolves `AutoSelect` to `SelectedBodies` with an explicit body list
during a full rebuild. Inspect and generated transcripts preserve that resolved scope.

Add, inspect, edit, and transcript replay use the same model. Changing or removing
the secondary plane preserves the native feature's name. Structure-system seeds,
assembly mirrors, and derived mirrored parts are outside this part-feature API.

SolidWorks 2026 does not expose the secondary plane or seed-only option through
its mirror data interfaces. The handler opens the native Edit Feature page,
binds the secondary selection, and commits or cancels it. The seed-only control
is checked and fails explicitly if unavailable. Finish any active
PropertyManager operation before invoking mirror operations.
