using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Handlers;
using Sldworks.Core.Handlers.Mates;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core;

public partial class File {
    private readonly ModelDoc2 _modelDoc;
    private readonly SldWorks _sldWorks;
    private readonly MathUtility _mathUtil;

    // Local flag so Lock/Unlock are idempotent.
    private bool _locked;

    internal File(ModelDoc2 modelDoc, SldWorks sldWorks) {
        _modelDoc = modelDoc;
        _sldWorks = sldWorks;
        _mathUtil = sldWorks.IGetMathUtility();
    }

    public string Name => _modelDoc.GetTitle();
    public string Path => _modelDoc.GetPathName() ?? string.Empty;
    public string Kind => _modelDoc.GetType() switch {
        (int)swDocumentTypes_e.swDocPART => "part",
        (int)swDocumentTypes_e.swDocASSEMBLY => "assembly",
        _ => throw new NotSupportedException("Only parts and assemblies are supported"),
    };

    internal ModelDoc2 ModelDoc => _modelDoc;
    internal SldWorks SldWorks => _sldWorks;
    internal MathUtility MathUtil => _mathUtil;
    internal ModelView ActiveView => (ModelView)_modelDoc.ActiveView;

    // Single-shot handoff: SetBodies writes; cutting handler reads in PromptBodiesToKeepNotify and clears.
    internal IReadOnlyList<Body2>? PendingBodiesToKeep { get; set; }

