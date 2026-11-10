var dahliaPoint = function(v) { return { "x" : v[0] / meter, "y" : v[1] / meter, "z" : v[2] / meter }; };
var dahliaVector = function(v) { return { "x" : v[0], "y" : v[1], "z" : v[2] }; };
var dahliaAxis = function(v) {
    // An edge or surface axis has no intrinsic sign; point it toward +z (then +y, +x) so
    // captured and user-authored definitions compare equal.
    const flip = v[2] < 0 || (v[2] == 0 && (v[1] < 0 || (v[1] == 0 && v[0] < 0)));
    return dahliaVector(flip ? v * -1 : v);
};
var dahliaBody = function(q) {
    const bounds = evBox3d(context, { "topology" : q, "tight" : true });
    return { "kind" : "body", "volume" : evVolume(context, { "entities" : q }) / meter^3,
        "surface_area" : evArea(context, { "entities" : q }) / meter^2,
        "centroid" : dahliaPoint(evApproximateCentroid(context, { "entities" : q })),
        "bbox" : { "min" : dahliaPoint(bounds.minCorner), "max" : dahliaPoint(bounds.maxCorner) } };
};
var dahliaCapture = function(q) {
    if (!isQueryEmpty(context, qEntityFilter(q, EntityType.BODY)))
        return dahliaBody(q);
    if (!isQueryEmpty(context, qEntityFilter(q, EntityType.VERTEX)))
        return { "kind" : "vertex", "point" : dahliaPoint(evVertexPoint(context, { "vertex" : q })) };
    if (!isQueryEmpty(context, qEntityFilter(q, EntityType.FACE))) {
        const surface = evSurfaceDefinition(context, { "face" : q });
        var points = [];
        for (var u in [0.2, 0.5, 0.8]) {
            for (var v in [0.2, 0.5, 0.8]) {
                const point = evFaceTangentPlane(context, { "face" : q, "parameter" : vector(u, v) }).origin;
                points = append(points, dahliaPoint(evDistance(context, { "side0" : point, "side1" : q }).sides[1].point));
            }
        }
        var result = { "kind" : "bsurface_face", "on_face_points" : points };
        if (surface is Plane)
            result = { "kind" : "planar_face", "on_face_point" : points[4], "normal" : dahliaVector(surface.normal) };
        else if (surface is Cylinder)
            result = { "kind" : "cylindrical_face", "on_face_point" : points[4],
                "axis_origin" : dahliaPoint(surface.coordSystem.origin), "axis_direction" : dahliaAxis(surface.coordSystem.zAxis),
                "radius" : surface.radius / meter };
        else if (surface is Sphere)
            result = { "kind" : "spherical_face", "on_face_point" : points[4],
                "center" : dahliaPoint(surface.coordSystem.origin), "radius" : surface.radius / meter };
        return result;
    }
    const curve = evCurveDefinition(context, { "edge" : q, "returnBSplinesAsOther" : false });
    const start = evEdgeTangentLine(context, { "edge" : q, "parameter" : 0 }).origin;
    const end = evEdgeTangentLine(context, { "edge" : q, "parameter" : 1 }).origin;
    if (curve is Line)
        return { "kind" : "line_edge", "start" : dahliaPoint(start), "end" : dahliaPoint(end) };
    if (curve is Circle) {
        var result = { "kind" : "circular_edge", "center" : dahliaPoint(curve.coordSystem.origin),
            "axis_direction" : dahliaAxis(curve.coordSystem.zAxis), "radius" : curve.radius / meter };
        if (!tolerantEquals(start, end)) {
            result.start = dahliaPoint(start);
            result.end = dahliaPoint(end);
        }
        return result;
    }
    if (curve is Ellipse)
        return { "kind" : "elliptical_edge", "center" : dahliaPoint(curve.coordSystem.origin),
            "major_axis" : dahliaVector(curve.coordSystem.xAxis), "minor_axis" : dahliaVector(yAxis(curve.coordSystem)),
            "major_radius" : curve.majorRadius / meter, "minor_radius" : curve.minorRadius / meter,
            "start" : dahliaPoint(start), "end" : dahliaPoint(end) };
    if (curve is BSplineCurve) {
        if (curve.isRational) throw "Rational spline edges need weights in the portable definition";
        return { "kind" : "spline_edge", "control_points" : mapArray(curve.controlPoints, dahliaPoint),
            "knots" : curve.knots, "degree" : curve.degree, "periodic" : curve.isPeriodic };
    }
    throw "Unsupported geometry in selection capture";
};
// A reference is the captured definition plus the deterministic id Onshape assigns the
// entity; features accept that id directly, so a reference needs no re-resolution.
var dahliaEntry = function(q) { return { "definition" : dahliaCapture(q), "id" : transientQueriesToStrings([q])[0] }; };
var dahliaEntries = function(q) { return mapArray(evaluateQuery(context, q), dahliaEntry); };
// What an existing feature references: entities whose geometry cannot be captured are left out,
// so the client keeps their native query.
var dahliaCaptured = function(q) {
    var entries = [];
    for (var entity in evaluateQuery(context, q)) {
        const definition = try silent(dahliaCapture(entity));
        if (definition != undefined)
            entries = append(entries, { "definition" : definition, "id" : transientQueriesToStrings([entity])[0] });
    }
    return entries;
};
var dahliaCandidates = function(category) {
    var candidates = qConstructionFilter(qSketchFilter(qEverything(category), SketchObject.NO), ConstructionObject.NO);
    if (category == EntityType.BODY) candidates = qBodyType(candidates, BodyType.SOLID);
    return candidates;
};
var dahliaNear = function(a, b) {
    if (a is number && b is number) return abs(a - b) <= 1e-7 * max(1, max(abs(a), abs(b)));
    return a == b;
};
var dahliaNearFlat = function(a, b) {
    if (!(a is map && b is map)) return dahliaNear(a, b);
    for (var entry in a) if (!dahliaNear(entry.value, b[entry.key])) return false;
    return true;
};
var dahliaNearMap = function(a, b) {
    if (!(a is map && b is map)) return dahliaNear(a, b);
    for (var entry in a) if (!dahliaNearFlat(entry.value, b[entry.key])) return false;
    return true;
};
// Definitions nest at most scalar -> point map -> map of point maps (bbox) -> arrays of those;
// snippet-level lambdas cannot recurse, so each level has its own comparer.
var dahliaClose = function(a, b) {
    if (!(a is map && b is map)) return false;
    for (var entry in a) {
        if (entry.key == "parent_body") continue;
        const other = b[entry.key];
        if (entry.value is array) {
            if (!(other is array) || size(other) != size(entry.value)) return false;
            for (var i = 0; i < size(other); i += 1) if (!dahliaNearMap(entry.value[i], other[i])) return false;
        } else if (!dahliaNearMap(entry.value, other)) return false;
    }
    return true;
};
var dahliaResolve = function(definition, category) {
    if (definition.axis_direction != undefined) {
        const a = definition.axis_direction;
        definition.axis_direction = dahliaAxis(vector(a.x, a.y, a.z));
    }
    var matches = [];
    for (var candidate in evaluateQuery(context, dahliaCandidates(category))) {
        if (category == EntityType.FACE) {
            const points = definition.kind == "bsurface_face" ? definition.on_face_points : [definition.on_face_point];
            var matched = true;
            for (var point in points) {
                if (evDistance(context, { "side0" : vector(point.x, point.y, point.z) * meter, "side1" : candidate }).distance > 1e-7 * meter)
                    matched = false;
            }
            if (matched && definition.parent_body != undefined)
                matched = dahliaClose(definition.parent_body, dahliaBody(qOwnerBody(candidate)));
            if (matched && definition.kind == "planar_face") {
                const surface = evSurfaceDefinition(context, { "face" : candidate });
                matched = surface is Plane && dahliaClose(definition.normal, dahliaVector(surface.normal));
            }
            if (matched) matches = append(matches, candidate);
        } else {
            const captured = dahliaCapture(candidate);
            if (dahliaClose(definition, captured)) matches = append(matches, candidate);
            else if (definition.kind == "line_edge" && dahliaClose(definition.start, captured.end) && dahliaClose(definition.end, captured.start))
                matches = append(matches, candidate);
        }
    }
    if (size(matches) != 1) throw "The definition matches " ~ size(matches) ~ " entities, not one";
    return matches[0];
};
// The sketch region (a face of the sketch) containing a 2D point in sketch coordinates.
var dahliaProbeRegion = function(sketchId, x, y) {
    const faces = qCreatedBy(sketchId, EntityType.FACE);
    const plane = evOwnerSketchPlane(context, { "entity" : faces });
    const hits = evaluateQuery(context, qContainsPoint(faces, planeToWorld(plane, vector(x, y) * meter)));
    if (size(hits) != 1) throw "The point lies in " ~ size(hits) ~ " sketch regions";
    return [dahliaEntry(hits[0])];
};
// Mirrors the SOLIDWORKS probe: faces and bodies by the first surface hit along the ray;
// edges and vertices by proximity to the ray within `radius`, front-most first.
var dahliaProbe = function(origin, direction, category, radius) {
    const ray = line(origin, direction);
    if (category == EntityType.FACE || category == EntityType.BODY) {
        const hits = evRaycast(context, { "entities" : dahliaCandidates(EntityType.FACE), "ray" : ray, "closest" : true });
        if (size(hits) == 0) return [];
        // qOwnerBody is a derived query; only an evaluated (transient) query has an id.
        return [dahliaEntry(category == EntityType.BODY ? evaluateQuery(context, qOwnerBody(hits[0].entity))[0] : hits[0].entity)];
    }
    var best = undefined;
    var bestAlong = 0 * meter;
    for (var candidate in evaluateQuery(context, dahliaCandidates(category))) {
        const result = evDistance(context, { "side0" : ray, "side1" : candidate });
        if (result.distance > radius) continue;
        const along = dot(result.sides[0].point - origin, direction);
        if (along < radius * -1) continue;
        if (best == undefined || along < bestAlong) {
            best = candidate;
            bestAlong = along;
        }
    }
    return best == undefined ? [] : [dahliaEntry(best)];
};
