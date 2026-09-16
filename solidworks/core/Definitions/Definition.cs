using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Handlers.Sketches.Entities;
using SolidWorks.Interop.sldworks;
// Disambiguate from System.Drawing.Point pulled in by global usings.
using PointEntity = Sldworks.Core.Handlers.Sketches.Entities.Point;

namespace Sldworks.Core.Definitions;

// Discriminator literals and property names are snake_case to match the Python pydantic schema.
//
// Discrimination is **manual** (see FromJson / ToJson below) rather than via System.Text.Json's
// [JsonPolymorphic] + [JsonDerivedType] resolver. Reason: the resolver throws
// NotSupportedException (not JsonException) when a payload's `kind` is missing or unknown — it
// bails into the abstract base and tries to instantiate it. NotSupportedException isn't caught
// by FromJson's narrow JsonException filter and propagates up the AddFeature stack. Manual
// dispatch is explicit, tolerates unknown kinds gracefully (returns null + logs), and lets the
// switch double as a one-stop "what kinds are on the wire" registry.
public abstract record Definition {
    public static readonly JsonSerializerOptions JsonOptions = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new DefinitionJsonConverterFactory() },
    };

    // Factory because STJ's JsonConverter<T> is invariant in T. Properties declared as
    // `IReadOnlyList<Definition>` need a JsonConverter<Definition>; properties declared as
    // `IReadOnlyList<SketchEntityDefinition>` need a JsonConverter<SketchEntityDefinition>.
    // The factory hands STJ a typed converter per request and routes both through
    // FromJson / ToJson.
    //
    // We match every ABSTRACT type in the Definition hierarchy and NEVER a concrete
    // subclass — ToJson calls SerializeToNode with the concrete runtime type, and STJ's
    // converter resolver short-circuits on exact-match. If we matched concrete subclasses,
    // ToJson's own SerializeToNode would re-enter Write -> ToJson -> ... ∞. The converter
    // still fires on nested polymorphic refs because record properties declared as
    // `IReadOnlyList<Definition>` / `IReadOnlyList<SketchEntityDefinition>` use the static
    // (abstract) element type.
    private sealed class DefinitionJsonConverterFactory : JsonConverterFactory {
        public override bool CanConvert(Type typeToConvert) =>
            typeof(Definition).IsAssignableFrom(typeToConvert) && typeToConvert.IsAbstract;

        public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter?)Activator.CreateInstance(
                typeof(TypedDefinitionConverter<>).MakeGenericType(typeToConvert));
    }

    private sealed class TypedDefinitionConverter<T> : JsonConverter<T?> where T : Definition {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            using var doc = JsonDocument.ParseValue(ref reader);
            var node = JsonNode.Parse(doc.RootElement.GetRawText());
            var def = FromJson(node);
            if (def is null) return null;
            if (def is T typed) return typed;
            throw new JsonException(
                $"Definition kind '{node?["kind"]?.GetValue<string>() ?? "<missing>"}' deserialized as " +
                $"{def.GetType().Name}, which is not assignable to expected {typeof(T).Name}");
        }
        public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options) {
            if (value is null) {
                writer.WriteNullValue();
                return;
            }
            value.ToJson().WriteTo(writer);
        }
    }

    public JsonNode ToJson() {
        // Serialize through the runtime type so subclass-specific properties emit. Without the
        // [JsonPolymorphic] resolver, STJ won't auto-prepend a `kind` field, so we add it
        // ourselves from KindFor. The DefinitionJsonConverter (registered on JsonOptions)
        // handles nested Definition properties (RegionDefinition.Edges,
        // SketchContourDefinition.Segments) by re-entering ToJson on each entry — terminates
        // when an entry has no further Definition-typed properties.
        var obj = JsonSerializer.SerializeToNode(this, GetType(), JsonOptions) as JsonObject
            ?? throw new InvalidOperationException(
                "Definition.ToJson: SerializeToNode produced a non-object node");
        obj["kind"] = KindFor(this);
        return obj;
    }

    public static Definition? FromJson(JsonNode? node) {
        if (node is null) return null;
        var kind = node["kind"]?.GetValue<string>();
        if (string.IsNullOrEmpty(kind)) {
            SldworksLog.Debug("Definition.FromJson: payload missing 'kind'; returning null");
            return null;
        }
        // Invalid JSON / unknown discriminator is a "not a Definition" signal — callers
        // treat null as "skip". Narrow to JsonException so real bugs still propagate.
        try {
            return kind switch {
                "planar_face"            => node.Deserialize<PlanarFaceDefinition>(JsonOptions),
                "cylindrical_face"       => node.Deserialize<CylindricalFaceDefinition>(JsonOptions),
                "conical_face"           => node.Deserialize<ConicalFaceDefinition>(JsonOptions),
                "spherical_face"         => node.Deserialize<SphericalFaceDefinition>(JsonOptions),
                "toroidal_face"          => node.Deserialize<ToroidalFaceDefinition>(JsonOptions),
                "bsurface_face"          => node.Deserialize<BSurfaceFaceDefinition>(JsonOptions),
                "line_edge"              => node.Deserialize<LineEdgeDefinition>(JsonOptions),
                "circular_edge"          => node.Deserialize<CircularEdgeDefinition>(JsonOptions),
                "elliptical_edge"        => node.Deserialize<EllipticalEdgeDefinition>(JsonOptions),
                "spline_edge"            => node.Deserialize<SplineEdgeDefinition>(JsonOptions),
                "vertex"                 => node.Deserialize<VertexDefinition>(JsonOptions),
                "body"                   => node.Deserialize<BodyDefinition>(JsonOptions),
                "sketch_entity_id"       => node.Deserialize<SketchEntityId>(JsonOptions),
                "sketch_line"            => node.Deserialize<Line>(JsonOptions),
                "sketch_circle"          => node.Deserialize<Circle>(JsonOptions),
                "sketch_arc"             => node.Deserialize<Arc>(JsonOptions),
                "sketch_point"           => node.Deserialize<PointEntity>(JsonOptions),
                "sketch_ellipse"         => node.Deserialize<Ellipse>(JsonOptions),
                "sketch_elliptical_arc"  => node.Deserialize<EllipticalArc>(JsonOptions),
                "sketch_parabola"        => node.Deserialize<Parabola>(JsonOptions),
                "sketch_spline"          => node.Deserialize<Spline>(JsonOptions),
                "sketch_contour"         => node.Deserialize<SketchContourDefinition>(JsonOptions),
                "region"                 => node.Deserialize<RegionDefinition>(JsonOptions),
                "feature"                => node.Deserialize<FeatureDefinition>(JsonOptions),
                "ref_axis"               => node.Deserialize<RefAxisDefinition>(JsonOptions),
                "temp_axis"              => node.Deserialize<TempAxisDefinition>(JsonOptions),
                "ref_plane"              => node.Deserialize<RefPlaneDefinition>(JsonOptions),
                "component"              => node.Deserialize<ComponentDefinition>(JsonOptions),
                "component_entity"       => node.Deserialize<ComponentEntityDefinition>(JsonOptions),
                _ => LogUnknownKind(kind),
            };
        } catch (JsonException ex) {
            SldworksLog.Debug("Definition.FromJson: deserialize as {Kind} failed: {Error}; returning null",
                kind, ex.Message);
            return null;
        }
    }

    private static Definition? LogUnknownKind(string kind) {
        SldworksLog.Debug("Definition.FromJson: unknown kind '{Kind}'; returning null", kind);
        return null;
    }

    // Inverse of the FromJson switch — pure runtime-type dispatch to the snake_case discriminator.
    // Add a new entry here whenever a new Definition flavor is added; the switch's default arm
    // throws so an unregistered flavor surfaces immediately at the first ToJson call instead of
    // round-tripping with no `kind`.
    private static string KindFor(Definition d) => d switch {
        PlanarFaceDefinition           => "planar_face",
        CylindricalFaceDefinition      => "cylindrical_face",
        ConicalFaceDefinition          => "conical_face",
        SphericalFaceDefinition        => "spherical_face",
        ToroidalFaceDefinition         => "toroidal_face",
        BSurfaceFaceDefinition         => "bsurface_face",
        LineEdgeDefinition             => "line_edge",
        CircularEdgeDefinition         => "circular_edge",
        EllipticalEdgeDefinition       => "elliptical_edge",
        SplineEdgeDefinition           => "spline_edge",
        VertexDefinition               => "vertex",
        BodyDefinition                 => "body",
        SketchEntityId                 => "sketch_entity_id",
        Line                           => "sketch_line",
        Circle                         => "sketch_circle",
        Arc                            => "sketch_arc",
        PointEntity                    => "sketch_point",
        Ellipse                        => "sketch_ellipse",
        EllipticalArc                  => "sketch_elliptical_arc",
        Parabola                       => "sketch_parabola",
        Spline                         => "sketch_spline",
        SketchContourDefinition        => "sketch_contour",
        RegionDefinition               => "region",
        FeatureDefinition              => "feature",
        RefAxisDefinition              => "ref_axis",
        TempAxisDefinition             => "temp_axis",
        RefPlaneDefinition             => "ref_plane",
        ComponentDefinition            => "component",
        ComponentEntityDefinition      => "component_entity",
        _ => throw new InvalidOperationException(
            $"Definition.ToJson: no kind discriminator registered for {d.GetType().Name}"),
    };

    // Capture every face produced by a feature (e.g. fillet / chamfer affected faces).
    // SW quirk: Feature.GetFaces returns null on degenerate features that produced no faces.
    public static JsonArray CaptureFaces(Feature feature) {
        var arr = new JsonArray();
        if (feature.GetFaces() is not object[] raw) return arr;
        foreach (var entry in raw) {
            if (entry is Face2 face) arr.Add(DefinitionCapture.Capture(face).ToJson());
        }
        return arr;
    }

    // Deserialize a JsonArray into a typed Definition list. Throws on malformed entries —
    // a missing/wrong discriminator can't be silently dropped because feature handlers rely
    // on per-index correspondence between input and resolved entities.
    public static List<Definition> FromJsonArray(JsonNode? node, string label) {
        if (node is not JsonArray array) {
            throw new ArgumentException($"{label}: expected JSON array, got {node?.GetType().Name ?? "null"}");
        }
        var result = new List<Definition>(array.Count);
        for (var i = 0; i < array.Count; i++) {
            var def = FromJson(array[i])
                ?? throw new ArgumentException($"{label}[{i}]: malformed Definition");
            result.Add(def);
        }
        return result;
    }

    // Resolve every Definition in a list and select onto `mark`. Throws on the first failure
    // with the failing index in the message — feature creation downstream needs every input
    // on its mark, so partial success isn't tolerable.
    public static void SelectAll(File file, IReadOnlyList<Definition> defs, int mark, string label) {
        for (var i = 0; i < defs.Count; i++) {
            if (!defs[i].Select(file, mark)) {
                throw new InvalidOperationException(
                    $"{label}[{i}]: {defs[i].GetType().Name} did not resolve+select onto mark {mark} " +
                    "(geometry may have been rebuilt away, or SW rejected the selection).");
            }
        }
    }

    // Resolve this Definition to a live SW entity by walking the model and matching geometry
    // (face / edge / etc.) or by id pair (sketch entity / contour). Throws when the geometry
    // matches multiple entities — definitions must be unique. Returns null when nothing
    // matches; callers get an Information-level log entry naming the unresolvable Definition
    // so the failure is traceable without having to add probes.
    public object? Resolve(File file, Sketch? sketchHint = null) {
        var live = DefinitionResolver.Resolve(file, this, sketchHint);
        if (live is null) {
            SldworksLog.Information(
                "Definition.Resolve: {Kind} did not resolve to a live entity (sketchHint={Hint}, payload={Payload})",
                GetType().Name,
                sketchHint is null ? "null" : ((Feature)sketchHint).Name,
                ToJson().ToJsonString());
        }
        return live;
    }

    // Resolve this Definition and select the result onto SW selection mark `mark`.
    // Returns true when both resolve and select succeeded; false on any failure (unresolved,
    // unsupported runtime type, or SW returned a false select). Logs the cause at
    // Information so callers can attribute the failure without extra probes.
    public bool Select(File file, int mark) {
        var live = Resolve(file);
        if (live is null) {
            // Resolve already logged the unresolvable payload; leave the trail there.
            return false;
        }
        var selected = SelectLive(file, live, mark);
        if (!selected) {
            SldworksLog.Information(
                "Definition.Select: SW rejected selection of {LiveType} on mark {Mark} for {Kind} (payload={Payload})",
                live.GetType().Name, mark, GetType().Name, ToJson().ToJsonString());
        }
        return selected;
    }

    // Select an already-resolved live entity at `mark`. Returns the SW Select* return value
    // (true = SW accepted the selection). Used by call sites where the live entity didn't
    // come from a Definition resolve (e.g. constraint refs resolved through tag/position
    // lookups). Per-runtime-type dispatch:
    //  - SW quirk: Edge / Face2 / Vertex expose Select4(append, SelectData) only via
    //    IEntity — direct call on the proxy fails in 2026.
    //  - SW quirk: SketchSegment / SketchPoint expose Select4 directly on the typed
    //    interface — call without the IEntity cast (the cast does QI for IID
    //    83A33D65-27C5-11CE-BFD4-00400513BB57, which SketchPointClass returns
    //    E_NOINTERFACE for in 2026).
    //  - SW quirk: Body2.Select2 / Feature.Select2 take a SelectData and a bare int mark
    //    respectively — different signature shape than the IEntity Select4 family.
    public static bool SelectLive(File file, object live, int mark, Point3D? clickPoint = null) {
        var sd = MakeSelectData(file, mark, clickPoint);
        return live switch {
            Edge e          => ((IEntity)e).Select4(true, sd),
            Face2 f         => ((IEntity)f).Select4(true, sd),
            Vertex v        => ((IEntity)v).Select4(true, sd),
            SketchSegment s => s.Select4(true, sd),
            SketchPoint sp  => sp.Select4(true, sd),
            Body2 b         => b.Select2(true, sd),
            Feature feat    => feat.Select2(true, mark),
            Component2 comp => comp.Select4(true, sd, false),
            _ => throw new InvalidOperationException(
                $"Definition.SelectLive: unsupported entity type {live.GetType().Name}"),
        };
    }

    // `clickPoint` populates SelectData.X/Y/Z as a positional hint — SW uses it
    // as the "click location" for entities whose selection has sub-position
    // ambiguity (e.g. AtPierce on a curve that intersects the sketch plane
    // at multiple points: SW picks the branch closest to the click point).
    // Without a hint, SW's solver picks a default branch which can mirror the
    // sketch on rebuild.
    private static SelectData MakeSelectData(File file, int mark, Point3D? clickPoint = null) {
        var sd = (SelectData)file.ModelDoc.ISelectionManager.CreateSelectData();
        sd.Mark = mark;
        if (clickPoint is { } cp) {
            sd.X = cp.X;
            sd.Y = cp.Y;
            sd.Z = cp.Z;
        }
        return sd;
    }
}

