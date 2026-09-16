using System.Text.Json.Serialization;

namespace Sldworks.Core.Definitions;

// Wire is meters (lengths) and radians (angles).

public sealed record Point2D(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

public sealed record Point3D(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z = 0.0);

// Not normalized at construction; the resolver normalizes when comparing.
public sealed record Direction(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z);

// `Radius` is the SelectByRay cylinder radius in meters: null → the consumer's
// default (Probe's DefaultProbeRadius). SW ignores it for faces (infinite-line
// select); it matters for edges/vertices/datums. Probe generation bakes a tight,
// clearance-sized radius here so replay re-selects the same entity with margin.
public sealed record Ray(
    [property: JsonPropertyName("origin")] Point3D Origin,
    [property: JsonPropertyName("direction")] Direction Direction,
    [property: JsonPropertyName("radius")] double? Radius = null);

// SW quirk: Face2.GetBox returns meters regardless of document display units.
public sealed record BoundingBox(
    [property: JsonPropertyName("min")] Point3D Min,
    [property: JsonPropertyName("max")] Point3D Max);
