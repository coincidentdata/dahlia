using System.Text.Json.Nodes;

namespace Sldworks.Core.Handlers.Sketches;

// Per-input-entity capture handle. Each sketch entity (Line, Circle, Arc, Ellipse,
// EllipticalArc, Parabola, Spline, standalone Point, SketchPolygon, ...) implements
// this interface directly — the input-side instance, deserialized from the wire,
// stores the live SW reference(s) it created during Add and uses them in GetJson
// to emit the response entry.
//
// AddToSketch creates the SW entity AND back-fills target-side state on `this`;
// GetJson serializes the populated state. Composites (Polygon) keep the
// composite shape on output — input has one polygon entry, output has one polygon
// entry — never decomposed into N+1 primitives. Capture is deferred to end-of-flow
// (after the constraint pass) so embedded ids and coords reflect the settled
// state of the sketch.
internal interface ISketchEntity {
    JsonNode GetJson(string sketchName);
}
