using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Sketches.Entities;

// Wire shape: point-end form (center + major-axis end + minor-axis end). SW's
// CreateEllipse takes the same arg shape; recovering (major, minor) magnitudes
// loses the rotation of the axes in the sketch plane, so we keep the points instead.
//
// SW quirk: SketchEllipse covers BOTH a closed ellipse and an elliptical arc — same
// COM type, swSketchELLIPSE for both. Disambiguate by point-equality of GetStartPoint2
// vs GetEndPoint2 (caller routes to EllipticalArc when distinct).
public sealed record Ellipse(
    SketchEntityId Id,
    bool Construction,
    [property: JsonPropertyName("center")] Point Center,
    [property: JsonPropertyName("major_axis_end")] Point MajorAxisEnd,
    [property: JsonPropertyName("minor_axis_end")] Point MinorAxisEnd) : SketchEntityDefinition(Id, Construction), ISketchEntity
{
    private SketchSegment? _live;

    // Capture from a live SW SketchSegment that the caller has already type-checked
    // as a closed SketchEllipse (DefinitionCapture routes closed→Ellipse, open→
    // EllipticalArc on a tolerance start==end check). SketchName is the owning sketch's
    // Feature.Name.
    internal static Ellipse Parse(SketchSegment segment, string sketchName) {
        var ellipse = segment as SketchEllipse
            ?? throw new InvalidOperationException(
                "Ellipse.Parse: segment is not a SketchEllipse");
        var ids = (segment as ISketchSegment)?.GetID() as int[]
            ?? throw new InvalidOperationException(
                "Ellipse.Parse: ISketchSegment.GetID() did not return an int[2]");
        if (ids.Length < 2) {
            throw new InvalidOperationException(
                $"Ellipse.Parse: GetID() returned an array of length {ids.Length}; expected 2");
        }
        var center = ellipse.IGetCenterPoint2()
            ?? throw new InvalidOperationException("Ellipse.Parse: IGetCenterPoint2 returned null");
        var major = ellipse.IGetMajorPoint2()
            ?? throw new InvalidOperationException("Ellipse.Parse: IGetMajorPoint2 returned null");
        var minor = ellipse.IGetMinorPoint2()
            ?? throw new InvalidOperationException("Ellipse.Parse: IGetMinorPoint2 returned null");
        return new Ellipse(
            new SketchEntityId(sketchName, SketchEntityId.KindEllipse, SketchEntityId.Combine(ids[0], ids[1])),
            Construction: segment.ConstructionGeometry,
            Point.Parse(center, sketchName),
            Point.Parse(major, sketchName),
            Point.Parse(minor, sketchName));
    }

    internal static new Ellipse FromJson(JsonNode node) {
        var def = Definition.FromJson(node)
            ?? throw new ArgumentException("Ellipse.FromJson: payload did not deserialize to a Definition");
        return def as Ellipse
            ?? throw new ArgumentException(
                $"Ellipse.FromJson: payload deserialized to {def.GetType().Name}, expected Ellipse");
    }

    internal JsonNode GetJson() => ToJson();

    internal override IEnumerable<Point2D> PatternExtentPoints() {
        var ux = MajorAxisEnd.P.X - Center.P.X;
        var uy = MajorAxisEnd.P.Y - Center.P.Y;
        var vx = MinorAxisEnd.P.X - Center.P.X;
        var vy = MinorAxisEnd.P.Y - Center.P.Y;
        var xExtent = Math.Sqrt(ux * ux + vx * vx);
        var yExtent = Math.Sqrt(uy * uy + vy * vy);
        yield return new Point2D(Center.P.X + xExtent, Center.P.Y + yExtent);
        yield return new Point2D(Center.P.X - xExtent, Center.P.Y - yExtent);
    }

    // Add this ellipse to the live SW sketch via SketchManager.CreateEllipse
    // (point-end form: center + major-axis-end + minor-axis-end). Returns the
    // freshly-created SketchSegment so the caller can register it in its
    // tag-to-live map without re-walking the sketch.
    internal Ellipse Add(Sketch sketch, SketchManager sketchManager) {
        var segment = sketchManager.CreateEllipse(
            Center.P.X, Center.P.Y, 0.0,
            MajorAxisEnd.P.X, MajorAxisEnd.P.Y, 0.0,
            MinorAxisEnd.P.X, MinorAxisEnd.P.Y, 0.0)
            ?? throw new InvalidOperationException(
                $"Ellipse.Add: SketchManager.CreateEllipse(center=({Center.P.X}, {Center.P.Y}), " +
                $"major=({MajorAxisEnd.P.X}, {MajorAxisEnd.P.Y}), " +
                $"minor=({MinorAxisEnd.P.X}, {MinorAxisEnd.P.Y})) returned null");
        segment.ConstructionGeometry = Construction;

        _live = segment;
        return this;
    }

    public JsonNode GetJson(string sketchName) {
        if (_live is null) {
            throw new InvalidOperationException("Ellipse.GetJson called before Add — no live segment to capture");
        }
        return DefinitionCapture.Capture(_live, sketchName).ToJson();
    }
}