    public JsonNode View() {
        if (Kind == "assembly") return ViewAssembly();
        var features = new JsonArray();
        var index = 0;
        var feat = _modelDoc.FirstFeature() as Feature;
        while (feat is not null) {
            features.Add(new JsonObject {
                ["index"] = index,
                ["name"] = feat.Name,
                ["type"] = ResolveFeatureTypeName(feat),
                ["status"] = ReadFeatureStatus(feat),
            });
            feat = feat.GetNextFeature() as Feature;
            index++;
        }

        // SW quirk: GetBodies2 lives on IPartDoc and returns null (not empty) when no bodies match.
        var bodyArray = ((IPartDoc)_modelDoc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
        var bodyCount = bodyArray?.Length ?? 0;

        // SW quirk: no IModelDocExtension.GetBox in this redist; union IBody2.GetBodyBox()
        // (6 doubles in METERS regardless of display units).
        var box = new double[] { 0, 0, 0, 0, 0, 0 };
        if (bodyArray is not null && bodyArray.Length > 0) {
            double xmin = double.PositiveInfinity, ymin = double.PositiveInfinity, zmin = double.PositiveInfinity;
            double xmax = double.NegativeInfinity, ymax = double.NegativeInfinity, zmax = double.NegativeInfinity;
            foreach (var entry in bodyArray) {
                if (entry is not Body2 body) continue;
                var b = body.GetBodyBox() as double[];
                if (b is null || b.Length < 6) continue;
                if (b[0] < xmin) xmin = b[0];
                if (b[1] < ymin) ymin = b[1];
                if (b[2] < zmin) zmin = b[2];
                if (b[3] > xmax) xmax = b[3];
                if (b[4] > ymax) ymax = b[4];
                if (b[5] > zmax) zmax = b[5];
            }
            if (!double.IsInfinity(xmin)) {
                box = new[] { xmin, ymin, zmin, xmax, ymax, zmax };
            }
        }

        return new JsonObject {
            ["kind"] = Kind,
            ["path"] = Path,
            ["units"] = CurrentUnitName,
            ["bbox"] = new JsonObject {
                ["min"] = new JsonArray(box[0], box[1], box[2]),
                ["max"] = new JsonArray(box[3], box[4], box[5]),
            },
            ["body_count"] = bodyCount,
            ["features"] = features,
        };
    }

    public JsonNode InspectFeature(int index) {
        using var _ = MassProperties.Push(_modelDoc);
        var feature = FeatureAt(index)
            ?? throw new ArgumentOutOfRangeException(nameof(index),
                $"InspectFeature: no feature at index {index}");

        if (Kind == "assembly" && feature.GetTypeName2().StartsWith("Mate") && feature.GetSpecificFeature2() is Mate2) return MateHandler.Inspect(this, feature);

        var typeName = ResolveFeatureTypeName(feature);
        return typeName switch {
            SketchHandler.SwTypeName           => SketchHandler.Inspect(this, feature),
            ExtrudeHandler.SwTypeName          => ExtrudeHandler.Inspect(this, feature),
            CutExtrudeHandler.SwTypeName       => CutExtrudeHandler.Inspect(this, feature),
            RevolveHandler.SwTypeName          => RevolveHandler.Inspect(this, feature),
            CutRevolveHandler.SwTypeName       => CutRevolveHandler.Inspect(this, feature),
            SweepHandler.SwTypeName            => SweepHandler.Inspect(this, feature),
            CutSweepHandler.SwTypeName         => CutSweepHandler.Inspect(this, feature),
            HelixHandler.SwTypeName            => HelixHandler.Inspect(this, feature),
            LoftHandler.SwTypeName or LoftHandler.CutSwTypeName => LoftHandler.Inspect(this, feature),
            ShellHandler.SwTypeName => ShellHandler.Inspect(this, feature),
            DraftHandler.SwTypeName => DraftHandler.Inspect(this, feature),
            RibHandler.SwTypeName => RibHandler.Inspect(this, feature),
            ProjectedCurveHandler.SwTypeName   => ProjectedCurveHandler.Inspect(this, feature),
            LinearPatternHandler.SwTypeName    => LinearPatternHandler.Inspect(this, feature),
            CircularPatternHandler.SwTypeName  => CircularPatternHandler.Inspect(this, feature),
            MirrorHandler.PatternType or MirrorHandler.BodyType => MirrorHandler.Inspect(this, feature),
            FilletHandler.SwTypeName           => FilletHandler.Inspect(this, feature),
            ChamferHandler.SwTypeName          => ChamferHandler.Inspect(this, feature),
            RefPlaneHandler.SwTypeName         => RefPlaneHandler.Inspect(this, feature),
            RefAxisHandler.SwTypeName          => RefAxisHandler.Inspect(this, feature),
            CombineHandler.SwTypeName          => CombineHandler.Inspect(this, feature),
            SplitHandler.SwTypeName            => SplitHandler.Inspect(this, feature),
            _ => throw new ArgumentException(
                $"InspectFeature: no handler for SolidWorks feature type '{typeName}' on feature '{feature.Name}'")
        };
    }

    internal static string ResolveFeatureTypeName(Feature feature) {
        // SW quirk: GetTypeName2() returns "ICE" for some boss extrudes — fall back
        // to legacy GetTypeName() and normalize via the alias map.
        var typeName = feature.GetTypeName2() ?? "";
        if (typeName.StartsWith("Mate") && feature.GetSpecificFeature2() is Mate2 mate)
            return MateHandler.TypeName(mate.Type);
        if (typeName == "ICE") {
            typeName = feature.GetTypeName() ?? "";
        }
        return LegacyTypeNameAliases.TryGetValue(typeName, out var canonical) ? canonical : typeName;
    }

    // GetTypeName() short-form -> GetTypeName2() long-form.
    private static readonly Dictionary<string, string> LegacyTypeNameAliases = new() {
        { "Boss",          "Extrusion" },
        { "Cut",           "Cut-Extrude" },
        { "RevCut",        "Cut-Revolve" },
        { "Sweep",         "SweepFeature" },
        { "SweepCut",      "Cut-Sweep" },
        { "Split",         "BodySplit" },
        { "CombineBodies", "BodyOperation" },
    };

    // LLM-friendly authoring helper for sketch regions. The author provides
    // a 2D point in the sketch's plane; we iterate `sketch.GetSketchRegions()`
    // and pick the one whose boundary loop contains the point via PNPoly.
    //
    // SW quirk why-not-SelectByID2: `SelectByID2("", "SKETCHREGION", x, y, z, ...)`
    // (with `SelectionMgr.EnableContourSelection = true`) DOES land on a region
    // — `GetSelectedObjectType3` reports `selType=25 = swSelSKETCHREGION`. But
    // `GetSelectedObject6(1, -1)` returns a generic __ComObject RCW that fails
    // QueryInterface for `ISketchRegion` at the raw IUnknown level (hr=
    // 0x80004002 E_NOINTERFACE). The append-select count-delta fallback also
    // doesn't reliably match. So the "ask SW directly" route is dead. We walk
    // the loop ourselves.
    public JsonNode ProbeRegion(string sketchName, double pointX, double pointY) {
        using var _ = MassProperties.Push(_modelDoc);
        Sketch? sketch = null;
        var feat = _modelDoc.IFirstFeature() as Feature;
        while (feat is not null) {
            if (feat.GetSpecificFeature2() is Sketch s && feat.Name == sketchName) {
                sketch = s;
                break;
            }
            feat = feat.IGetNextFeature() as Feature;
        }
        if (sketch is null) {
            throw new ArgumentException(
                $"ProbeRegion: no sketch named '{sketchName}'");
        }
        var raw = sketch.GetSketchRegions() as object[];
        if (raw is null) {
            throw new InvalidOperationException(
                $"ProbeRegion: sketch '{sketchName}' has no regions");
        }
        // SW quirk: `GetSketchRegions()` can return OVERLAPPING regions (e.g.
        // the inscribed-triangle-in-circle sketch in 4054A11 returns 7 regions
        // including a wrapping "outer minus triangle" region that contains
        // every point its lens sub-regions contain). Pick the SMALLEST-bbox
        // matching region — that's the most specific.
        SketchRegion? hit = null;
        double hitArea = double.PositiveInfinity;
        foreach (var rgnObj in raw) {
            if (rgnObj is not SketchRegion region) continue;
            var loops = SketchRegionGeometry.SampleLoops(region, sketch);
            if (loops.Count == 0) continue;
            if (!SketchRegionGeometry.PointInRegion(loops, pointX, pointY)) continue;
            var area = SketchRegionGeometry.LoopsBboxArea(loops);
            if (area < hitArea) {
                hit = region;
                hitArea = area;
            }
        }
        if (hit is null) {
            throw new InvalidOperationException(
                $"ProbeRegion: no SketchRegion in '{sketchName}' contains " +
                $"sketch-local ({pointX:G},{pointY:G})");
        }
        return DefinitionCapture.Capture(hit).ToJson();
    }

    public JsonNode? Probe(JsonNode ray, string entityType, JsonNode? onBody = null) {
        if (Kind == "assembly") return ProbeAssembly(ray, entityType, onBody);
        using var _ = MassProperties.Push(_modelDoc);
        var parsed = ray.Deserialize<Ray>(Definition.JsonOptions)
            ?? throw new ArgumentException("Select: malformed ray payload");
        var scope = onBody is null || onBody.GetValueKind() == JsonValueKind.Null
            ? null
            : Definition.FromJson(onBody)
              ?? throw new ArgumentException("Probe: malformed on_body payload");

        var radius = parsed.Radius is double pr && pr > 0 ? pr : DefaultProbeRadius;
        var hit = SelectByRayDetailed(parsed, entityType, radius, scope);
        SldworksLog.Information(
            // counted < countAll means a coincident tie was resolved by normal and the
            // excluded hits were dropped from the clearance list — the visible signal
            // that the front-facing rule fired on this ray.
            "Select[{Type}] r={Radius:E2}m countAll={CountAll} counted={Counted} picked={Picked}@{BestDist:E2}m scoped={Scoped}",
            entityType, radius, hit.CountAll, hit.SortedDistances.Count,
            hit.Def?.GetType().Name ?? "<miss>",
            hit.SortedDistances.Count > 0 ? hit.SortedDistances[0] : double.NaN,
            scope is not null);
        if (hit.Def is null) return null;

        var json = hit.Def.ToJson();
        // Echo an UNRESOLVED ambiguity: this ray hit coincident geometry that the
        // front-facing rule could not order, so replaying it may bind the other entity.
        // A coincidence the rule did resolve is deterministic and says nothing here.
        // `echo_*` fields are ignored by Add and stripped from diffs, so it rides along.
        //
        // The warning belongs HERE rather than in the selection routine: this is a ray a
        // caller actually uses, whereas the selection routine also serves ScoreProbeGrid's
        // throwaway candidates.
        if (hit.Ambiguous && json is JsonObject obj) {
            obj["echo_probe_ambiguous"] = true;
            SldworksLog.Warning(
                "Probe[{Type}]: ray hit coincident geometry ({CountAll} hits) that cannot be " +
                "ordered by normal — replaying this ray may bind a different entity; scope " +
                "it with on_body",
                entityType, hit.CountAll);
        }
        return json;
    }

    // One MultiSelectByRay sweep, surfaced with the robustness signals the probe-
    // generation pipeline scores on: the closest-to-axis entity's captured
    // Definition (the pick), the total hit count, and every hit's ray-axis distance
    // sorted ascending — [0] is the picked entity, [1] (if present) the nearest
    // competitor. The gap between them is the clearance the scored generator
    // maximizes.
    //
    // We use MultiSelectByRay, NOT SelectByRay. Plain SelectByRay returns only the
    // single nearest entity (countAll=1 always), which left the competitor term
    // dead and the closest-pick loop below the no-op the original author flagged
    // ("would only matter if we ever switched to MultiSelectByRay"). MultiSelectByRay
    // selects EVERY entity within `radius` of the ray axis, so the loop can both pick
    // the target (smallest axis distance) AND measure the gap to the next entity.
    // SW quirk: radius is ignored for faces (infinite-line select) but matters for
    // edges/vertices/datums; the bool return is unreliable, so we count via
    // GetSelectedObjectCount2. IMultiSelectByRay takes point/vector as refs to the
    // first element of 3-double arrays.
    // `CountAll` is every entity the ray selected. `SortedDistances` can be SHORTER:
    // coincident hits that the front-facing rule excluded are dropped, because clearance
    // exists to size the baked radius and the radius is not what decides those. So read
    // CountAll for "what did the ray touch" and SortedDistances for "what must the radius
    // separate" — they are deliberately different questions.
    private readonly record struct ProbeHit(
        Definition? Def, int CountAll, IReadOnlyList<double> SortedDistances,
        bool Ambiguous = false);

    // Two hits whose representative points land this close are the SAME point in
    // space, i.e. genuinely coincident geometry rather than two entities the ray
    // merely passes near. Deliberately not derived from ray-axis distance: that
    // measure is lateral, so every face an infinite line crosses scores ~0 even
    // when it sits 150 mm away.
    private const double CoincidentPointTol = 1e-7;

    // Reuse the representative point from RayAxisDistance; another GetClosestPointOn
    // call per hit makes coincidence detection expensive on dense geometry.
    private ProbeHit SelectByRayDetailed(
        Ray ray, string entityType, double radius, Definition? onBody = null) {
        var filterBit = entityType switch {
            "face"   => (int)swSelectType_e.swSelFACES,
            "edge"   => (int)swSelectType_e.swSelEDGES,
            "vertex" => (int)swSelectType_e.swSelVERTICES,
            "body"   => (int)swSelectType_e.swSelSOLIDBODIES,
            "plane"  => (int)swSelectType_e.swSelDATUMPLANES,
            _ => -1,
        };
        if (filterBit < 0)
            throw new ArgumentException($"Select: unknown entityType '{entityType}'");

        var selMgr = _modelDoc.ISelectionManager;
        _modelDoc.ClearSelection2(true);
        try {
            var pt = new[] { ray.Origin.X, ray.Origin.Y, ray.Origin.Z };
            var vec = new[] { ray.Direction.X, ray.Direction.Y, ray.Direction.Z };
            _modelDoc.IMultiSelectByRay(ref pt[0], ref vec[0], radius, filterBit, /*Append*/ false);
            var countAll = selMgr.GetSelectedObjectCount2(-1);
            if (countAll == 0) return new ProbeHit(null, 0, Array.Empty<double>());

            var hits = new List<(object Obj, double Dist, double[]? Point, Face2? Face)>(countAll);
            for (var i = 1; i <= countAll; i++) {
                var obj = selMgr.GetSelectedObject6(i, -1);
                if (obj is null) continue;
                var d = RayAxisDistance(obj, ray, out var rep, out var repFace);
                hits.Add((obj, d, rep, repFace));
            }

            // `on_body` scope: keep only hits owned by the requested body. This is
            // how a caller disambiguates coincident geometry — "the face there, on
            // THAT body". Filtering to nothing is a miss, not a fallback to an
            // unscoped pick; the caller asked for a specific body and did not get it.
            if (onBody is not null) {
                var kept = hits.Where(h => OwningBody(h.Obj) is { } ob
                                           && DefinitionMatch.Approx(ob, onBody)).ToList();
                SldworksLog.Information(
                    "Select[{Type}]: on_body scope kept {Kept} of {All} hit(s)",
                    entityType, kept.Count, hits.Count);
                hits = kept;
                if (hits.Count == 0) return new ProbeHit(null, countAll, Array.Empty<double>());
            }

            var ordered = hits.OrderBy(h => h.Dist).ToList();
            object? best = ordered.Count > 0 ? ordered[0].Obj : selMgr.GetSelectedObject6(1, -1);
            if (best is null) return new ProbeHit(null, countAll, Array.Empty<double>());

            // Two hits whose representative points are the SAME point in space are
            // coincident, and SW's enumeration order between them is not stable across
            // rebuilds — so a transcript replaying this ray could bind to the other one.
            // The points were computed above, so this is arithmetic and always runs.
            var bestPoint = ordered.Count > 0 ? ordered[0].Point : null;
            var group = bestPoint is null
                ? new List<(object Obj, double Dist, double[]? Point, Face2? Face)>()
                : ordered.Where(h => h.Point is not null && PointsCoincide(h.Point!, bestPoint))
                         .ToList();
            // Coincidence alone is not a problem — an UNBREAKABLE tie is. Break it
            // deterministically instead of accepting SW's order: a ray strikes the side
            // of a surface it can SEE, so among faces at one point prefer the one whose
            // outward normal opposes the ray. That is what an author means by aiming from
            // a side ("the downward-facing face there"), needs no second probe, and makes
            // both faces of a non-merged interface reachable by aiming from opposite
            // directions. `ambiguous` is reported only when the tie cannot be decided,
            // because that is the case that actually replays nondeterministically.
            var ambiguous = false;
            var tieResolved = false;
            if (group.Count > 1) {
                var front = FrontFacingPick(group, ray, entityType);
                if (front is not null) {
                    best = front;
                    tieResolved = true;
                } else {
                    // Reported by the caller, not here. ScoreProbeGrid fires thousands of
                    // CANDIDATE rays through this method and many transiently graze
                    // coincident geometry before a clean ray is chosen; warning here logged
                    // 8 warnings on a part whose accepted probes were all unambiguous.
                    // Only a ray that survives to be USED is worth a warning.
                    ambiguous = true;
                }
            }

            // Clearance measures what the BAKED RADIUS has to separate. A coincident hit
            // the normal rule already excluded is not such a competitor — ordering, not
            // radius, decided the pick — so it must not sit at distance 0 and drag
            // clearance to 0. That zero is what defeats ScoreProbeGrid's early exit and
            // makes it scan the whole candidate grid. An UNRESOLVED tie keeps its
            // competitors: there the ray really is
            // ambiguous and the scan should keep looking.
            var counted = tieResolved
                ? ordered.Where(h => ReferenceEquals(h.Obj, best)
                                     || !group.Any(g => ReferenceEquals(g.Obj, h.Obj)))
                : ordered;
            var def = CaptureSelectedObject(best, entityType);
            return new ProbeHit(def, countAll, counted.Select(h => h.Dist).ToList(), ambiguous);
        } finally {
            _modelDoc.ClearSelection2(true);
        }
    }

    // Distance from a representative point on the selected entity to the ray axis
    // (treating the ray as an infinite line, matching SelectByRay's semantics).
    // Representative points:
    //   Edge   — midpoint of the curve parameter range
    //   Vertex — the vertex point
    //   Face2  — face point closest to the ray origin (snapped onto the trim)
    //   Body2  — mass-properties centroid
    // Returns +Inf if no representative point can be derived — that entity
    // loses the tie-break naturally, and the caller's first-hit fallback picks
    // it up if it's the only candidate.
    // `repPoint` is the representative point itself, handed back so callers can test
    // two hits for coincidence without re-fetching it. Faces cost a
    // GetClosestPointOn COM call to locate; making that call twice per hit is what
    // used to make coincidence detection too expensive to leave on.
    //
    // `repFace` is the face that point sits on, and it is what lets the front-facing
    // rule work for BODIES as well as faces: a body's representative point is the
    // point on its surface nearest the ray, so the face owning it is the surface the
    // ray is looking at. Two bodies meeting at a non-merged interface hand back the
    // same point and the two opposite-facing halves of that interface.
    private static double RayAxisDistance(object obj, Ray ray, out double[]? repPoint,
                                          out Face2? repFace) {
        repPoint = null;
        repFace = null;
        double[]? point;
        switch (obj) {
            case Edge e: {
                // Aim point: the point on the edge nearest the ray ORIGIN (which sits a
                // hair off the seed we aimed at) — NOT the parameter-midpoint. On a long
                // or curved edge the midpoint can be many mm from where the ray actually
                // crosses, which made the clearance metric garbage (tgt distances of 11mm
                // on caught edges). This makes targetDist ~0 for the aimed entity and the
                // true ray-distance for competitors.
                var curve = (Curve?)e.IGetCurve();
                if (curve is null) { point = null; break; }
                var cp = (double[])curve.GetClosestPointOn(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
                point = cp.Length >= 3 ? new[] { cp[0], cp[1], cp[2] } : null;
                break;
            }
            case Vertex v:
                point = (double[])v.GetPoint();
                break;
            case Face2 f: {
                // SW quirk: Face2.GetClosestPointOn snaps an arbitrary 3D probe
                // onto the trimmed face, returning a true on-face point. Using
                // ray.Origin biases toward the near side of the face (which is
                // where the ray meant to land anyway), so the resulting
                // distance is a faithful "how close does this face sit to the
                // intended ray".
                point = (double[])f.GetClosestPointOn(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
                repFace = f;
                break;
            }
            case Body2 b: {
                // A body's representative is the point on its SURFACE nearest the ray axis — NOT
                // its mass-centroid, which is degenerate on coaxial bodies (every centroid sits on
                // the shared axis, so all distances collapse to ~0 and the tie-break picks a random
                // sibling). Take each face's closest-to-origin point and keep the one nearest the
                // axis: the body the ray is aimed at has a surface point right on the axis; siblings
                // don't. Same "where does this entity sit relative to the aimed ray" metric as
                // Face2, lifted to the whole body. Return directly (the min is already a distance).
                if (b.GetFaces() is not object[] faces) return double.PositiveInfinity;
                var best = double.PositiveInfinity;
                foreach (var fo in faces) {
                    if (fo is not Face2 fc) continue;
                    var fp = (double[])fc.GetClosestPointOn(ray.Origin.X, ray.Origin.Y, ray.Origin.Z);
                    if (fp.Length < 3) continue;
                    var fd = PointAxisDistance(fp[0], fp[1], fp[2], ray);
                    if (fd >= best) continue;
                    // Keep the winner, not just its distance: the point makes body
                    // coincidence detectable at all, and the face it lies on is what the
                    // front-facing rule orders two coincident bodies by.
                    best = fd; repPoint = fp; repFace = fc;
                }
                return best;
            }
            default:
                point = null;
                break;
        }
        if (point is null || point.Length < 3) return double.PositiveInfinity;
        repPoint = point;
        return PointAxisDistance(point[0], point[1], point[2], ray);
    }

    // Perpendicular distance from a point to the ray's infinite axis line.
    // v = point - origin; closest = origin + (v·d) d; distance = |v - (v·d) d|.
    private static double PointAxisDistance(double px, double py, double pz, Ray ray) {
        var ox = ray.Origin.X; var oy = ray.Origin.Y; var oz = ray.Origin.Z;
        var dx = ray.Direction.X; var dy = ray.Direction.Y; var dz = ray.Direction.Z;
        var dlen = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dlen <= 0) return double.PositiveInfinity;
        dx /= dlen; dy /= dlen; dz /= dlen;
        var vx = px - ox; var vy = py - oy; var vz = pz - oz;
        var t = vx * dx + vy * dy + vz * dz;
        var rx = vx - t * dx; var ry = vy - t * dy; var rz = vz - t * dz;
        return Math.Sqrt(rx * rx + ry * ry + rz * rz);
    }


    // Among hits at one point, the entity the ray meets head-on: the one whose outward
    // normal most opposes the ray direction. Returns null when the rule cannot decide —
    // a member with no representative face, one with no single normal (cylinder,
    // b-surface), or two whose normals tie — so the caller reports ambiguity instead of
    // inventing an order.
    //
    // Applies to FACES and BODIES. A face is ordered by its own normal; a body by the
    // normal of the face its representative point lies on, which for a non-merged
    // interface is that body's half of the shared surface. Edges and vertices have no
    // normal and no analogue: two coincident edges are the SAME curve in space, so no
    // ray can separate them and `on_body` is the only answer.
    private static object? FrontFacingPick(
            List<(object Obj, double Dist, double[]? Point, Face2? Face)> group,
            Ray ray, string entityType) {
        if (entityType != "face" && entityType != "body") return null;
        var dx = ray.Direction.X; var dy = ray.Direction.Y; var dz = ray.Direction.Z;
        var dlen = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dlen <= 0) return null;
        dx /= dlen; dy /= dlen; dz /= dlen;

        object? pick = null;
        var bestDot = double.PositiveInfinity;
        var tied = 0;
        foreach (var h in group) {
            if (h.Face is null || PlaneNormal(h.Face) is not { } n) return null;
            var dot = n[0] * dx + n[1] * dy + n[2] * dz;
            if (dot < bestDot - Flags.GeometryTolerance) {
                bestDot = dot; pick = h.Obj; tied = 1;
            } else if (Math.Abs(dot - bestDot) <= Flags.GeometryTolerance) {
                tied++;
            }
        }
        return tied == 1 ? pick : null;
    }

    // A plane's outward normal, or null for anything else. This is the one face property
    // DefinitionCapture reads verbatim off the COM object — `(double[])face.Normal`, gated
    // on `surface.IsPlane()` because IFace2.Normal evaluates at an unspecified parameter
    // and is only reliable for planes — so reading it directly here cannot drift from what
    // the resolver matches on. Deliberately NOT a full Capture: that computes the parent
    // body's mass properties, and ScoreProbeGrid fires thousands of candidate rays through
    // this path.
    private static double[]? PlaneNormal(Face2 f) {
        if ((Surface?)f.IGetSurface() is not { } surface || !surface.IsPlane()) return null;
        var n = (double[])f.Normal;
        return n.Length >= 3 ? n : null;
    }

    private static bool PointsCoincide(double[] a, double[] b) =>
        Math.Abs(a[0] - b[0]) <= CoincidentPointTol
        && Math.Abs(a[1] - b[1]) <= CoincidentPointTol
        && Math.Abs(a[2] - b[2]) <= CoincidentPointTol;

    // The body that owns a hit, as a Definition, so it can be matched against a
    // caller-supplied `on_body` scope.
    private static Definition? OwningBody(object obj) => obj switch {
        Face2 f when f.IGetBody() is Body2 b => DefinitionCapture.Capture(b),
        Edge e when e.GetBody() is Body2 b   => DefinitionCapture.Capture(b),
        _ => null,
    };

    private static Definition? CaptureSelectedObject(object obj, string entityType) {
        return entityType switch {
            "face"   when obj is Face2 f      => DefinitionCapture.Capture(f),
            "edge"   when obj is Edge e       => DefinitionCapture.Capture(e),
            "vertex" when obj is Vertex v     => DefinitionCapture.Capture(v),
            "body"   when obj is Body2 b      => DefinitionCapture.Capture(b),
            "plane"  when obj is Feature feat => DefinitionCapture.Capture(feat),
            _ => null,
        };
    }

    // Synthesize a Ray that, when fired through Probe(ray, entity_type), lands on the
    // live entity identified by `definition`. Used by the round-trip runner to swap
    // source-captured Definitions for target-captured ones: source generates a probe,
    // target.probe(ray) re-captures the corresponding entity using the target's
    // own geometry, and the resolver downstream sees Definitions whose tolerances
    // match the body that produced them — sidestepping SW's source-vs-target drift.
    //
    // Recipe per entity flavor:
    //   face   — random (u,v) on the live face, evaluate point + outward normal,
    //            pad along normal, cast back along -normal.
    //   edge   — edge midpoint, normal of first adjacent face at that midpoint,
    //            pad along normal, cast back along -normal.
    //   vertex — vertex point, normal of first adjacent face at that point,
    //            pad along normal, cast back along -normal.
    //   body   — body centroid, cardinal outward from bbox (+x by default),
    //            pad along that direction, cast back along -direction.
    //
    // After each candidate ray we re-fire it through the same SelectByRay path
    // Select uses and verify the captured Definition equals the input (record-equality,
    // since Definitions are records). On mismatch we resample / perturb and retry.
    // After MaxRetries the method throws — "we should 100% be able to build all probes";
    // exhaustion is a real signal, not a fallback condition.
    public JsonNode GenerateProbe(JsonNode definitionNode, string entityType, int startAttempt = 0) {
        using var _ = MassProperties.Push(_modelDoc);
        var def = Definition.FromJson(definitionNode)
            ?? throw new ArgumentException("GenerateProbe: malformed definition payload");

        // Resolve through the existing per-type matcher so we always probe the SAME live
        // entity the runner would have resolved had the Definition been used directly.
        // A null resolve here is a real signal — the captured parent_body fingerprint
        // doesn't match any live body in source's rolled-back state. Don't silently
        // strip parent_body and walk all bodies; that masks a fix-worthy root cause
        // (capture timing, rollback drift, etc.).
        var live = DefinitionResolver.Resolve(this, def)
            ?? throw new InvalidOperationException(
                $"GenerateProbe: definition did not resolve to a live entity: {def.ToJson().ToJsonString()}");

        // Edge / face / vertex / BODY all go through the deterministic, clearance-scored grid
        // (TryGenerateScoredProbe): seed points on the entity's OWN surface, fire a ray at each,
        // keep the ones that select the target, rank by clearance, bake a tight replay radius. A
        // body seeds across its faces and tie-breaks by nearest-surface-point (see SampleSeedPoint
        // + RayAxisDistance) — so the ray is aimed AT the body, not cast through its centroid (which
        // is degenerate on coaxial bodies). The scored path returns null only when its fixed grid
        // found no verifying ray, in which case we fall through to the legacy cardinal cycle below
        // so we never regress to "can't probe this entity at all".
        if (entityType is "edge" or "face" or "vertex" or "body") {
            var scored = TryGenerateScoredProbe(live, entityType, def, startAttempt);
            if (scored is not null) return scored;
            SldworksLog.Information(
                "GenerateProbe[{Kind}] scored grid found no verifying ray (startAttempt={Start}); falling back to legacy cycle",
                def.GetType().Name, startAttempt);
        }

        // Per-kind retry budget. Bodies get the largest budget because their probe
        // ray has to start outside the bbox and pass through the body, so the
        // cardinal direction × jittered-centroid combinatorics need more shots —
        // and a wrong-body hit on a multi-body part can only be disambiguated by
        // trying a different cardinal direction or a different jittered cast
        // origin. Faces / edges / vertices benefit from finer quantization (more
        // attempts → finer mm grid) but plateau at ~30 attempts (decimalsToRound
        // saturates at 15, the double-precision limit).
        var MaxRetries = entityType == "body" ? 200 : 100;
        // Seed deterministically per call so re-running on the same model is reproducible
        // (helps debugging when a particular face refuses to probe).
        var rng = new Random(definitionNode.ToJsonString().GetHashCode());
        const double Padding = 0.00005;  // 0.05 mm — half the Select cylinder radius, so the ray origin sits well inside the catchment without crossing unrelated geometry on the way to the target point.

        // Capture the last (attempt, candidate, hitJson) tuple seen so we can report
        // what the verify loop actually saw on exhaustion — without this, "50 attempts
        // failed" is uninvestigable without a debugger attached.
        Ray? lastRay = null;
        JsonNode? lastHitJson = null;
        var nullSelects = 0;
        var nullCandidates = 0;
        var wrongHits = 0;

        // `startAttempt` lets the runner regenerate a probe that target's
        // approx-match rejected by skipping past previously-tried cardinals /
        // sample seeds. Each new call to GenerateProbe with a higher
        // startAttempt produces a structurally different ray (different
        // cardinal direction for body, different sample point for face, etc.)
        // so the runner can iterate on probe drift without retrying the same
        // failing ray.
        for (var attempt = startAttempt; attempt < startAttempt + MaxRetries; attempt++) {
            // Every per-flavor builder takes `attempt` so retries explore a different
            // sample / adjacent face / cardinal direction; without this an edge/vertex/body
            // probe that misses on attempt 0 would loop forever with the same ray.
            Ray? candidate = (live, entityType) switch {
                (Face2 f,  "face")   => BuildFaceProbe(f, rng, attempt, Padding),
                (Edge e,   "edge")   => BuildEdgeProbe(e, rng, attempt, Padding),
                (Vertex v, "vertex") => BuildVertexProbe(v, attempt, Padding),
                (Body2 b,  "body")   => BuildBodyProbe(b, attempt, Padding),
                _ => throw new InvalidOperationException(
                    $"GenerateProbe: live entity {live.GetType().Name} does not match entityType '{entityType}'"),
            };
            // Fail-fast on null candidate. The per-kind builders return null
            // only on deterministic SW-API failures (no adjacent face, surface
            // EvaluateAtPoint returns null on cone apex, etc.) that repeat the
            // same way on every retry — looping 100x against the same null
            // wastes ~50s of SW round-trips for nothing. The throw includes
            // the input Def so the runner's ADD_FAIL surfaces which entity.
            if (candidate is null) {
                throw new InvalidOperationException(
                    $"GenerateProbe: per-kind builder returned null on attempt {attempt} for {def.GetType().Name}; " +
                    $"likely no adjacent face / unsupported surface eval / degenerate geometry. " +
                    $"payload={def.ToJson().ToJsonString()}");
            }
            lastRay = candidate;

            // Source-verify: fire the candidate through the same SelectByRay sweep
            // the target will use, capture the hit, and require record-equality
            // against the input. Misses (null) and wrong-entity hits both retry.
            var hitJson = Probe(SerializeRay(candidate), entityType);
            if (hitJson is null) { nullSelects++; continue; }
            lastHitJson = hitJson;
            var hitDef = Definition.FromJson(hitJson);
            if (hitDef is null) { wrongHits++; continue; }
            // Re-capture from `live` so both Defs come from the same context
            // (current source state) — no cross-context mass-props drift.
            // Identical entities then bit-match on geometric fields and match
            // on parent_body within 1e-7 noise from consecutive
            // GetMassProperties calls.
            var liveDef = DefinitionCapture.Capture(live);
            if (liveDef is null) { wrongHits++; continue; }
            if (DefinitionMatch.Exact(hitDef, liveDef)) {
                return SerializeRay(candidate);
            }
            wrongHits++;
        }

        SldworksLog.Information(
            "GenerateProbe[{Kind}] EXHAUSTED after {Retries} attempts " +
            "(nullCandidates={NC}, nullSelects={NS}, wrongHits={WH}); " +
            "last_ray={Ray}, last_hit={Hit}, expected={Expected}",
            def.GetType().Name, MaxRetries, nullCandidates, nullSelects, wrongHits,
            lastRay is null ? "null" : SerializeRay(lastRay).ToJsonString(),
            lastHitJson?.ToJsonString() ?? "null",
            def.ToJson().ToJsonString());

        throw new InvalidOperationException(
            $"GenerateProbe: failed to synthesize a verifying ray for {def.GetType().Name} " +
            $"after {MaxRetries} attempts (nullCandidates={nullCandidates}, " +
            $"nullSelects={nullSelects}, wrongHits={wrongHits}); " +
            $"payload={def.ToJson().ToJsonString()}");
    }

    internal static JsonNode SerializeRay(Ray ray) =>
        JsonSerializer.SerializeToNode(ray, Definition.JsonOptions)
            ?? throw new InvalidOperationException("GenerateProbe: failed to serialize Ray");

    // Probe-generation tuning.
    private const int ProbeCardinalCap = 26;           // cardinals with magnitude <= sqrt(3): axis (6) + face-diag (12) + space-diag (8)
    private const double ProbePadding = 0.00005;       // 0.05 mm — lifts the ray origin just off the entity
    // BIG selection window: scoring fires here so it sees EVERY nearby competitor and
    // can pick a genuinely clear ray. A tight window would hide a competitor sitting
    // just outside it and re-pick the ambiguous ray MultiSelect exists to avoid. Only
    // the SELECTION uses this width — replay fires the much smaller baked radius below.
    private const double ProbeTestRadius = 0.0005;     // 0.5 mm
    private const double ProbeMaxBakeRadius = 0.00015; // 0.15 mm — cap on the baked replay radius (normally far smaller, midway to the true competitor)
    private const double DefaultProbeRadius = 0.0001;  // 0.1 mm — Probe's fallback Select radius when a ray carries none
    // "Not crowded" = the nearest competitor is effectively outside the selection
    // window (clearance approaches the window width). Above this we keep coarse/clean
    // origins; below it we drop a quant step (finer grid, smaller baked radius) and
    // retry -- the more crowding, the less we quantize.
    private const double ProbeNotCrowded = 0.00045;    // 0.45 mm (0.9 x window)
    // Margin each side of the baked replay radius for the "robustly bakeable" early-exit:
    // the target must sit this far inside the baked radius and the competitor this far
    // outside it, so the probe survives source→target rebuild drift (~1e-5 m on edges).
    private const double ProbeBakeMargin = 0.00003;    // 0.03 mm

    // Seed-quant ladder, COARSEST first. Few levels: the dense uniform seed grid does
    // the exploring; quant only needs coarse (clean origins) vs fine (keep the digits
    // that separate a crowded target from its neighbor). step 2 = 0.1mm, step 4 = 0.01mm.
    private static readonly int[] _quantSteps = { 2, 4 };

    // Uniform seed grid: sample the entity parameter space at fixed 0.05 steps
    // (ProbeSeedsPerDim per dim), avoiding endpoints. Edges (1D) = 20 seeds; faces (2D)
    // = 20x20 = 400. Replaces the prime-subdivision sequence; the early-exit means we
    // usually only touch the first seed anyway.
    private const int ProbeEdgeSeeds = 20;   // 1D edge: 20 samples along the curve (0.05 step)
    private const int ProbeFaceDim = 8;      // 2D face: 8x8 = 64 samples (kept small — faces rarely crowded; 400 was too slow)
    private static int SeedCount(string entityType) => entityType switch {
        "edge" => ProbeEdgeSeeds,
        "face" => ProbeFaceDim * ProbeFaceDim,
        "body" => ProbeFaceDim * ProbeFaceDim,  // sample points spread across the body's own faces
        _ => 1,  // vertex: single seed
    };
    private static double SeedFrac(int i, int n) => (i + 0.5) / n;  // centered samples, endpoints avoided

    // Deterministic, clearance-scored probe search for edge/face/vertex. Sweeps a
    // fixed grid of (prime-subdivided seed point) x (cardinal direction, magnitude
    // <= sqrt(3)), fires each candidate once at a generous test radius, and keeps
    // every candidate whose closest-to-axis hit IS the target (record-equality).
    // Survivors are ranked by CLEARANCE — the gap between the target and the
    // nearest competing entity along the ray — and the `rank`-th best is returned
    // (rank = startAttempt lets the runner ask for the next-best ray when target's
    // approx-match rejects one; past the last survivor it returns null so the
    // caller falls back to the legacy random cycle). The winner is baked with a
    // tight replay radius sized to a fraction of its clearance: small ("start as
    // small as possible") but with margin so float-noise edge drift on replay
    // still lands. Returns null if no candidate verified.
    //
    // We do NOT prefer the lowest-magnitude cardinal: a 45-degree ray that sits in
    // open space beats an axis ray that grazes a neighbor. Clearance decides.
    private JsonNode? TryGenerateScoredProbe(object live, string entityType, Definition def, int rank) {
        var liveDef = DefinitionCapture.Capture(live);
        if (liveDef is null) return null;

        // BIG WINDOW for selection (always ProbeTestRadius): the scorer sees EVERY
        // nearby competitor and picks a genuinely clear ray (a tight window hides one
        // just outside it and re-picks the ambiguous ray).
        //
        // Seed quant adapts to crowding: walk the ladder COARSE→FINE and stop at the
        // coarsest grid whose clearance comfortably exceeds the grid size, so the snap
        // can't shift the seed across the gap. An isolated target keeps clean coarse
        // origins; a crowded one falls through to the finest grid (keep the digits that
        // separate it from its neighbor).
        (Ray Ray, double Clearance, double TargetDist, double CompetitorDist) pick = default;
        var picked = false;
        var pickHits = 0;
        var pickQuant = 0;
        foreach (var quantStep in _quantSteps) {
            var scored = ScoreProbeGrid(live, entityType, liveDef, quantStep, ProbeTestRadius, out var maxHits);
            if (scored.Count <= rank) continue;
            // Stable sort so exact-clearance ties break by insertion order (deterministic).
            pick = scored.OrderByDescending(x => x.Clearance).ToList()[rank];
            picked = true; pickHits = maxHits; pickQuant = quantStep;
            if (pick.Clearance > ProbeNotCrowded) break;  // not crowded — done; else drop a quant step
        }
        if (!picked || pick.Ray is null) return null;
        // GO DOWN on radius: bake far smaller than the window — midway between the pick's
        // target and its TRUE nearest competitor. Above targetDist (target caught), below
        // competitorDist (competitor excluded); shrinks automatically with the crowding.
        var replayRadius = Math.Clamp((pick.TargetDist + pick.CompetitorDist) / 2, 2e-5, ProbeMaxBakeRadius);
        // `cmp` is what makes a zero clearance readable: clr == 0 with cmp == 0 means a
        // competitor sits ON the axis (the grid scanned everything for nothing), whereas
        // a large cmp means the ray is genuinely clear.
        SldworksLog.Information(
            "GenerateProbe[{Kind}] q={Q} maxHits={MaxN}; rank={Rank} clr={Clr:E2}m tgt={Tgt:E2}m cmp={Cmp:E2}m radius={R:E2}m dir=({X:G3},{Y:G3},{Z:G3})",
            def.GetType().Name, pickQuant, pickHits, rank, pick.Clearance, pick.TargetDist,
            pick.CompetitorDist, replayRadius,
            pick.Ray.Direction.X, pick.Ray.Direction.Y, pick.Ray.Direction.Z);
        return SerializeRay(pick.Ray with { Radius = replayRadius });
    }

    // Score every (uniform-grid seed x cardinal) candidate at one scale: fire each
    // at `testRadius`, keep only candidates whose closest-to-axis pick IS the target
    // (record-equality), and rank by clearance (gap to the nearest competing entity).
    // Walks the whole seed list, stopping early once a clearly isolated hit is found.
    // `quantStep` sets the seed-snap grid for this scale.
    private List<(Ray Ray, double Clearance, double TargetDist, double CompetitorDist)> ScoreProbeGrid(
            object live, string entityType, Definition liveDef, int quantStep, double testRadius, out int maxHits) {
        maxHits = 0;
        var cardinalCount = Math.Min(_cardinals.Length, ProbeCardinalCap);
        var scored = new List<(Ray Ray, double Clearance, double TargetDist, double CompetitorDist)>();
        var bestClearance = 0.0;
        var bestTargetDist = 0.0;
        var bestCompetitorDist = 0.0;
        // Walk the uniform seed grid, but stop early once we have a ray whose BAKED probe
        // robustly separates the target from its nearest competitor. Most entities resolve on
        // the first seed; only crowded ones walk deeper. (The old gate waited for an absolute
        // 0.45mm clearance gap, which tightly-spaced geometry can never provide — so it scanned
        // the entire 20×26×2 grid and blew past the 600s transport timeout. A ~0.1mm gap is
        // already trivially bakeable; there's no reason to keep searching for a bigger one.)
        var seedCount = SeedCount(entityType);
        for (var s = 0; s < seedCount; s++) {
            var seedPt = SampleSeedPoint(live, entityType, s);
            if (seedPt is null) continue;
            var (qx, qy, qz) = QuantizePointMmAligned(seedPt[0], seedPt[1], seedPt[2], quantStep);
            for (var c = 0; c < cardinalCount; c++) {
                var cardinal = _cardinals[c];
                var candidate = MakePaddedRay(qx, qy, qz, cardinal.X, cardinal.Y, cardinal.Z, ProbePadding);
                var hit = SelectByRayDetailed(candidate, entityType, testRadius);
                if (hit.CountAll > maxHits) maxHits = hit.CountAll;
                if (hit.Def is null) continue;
                if (!DefinitionMatch.Exact(hit.Def, liveDef)) continue;
                var targetDist = hit.SortedDistances.Count > 0 ? hit.SortedDistances[0] : 0.0;
                var competitorDist = hit.CountAll > 1 && hit.SortedDistances.Count > 1
                    ? hit.SortedDistances[1]
                    : testRadius;  // no competitor inside the test cylinder
                var clearance = competitorDist - targetDist;
                scored.Add((candidate, clearance, targetDist, competitorDist));
                if (clearance > bestClearance) {
                    bestClearance = clearance;
                    bestTargetDist = targetDist;
                    bestCompetitorDist = competitorDist;
                }
            }
            if (bestClearance > 0 && RobustlyBakeable(bestTargetDist, bestCompetitorDist)) break;
        }
        return scored;
    }

    // True iff a probe baked at the midpoint radius cleanly separates the target from its
    // nearest competitor with margin to survive source→target rebuild drift. This is the real
    // "good enough" test for stopping the grid scan: a crowded entity whose competitor is only
    // ~0.1mm away is still trivially bakeable (the baked radius clamps far below that gap), so
    // the search stops the moment such a ray is found — instead of scanning the whole grid for
    // an absolute-clearance gap that tightly-spaced geometry can never provide.
    private static bool RobustlyBakeable(double targetDist, double competitorDist) {
        var baked = Math.Clamp((targetDist + competitorDist) / 2, 2e-5, ProbeMaxBakeRadius);
        return baked - targetDist > ProbeBakeMargin && competitorDist - baked > ProbeBakeMargin;
    }

    // Deterministic seed point on the entity for the scored search. Edges/faces use
    // the uniform seed grid (SeedParam) so the Nth seed is reproducible;
    // vertices have a single point (seed index ignored). Returns null on a
    // deterministic SW-API failure (e.g. no surface) so the caller skips that seed.
    private static double[]? SampleSeedPoint(object live, string entityType, int seedIdx) {
        switch (live, entityType) {
            case (Edge e, "edge"): {
                var cpd = e.GetCurveParams3();
                var startU = cpd.UMinValue;
                var endU = cpd.UMaxValue;
                if (!cpd.Sense) {
                    if (startU > endU) { var tmp = startU; startU = -endU; endU = -tmp; }
                    else { (startU, endU) = (endU, startU); }
                }
                var t = SeedFrac(seedIdx, ProbeEdgeSeeds);
                var u = startU + t * (endU - startU);
                return (double[])e.Evaluate2(u, 0);
            }
            case (Face2 f, "face"):
                return SampleFacePoint(f, seedIdx);
            case (Body2 b, "body"): {
                // Probe a body by aiming at points on its OWN surface (its faces), so the ray
                // hits the body — NOT by casting through its centroid (degenerate on concentric/
                // coaxial bodies, where every body's centroid sits on the shared axis). Spread
                // seeds: face by the low-order index, UV point on that face by the high-order one.
                if (b.GetFaces() is not object[] faces || faces.Length == 0) return null;
                if (faces[seedIdx % faces.Length] is not Face2 face) return null;
                return SampleFacePoint(face, seedIdx / faces.Length);
            }
            case (Vertex v, "vertex"):
                return (double[])v.GetPoint();
            default:
                throw new InvalidOperationException(
                    $"SampleSeedPoint: live {live.GetType().Name} does not match entityType '{entityType}'");
        }
    }

    // Deterministic UV-grid point on a face, snapped onto its trimmed extent. Shared by the
    // face seed path and the body seed path (a body samples points across each of its faces).
    private static double[]? SampleFacePoint(Face2 f, int seedIdx) {
        var surface = (Surface?)f.IGetSurface();
        if (surface is null) return null;
        var uv = (double[])f.GetUVBounds();
        var tu = SeedFrac(seedIdx % ProbeFaceDim, ProbeFaceDim);
        var tv = SeedFrac(seedIdx / ProbeFaceDim, ProbeFaceDim);
        var u = uv[0] + tu * (uv[1] - uv[0]);
        var v = uv[2] + tv * (uv[3] - uv[2]);
        var pt = (double[])surface.Evaluate(u, v, 0, 0);
        // SW quirk: non-convex (L-shaped / donut) faces have UV samples that fall in holes;
        // GetClosestPointOn snaps them onto the trimmed extent.
        return (double[])f.GetClosestPointOn(pt[0], pt[1], pt[2]);
    }

    // Body-only probe builder for the BodiesToKeepScope flow: the body is a
    // live Body2 that exists transiently inside a PromptBodiesToKeepNotify
    // callback, so it can't be resolved via DefinitionResolver (its parent_body
    // fingerprint doesn't match any committed body in the part). We pick the
    // ray geometrically without the SelectByRay verify loop — SelectByRay
    // semantics during a mid-rebuild notify aren't documented, and target uses
    // a pure-geometric "closest body to ray axis" pick anyway, so the
    // verification doesn't reflect target's resolution path.
    //
    // To maximize target disambiguation when many fragments cluster (e.g. a
    // cut produces N similarly-sized pieces), we iterate cardinals and pick
    // the first one where `body` is unambiguously the closest body to the ray
    // axis among `siblings` (the full allBodies list including `body` itself)
    // by at least `_BodyProbeSeparationTol`. Bails out at the first such ray
    // rather than searching exhaustively. Falls back to attempt 0 if no
    // cardinal disambiguates — that's still better than mass-props matching
    // because it gives target a specific geometric target to aim at.
    private const double _BodyProbeSeparationTol = 0.0005;  // 0.5 mm margin between intended hit and runner-up.
    private const int _BodyProbeMaxAttempts = 40;

    internal static Ray BuildBodyProbeAgainstSiblings(Body2 body, IReadOnlyList<Body2> siblings) {
        const double Padding = 0.00005;
        Ray? fallback = null;
        for (var attempt = 0; attempt < _BodyProbeMaxAttempts; attempt++) {
            var candidate = BuildBodyProbe(body, attempt, Padding);
            if (candidate is null) continue;
            fallback ??= candidate;
            var bestDist = double.PositiveInfinity;
            var bestIsTarget = false;
            var runnerUpDist = double.PositiveInfinity;
            foreach (var sib in siblings) {
                if (sib is null) continue;
                var (sibCentroid, _, _) = MassProperties.Compute(sib);
                var dist = PointToRayDistance(sibCentroid.X, sibCentroid.Y, sibCentroid.Z, candidate);
                if (dist < bestDist) {
                    runnerUpDist = bestDist;
                    bestDist = dist;
                    bestIsTarget = ReferenceEquals(sib, body);
                } else if (dist < runnerUpDist) {
                    runnerUpDist = dist;
                }
            }
            if (bestIsTarget && (runnerUpDist - bestDist) >= _BodyProbeSeparationTol) {
                return candidate;
            }
        }
        return fallback ?? throw new InvalidOperationException(
            "BuildBodyProbeAgainstSiblings: all cardinal attempts returned null");
    }

    private static double PointToRayDistance(double px, double py, double pz, Ray ray) {
        var ox = ray.Origin.X; var oy = ray.Origin.Y; var oz = ray.Origin.Z;
        var dx = ray.Direction.X; var dy = ray.Direction.Y; var dz = ray.Direction.Z;
        var dlen = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (dlen <= 0) return double.PositiveInfinity;
        dx /= dlen; dy /= dlen; dz /= dlen;
        var vx = px - ox; var vy = py - oy; var vz = pz - oz;
        var t = vx * dx + vy * dy + vz * dz;
        var rx = vx - t * dx; var ry = vy - t * dy; var rz = vz - t * dz;
        return Math.Sqrt(rx * rx + ry * ry + rz * rz);
    }

    // Target-side counterpart to BuildBodyProbeAgainstSiblings: given a ray
    // built on source and the live `allBodies` array passed into the
    // PromptBodiesToKeepNotify callback, return the body whose centroid is
    // closest to the ray axis. Throws when no body is within `radius` of the
    // ray axis (catches probe-vs-target-geometry drift loudly rather than
    // silently picking a wrong body).
    internal static Body2 ResolveBodyByRay(IReadOnlyList<Body2> allBodies, Ray ray) {
        Body2? best = null;
        var bestDist = double.PositiveInfinity;
        foreach (var b in allBodies) {
            if (b is null) continue;
            var (centroid, _, _) = MassProperties.Compute(b);
            var dist = PointToRayDistance(centroid.X, centroid.Y, centroid.Z, ray);
            if (dist < bestDist) {
                bestDist = dist;
                best = b;
            }
        }
        if (best is null) {
            throw new InvalidOperationException(
                $"ResolveBodyByRay: allBodies is empty; ray={SerializeRay(ray).ToJsonString()}");
        }
        // Body bbox diagonals run ~tens of mm; if the closest centroid is
        // >10 mm off the ray axis, the source ray almost certainly doesn't
        // describe any of these target fragments — fail loudly.
        const double MaxBodyRayMissTol = 0.01;
        if (bestDist > MaxBodyRayMissTol) {
            throw new InvalidOperationException(
                $"ResolveBodyByRay: closest body centroid is {bestDist:G4} m from ray axis " +
                $"(threshold {MaxBodyRayMissTol} m) — source probe ray does not align with any target body. " +
                $"ray={SerializeRay(ray).ToJsonString()}");
        }
        return best;
    }

    // The 124-cardinal grid: every nonzero vector in {-2,-1,0,1,2}³. Sorted by
    // ascending magnitude so axis-aligned directions come first (most likely to
    // hit a face/edge cleanly), then face-diagonals, space-diagonals, and the
    // skewed (2, ±1, 0) variants last. We do NOT pre-bias toward surface normals
    // or edge tangents anymore: coaxial cylinders share a radial normal, axis-
    // aligned edges share a face-normal, and the "natural" cardinal frequently
    // aims the ray through multiple candidates at once. Brute-cycling cardinals
    // disambiguates by direction alone.
    //
    // Note: (2,0,0) and (1,0,0) normalize to the same direction. Those duplicates
    // are wasted attempts but cheap (the verify loop is what costs time, not the
    // ray math), and matching the user-described {0, ±1, ±2}³ literally is more
    // important than micro-optimizing the grid.
    private static readonly (double X, double Y, double Z)[] _cardinals = BuildCardinalGrid();

    private static (double, double, double)[] BuildCardinalGrid() {
        var values = new[] { -2.0, -1.0, 0.0, 1.0, 2.0 };
        var list = new List<((double dx, double dy, double dz) dir, double mag)>(124);
        foreach (var cx in values) {
            foreach (var cy in values) {
                foreach (var cz in values) {
                    if (cx == 0 && cy == 0 && cz == 0) continue;
                    var mag = Math.Sqrt(cx * cx + cy * cy + cz * cz);
                    list.Add(((cx, cy, cz), mag));
                }
            }
        }
        list.Sort((a, b) => a.mag.CompareTo(b.mag));
        return list.Select(e => e.dir).ToArray();
    }

    // Unified per-point cardinal probe. Per attempt: pick the seed point on the
    // entity (kind-specific), pick the next cardinal direction from `_cardinals`,
    // quantize the point, and cast a 5 mm-padded ray. Quantize precision refines
    // every full cardinal cycle (124 attempts).
    private static Ray BuildPointCardinalProbe(double px, double py, double pz, int attempt, double paddingMeters) {
        var (dx, dy, dz) = _cardinals[attempt % _cardinals.Length];
        var quantSteps = 1 + attempt / _cardinals.Length;
        var (qx, qy, qz) = QuantizePointMmAligned(px, py, pz, quantSteps);
        return MakePaddedRay(qx, qy, qz, dx, dy, dz, paddingMeters);
    }

    // Sample a random (u, v) on the face, snap into the trimmed extent
    // (donut / L-shaped faces have UV holes), and hand off to the unified
    // cardinal-cycle probe.
    private static Ray? BuildFaceProbe(Face2 face, Random rng, int attempt, double paddingMeters) {
        var surface = (Surface?)face.IGetSurface();
        if (surface is null) return null;
        var uv = (double[])face.GetUVBounds();
        double minU = uv[0], maxU = uv[1], minV = uv[2], maxV = uv[3];
        // Random UV every attempt. The UV midpoint sounded stabler, but
        // for L-shaped / donut faces the midpoint falls in a hole and
        // GetClosestPointOn snaps it onto the trim boundary — every
        // attempt produces the same boundary-near point, and the ray
        // gets resolved onto a NEIGHBOR face on rebuild, captured as if
        // it were the intended face. Downstream features (8352T45_8352T27
        // idx 42 CircPattern body) then resolve against the wrong body and
        // approx-match fails. Random sampling avoids the snap-to-edge
        // failure mode.
        var u = minU + rng.NextDouble() * (maxU - minU);
        var v = minV + rng.NextDouble() * (maxV - minV);
        var seed = (double[])surface.Evaluate(u, v, 0, 0);
        // SW quirk: non-convex (L-shaped / donut) faces have UV samples that
        // fall in holes — GetClosestPointOn snaps them onto the trimmed extent.
        var snapped = (double[])face.GetClosestPointOn(seed[0], seed[1], seed[2]);
        return BuildPointCardinalProbe(snapped[0], snapped[1], snapped[2], attempt, paddingMeters);
    }

    // Resample a random U along the edge per attempt → cardinal cycle. Random
    // U (clamped away from endpoints) avoids the failure mode where a short
    // edge shares its endpoint with a neighbor edge: a fixed-midpoint probe
    // with a cardinal pointing along the shared axis hits both edges at the
    // shared vertex, and SW arbitrarily picks the neighbor every retry. Fresh
    // U each attempt moves the aim away from the junction.
    private static Ray? BuildEdgeProbe(Edge edge, Random rng, int attempt, double paddingMeters) {
        var cpd = edge.GetCurveParams3();
        var startU = cpd.UMinValue;
        var endU = cpd.UMaxValue;
        if (!cpd.Sense) {
            if (startU > endU) { var tmp = startU; startU = -endU; endU = -tmp; }
            else { (startU, endU) = (endU, startU); }
        }
        // Sample U inside [0.2, 0.8] of the curve range so endpoints (and their
        // shared vertices with neighbors) stay outside the ray's catchment.
        var t = 0.2 + 0.6 * rng.NextDouble();
        var u = startU + t * (endU - startU);
        var pt = (double[])edge.Evaluate2(u, 0);
        return BuildPointCardinalProbe(pt[0], pt[1], pt[2], attempt, paddingMeters);
    }

    private static Ray? BuildVertexProbe(Vertex vertex, int attempt, double paddingMeters) {
        var p = (double[])vertex.GetPoint();
        return BuildPointCardinalProbe(p[0], p[1], p[2], attempt, paddingMeters);
    }

    // Operates in mm-aligned
    // precision regardless of wire units (the coincident wire is meters,
    // so we scale to mm, quantize, scale back). Quantization grid by step:
    //   step=1: nearest 0.5 mm
    //   step=2: nearest 0.1 mm
    //   step=3: nearest 0.05 mm
    //   step=4: nearest 0.01 mm
    //   step=5: nearest 0.005 mm
    //   step=6: nearest 0.001 mm
    //   ...
    // Coarse-first → fine, so the first retry lands the ray on design-intent
    // coordinates (parts authored at integer / half-mm grids); later retries
    // refine if the entity sits off-grid.
    private static (double, double, double) QuantizePointMmAligned(double xMeters, double yMeters, double zMeters, int steps) {
        const double M_PER_MM = 1.0 / 1000.0;
        var xMm = xMeters * 1000.0;
        var yMm = yMeters * 1000.0;
        var zMm = zMeters * 1000.0;
        // .NET quirk: Math.Round throws when digits > 15 (double precision limit).
        // Cap at 15 — that's already finer than 0.001mm * 10^-6 which is far below
        // any meaningful CAD geometric tolerance.
        var decimalsToRound = Math.Min(steps / 2, 15);
        var useNearestFive = steps % 2 == 1;
        double Quantize(double mm) {
            if (useNearestFive) {
                // Nearest 5 × 10^-(decimalsToRound+1). Round mm to (decimalsToRound+1)
                // decimal places on a half-step grid via `*2 / 2`. The earlier
                // inner `Math.Round(mm, decimalsToRound)` pre-rounded to the
                // COARSER even step (e.g. step=1 → nearest 1mm instead of the
                // claimed 0.5mm), so a 67.47 mm midpoint snapped to 67 mm and
                // the resulting ray was aimed 0.5mm off the entity.
                var multiplier = Math.Pow(10, decimalsToRound);
                return Math.Round(mm * multiplier * 2.0) / multiplier / 2.0;
            }
            return Math.Round(mm, decimalsToRound);
        }
        return (Quantize(xMm) * M_PER_MM, Quantize(yMm) * M_PER_MM, Quantize(zMm) * M_PER_MM);
    }

    // Body probe: aim a cardinal ray at the body's quantized centroid from
    // outside the bbox. Same 124-cardinal cycle as the other entity kinds;
    // the only kind-specific bit is the bbox-diag offset so the ray starts
    // safely outside the body regardless of its extent in the cast direction.
    private static Ray? BuildBodyProbe(Body2 body, int attempt, double padding) {
        var (centroid, _, _) = MassProperties.Compute(body);
        var (dx, dy, dz) = _cardinals[attempt % _cardinals.Length];
        var quantSteps = 1 + attempt / _cardinals.Length;
        var (qx, qy, qz) = QuantizePointMmAligned(centroid.X, centroid.Y, centroid.Z, quantSteps);

        body.GetExtremePoint( 1, 0, 0, out var maxX, out _,        out _);
        body.GetExtremePoint(-1, 0, 0, out var minX, out _,        out _);
        body.GetExtremePoint( 0, 1, 0, out _,        out var maxY, out _);
        body.GetExtremePoint( 0,-1, 0, out _,        out var minY, out _);
        body.GetExtremePoint( 0, 0, 1, out _,        out _,        out var maxZ);
        body.GetExtremePoint( 0, 0,-1, out _,        out _,        out var minZ);
        var diag = Math.Sqrt(
            (maxX - minX) * (maxX - minX) +
            (maxY - minY) * (maxY - minY) +
            (maxZ - minZ) * (maxZ - minZ));
        var farOffset = 0.5 * diag + padding;

        // Normalize the cardinal so farOffset is a true distance.
        var len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        var nx = dx / len; var ny = dy / len; var nz = dz / len;
        return new Ray(
            new Point3D(qx - nx * farOffset, qy - ny * farOffset, qz - nz * farOffset),
            new Direction(nx, ny, nz));
    }

    private static Ray MakePaddedRay(double px, double py, double pz,
                                     double cx, double cy, double cz,
                                     double padding, double? radius = null) {
        // Do NOT normalize the cardinal. SelectByRay treats the direction as a pure
        // direction (magnitude irrelevant — see SelectByRayDetailed), and the
        // closest-pick loop normalizes internally, so a unit vector buys nothing on
        // the consumer side. Normalizing only injected 0.7071… noise into BOTH the
        // emitted direction and the origin (origin = seed + unit·padding). Keeping
        // the raw integer cardinal leaves the direction clean (e.g. 1,0,-1) and the
        // origin on the same mm grid the seed was quantized to. Padding becomes
        // |cardinal|·padding — still a few tenths of a mm, plenty to lift the origin
        // off the entity.
        var origin = new Point3D(px + cx * padding, py + cy * padding, pz + cz * padding);
        // `0.0 - c` instead of `-c` so a zero component negates to +0.0, not -0.0 —
        // keeps emitted directions clean (1,0,-1) rather than (1,-0,-1).
        var direction = new Direction(0.0 - cx, 0.0 - cy, 0.0 - cz);
        return new Ray(origin, direction, radius);
    }

    // Re-commit an existing Split feature with new `consume_marked_bodies` +
    // `marked_bodies` settings. Internally: delete the existing Split and
    // re-commit via SplitHandler.Edit. Runner calls this after
    // `add_feature(Split)` to apply source's actual consume flag + marked
    // subset, once the body defs have been probe-swapped onto target.
    //
    // `selectAll`: the common "keep all bodies" case (every PreSplitBody2
    // fragment marked, consume=false). When true, `markedBodies` is ignored
    // (it can be empty) and SplitHandler.Edit marks every candidate. The
    // transient PreSplitBody2 fragments have no stable geometry to probe, so
    // this flag — not a baked body-fingerprint list — is how the round-trip
    // replays a keep-all Split. When false, behaves exactly as before: resolve
    // each given BodyDefinition against target's candidates.
    public JsonNode EditSplit(
        string featureName, bool consumeMarkedBodies, JsonArray markedBodies, bool selectAll,
        JsonArray? markedBodyRays = null)
    {
        using var _ = MassProperties.Push(_modelDoc);
        // When select_all, skip parsing entirely — the body list is ignored
        // downstream and may legitimately be empty on the wire.
        var bodies = new List<BodyDefinition>(markedBodies.Count);
        if (!selectAll) {
            for (var i = 0; i < markedBodies.Count; i++) {
                var node = markedBodies[i]
                    ?? throw new ArgumentException($"EditSplit: marked_bodies[{i}] is null");
                var def = Definition.FromJson(node)
                    ?? throw new ArgumentException($"EditSplit: marked_bodies[{i}] is malformed");
                if (def is not BodyDefinition bd) {
                    throw new ArgumentException(
                        $"EditSplit: marked_bodies[{i}] is not a body Definition (kind='{def.GetType().Name}')");
                }
                bodies.Add(bd);
            }
        }
        // Rays take precedence over the body fingerprints when supplied — they
        // name each fragment by position, which is the only handle that survives
        // to a rebuild, and the only form an author can reasonably write.
        List<Ray>? rays = null;
        if (!selectAll && markedBodyRays is { Count: > 0 }) {
            rays = new List<Ray>(markedBodyRays.Count);
            for (var i = 0; i < markedBodyRays.Count; i++) {
                var node = markedBodyRays[i]
                    ?? throw new ArgumentException($"EditSplit: marked_body_rays[{i}] is null");
                rays.Add(node.Deserialize<Ray>(Definition.JsonOptions)
                    ?? throw new ArgumentException(
                        $"EditSplit: marked_body_rays[{i}] is not a valid ray"));
            }
        }
        return SplitHandler.Edit(this, featureName, consumeMarkedBodies, bodies, selectAll, rays);
    }

    // On-demand probe builder for a committed Cut-Extrude/Revolve/Sweep feature.
    // Returns a JsonArray of {body, ray} BodyProbe objects, one per fragment
    // the original cut discarded (empty for single-body cuts). The roundtrip
    // runner calls this immediately after the Cut* `InspectFeature` and pipes
    // the result into `target.drop_bodies(name, probes)`.
    //
    // Moved off `InspectFeature` because the underlying `IModifyDefinition2`
    // dance triggers a Parasolid rebuild that drifts the surviving body's
    // mass-props by ~1e-7. Non-roundtrip callers (model authors, diff tools)
    // shouldn't pay that price just to read a Cut* feature.
    public JsonNode GetDiscardProbes(string featureName) {
        using var _ = MassProperties.Push(_modelDoc);
        var feature = FindFeatureByName(featureName)
            ?? throw new ArgumentException(
                $"GetDiscardProbes: no feature named '{featureName}' in the feature tree");
        var typeName = ResolveFeatureTypeName(feature);
        if (typeName != CutExtrudeHandler.SwTypeName
            && typeName != CutRevolveHandler.SwTypeName
            && typeName != CutSweepHandler.SwTypeName) {
            throw new InvalidOperationException(
                $"GetDiscardProbes: feature '{featureName}' is '{typeName}'; " +
                "only Cut-Extrude / Cut-Revolve / Cut-Sweep are supported");
        }
        var probes = Handlers.Shared.BodiesToKeepScope.ProbeBodiesToDiscard(this, feature);
        // SW quirk: Cut-Sweep's ModifyDefinition leaves the feature dirty;
        // mirror the Inspect-side fixup that used to follow probe building.
        if (typeName == CutSweepHandler.SwTypeName) {
            Handlers.Shared.BodiesToKeepScope.FixupCutSweepViaEditOk(this, feature);
        }
        return probes;
    }

    // Drop a list of post-cut fragments from an already-committed Cut* feature.
    // Each entry is a BodyDefinition naming a fragment the cut kept (the cut
    // commits with all bodies alive — typically obtained client-side via
    // `target.probe(ray, BODY)`). For each body def we (a) resolve it to a
    // live Body2 in the current part state, (b) synthesize a probe ray aimed
    // at that body, then (c) re-fire the cut's PromptBodiesToKeepNotify so the
    // ray-resolved candidates are excluded from the new keep-list.
    //
    // CutSweep additionally runs FixupCutSweepViaEditOk to clear the dirty-
    // rebuild state ModifyDefinition leaves on Cut-Sweep.
    public JsonNode DropBodies(string featureName, JsonArray bodiesArray) {
        using var _ = MassProperties.Push(_modelDoc);
        var feature = FindFeatureByName(featureName)
            ?? throw new ArgumentException($"DropBodies: no feature named '{featureName}' in the feature tree");
        var typeName = ResolveFeatureTypeName(feature);
        if (typeName != CutExtrudeHandler.SwTypeName
            && typeName != CutRevolveHandler.SwTypeName
            && typeName != CutSweepHandler.SwTypeName) {
            throw new InvalidOperationException(
                $"DropBodies: feature '{featureName}' is '{typeName}'; " +
                "only Cut-Extrude / Cut-Revolve / Cut-Sweep are supported");
        }

        // Parse body defs and resolve each to a live Body2 in the current
        // (post-Cut*) part state. Build a probe ray per body, disambiguating
        // against the full current body list — same construction the original
        // notify-time probes use, but built off the stable post-add bodies
        // instead of transient notify candidates.
        var allBodies = new List<Body2>();
        var raw = ((IPartDoc)_modelDoc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
        if (raw is not null) {
            foreach (Body2 body in raw) {
                if (body is not null) allBodies.Add(body);
            }
        }

        var rays = new List<Ray>(bodiesArray.Count);
        for (var i = 0; i < bodiesArray.Count; i++) {
            var node = bodiesArray[i]
                ?? throw new ArgumentException($"DropBodies: bodies[{i}] is null");
            var def = Definition.FromJson(node)
                ?? throw new ArgumentException($"DropBodies: bodies[{i}] is not a valid Definition");
            if (def is not BodyDefinition) {
                throw new ArgumentException(
                    $"DropBodies: bodies[{i}] is not a BodyDefinition (kind='{def.GetType().Name}')");
            }
            var liveBody = DefinitionResolver.Resolve(this, def) as Body2
                ?? throw new InvalidOperationException(
                    $"DropBodies: bodies[{i}] did not resolve to a live body in the current part state");
            var ray = BuildBodyProbeAgainstSiblings(liveBody, allBodies);
            rays.Add(ray);
        }

        Handlers.Shared.BodiesToKeepScope.ApplyDropBodies(this, feature, rays);

        // SW quirk: ModifyDefinition on Cut-Sweep leaves the feature in a
        // dirty-rebuild state (mass props drift, stale getters). Manual
        // Edit-Feature → OK clears it; mirror that here.
        if (typeName == CutSweepHandler.SwTypeName) {
            Handlers.Shared.BodiesToKeepScope.FixupCutSweepViaEditOk(this, feature);
        }

        return new JsonObject {
            ["feature"] = featureName,
            ["dropped"] = rays.Count,
        };
    }

    private Feature? FindFeatureByName(string name) => Features().FirstOrDefault(feature => feature.Name == name);

    public JsonNode AddFeature(JsonNode feature) {
        using var _ = MassProperties.Push(_modelDoc);
        if (feature["name"] is { } nameNode) {
            var name = nameNode.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Feature name must be nonempty");
            if (FindFeatureByName(name) is not null)
                throw new ArgumentException($"A feature named '{name}' already exists");
        }
        var typeNode = feature["type"]
            ?? throw new ArgumentException("AddFeature: missing required field 'type'");
        var typeStr = typeNode.GetValue<string>();
        if (typeStr.StartsWith("Mate", StringComparison.Ordinal)) return MateHandler.Add(this, feature);
        var result = typeStr switch {
            SketchHandler.TypeName             => SketchHandler.Add(this, feature),
            ExtrudeHandler.TypeName            => ExtrudeHandler.Add(this, feature),
            CutExtrudeHandler.TypeName         => CutExtrudeHandler.Add(this, feature),
            RevolveHandler.TypeName            => RevolveHandler.Add(this, feature),
            CutRevolveHandler.TypeName         => CutRevolveHandler.Add(this, feature),
            SweepHandler.TypeName              => SweepHandler.Add(this, feature),
            CutSweepHandler.TypeName           => CutSweepHandler.Add(this, feature),
            HelixHandler.TypeName              => HelixHandler.Add(this, feature),
            LoftHandler.TypeName or LoftHandler.CutTypeName => LoftHandler.Add(this, feature),
            ShellHandler.TypeName => ShellHandler.Add(this, feature),
            DraftHandler.TypeName => DraftHandler.Add(this, feature),
            RibHandler.TypeName => RibHandler.Add(this, feature),
            ProjectedCurveHandler.TypeName     => ProjectedCurveHandler.Add(this, feature),
            LinearPatternHandler.TypeName      => LinearPatternHandler.Add(this, feature),
            CircularPatternHandler.TypeName    => CircularPatternHandler.Add(this, feature),
            MirrorHandler.TypeName             => MirrorHandler.Add(this, feature),
            FilletHandler.TypeName             => FilletHandler.Add(this, feature),
            ChamferHandler.TypeName            => ChamferHandler.Add(this, feature),
            RefPlaneHandler.TypeName           => RefPlaneHandler.Add(this, feature),
            RefAxisHandler.TypeName            => RefAxisHandler.Add(this, feature),
            CombineHandler.TypeName            => CombineHandler.Add(this, feature),
            SplitHandler.TypeName              => SplitHandler.Add(this, feature),
            _ => throw new ArgumentException($"AddFeature: unknown feature type '{typeStr}'")
        };

        // Naming AND the rollback-bar move are done per-handler (each Add calls
        // File.ApplyFeatureName + file.MoveRollbackBar(...AfterFeature) right after creating
        // its feature) — see e.g. SplitHandler.CommitSplit. Doing the bar move before the
        // handler builds its result means any live-state echo_/expected_ diagnostic fields
        // are captured AFTER the rebuild lands. The rollback re-derives body topology
        // deterministically (ForceRebuildAll), which is what keeps body-probes from drifting
        // run-to-run. Sketch handlers deliberately skip
        // the bar move: an EMPTY sketch is auto-deleted by ForceRebuildAll.
        return result;
    }

    // Edit an EXISTING feature in place (no delete/re-add) so its name — and every
    // downstream name-ref / probe — survives. Routes by the live feature's kind (same
    // resolver InspectFeature uses); `changes` is a sparse partial in the feature's own
    // inspect/add schema (only named fields change). Rolled out per-handler: only the
    // kinds with an Edit method are supported; others throw.
    public JsonNode EditFeature(string name, JsonNode input) {
        using var _ = MassProperties.Push(_modelDoc);
        var feature = FindFeatureByName(name)
            ?? throw new ArgumentException($"EditFeature: no feature named '{name}'");
        if (Kind == "assembly" && feature.GetTypeName2().StartsWith("Mate") && feature.GetSpecificFeature2() is Mate2) return MateHandler.Edit(this, feature, input);
        var typeName = ResolveFeatureTypeName(feature);
        return typeName switch {
            ChamferHandler.SwTypeName          => ChamferHandler.Edit(this, feature, input),
            FilletHandler.SwTypeName           => FilletHandler.Edit(this, feature, input),
            ExtrudeHandler.SwTypeName          => ExtrudeHandler.Edit(this, feature, input),
            CutExtrudeHandler.SwTypeName       => CutExtrudeHandler.Edit(this, feature, input),
            RevolveHandler.SwTypeName          => RevolveHandler.Edit(this, feature, input),
            CutRevolveHandler.SwTypeName       => CutRevolveHandler.Edit(this, feature, input),
            HelixHandler.SwTypeName            => HelixHandler.Edit(this, feature, input),
            LinearPatternHandler.SwTypeName    => LinearPatternHandler.Edit(this, feature, input),
            CircularPatternHandler.SwTypeName  => CircularPatternHandler.Edit(this, feature, input),
            MirrorHandler.PatternType or MirrorHandler.BodyType => MirrorHandler.Edit(this, feature, input),
            RefPlaneHandler.SwTypeName         => RefPlaneHandler.Edit(this, feature, input),
            SweepHandler.SwTypeName            => SweepHandler.Edit(this, feature, input),
            CutSweepHandler.SwTypeName         => CutSweepHandler.Edit(this, feature, input),
            LoftHandler.SwTypeName or LoftHandler.CutSwTypeName => LoftHandler.Edit(this, feature, input),
            ShellHandler.SwTypeName => ShellHandler.Edit(this, feature, input),
            DraftHandler.SwTypeName => DraftHandler.Edit(this, feature, input),
            RibHandler.SwTypeName => RibHandler.Edit(this, feature, input),
            CombineHandler.SwTypeName          => CombineHandler.Edit(this, feature, input),
            _ => throw new ArgumentException(
                $"EditFeature: in-place editing is not supported for feature type '{typeName}' "
                + $"on '{name}'. ProjectedCurve (SW ModifyDefinition crashes on ref curves) and "
                + "RefAxis (reference-defined, no scalar) have "
                + "no safe in-place edit; Sketch needs its own EditSketch mechanism.")
        };
    }

    // Rename a feature (any kind — Boss-Extrude, Fillet, Sketch, RefPlane, …) in place.
    // Feature names are the durable handle downstream refs/probes resolve through, so this
    // is a metadata-only edit: no rebuild, no geometry change. Refuses a blank name.
    public JsonNode RenameFeature(string name, string newName) {
        if (string.IsNullOrWhiteSpace(newName)) {
            throw new ArgumentException("RenameFeature: new_name must be a non-empty string");
        }
        var feature = FindFeatureByName(name)
            ?? throw new ArgumentException($"RenameFeature: no feature named '{name}'");
        ApplyFeatureName(feature, new JsonObject { ["name"] = newName });
        SldworksLog.Information("RenameFeature: {Old} -> {New}", name, newName);
        return new JsonObject {
            ["old_name"] = name,
            ["new_name"] = feature.Name,
        };
    }

    // Apply phase-2 composite entities (LinearPattern / CircularPattern) to an existing
    // sketch. Their `seeds` must carry target-side SketchEntityIds, back-filled from
    // the AddSketch response. Returns the produced composites with their instance
    // entities populated.
    public JsonNode AddSketchEntities(string sketchName, JsonArray entities) {
        using var _ = MassProperties.Push(_modelDoc);
        return SketchHandler.AddSketchEntities(this, sketchName, entities);
    }

    // Apply constraints to an existing sketch identified by feature name. Refs in each
    // constraint must already carry target-side SketchEntityIds — the caller back-fills
    // these from the AddSketch response before serializing.
    public JsonNode AddConstraints(string sketchName, JsonArray constraints) {
        using var _ = MassProperties.Push(_modelDoc);
        return SketchHandler.AddConstraints(this, sketchName, constraints);
    }

    // Edit one existing primitive sketch entity (Line/Circle/Arc/Point) in place by moving
    // its defining points; keeps the entity id so constraints referencing it survive.
    public JsonNode EditSketchEntity(string sketchName, JsonNode entity) {
        using var _ = MassProperties.Push(_modelDoc);
        return SketchHandler.EditSketchEntity(this, sketchName, entity);
    }

    // Delete one existing primitive sketch entity (by its id).
    public JsonNode DeleteSketchEntity(string sketchName, JsonNode entity) {
        using var _ = MassProperties.Push(_modelDoc);
        return SketchHandler.DeleteSketchEntity(this, sketchName, entity);
    }

    // Edit a sketch driving dimension by name (the `echo_dim_name` from inspect): change its
    // value and/or rename it. Returns the owning sketch's Inspect.
    public JsonNode EditDimension(string dimName, double? value, string? newName) {
        using var _ = MassProperties.Push(_modelDoc);
        return SketchHandler.EditDimension(this, dimName, value, newName);
    }

    // Delete one existing sketch constraint (dimension matched by name, relation by kind + refs).
    public JsonNode DeleteConstraint(string sketchName, JsonNode constraint) {
        using var _ = MassProperties.Push(_modelDoc);
        return SketchHandler.DeleteConstraint(this, sketchName, constraint);
    }

    public JsonNode DeleteFeature(int index) {
        var feat = FeatureAt(index)
            ?? throw new ArgumentOutOfRangeException(nameof(index),
                $"Feature index {index} out of range");

        feat.Select2(false, 0);

        // SW quirk: pass 0 so DeleteSelection2 FAILS on dependents rather than silently cascading
        // (swDelete_Absorbed=1 deletes children, swDelete_Children=2 cascades).
        var ok = _modelDoc.Extension.DeleteSelection2(0);
        if (!ok) {
            throw new InvalidOperationException(
                $"Cannot delete feature {index} ({feat.Name}); it likely has dependents. " +
                "Delete dependents first or set rollback before the parent.");
        }

        SldworksLog.Information("DeleteFeature: removed index {Index} ({Name})", index, feat.Name);
        return new JsonObject {
            ["deleted"] = true,
            ["index"] = index,
        };
    }

    public JsonNode SetRollback(int index) {
        if (index < 0) {
            // -1 = rollback to end (all features active).
            MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
            return new JsonObject { ["index"] = index };
        }

        var feat = FeatureAt(index)
            ?? throw new ArgumentOutOfRangeException(nameof(index),
                $"Feature index {index} out of range");

        MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToBeforeFeature, feat.Name);

        SldworksLog.Information("SetRollback: moved bar to before index {Index} ({Name})", index, feat.Name);
        return new JsonObject { ["index"] = index };
    }

    // SW quirk: EditRollback takes the feature *name* (not index); the End mode
    // ignores the name and conventionally takes empty string. Every caller
    // pairs EditRollback with a rebuild — bundle them so the pairing can't
    // drift. Uses ForceRebuildAll (not EditRebuild3) because EditRebuild3
    // only recomputes features SW flags as dirty; the source-side probe needs
    // body topology to actually reflect the new bar position so the resolver
    // can find the inspected entity's live counterpart.
    internal void MoveRollbackBar(swMoveRollbackBarTo_e mode, string featureName = "") {
        _modelDoc.FeatureManager.EditRollback((int)mode, featureName);
        _modelDoc.Extension.ForceRebuildAll();
    }

    public JsonNode SetBodies(JsonArray bodyIds) {
        using var _ = MassProperties.Push(_modelDoc);
        var hits = new List<Body2>();
        var unresolved = new List<int>();

        for (var i = 0; i < bodyIds.Count; i++) {
            var entry = bodyIds[i]
                ?? throw new ArgumentException($"SetBodies: entry at index {i} is null");
            var def = Definition.FromJson(entry)
                ?? throw new ArgumentException($"SetBodies: entry at index {i} is not a valid Definition");
            if (DefinitionResolver.Resolve(this, def) is Body2 body) {
                hits.Add(body);
            } else {
                unresolved.Add(i);
            }
        }

        PendingBodiesToKeep = hits;

        SldworksLog.Information("SetBodies: resolved {Hits}/{Total} body definitions",
            hits.Count, bodyIds.Count);

        var unresolvedJson = new JsonArray();
        foreach (var idx in unresolved) unresolvedJson.Add(idx);

        return new JsonObject {
            ["resolved"] = hits.Count,
            ["requested"] = bodyIds.Count,
            ["unresolved_indices"] = unresolvedJson,
        };
    }

    public JsonNode SetUnits(string units) {
        // Wire is meters/radians; this only changes display. Map system shorthands to length units.
        var lengthName = units switch {
            "MMGS" => "mm",
            "CGS"  => "cm",
            "MKS"  => "m",
            "IPS"  => "in",
            "inch" => "in",
            _      => units,
        };

        SetUnitMult(lengthName);

        SldworksLog.Information("SetUnits: {Units} -> {LengthName}", units, lengthName);
        return new JsonObject { ["units"] = units };
    }

    public byte[] GetImage(string orientation) {
        // SW quirk: ModelView.SaveAs PNG support is spotty across versions — SaveBMP is reliable;
        // crop the FeatureManager column out then re-encode as PNG. SaveBMP also fails silently
        // if the target file exists, so write to a fresh temp path. SaveBMP needs graphics
        // updates enabled; capturing while File.Lock() is active produces blank/white BMPs.

        var dir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "Sldworks");
        Directory.CreateDirectory(dir);
        var bmpPath = System.IO.Path.Combine(dir, $"View_{Guid.NewGuid():N}.bmp");

        var relockAfterCapture = _locked;
        if (relockAfterCapture) Unlock();

        try {
            SetView(orientation);

            const int imageSize = 1536;
            var fmWidth = _modelDoc.GetFeatureManagerWidth();
            var width = imageSize + fmWidth;
            var height = imageSize;

            _modelDoc.SaveBMP(bmpPath, width, height);

            using var image = System.Drawing.Image.FromFile(bmpPath);
            using var bitmap = new System.Drawing.Bitmap(image);
            var crop = new System.Drawing.Rectangle(fmWidth, 0, imageSize, imageSize);
            using var cropped = bitmap.Clone(crop, bitmap.PixelFormat);

            using var ms = new MemoryStream();
            cropped.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        } finally {
            try {
                System.IO.File.Delete(bmpPath);
            } finally {
                if (relockAfterCapture) Lock();
            }
        }
    }

