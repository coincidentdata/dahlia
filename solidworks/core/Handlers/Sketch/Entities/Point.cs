using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Handlers.Sketches.Entities;

// Wire shape: construction + position. Standalone SketchPoints are their own
// entities; line/arc endpoints aren't emitted as separate Point entries (those
// are reachable via the parent segment).
public sealed record Point(
    SketchEntityId Id,
    bool Construction,
    [property: JsonPropertyName("p")] Point2D P) : SketchEntityDefinition(Id, Construction), ISketchEntity
{
    private SketchPoint? _live;

    // Capture from a live SW SketchPoint that the caller has already type-checked.
    // SketchName is the owning sketch's Feature.Name; pulled by the SketchHandler
    // caller (avoids a redundant GetSketch() round-trip per entity).
    //
    // Construction is always false: SketchPoints have no construction concept.
    // Z is always 0 in sketch-local coords.
    //
    // The Id is filled in here from point.GetID() so the returned record is
    // self-consistent in isolation; DefinitionCapture.Capture then re-applies the
    // same id via `with { Id = ... }`, which is redundant but harmless.
    internal static Point Parse(SketchPoint point, string sketchName) {
        // SW quirk: SketchPoint.GetID() lives on ISketchPoint; the coclass returns int[2].
        var ids = (point as ISketchPoint)?.GetID() as int[]
            ?? throw new InvalidOperationException(
                "Point.Parse: ISketchPoint.GetID() did not return an int[2]");
        if (ids.Length < 2) {
            throw new InvalidOperationException(
                $"Point.Parse: GetID() returned an array of length {ids.Length}; expected 2");
        }
        return new Point(
            new SketchEntityId(sketchName, SketchEntityId.KindPoint, SketchEntityId.Combine(ids[0], ids[1])),
            Construction: false,
            new Point2D(point.X, point.Y));
    }

    internal static new Point FromJson(JsonNode node) {
        var def = Definition.FromJson(node)
            ?? throw new ArgumentException("Point.FromJson: payload did not deserialize to a Definition");
        return def as Point
            ?? throw new ArgumentException(
                $"Point.FromJson: payload deserialized to {def.GetType().Name}, expected Point");
    }

    internal JsonNode GetJson() => ToJson();

    internal override IEnumerable<Point2D> PatternExtentPoints() {
        yield return P;
    }

    // Adds this point to the live SW sketch and returns the freshly-created SketchPoint.
    // Caller is responsible for entering the sketch and toggling AddToDB / wireframe scopes.
    internal Point Add(Sketch sketch, SketchManager sketchManager) {
        var created = sketchManager.CreatePoint(P.X, P.Y, 0.0)
            ?? throw new InvalidOperationException(
                $"Point.Add: SketchManager.CreatePoint({P.X}, {P.Y}, 0) returned null");
        _live = created;
        return this;
    }

    public JsonNode GetJson(string sketchName) {
        if (_live is null) {
            throw new InvalidOperationException("Point.GetJson called before Add — no live point to capture");
        }
        return DefinitionCapture.Capture(_live, sketchName).ToJson();
    }
}
