using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Sketches.Entities;

// Wire shape: construction + center + start + end + clockwise. Direct CW/CCW
// direction is unambiguous in every case; a major/minor flag would degenerate at
// the semi-circle (start, end, center collinear) where IsClockwise/IsMajor both
// return the same answer regardless of the input direction.
public sealed record Arc(
    SketchEntityId Id,
    bool Construction,
    [property: JsonPropertyName("center")] Point Center,
    [property: JsonPropertyName("start")] Point Start,
    [property: JsonPropertyName("end")] Point End,
    [property: JsonPropertyName("clockwise")] bool Clockwise = false) : SketchEntityDefinition(Id, Construction), ISketchEntity
{
    private SketchSegment? _live;

    // SW quirk: SketchArc.GetType() == swSketchARC for both circles and arcs;
    // disambiguate via SketchArc.IsCircle() != 0 (caller routes to Circle when so).
    internal static Arc Parse(SketchSegment segment, string sketchName) {
        var arc = segment as SketchArc
            ?? throw new InvalidOperationException(
                "Arc.Parse: segment is not a SketchArc");
        var ids = (segment as ISketchSegment)?.GetID() as int[]
            ?? throw new InvalidOperationException(
                "Arc.Parse: ISketchSegment.GetID() did not return an int[2]");
        if (ids.Length < 2) {
            throw new InvalidOperationException(
                $"Arc.Parse: GetID() returned an array of length {ids.Length}; expected 2");
        }
        var centerPt = arc.IGetCenterPoint2()
            ?? throw new InvalidOperationException("Arc.Parse: IGetCenterPoint2 returned null");
        var startPt = arc.IGetStartPoint2()
            ?? throw new InvalidOperationException("Arc.Parse: IGetStartPoint2 returned null");
        var endPt = arc.IGetEndPoint2()
            ?? throw new InvalidOperationException("Arc.Parse: IGetEndPoint2 returned null");

        return new Arc(
            new SketchEntityId(sketchName, SketchEntityId.KindArc, SketchEntityId.Combine(ids[0], ids[1])),
            Construction: segment.ConstructionGeometry,
            Point.Parse(centerPt, sketchName),
            Point.Parse(startPt, sketchName),
            Point.Parse(endPt, sketchName),
            Clockwise: arc.GetRotationDir() == -1);
    }

    internal static new Arc FromJson(JsonNode node) {
        var def = Definition.FromJson(node)
            ?? throw new ArgumentException("Arc.FromJson: payload did not deserialize to a Definition");
        return def as Arc
            ?? throw new ArgumentException(
                $"Arc.FromJson: payload deserialized to {def.GetType().Name}, expected Arc");
    }

    internal JsonNode GetJson() => ToJson();

    internal override IEnumerable<Point2D> PatternExtentPoints() {
        yield return Start.P;
        yield return End.P;

        foreach (var p in CircularArcCardinalExtents(Center.P, Start.P, End.P, Clockwise)) {
            yield return p;
        }
    }

    private static IEnumerable<Point2D> CircularArcCardinalExtents(
        Point2D center, Point2D start, Point2D end, bool clockwise) {
        var radius = Distance(center, start);
        if (radius <= 1e-12) yield break;

        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var endAngle = Math.Atan2(end.Y - center.Y, end.X - center.X);
        var cardinalAngles = new[] { 0.0, Math.PI / 2, Math.PI, 3 * Math.PI / 2 };
        foreach (var angle in cardinalAngles) {
            if (!AngleOnArc(angle, startAngle, endAngle, clockwise)) continue;
            yield return new Point2D(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle));
        }
    }

    private static bool AngleOnArc(double angle, double start, double end, bool clockwise) {
        var total = clockwise
            ? NormalizeAngle(start - end)
            : NormalizeAngle(end - start);
        var offset = clockwise
            ? NormalizeAngle(start - angle)
            : NormalizeAngle(angle - start);
        return offset <= total + 1e-12;
    }

    private static double NormalizeAngle(double angle) {
        angle %= 2 * Math.PI;
        if (angle < 0) angle += 2 * Math.PI;
        return angle;
    }

    private static double Distance(Point2D a, Point2D b) {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // SW quirk: SketchManager.CreateArc direction is +1 (CCW) / -1 (CW). The wire
    // carries the same CW/CCW choice directly.
    internal Arc Add(Sketch sketch, SketchManager sketchManager) {
        var segment = sketchManager.CreateArc(
            Center.P.X, Center.P.Y, 0.0,
            Start.P.X, Start.P.Y, 0.0,
            End.P.X, End.P.Y, 0.0,
            Clockwise ? (short)-1 : (short)1)
            ?? throw new InvalidOperationException(
                $"Arc.Add: SketchManager.CreateArc(center=({Center.P.X}, {Center.P.Y}), " +
                $"start=({Start.P.X}, {Start.P.Y}), end=({End.P.X}, {End.P.Y}), " +
                $"dir={(Clockwise ? -1 : 1)}) returned null");
        segment.ConstructionGeometry = Construction;

        _live = segment;
        return this;
    }

    public JsonNode GetJson(string sketchName) {
        if (_live is null) {
            throw new InvalidOperationException("Arc.GetJson called before Add — no live segment to capture");
        }
        return DefinitionCapture.Capture(_live, sketchName).ToJson();
    }
}