// Face flavors carry an optional ParentBody (the body the face belongs to) as a
// deterministic anchor — narrows resolution when multiple bodies share a face
// flavor. SW quirk: face's own bbox via GetBox() is computed from the trimmed
// parametric loop and drifts up to ~3e-5 m across rebuilds, so we don't carry it
// as identity. Body bbox/CoM is exact (zero drift observed) and gives a stable
// coarse identifier.
//
// Trim disambiguation = OnFacePoint: an XYZ point captured to lie on the face's
// trimmed extent (snapped via Face2.GetClosestPointOn so it's on-trim even for
// non-convex / L-shaped faces). On resolve we test `face.GetClosestPointOn(p) ≈ p`,
// which is rotation-invariant under SW's in-plane UV reparameterization — the
// failure mode that defeats centroid-equality on tilted planar faces and UV-based
// checks on analytic non-planar faces across chained Booleans.

public sealed record ComponentDefinition(
    [property: JsonPropertyName("path")] IReadOnlyList<string> Path) : Definition;

public sealed record ComponentEntityDefinition(
    [property: JsonPropertyName("component")] IReadOnlyList<string> Component,
    [property: JsonPropertyName("entity")] Definition Entity) : Definition;

public sealed record PlanarFaceDefinition(
    [property: JsonPropertyName("on_face_point")] Point3D OnFacePoint,
    [property: JsonPropertyName("normal")] Direction Normal,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

public sealed record CylindricalFaceDefinition(
    [property: JsonPropertyName("axis_origin")] Point3D AxisOrigin,
    [property: JsonPropertyName("axis_direction")] Direction AxisDirection,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("on_face_point")] Point3D OnFacePoint,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

public sealed record ConicalFaceDefinition(
    [property: JsonPropertyName("apex")] Point3D Apex,
    [property: JsonPropertyName("axis_direction")] Direction AxisDirection,
    [property: JsonPropertyName("half_angle")] double HalfAngle,
    [property: JsonPropertyName("on_face_point")] Point3D OnFacePoint,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

public sealed record SphericalFaceDefinition(
    [property: JsonPropertyName("center")] Point3D Center,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("on_face_point")] Point3D OnFacePoint,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

public sealed record ToroidalFaceDefinition(
    [property: JsonPropertyName("center")] Point3D Center,
    [property: JsonPropertyName("axis_direction")] Direction AxisDirection,
    [property: JsonPropertyName("major_radius")] double MajorRadius,
    [property: JsonPropertyName("minor_radius")] double MinorRadius,
    [property: JsonPropertyName("on_face_point")] Point3D OnFacePoint,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

// Non-analytic surfaces (BSurface). No analytic params to discriminate; instead
// carry N XYZ samples each snapped to lie on the face's trimmed extent. Match
// requires every sample to project onto the candidate face within tolerance —
// two different freeform surfaces won't share all N samples.
public sealed record BSurfaceFaceDefinition(
    [property: JsonPropertyName("on_face_points")] List<Point3D> OnFacePoints,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

// Edges + vertex carry the same optional ParentBody anchor as faces — narrows
// resolution to entities owned by the matching body. Geometric identity stays in
// the per-flavor analytic fields below.

public sealed record LineEdgeDefinition(
    [property: JsonPropertyName("start")] Point3D Start,
    [property: JsonPropertyName("end")] Point3D End,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

// SW quirk: closed-curve edges report null start/end vertices, so Start/End are null for a full circle and non-null for an arc. Arcs are orientation-agnostic.
public sealed record CircularEdgeDefinition(
    [property: JsonPropertyName("center")] Point3D Center,
    [property: JsonPropertyName("axis_direction")] Direction AxisDirection,
    [property: JsonPropertyName("radius")] double Radius,
    [property: JsonPropertyName("start")] Point3D? Start = null,
    [property: JsonPropertyName("end")] Point3D? End = null,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

// SW quirk: closed-curve edges report null start/end vertices, so Start/End are null for a full ellipse and non-null for an arc. Arcs are orientation-agnostic.
public sealed record EllipticalEdgeDefinition(
    [property: JsonPropertyName("center")] Point3D Center,
    [property: JsonPropertyName("major_axis")] Direction MajorAxis,
    [property: JsonPropertyName("minor_axis")] Direction MinorAxis,
    [property: JsonPropertyName("major_radius")] double MajorRadius,
    [property: JsonPropertyName("minor_radius")] double MinorRadius,
    [property: JsonPropertyName("start")] Point3D? Start = null,
    [property: JsonPropertyName("end")] Point3D? End = null,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

// SW quirk: helix edges fall through to this flavor — IGetCurve().IsBcurve() returns true, indistinguishable from a genuine spline. Two helix edges that share start/end may collide on the same definition.
public sealed record SplineEdgeDefinition(
    [property: JsonPropertyName("control_points")] List<Point3D> ControlPoints,
    [property: JsonPropertyName("knots")] List<double> Knots,
    [property: JsonPropertyName("degree")] int Degree = 3,
    [property: JsonPropertyName("periodic")] bool Periodic = false,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

public sealed record VertexDefinition(
    [property: JsonPropertyName("point")] Point3D Point,
    [property: JsonPropertyName("parent_body")] Definition? ParentBody = null) : Definition;

// Body identity = mass-properties (centroid+volume+surface_area) + bbox.
//
// Mass properties drift on rebuild for curved-face bodies (Parasolid tessellation
// non-determinism), so they compare at the loose `BodyMassPropertyTol`. Bbox is
// tighter (Flags.GeometryTolerance) because extreme points are stable across
// rebuilds for the same body — the multiple checks together discriminate
// mirror-symmetric and coincidence collisions.
//
// We used to also carry face_count and edge_count as a "strong topology guard",
// but SW's topology counts are NOT stable across rebuilds for the same geometric
// body — tangent-edge imprints come and go based on numerical-tangency decisions
// in Parasolid, so the same swept body can report e.g. ec=20 vs ec=16 across
// rebuilds. Filtering on the counts rejected obviously-matching bodies; mass
// props + bbox are the real identity invariants.
public sealed record BodyDefinition(
    [property: JsonPropertyName("centroid")] Point3D Centroid,
    [property: JsonPropertyName("volume")] double Volume,
    [property: JsonPropertyName("bbox")] BoundingBox Bbox,
    [property: JsonPropertyName("surface_area")] double SurfaceArea) : Definition;

// Stable handle for a SolidWorks sketch entity (segment or point) across rebuilds.
//
//   SketchName  — the owning sketch's Feature.Name. Required: SW reissues per-sketch
//                 ids starting from low numbers, so neither Id nor (EntityKind, Id)
//                 is globally unique across sketches.
//   EntityKind  — COM-level segment-type tag: "line" | "arc" | "ellipse" | "spline"
//                 | "parabola" | "point". Required: per the SW API docs, GetID() is
//                 only unique within one COM subtype's namespace — a SketchLine and a
//                 SketchArc in the same sketch CAN report the same int[2], and
//                 SketchSegment vs SketchPoint also share the lookup space. The kind
//                 closes both holes so (SketchName, EntityKind, Id) names exactly one
//                 live entity.
//   Id          — the int[2] returned by SketchSegment.GetID() / SketchPoint.GetID()
//                 packed into a single long via `((long)ids[0] << 32) | (uint)ids[1]`.
//                 Always compare against the combined long; never decompose back into
//                 a pair.
//
// Wire field is named `entity_kind` (not `kind`) to avoid colliding with the
// polymorphic Definition discriminator when a SketchEntityId appears bare.
public sealed record SketchEntityId(
    [property: JsonPropertyName("sketch_name")] string SketchName,
    [property: JsonPropertyName("entity_kind")] string EntityKind,
    [property: JsonPropertyName("id")] long Id) : Definition {

    public const string KindLine     = "line";
    public const string KindArc      = "arc";
    public const string KindEllipse  = "ellipse";
    public const string KindSpline   = "spline";
    public const string KindParabola = "parabola";
    public const string KindPoint    = "point";

    public static long Combine(int id0, int id1) => ((long)id0 << 32) | (uint)id1;

    public static long? CombineFrom(int[]? ids) =>
        ids is { Length: >= 2 } ? Combine(ids[0], ids[1]) : null;
}

// Polymorphic base for everything that lives inside a sketch as a real, addressable
// SolidWorks entity (one SketchSegment or SketchPoint with an int[2] GetID()). Each
// concrete subclass — Line / Circle / Arc / Point / Ellipse / EllipticalArc /
// Parabola / Spline — lives one-per-file in Handlers/Sketch/Entities/ and is
// registered as a JsonDerivedType on Definition above so that (a) it round-trips
// through Definition.ToJson / FromJson and (b) it can stand in wherever a
// Definition is accepted (Sweep.path, CircularPattern.axis, etc.).
//
// Composites (Polygon, LinearPattern, CircularPattern, OffsetEntity, Rectangle) are
// NOT subclasses of this base — they're sketch-feature children with no single live
// (EntityKind, Id) handle. Reconstructing them from SW's hidden Patterned/OffsetEdge/SketchOffset
// relations on Inspect is a deferred follow-up.
public abstract record SketchEntityDefinition(
    [property: JsonPropertyName("id")] SketchEntityId Id,
    [property: JsonPropertyName("construction")] bool Construction) : Definition {
    internal abstract IEnumerable<Point2D> PatternExtentPoints();
}

// SW quirk: ISketchContour has no GetID(), so model as its constituent segments and
// match by set-equality of segment ids. Segments carry the live geometry so a
// constraint that targets the contour by id pair can disambiguate when SW has
// reissued ids on a round-tripped target.
public sealed record SketchContourDefinition(
    [property: JsonPropertyName("segments")] IReadOnlyList<SketchEntityDefinition> Segments) : Definition;

// SW quirk: ISketchRegion exposes GetEdges() but no GetSketchSegments(), so model a region as the unordered set of bordering edges; resolver matches by set-equality on the serialized edge definitions.
//
// `echo_interior_point` is an Inspect-only hint: a 2D point in sketch-plane
// coords known to lie inside the region (computed from the loop tessellation
// by `SketchRegionGeometry.ComputeInteriorPoint`). The transcript emitter
// uses it to issue `target.probe_region(sketch_name, (x, y))` calls so
// roundtrip authoring follows the same path an LLM would take.
public sealed record RegionDefinition(
    [property: JsonPropertyName("edges")] IReadOnlyList<Definition> Edges,
    [property: JsonPropertyName("echo_interior_point")] Point2D? EchoInteriorPoint = null) : Definition;

// Feature lookup by name. Covers default planes ("Top Plane" / "Front Plane" / "Right Plane") and any user RefPlane / RefAxis / RefPoint.
public sealed record FeatureDefinition(
    [property: JsonPropertyName("name")] string Name) : Definition;

// A RefAxis is a FEATURE — resolved by NAME (DefinitionResolver.ResolveRefAxis).
// start/end are the displayed-extent endpoints, kept ONLY to show the user which axis
// this is (echo_* — drift across rebuilds, so not diffed and stripped from the emitted
// reference). NOT the same as TempAxisDefinition below, which IS geometry-resolved.
public sealed record RefAxisDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("echo_start")] Point3D Start,
    [property: JsonPropertyName("echo_end")] Point3D End) : Definition;

// Temp axis = the implicit axis of a curved face (cylinder / cone / torus).
// SW doesn't expose temp axes as durable features (per-rebuild handle changes;
// SelectByID2-recovered proxies die on ClearSelection), but the displayed
// axis extent from GetRefAxisParams is stable across rebuilds. Identity rides
// on (start, end) — endpoints sorted lex for rebuild-stable JSON. Resolver
// SelectByID2("AXIS") at the midpoint of (start, end), unwraps to RefAxis,
// returns the proxy without clearing selection so AddRelation can consume it.
public sealed record TempAxisDefinition(
    [property: JsonPropertyName("start")] Point3D Start,
    [property: JsonPropertyName("end")] Point3D End) : Definition;

// A RefPlane is a FEATURE (default planes too) — resolved by NAME
// (DefinitionResolver.ResolveRefPlane). x/y/z/origin are the plane basis, kept ONLY to
// show the user which plane this is (echo_* — the X/Y basis drifts across rebuilds, so
// not diffed and stripped from the emitted reference).
public sealed record RefPlaneDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("echo_x")] Direction X,
    [property: JsonPropertyName("echo_y")] Direction Y,
    [property: JsonPropertyName("echo_z")] Direction Z,
    [property: JsonPropertyName("echo_origin")] Point3D Origin) : Definition;
