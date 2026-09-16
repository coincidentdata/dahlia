using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Utils;

// Diagnostic helper: dump the SelectionManager's current selection set to the log.
// The proxy types SW returns from `selMgr.GetSelectedObject6` show up as bare
// `__ComObject` under `.GetType().Name` because the runtime doesn't have the COM
// type metadata loaded — so we explicitly probe for the common SW interfaces
// (Feature, Face2, Edge, Vertex, Body2, SketchSegment, SketchPoint, RefAxis,
// RefPlane) and prefer the match-name, falling back to the bare runtime name when
// nothing matches. Mark goes through `GetSelectedObjectMark` so the per-handler
// "what's on which mark" picture shows up clearly in the log.
public static class SelectionDebug {
    // Returns a list of `"#i <type> mark=<m>"` strings — small enough to log inline,
    // ordered by selection index (1-based, matching SW's API).
    public static List<string> Snapshot(File file) {
        var selMgr = (SelectionMgr)file.ModelDoc.SelectionManager;
        var count = selMgr.GetSelectedObjectCount2(-1);
        var entries = new List<string>(count);
        for (var i = 1; i <= count; i++) {
            var obj = selMgr.GetSelectedObject6(i, -1);
            var mark = selMgr.GetSelectedObjectMark(i);
            var typeName = TypeNameFor(obj);
            var detail = ExtraDetail(obj);
            entries.Add(detail is null
                ? $"#{i} {typeName} mark={mark}"
                : $"#{i} {typeName} mark={mark} {detail}");
        }
        return entries;
    }

    // Log the current selection state at Information level with a caller-supplied tag.
    // Use this around tricky Add paths (pattern, sweep, fillet, refplane) to catch
    // mark-mismatches and stray prior selections without manually wiring a snapshot
    // call each time.
    public static void Log(File file, string tag) {
        var entries = Snapshot(file);
        SldworksLog.Information(
            "Selection {Tag}: count={Count} entries={@Entries}", tag, entries.Count, entries);
    }

    private static string TypeNameFor(object? obj) {
        if (obj is null) return "<null>";
        // Order matters: more-specific interfaces first. SketchPoint is an ISketchPoint
        // (NOT an IEntity in 2026), so probe before IEntity-derived types.
        return obj switch {
            Feature           => "Feature",
            Body2             => "Body2",
            Face2             => "Face2",
            Edge              => "Edge",
            Vertex            => "Vertex",
            SketchSpline      => "SketchSpline",
            SketchEllipse     => "SketchEllipse",
            SketchParabola    => "SketchParabola",
            SketchArc         => "SketchArc",
            SketchLine        => "SketchLine",
            SketchPoint       => "SketchPoint",
            SketchSegment     => "SketchSegment",
            RefAxis           => "RefAxis",
            RefPlane          => "RefPlane",
            DisplayDimension  => "DisplayDimension",
            SketchRelation    => "SketchRelation",
            _                 => obj.GetType().Name,  // falls back to "__ComObject" for unknown SW proxies
        };
    }

    // Optional one-line detail for entity types where a single field is far more
    // useful than just the type. Keep it short — the Snapshot list goes into a log
    // line and we don't want to flood it.
    private static string? ExtraDetail(object? obj) {
        return obj switch {
            Feature feat              => $"name='{feat.Name}' type='{feat.GetTypeName2()}'",
            SketchSegment seg         => SketchSegmentDetail(seg),
            SketchPoint pt            => $"id={SketchIdString((pt as ISketchPoint)?.GetID())}",
            _                         => null,
        };
    }

    private static string SketchSegmentDetail(SketchSegment seg) {
        var ids = (seg as ISketchSegment)?.GetID() as int[];
        var sketchName = (seg.GetSketch() as Feature)?.Name;
        return $"id={SketchIdString(ids)} sketch='{sketchName}' construction={seg.ConstructionGeometry}";
    }

    private static string SketchIdString(object? raw) {
        if (raw is int[] ids && ids.Length >= 2) return $"[{ids[0]},{ids[1]}]";
        return "?";
    }
}
