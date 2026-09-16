using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Sketches.Entities;

// Wire shape: construction + start + end. Each endpoint is a full Point Definition
// (id + p), so the child SketchPoint's per-sketch id round-trips alongside its 2D coords.
public sealed record Line(
    SketchEntityId Id,
    bool Construction,
    [property: JsonPropertyName("start")] Point Start,
    [property: JsonPropertyName("end")] Point End) : SketchEntityDefinition(Id, Construction), ISketchEntity
{
    // Live ref captured at Add time, read by GetJson at end-of-flow. Not part of
    // record equality / serialization (records use positional members for both;
    // this is a regular instance field).
    private SketchSegment? _live;

    // Capture from a live SW SketchSegment that the caller has already type-checked
    // as a SketchLine. SketchName is the owning sketch's Feature.Name; pulled by the
    // SketchHandler caller (avoids a redundant GetSketch() round-trip per entity).
    internal static Line Parse(SketchSegment segment, string sketchName) {
        var line = segment as SketchLine
            ?? throw new InvalidOperationException(
                "Line.Parse: segment is not a SketchLine");
        var ids = (segment as ISketchSegment)?.GetID() as int[]
            ?? throw new InvalidOperationException(
                "Line.Parse: ISketchSegment.GetID() did not return an int[2]");
        if (ids.Length < 2) {
            throw new InvalidOperationException(
                $"Line.Parse: GetID() returned an array of length {ids.Length}; expected 2");
        }
        var start = line.IGetStartPoint2()
            ?? throw new InvalidOperationException("Line.Parse: IGetStartPoint2 returned null");
        var end = line.IGetEndPoint2()
            ?? throw new InvalidOperationException("Line.Parse: IGetEndPoint2 returned null");
        return new Line(
            new SketchEntityId(sketchName, SketchEntityId.KindLine, SketchEntityId.Combine(ids[0], ids[1])),
            Construction: segment.ConstructionGeometry,
            Point.Parse(start, sketchName),
            Point.Parse(end, sketchName));
    }

    // Inverse of Parse — deserialize a wire payload into a typed Line. Round-trips
    // with GetJson via the Definition polymorphism resolver.
    internal static new Line FromJson(JsonNode node) {
        var def = Definition.FromJson(node)
            ?? throw new ArgumentException("Line.FromJson: payload did not deserialize to a Definition");
        return def as Line
            ?? throw new ArgumentException(
                $"Line.FromJson: payload deserialized to {def.GetType().Name}, expected Line");
    }

    // Reuses Definition.ToJson — the polymorphism resolver fires on the base type
    // and emits the kind discriminator + all properties.
    internal JsonNode GetJson() => ToJson();

    internal override IEnumerable<Point2D> PatternExtentPoints() {
        yield return Start.P;
        yield return End.P;
    }

    // Add this line to the live SW sketch via SketchManager.CreateLine. Caller is
    // responsible for entering the sketch and toggling AddToDB / wireframe scopes.
    // Returns `this` so SketchHandler.Add can collect the entity into its
    // List<ISketchEntity> in input order. CreateLine lands its endpoints exactly
    // on the requested coords, so no SetCoords pin is needed.
    internal Line Add(Sketch sketch, SketchManager sketchManager) {
        var segment = sketchManager.CreateLine(Start.P.X, Start.P.Y, 0.0, End.P.X, End.P.Y, 0.0)
            ?? throw new InvalidOperationException(
                $"Line.Add: SketchManager.CreateLine(({Start.P.X}, {Start.P.Y}, 0) -> ({End.P.X}, {End.P.Y}, 0)) returned null");
        segment.ConstructionGeometry = Construction;

        _live = segment;
        return this;
    }

    public JsonNode GetJson(string sketchName) {
        if (_live is null) {
            throw new InvalidOperationException("Line.GetJson called before Add — no live segment to capture");
        }
        return DefinitionCapture.Capture(_live, sketchName).ToJson();
    }
}
