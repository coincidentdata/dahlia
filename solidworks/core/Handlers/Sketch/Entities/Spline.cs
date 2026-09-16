using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swcommands;

namespace Sldworks.Core.Handlers.Sketches.Entities;

// Non-rational B-spline. Control points + knots describe a degree-3 (cubic) B-curve;
// the handler hard-codes order=4 / dimension=3.
//
// SW quirk: SketchSpline.IsRationalCurve — rational splines (NURBS with non-unit
// weights) refuse to round-trip via CreateSplineByEqnParams; throws to surface the case.
//
// `Generic` — when true the handler runs swCommands_ConvertToModif after creation so
// the spline becomes editable via SW's generic-spline UI. Recovered via
// SketchSpline.GetSplineHandles()[0].Editable == false.
//
// `SplinePoints` is Inspect-only: interpolation/handle points the SW spline maintains
// alongside the control points. Read from SketchSpline.GetPoints2().
public sealed record Spline(
    SketchEntityId Id,
    bool Construction,
    [property: JsonPropertyName("control_points")] IReadOnlyList<Point2D> ControlPoints,
    [property: JsonPropertyName("knots")] IReadOnlyList<double> Knots,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("periodic")] bool Periodic = false,
    [property: JsonPropertyName("generic")] bool Generic = false,
    [property: JsonPropertyName("echo_spline_points")] IReadOnlyList<Point2D>? SplinePoints = null)
    : SketchEntityDefinition(Id, Construction), ISketchEntity
{
    private SketchSegment? _live;

    // Capture from a live SW SketchSegment that the caller has already type-checked
    // as a SketchSpline. SketchName is the owning sketch's Feature.Name.
    //
    // Read path: pull the underlying Curve via IGetCurve, ask for
    // BCurveParams5(cubic=false, biz=false, blend=false, isClosed) — `isClosed`
    // comes from Curve.GetEndParams's third out-param (bool, not int). Then
    // GetControlPoints / GetKnotPoints flatten to (double[]) and double[].
    internal static Spline Parse(SketchSegment segment, string sketchName) {
        var spline = segment as SketchSpline
            ?? throw new InvalidOperationException(
                "Spline.Parse: segment is not a SketchSpline");
        var ids = (segment as ISketchSegment)?.GetID() as int[]
            ?? throw new InvalidOperationException(
                "Spline.Parse: ISketchSegment.GetID() did not return an int[2]");
        if (ids.Length < 2) {
            throw new InvalidOperationException(
                $"Spline.Parse: GetID() returned an array of length {ids.Length}; expected 2");
        }
        // SW quirk: rational splines (non-unit weights) can't be re-authored via
        // CreateSplineByEqnParams.
        if (spline.IsRationalCurve) {
            throw new InvalidOperationException(
                $"Spline.Parse: SketchSpline {ids[0]}/{ids[1]} is rational; rational splines refuse to round-trip");
        }
        var curve = segment.IGetCurve()
            ?? throw new InvalidOperationException("Spline.Parse: IGetCurve returned null");
        curve.GetEndParams(out _, out _, out bool isClosed, out bool _);
        var splineParams = curve.GetBCurveParams5(false, false, false, isClosed)
            ?? throw new InvalidOperationException("Spline.Parse: GetBCurveParams5 returned null");

        if (!splineParams.GetControlPoints(out var controlPointsObj)) {
            throw new InvalidOperationException("Spline.Parse: BCurveParams5.GetControlPoints failed");
        }
        if (!splineParams.GetKnotPoints(out var knotsObj)) {
            throw new InvalidOperationException("Spline.Parse: BCurveParams5.GetKnotPoints failed");
        }

        var controlPointDefs = (double[])controlPointsObj;
        var controlPoints = new List<Point2D>(controlPointDefs.Length / 3);
        // SW returns control points as flat (x, y, z) triples; sketch-local Z is zero
        // and not on the wire — only X, Y survive.
        for (int i = 0; i < controlPointDefs.Length; i += 3) {
            controlPoints.Add(new Point2D(controlPointDefs[i], controlPointDefs[i + 1]));
        }

        var knots = ((double[])knotsObj).ToList();

        // SW quirk: GetSplineHandles()[0].Editable == false means the spline is in
        // "generic" mode (post-ConvertToModif).
        var handles = (object[])spline.GetSplineHandles();
        var generic = handles.Length > 0 && ((SplineHandle)handles[0]).Editable == false;

        // SW quirk: GetPoints2 returns the interpolation/handle points; for closed
        // splines the first and last refer to the same SketchPoint, so de-duplicate
        // the trailing element. Inspect-only — re-author from control points + knots
        // via CreateSplineByEqnParams.
        var splinePointObjs = (object[])spline.GetPoints2();
        if (splinePointObjs.Length > 0 && splinePointObjs.First() == splinePointObjs.Last()) {
            splinePointObjs = splinePointObjs.Skip(1).ToArray();
        }
        var splinePoints = new List<Point2D>(splinePointObjs.Length);
        for (int i = 0; i < splinePointObjs.Length; i++) {
            var pt = (SketchPoint)splinePointObjs[i];
            splinePoints.Add(new Point2D(pt.X, pt.Y));
        }

        return new Spline(
            new SketchEntityId(sketchName, SketchEntityId.KindSpline, SketchEntityId.Combine(ids[0], ids[1])),
            Construction: segment.ConstructionGeometry,
            ControlPoints: controlPoints,
            Knots: knots,
            // ISplineParamData.Order is the source of truth; for periodic splines SW
            // returns the COMPACT knot vector where `knots = cps + 1` regardless of
            // order, so deriving order from `knots - cps` would yield 1 for cubic
            // periodic. Reading splineParams.Order directly avoids guessing.
            Order: splineParams.Order,
            Periodic: splineParams.Periodic == 1,
            Generic: generic,
            SplinePoints: splinePoints);
    }

    internal static new Spline FromJson(JsonNode node) {
        var def = Definition.FromJson(node)
            ?? throw new ArgumentException("Spline.FromJson: payload did not deserialize to a Definition");
        return def as Spline
            ?? throw new ArgumentException(
                $"Spline.FromJson: payload deserialized to {def.GetType().Name}, expected Spline");
    }

    internal JsonNode GetJson() => ToJson();

    internal override IEnumerable<Point2D> PatternExtentPoints() {
        var points = SplinePoints is { Count: > 0 } ? SplinePoints : ControlPoints;
        foreach (var p in points) {
            yield return p;
        }
    }

    // Add via SketchManager.CreateSplineByEqnParams. Param-array layout:
    //   header [Dimension=3, Order, cp-count, periodic ? 1 : 0]
    //   then knots,
    //   then control points (each 3 doubles x, y, 0 — sketch-local Z is zero).
    //
    // Order travels on the wire (captured from ISplineParamData.Order on Inspect)
    // because SW returns the COMPACT periodic knot vector for periodic splines —
    // `knots = cps + 1` regardless of order — so we can't derive order from knot
    // count alone. Cubic non-periodic does happen to satisfy `knots = cps + 4`,
    // but periodic and non-cubic cases need the explicit value.
    //
    // SW quirk: when Generic is FALSE the spline must be promoted to SW's
    // "modify"/generic UI mode via swCommands_ConvertToModif (the spline must be
    // selected first). The naming is inverted vs what one might expect —
    // `Generic=false` (handles ARE editable) is the case that needs ConvertToModif;
    // `Generic=true` (handles non-editable) is already in the post-ConvertToModif
    // state and skips the command. ModelDoc is required for Extension.RunCommand
    // and is not reachable from Sketch / SketchManager alone, so the orchestrator
    // passes it through.
    internal Spline Add(Sketch sketch, SketchManager sketchManager, ModelDoc2 modelDoc) {
        var pointData = new List<double>(4 + Knots.Count + ControlPoints.Count * 3);
        pointData.Add(3);                              // Dimension (3D points)
        pointData.Add(Order);                          // Order (degree+1)
        pointData.Add(ControlPoints.Count);
        pointData.Add(Periodic ? 1 : 0);
        pointData.AddRange(Knots);
        foreach (var cp in ControlPoints) {
            pointData.Add(cp.X);
            pointData.Add(cp.Y);
            pointData.Add(0.0);
        }

        var segment = sketchManager.CreateSplineByEqnParams(pointData.ToArray())
            ?? throw new InvalidOperationException(
                $"Spline.Add: SketchManager.CreateSplineByEqnParams " +
                $"(cps={ControlPoints.Count}, knots={Knots.Count}, order={Order}, periodic={Periodic}) returned null");
        segment.ConstructionGeometry = Construction;

        if (!Generic) {
            segment.Select4(false, null);
            modelDoc.Extension.RunCommand((int)swCommands_e.swCommands_ConvertToModif, "");
        }

        _live = segment;
        return this;
    }

    public JsonNode GetJson(string sketchName) {
        if (_live is null) {
            throw new InvalidOperationException("Spline.GetJson called before Add — no live segment to capture");
        }
        return DefinitionCapture.Capture(_live, sketchName).ToJson();
    }
}
