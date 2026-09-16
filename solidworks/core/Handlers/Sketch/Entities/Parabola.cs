using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Sketches.Entities;

// Wire shape: focal + apex + start + end of the swept arc. SW's CreateParabola
// takes (focal, apex, start, end) in that order.
//
// SW quirk: SketchParabola only round-trips when GetApexPoint2() returns non-null —
// generic conics use the parabola COM type but lack an apex. Throws on null
// apex/focal — generic conics aren't representable on the wire.
public sealed record Parabola(
    SketchEntityId Id,
    bool Construction,
    [property: JsonPropertyName("focal")] Point Focal,
    [property: JsonPropertyName("apex")] Point Apex,
    [property: JsonPropertyName("start")] Point Start,
    [property: JsonPropertyName("end")] Point End) : SketchEntityDefinition(Id, Construction), ISketchEntity
{
    private SketchSegment? _live;

    // Capture from a live SW SketchSegment that the caller has already type-checked
    // as a SketchParabola. SketchName is the owning sketch's Feature.Name.
    //
    // SW quirk: GetApexPoint2 / GetFocalPoint2 return generic Object and must be cast
    // to SketchPoint. A null return means this is a generic conic (parabola COM type,
    // no apex); throw rather than emit a half-formed entity, since generic conics
    // aren't on the wire.
    internal static Parabola Parse(SketchSegment segment, string sketchName) {
        var parabola = segment as SketchParabola
            ?? throw new InvalidOperationException(
                "Parabola.Parse: segment is not a SketchParabola");
        var ids = (segment as ISketchSegment)?.GetID() as int[]
            ?? throw new InvalidOperationException(
                "Parabola.Parse: ISketchSegment.GetID() did not return an int[2]");
        if (ids.Length < 2) {
            throw new InvalidOperationException(
                $"Parabola.Parse: GetID() returned an array of length {ids.Length}; expected 2");
        }
        var focalObj = parabola.IGetFocalPoint2()
            ?? throw new InvalidOperationException(
                "Parabola.Parse: IGetFocalPoint2 returned null (generic conic, not round-trippable)");
        var apexObj = parabola.IGetApexPoint2()
            ?? throw new InvalidOperationException(
                "Parabola.Parse: IGetApexPoint2 returned null (generic conic, not round-trippable)");
        var startObj = parabola.IGetStartPoint2()
            ?? throw new InvalidOperationException("Parabola.Parse: IGetStartPoint2 returned null");
        var endObj = parabola.IGetEndPoint2()
            ?? throw new InvalidOperationException("Parabola.Parse: IGetEndPoint2 returned null");
        return new Parabola(
            new SketchEntityId(sketchName, SketchEntityId.KindParabola, SketchEntityId.Combine(ids[0], ids[1])),
            Construction: segment.ConstructionGeometry,
            Point.Parse(focalObj, sketchName),
            Point.Parse(apexObj, sketchName),
            Point.Parse(startObj, sketchName),
            Point.Parse(endObj, sketchName));
    }

    internal static new Parabola FromJson(JsonNode node) {
        var def = Definition.FromJson(node)
            ?? throw new ArgumentException("Parabola.FromJson: payload did not deserialize to a Definition");
        return def as Parabola
            ?? throw new ArgumentException(
                $"Parabola.FromJson: payload deserialized to {def.GetType().Name}, expected Parabola");
    }

    internal JsonNode GetJson() => ToJson();

    internal override IEnumerable<Point2D> PatternExtentPoints() {
        yield return Apex.P;
        yield return Start.P;
        yield return End.P;
    }

    // Add via SketchManager.CreateParabola — arg order is (focal, apex, start, end).
    //
    // SW quirk: CreateParabola treats the inputs as fit data — SW re-fits the
    // parabola to the four points and the resulting child SketchPoints can drift
    // 1e-3 to 1e-2 from the inputs (the four points rarely lie EXACTLY on one
    // parabola due to round-trip float noise on capture). SetCoords on each child
    // point corrects the drift — two passes because SetCoords is iterative and
    // one call leaves residual drift. Other primitives (Line / Arc / Circle /
    // Ellipse / EllipticalArc) land their endpoints on the requested coords
    // without re-fitting and don't need this pin.
    internal Parabola Add(Sketch sketch, SketchManager sketchManager) {
        var segment = sketchManager.CreateParabola(
            Focal.P.X, Focal.P.Y, 0.0,
            Apex.P.X, Apex.P.Y, 0.0,
            Start.P.X, Start.P.Y, 0.0,
            End.P.X, End.P.Y, 0.0)
            ?? throw new InvalidOperationException(
                $"Parabola.Add: SketchManager.CreateParabola(focal=({Focal.P.X}, {Focal.P.Y}), " +
                $"apex=({Apex.P.X}, {Apex.P.Y}), start=({Start.P.X}, {Start.P.Y}), " +
                $"end=({End.P.X}, {End.P.Y})) returned null");
        segment.ConstructionGeometry = Construction;

        var parabola = (SketchParabola)segment;
        var focalPt = parabola.IGetFocalPoint2();
        var apexPt  = parabola.IGetApexPoint2();
        var startPt = parabola.IGetStartPoint2();
        var endPt   = parabola.IGetEndPoint2();
        for (int i = 0; i < 2; i++) {
            focalPt?.SetCoords(Focal.P.X, Focal.P.Y, 0.0);
            apexPt?.SetCoords(Apex.P.X,   Apex.P.Y,  0.0);
            startPt?.SetCoords(Start.P.X, Start.P.Y, 0.0);
            endPt?.SetCoords(End.P.X,     End.P.Y,   0.0);
        }

        _live = segment;
        return this;
    }

    public JsonNode GetJson(string sketchName) {
        if (_live is null) {
            throw new InvalidOperationException("Parabola.GetJson called before Add — no live segment to capture");
        }
        return DefinitionCapture.Capture(_live, sketchName).ToJson();
    }
}
