using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Sketches.Entities;

// Elliptical arc — full ellipse parameters plus start/end points and a rotation
// direction. SW's CreateEllipticalArc takes 16 doubles + a +1/-1 direction short;
// `clockwise=true` maps to -1.
//
// SW quirk: SketchEllipse.GetRotationDir() == -1 means the arc was authored
// clockwise — SW stores a CW arc with start/end internally swapped vs.
// IGetStartPoint2 / IGetEndPoint2. We surface `clockwise` on the wire and let Add
// route to the right direction arg of CreateEllipticalArc.
public sealed record EllipticalArc(
    SketchEntityId Id,
    bool Construction,
    [property: JsonPropertyName("center")] Point Center,
    [property: JsonPropertyName("major_axis_end")] Point MajorAxisEnd,
    [property: JsonPropertyName("minor_axis_end")] Point MinorAxisEnd,
    [property: JsonPropertyName("start")] Point Start,
    [property: JsonPropertyName("end")] Point End,
    [property: JsonPropertyName("clockwise")] bool Clockwise = false) : SketchEntityDefinition(Id, Construction), ISketchEntity
{
    private SketchSegment? _live;

    // Capture from a live SW SketchSegment that DefinitionCapture has already
    // type-checked as an open SketchEllipse (start != end). SketchName is the owning
    // sketch's Feature.Name. We do NOT swap start/end on CW arcs — `Clockwise` is
    // surfaced on the wire so Add can re-author with the correct direction arg.
    internal static EllipticalArc Parse(SketchSegment segment, string sketchName) {
        var ellipse = segment as SketchEllipse
            ?? throw new InvalidOperationException(
                "EllipticalArc.Parse: segment is not a SketchEllipse");
        var ids = (segment as ISketchSegment)?.GetID() as int[]
            ?? throw new InvalidOperationException(
                "EllipticalArc.Parse: ISketchSegment.GetID() did not return an int[2]");
        if (ids.Length < 2) {
            throw new InvalidOperationException(
                $"EllipticalArc.Parse: GetID() returned an array of length {ids.Length}; expected 2");
        }
        var center = ellipse.IGetCenterPoint2()
            ?? throw new InvalidOperationException("EllipticalArc.Parse: IGetCenterPoint2 returned null");
        var major = ellipse.IGetMajorPoint2()
            ?? throw new InvalidOperationException("EllipticalArc.Parse: IGetMajorPoint2 returned null");
        var minor = ellipse.IGetMinorPoint2()
            ?? throw new InvalidOperationException("EllipticalArc.Parse: IGetMinorPoint2 returned null");
        var start = ellipse.IGetStartPoint2()
            ?? throw new InvalidOperationException("EllipticalArc.Parse: IGetStartPoint2 returned null");
        var end = ellipse.IGetEndPoint2()
            ?? throw new InvalidOperationException("EllipticalArc.Parse: IGetEndPoint2 returned null");
        return new EllipticalArc(
            new SketchEntityId(sketchName, SketchEntityId.KindEllipse, SketchEntityId.Combine(ids[0], ids[1])),
            Construction: segment.ConstructionGeometry,
            Point.Parse(center, sketchName),
            Point.Parse(major, sketchName),
            Point.Parse(minor, sketchName),
            Point.Parse(start, sketchName),
            Point.Parse(end, sketchName),
            Clockwise: ellipse.GetRotationDir() == -1);
    }

    internal static new EllipticalArc FromJson(JsonNode node) {
        var def = Definition.FromJson(node)
            ?? throw new ArgumentException("EllipticalArc.FromJson: payload did not deserialize to a Definition");
        return def as EllipticalArc
            ?? throw new ArgumentException(
                $"EllipticalArc.FromJson: payload deserialized to {def.GetType().Name}, expected EllipticalArc");
    }

    internal JsonNode GetJson() => ToJson();

    internal override IEnumerable<Point2D> PatternExtentPoints() {
        yield return Start.P;
        yield return End.P;
    }

    // Add via SketchManager.CreateEllipticalArc — 16 doubles + a +1/-1 direction
    // short. `Clockwise=true` maps to -1, otherwise +1 (CCW).
    internal EllipticalArc Add(Sketch sketch, SketchManager sketchManager) {
        short direction = Clockwise ? (short)-1 : (short)1;
        var segment = sketchManager.CreateEllipticalArc(
            Center.P.X, Center.P.Y, 0.0,
            MajorAxisEnd.P.X, MajorAxisEnd.P.Y, 0.0,
            MinorAxisEnd.P.X, MinorAxisEnd.P.Y, 0.0,
            Start.P.X, Start.P.Y, 0.0,
            End.P.X, End.P.Y, 0.0,
            direction)
            ?? throw new InvalidOperationException(
                $"EllipticalArc.Add: SketchManager.CreateEllipticalArc(center=({Center.P.X}, {Center.P.Y}), " +
                $"major=({MajorAxisEnd.P.X}, {MajorAxisEnd.P.Y}), minor=({MinorAxisEnd.P.X}, {MinorAxisEnd.P.Y}), " +
                $"start=({Start.P.X}, {Start.P.Y}), end=({End.P.X}, {End.P.Y}), dir={direction}) returned null");
        segment.ConstructionGeometry = Construction;

        _live = segment;
        return this;
    }

    public JsonNode GetJson(string sketchName) {
        if (_live is null) {
            throw new InvalidOperationException("EllipticalArc.GetJson called before Add — no live segment to capture");
        }
        return DefinitionCapture.Capture(_live, sketchName).ToJson();
    }
}
