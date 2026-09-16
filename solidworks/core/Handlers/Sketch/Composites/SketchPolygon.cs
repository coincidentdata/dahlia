using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers.Sketches.Entities;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Sketches.Composites;

// Regular polygon. Composite (type-discriminated, NOT a Definition flavor) — the
// authoritative SW representation is the expanded set of N edge SketchSegments + 1
// construction circle SketchSegment that SketchManager.CreatePolygon emits as a
// single batch. The composite stays a composite in the response: input has one
// polygon entry, output has one polygon entry, with `edges` and `circle` populated
// from the live captures (never decomposed into N+1 primitives). Round-trip
// preserves the polygon abstraction by detecting the same ring on Inspect via
// PolygonParser.
//
// Wire shape (Center + FirstVertex + EdgeCount + InscribedCircle):
//   type:         "Polygon" (discriminator; type-, not kind-, based — composites)
//   center:       Point2D (sketch-local coords, meters) — polygon centroid
//   first_vertex: Point2D (sketch-local) — one polygon CORNER (= echo_edges[0].
//                 start). Passed straight to SW's CreatePolygon as the
//                 reference point. Fixes BOTH size (|first_vertex − center|)
//                 and rotation — placing the initial polygon at the captured
//                 orientation so the constraint solver doesn't have to rotate
//                 it (a large solve there converged differently across
//                 rebuilds and drifted the corners ~2°, 1511K279). Replaces
//                 the old redundant `radius` scalar.
//   sides:        int (>= 3)
//   inscribed:    bool — construction circle inscribed (tangent to edge
//                 midpoints) vs circumscribed (through vertices). Does NOT
//                 affect where `first_vertex` lands (SW's reference is always
//                 a vertex); it only sets which circle SW builds. Preserving
//                 it keeps the construction circle matching source so
//                 PolygonParser re-detects the polygon on round-trip.
//   construction: bool — applied to every edge (the construction circle is
//                 always construction, regardless).
//   echo_edges:   optional list of SketchLineDefinition. Populated on Inspect
//                 (one per polygon edge); empty on author input. Inspect-only —
//                 consumed by PolygonParser.Detect to fold expanded primitives
//                 back into a single composite entry.
//   echo_circle:  optional SketchCircleDefinition. Populated on Inspect; the
//                 construction circle bound by the polygon. Inspect-only.
public sealed record SketchPolygon(
    [property: JsonPropertyName("type")]   string Type,
    [property: JsonPropertyName("center")] Point2D Center,
    [property: JsonPropertyName("first_vertex")] Point2D FirstVertex,
    [property: JsonPropertyName("sides")]  int Sides,
    [property: JsonPropertyName("inscribed")]    bool Inscribed = true,
    [property: JsonPropertyName("construction")] bool Construction = false,
    [property: JsonPropertyName("echo_edges")]  IReadOnlyList<Line>? Edges = null,
    [property: JsonPropertyName("echo_circle")] Circle? Circle = null) : ISketchEntity
{
    public const string TypeName = "Polygon";

    // Live SW segments captured at Add time, read by GetJson at end-of-flow. Mutable
    // fields outside the record's positional members so they don't affect equality /
    // serialization. Populated together inside Add or both null.
    private List<SketchSegment>? _liveEdges;
    private SketchSegment? _liveCircle;

    // CreatePolygon takes (center, referencePoint, sides, inscribed). We pass
    // `first_vertex` (a corner) as the reference and the captured `Inscribed`
    // flag. SW places a polygon
    // vertex at first_vertex (the reference is always a vertex, regardless of
    // inscribed), reproducing the captured rotation + size; `inscribed`
    // selects the construction circle (inscribed vs circumscribed).
    //
    // The construction circle is always construction; edges inherit `Construction`
    // from the wire field.
    internal SketchPolygon Add(SketchManager sketchManager) {
        if (Sides < 3) {
            throw new ArgumentException(
                $"SketchPolygon.Add: sides must be >= 3, got {Sides}");
        }

        var vx = FirstVertex.X;
        var vy = FirstVertex.Y;

        var raw = sketchManager.CreatePolygon(
            Center.X, Center.Y, 0.0,
            vx, vy, 0.0,
            Sides, Inscribed) as object[]
            ?? throw new InvalidOperationException(
                $"SketchPolygon.Add: SketchManager.CreatePolygon(center=({Center.X}, {Center.Y}), " +
                $"vertex=({vx}, {vy}), sides={Sides}, inscribed={Inscribed}) returned null");
        if (raw.Length < Sides + 1) {
            throw new InvalidOperationException(
                $"SketchPolygon.Add: CreatePolygon returned {raw.Length} entries; expected {Sides + 1} " +
                $"({Sides} edges + 1 construction circle)");
        }

        // Edges: indices 0..Sides-1, but rotated so the capture order matches
        // the Inspect (PolygonParser) order.
        //
        // SW quirk: CreatePolygon STARTS the ring at the reference vertex, so
        // raw[0].start == first_vertex and the edge that *ends* at the reference
        // is raw[Sides-1] (the ring closes raw[Sides-1].end -> raw[0].start).
        // PolygonParser, on Inspect, walks the Patterned-constraint chain and
        // makes the edge ending at first_vertex its edge[0]. So raw order is the
        // Inspect order rotated left by one (the ref-edge comes out LAST instead
        // of FIRST). If we echoed raw order as-is, add_result.echo_edges would be
        // off-by-one vs source's Inspect echo_edges; the runner's id remap zips
        // the two lists positionally and would bind each polygon vertex id to its
        // neighbour — pulling a dimension/relation onto the wrong corner and
        // rotating the polygon 60° on rebuild (1511K279). Rotate raw right by one
        // so liveEdges[0] is the ref-edge, matching Inspect 1:1.
        var liveEdges = new List<SketchSegment>(Sides);
        for (int i = 0; i < Sides; i++) {
            int rawIdx = (i + Sides - 1) % Sides;
            var seg = raw[rawIdx] as SketchSegment
                ?? throw new InvalidOperationException(
                    $"SketchPolygon.Add: CreatePolygon entry [{rawIdx}] is not a SketchSegment ({TypeName_(raw[rawIdx])})");
            seg.ConstructionGeometry = Construction;
            liveEdges.Add(seg);
        }
        // Construction circle: last entry. Always construction regardless of input flag.
        var circleSeg = raw[Sides] as SketchSegment
            ?? throw new InvalidOperationException(
                $"SketchPolygon.Add: CreatePolygon last entry is not a SketchSegment ({TypeName_(raw[Sides])})");
        circleSeg.ConstructionGeometry = true;

        _liveEdges = liveEdges;
        _liveCircle = circleSeg;
        return this;
    }

    // Captures self as a single Polygon JSON entry, embedding per-edge Line captures
    // and the construction-circle capture. Called by SketchHandler at end-of-flow so
    // the embedded ids and coords reflect the settled post-constraint state.
    public JsonNode GetJson(string sketchName) {
        if (_liveEdges is null || _liveCircle is null) {
            throw new InvalidOperationException(
                "SketchPolygon.GetJson called before Add — no live segments to capture");
        }
        var edgesArr = new JsonArray();
        foreach (var seg in _liveEdges) {
            edgesArr.Add(DefinitionCapture.Capture(seg, sketchName).ToJson());
        }
        var circleDef = DefinitionCapture.Capture(_liveCircle, sketchName);
        // `first_vertex` is a polygon CORNER read from the SAME slot SW writes
        // the CreatePolygon reference to (edge[0].end), so source-capture and a
        // re-Add re-capture pick the identical corner — making the round-trip a
        // fixed point (see FirstVertexFromEdges).
        var (fvX, fvY) = FirstVertexFromEdges(edgesArr, Center.X, Center.Y);
        return new JsonObject {
            ["type"]         = TypeName,
            ["center"]       = new JsonObject { ["x"] = Center.X, ["y"] = Center.Y },
            ["first_vertex"] = new JsonObject { ["x"] = fvX, ["y"] = fvY },
            ["sides"]        = Sides,
            ["inscribed"]    = Inscribed,
            ["construction"] = Construction,
            ["echo_edges"]   = edgesArr,
            ["echo_circle"]  = circleDef.ToJson(),
        };
    }

    // `first_vertex` is captured as edge[0].END, not edge[0].start.
    //
    // SW's CreatePolygon(center, ref, sides, inscribed) places `ref` as the
    // ring's edge[0].END (the first edge runs prev_corner → ref). So to make
    // the round-trip a fixed point — capture V, re-Add CreatePolygon(ref=V),
    // re-capture V — we must read the SAME slot SW writes the reference to:
    // edge[0].end. Reading edge[0].start instead is off by one corner (the
    // 1511K279 hexagon: captured 90° start, but re-Add put 90° at edge[0].end
    // and the new edge[0].start landed on 30°, tripping a spurious diff).
    internal static (double X, double Y) FirstVertexFromEdges(
            JsonArray edges, double cx, double cy) {
        var p = (edges.FirstOrDefault() as JsonObject)?["end"]?["p"] as JsonObject;
        var x = p?["x"]?.GetValue<double>() ?? cx;
        var y = p?["y"]?.GetValue<double>() ?? cy;
        return (x, y);
    }

    // SW Interop's SketchSegment defines an `int GetType()` that shadows Object.GetType(),
    // so the usual `.GetType().Name` returns int's lookup which fails to compile. Boxing
    // the value to `object` reaches Object.GetType() unambiguously.
    private static string TypeName_(object? value) => value is null ? "null" : value.GetType().Name;
}
