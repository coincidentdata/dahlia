using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Sketches.Entities;

// Wire shape: construction + center + radius.
// SW quirk: SketchArc.GetType() == swSketchARC for both circles and arcs;
// disambiguate at Parse via SketchArc.IsCircle() != 0.
public sealed record Circle(
    SketchEntityId Id,
    bool Construction,
    [property: JsonPropertyName("center")] Point Center,
    [property: JsonPropertyName("radius")] double Radius) : SketchEntityDefinition(Id, Construction), ISketchEntity
{
    private SketchSegment? _live;

    internal static Circle Parse(SketchSegment segment, string sketchName) {
        var arc = segment as SketchArc
            ?? throw new InvalidOperationException(
                "Circle.Parse: segment is not a SketchArc");
        var ids = (segment as ISketchSegment)?.GetID() as int[]
            ?? throw new InvalidOperationException(
                "Circle.Parse: ISketchSegment.GetID() did not return an int[2]");
        if (ids.Length < 2) {
            throw new InvalidOperationException(
                $"Circle.Parse: GetID() returned an array of length {ids.Length}; expected 2");
        }
        var center = arc.IGetCenterPoint2()
            ?? throw new InvalidOperationException("Circle.Parse: IGetCenterPoint2 returned null");
        return new Circle(
            new SketchEntityId(sketchName, SketchEntityId.KindArc, SketchEntityId.Combine(ids[0], ids[1])),
            Construction: segment.ConstructionGeometry,
            Point.Parse(center, sketchName),
            arc.GetRadius());
    }

    internal static new Circle FromJson(JsonNode node) {
        var def = Definition.FromJson(node)
            ?? throw new ArgumentException("Circle.FromJson: payload did not deserialize to a Definition");
        return def as Circle
            ?? throw new ArgumentException(
                $"Circle.FromJson: payload deserialized to {def.GetType().Name}, expected Circle");
    }

    internal JsonNode GetJson() => ToJson();

    internal override IEnumerable<Point2D> PatternExtentPoints() {
        yield return Center.P;
    }

    // SW quirk: CreateCircleByRadius2 doesn't exist on every Interop build — use the
    // unsuffixed CreateCircleByRadius for center+radius creation.
    internal Circle Add(Sketch sketch, SketchManager sketchManager) {
        var segment = sketchManager.CreateCircleByRadius(Center.P.X, Center.P.Y, 0.0, Radius)
            ?? throw new InvalidOperationException(
                $"Circle.Add: SketchManager.CreateCircleByRadius(({Center.P.X}, {Center.P.Y}, 0), r={Radius}) returned null");
        segment.ConstructionGeometry = Construction;

        _live = segment;
        return this;
    }

    public JsonNode GetJson(string sketchName) {
        if (_live is null) {
            throw new InvalidOperationException("Circle.GetJson called before Add — no live segment to capture");
        }
        return DefinitionCapture.Capture(_live, sketchName).ToJson();
    }
}