    public bool IsLocked => _locked;

    public void Lock() {
        if (_locked) return;
        _sldWorks.CommandInProgress = true;
        _modelDoc.Lock();
        _modelDoc.FeatureManager.EnableFeatureTree = false;
        ActiveView.EnableGraphicsUpdate = false;
        ActiveView.SuppressWaitCursorDuringRedraw = true;
        _locked = true;
        SldworksLog.Verbose("File.Lock: speedup engaged for {Name}", Name);
    }

    public void Unlock() {
        if (!_locked) return;
        _sldWorks.CommandInProgress = false;
        _modelDoc.UnLock();
        _modelDoc.FeatureManager.EnableFeatureTree = true;
        ActiveView.EnableGraphicsUpdate = true;
        ActiveView.SuppressWaitCursorDuringRedraw = false;
        _locked = false;
        SldworksLog.Verbose("File.Unlock: speedup released for {Name}", Name);
    }

    public JsonNode Save(string? path = null) {
        if (!string.IsNullOrEmpty(path)) {
            var targetName = System.IO.Path.GetFileName(path);
            // SolidWorks SaveAs can introduce duplicate titles that OpenDoc6 rejects.
            foreach (var doc in Session.OpenDocuments(_sldWorks)) {
                if ((string.Equals(doc.GetTitle(), targetName, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(System.IO.Path.GetFileName(doc.GetPathName()), targetName, StringComparison.OrdinalIgnoreCase))
                    && _sldWorks.IsSame(doc, _modelDoc) != (int)swObjectEquality.swObjectSame) {
                    throw new InvalidOperationException(
                        $"Cannot save as '{path}': another open document is named '{targetName}' ({doc.GetPathName()}).");
                }
            }
        }
        _modelDoc.ClearSelection2(true);

        int errors = 0, warnings = 0;
        bool ok;

        // SW quirk: STL fidelity is governed by *system* user preferences read at export
        // time, NOT by any SaveAs3 argument. The default Coarse preset produces heavily
        // faceted curves (a flange bore comes out ~2k triangles). Set a fine custom
        // tessellation (tight chord + small facet-angle) for the duration of an .stl
        // export so curved surfaces are smooth + watertight, then restore the prefs.
        bool isStl = !string.IsNullOrEmpty(path)
                     && path!.EndsWith(".stl", StringComparison.OrdinalIgnoreCase);
        int prevQuality = 0;
        double prevDeviation = 0, prevAngle = 0;
        if (isStl) {
            prevQuality = _sldWorks.GetUserPreferenceIntegerValue(
                (int)swUserPreferenceIntegerValue_e.swSTLQuality);
            prevDeviation = _sldWorks.GetUserPreferenceDoubleValue(
                (int)swUserPreferenceDoubleValue_e.swSTLDeviation);
            prevAngle = _sldWorks.GetUserPreferenceDoubleValue(
                (int)swUserPreferenceDoubleValue_e.swSTLAngleTolerance);
            _sldWorks.SetUserPreferenceIntegerValue(
                (int)swUserPreferenceIntegerValue_e.swSTLQuality,
                (int)swSTLQuality_e.swSTLQuality_Custom);
            // Loose chord (just caps deviation on huge radii — a tight one needlessly
            // explodes triangle counts on large smooth surfaces); tight facet-angle
            // drives uniform smoothness on small round features. 0.02mm is ~7x below the
            // 0.15mm grading tau, so accuracy is unaffected.
            _sldWorks.SetUserPreferenceDoubleValue(
                (int)swUserPreferenceDoubleValue_e.swSTLDeviation, 2.0e-5);      // 0.02 mm chord
            _sldWorks.SetUserPreferenceDoubleValue(
                (int)swUserPreferenceDoubleValue_e.swSTLAngleTolerance, 0.05236); // ~3.0 deg between facets
        }

        try {
            if (string.IsNullOrEmpty(path)) {
                // SW quirk: Save3 fails with "no path" on never-saved docs; caller must pass a path on first save.
                ok = _modelDoc.Save3(
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    ref errors,
                    ref warnings);
            } else {
                // SW quirk: SaveAs3 lives on Extension; trailing nulls are export/advanced save data.
                ok = _modelDoc.Extension.SaveAs3(
                    path,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    null,
                    null,
                    ref errors,
                    ref warnings);
            }
        } finally {
            if (isStl) {
                _sldWorks.SetUserPreferenceIntegerValue(
                    (int)swUserPreferenceIntegerValue_e.swSTLQuality, prevQuality);
                _sldWorks.SetUserPreferenceDoubleValue(
                    (int)swUserPreferenceDoubleValue_e.swSTLDeviation, prevDeviation);
                _sldWorks.SetUserPreferenceDoubleValue(
                    (int)swUserPreferenceDoubleValue_e.swSTLAngleTolerance, prevAngle);
            }
        }

        if (!ok || errors != 0) {
            var details = DecodeFileSaveResult(errors, warnings);
            throw new InvalidOperationException($"Save failed for {Name}: {details}");
        }
        if (warnings != 0) {
            SldworksLog.Warning("File.Save: warnings for {Name}: {Details}", Name, DecodeFileSaveResult(0, warnings));
        }

        return new JsonObject {
            ["FileName"] = Name,
            ["path"] = _modelDoc.GetPathName() ?? "",
        };
    }

    public void Close() {
        var name = Name;
        _sldWorks.CloseDoc(name);
        SldworksLog.Information("File.Close: closed {Name}", name);
    }

    // ---- helpers --------------------------------------------------------------------

    // Apply the source-supplied feature name. Inspect emits the live `feature.Name`; on
    // round-trip we rename the freshly-created feature so downstream lookups by name
    // (sketch refs, inspect index→name diffs) stay stable across rebuilds even when SW's
    // auto-numbering would diverge (deleted features, reordered tree).
    internal static void ApplyFeatureName(Feature feature, JsonNode input) {
        var requested = input["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(requested)) return;
        if (feature.Name == requested) return;
        feature.Name = requested;
        // SW silently keeps the auto-name on collision/illegal-char rather than throwing.
        if (feature.Name != requested)
            throw new InvalidOperationException($"Cannot name feature '{requested}'; SolidWorks retained '{feature.Name}' (name collision or invalid name)");
    }

    private Feature? FeatureAt(int index) {
        return index < 0 ? null : Features().ElementAtOrDefault(index);
    }

    internal IEnumerable<Feature> Features() {
        var feat = _modelDoc.FirstFeature() as Feature;
        while (feat is not null) {
            yield return feat;
            if (feat.GetTypeName2() == "MateGroup") {
                var child = feat.GetFirstSubFeature() as Feature;
                while (child is not null) {
                    yield return child;
                    child = child.GetNextSubFeature() as Feature;
                }
            }
            feat = feat.GetNextFeature() as Feature;
        }
    }

    internal static string ReadFeatureStatus(Feature feat) {
        // SW quirk: no parameterless IsSuppressed in this redist; IsSuppressed2 returns bool[]
        // (one entry per resolved config — length 1 for swThisConfiguration).
        bool suppressed = false;
        var arr = feat.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) as bool[];
        if (arr is { Length: > 0 }) suppressed = arr[0];
        if (suppressed) return "suppressed";

        // GetErrorCode2 != 0 means SW flagged the feature as failed during last rebuild.
        var err = feat.GetErrorCode2(out _);
        if (err != 0) return "failed";
        return "ok";
    }

    private void SetView(string orientation) {
        var standard = orientation switch {
            "Front"     => (int?)swStandardViews_e.swFrontView,
            "Back"      => (int?)swStandardViews_e.swBackView,
            "Top"       => (int?)swStandardViews_e.swTopView,
            "Bottom"    => (int?)swStandardViews_e.swBottomView,
            "Left"      => (int?)swStandardViews_e.swLeftView,
            "Right"     => (int?)swStandardViews_e.swRightView,
            "Isometric" => (int?)swStandardViews_e.swIsometricView,
            "Trimetric" => (int?)swStandardViews_e.swTrimetricView,
            "Dimetric"  => (int?)swStandardViews_e.swDimetricView,
            "Current"   => null,
            _ => throw new ArgumentException($"GetImage: unknown orientation '{orientation}'"),
        };

        if (standard is int v) {
            _modelDoc.ShowNamedView2("", v);
        }

        // Zoomtofit + two zoomouts adds a margin around the part.
        _modelDoc.ViewZoomtofit2();
        _modelDoc.ViewZoomout();
        _modelDoc.ViewZoomout();
        _modelDoc.GraphicsRedraw2();
    }

    private static string DecodeBitmask<TEnum>(int value) where TEnum : Enum {
        if (value == 0) return "";
        var names = new List<string>();
        foreach (TEnum entry in Enum.GetValues(typeof(TEnum))) {
            var bit = Convert.ToInt32(entry);
            if (bit != 0 && (value & bit) == bit) names.Add(entry.ToString()!);
        }
        return string.Join(", ", names);
    }

    private static string DecodeFileSaveResult(int errors, int warnings) {
        var parts = new List<string>();
        if (errors != 0) parts.Add($"errors=[{DecodeBitmask<swFileSaveError_e>(errors)}]");
        if (warnings != 0) parts.Add($"warnings=[{DecodeBitmask<swFileSaveWarning_e>(warnings)}]");
        return parts.Count == 0 ? "ok" : string.Join("; ", parts);
    }

    // ---- units ----------------------------------------------------------------------

    // SW quirk: swFEETINCHES (sw=5) is read-only and shares feet's multiplier — never settable.
    private static readonly (string Name, short SwUnit)[] UnitTable = [
        ("mm",  0),
        ("cm",  1),
        ("m",   2),
        ("in",  3),
        ("ft",  4),
        ("A",   6),
        ("nm",  7),
        ("um",  8),
        ("mil", 9),
        ("uin", 10),
    ];

    internal string CurrentUnitName {
        get {
            var lengthUnit = _modelDoc.LengthUnit;
            // SW quirk: swFEETINCHES (5) maps to feet on the wire.
            if (lengthUnit == 5) return "ft";
            foreach (var (name, sw) in UnitTable) {
                if (sw == lengthUnit) return name;
            }
            throw new ArgumentException($"Unknown length unit: {lengthUnit}");
        }
    }

    private void SetUnitMult(string unitName) {
        // SW quirk: GetUnits = [units, decimal-or-fraction, fraction denominator, sig figs, round-to-fraction].
        var units = (short[])_modelDoc.GetUnits();
        var roundToFrac = Convert.ToBoolean(units[4]);
        _modelDoc.SetUnits(LookupSwUnit(unitName), units[1], units[2], units[3], roundToFrac);
    }

    private static short LookupSwUnit(string unitName) {
        foreach (var (name, sw) in UnitTable) {
            if (name == unitName) return sw;
        }
        throw new ArgumentException($"Unknown length unit: {unitName}");
    }
}
