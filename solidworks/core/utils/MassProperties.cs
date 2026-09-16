using System.Runtime.InteropServices;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Utils;

// Capture and resolution must use the same mass-property integration path.
// IMassProperty2 can be unavailable on rolled-back documents, so use the per-body
// API consistently. Its curved-face values drift between rebuilds; Flags defines
// separate length, volume, and area tolerances for matching.
public static class MassProperties {
    [ThreadStatic]
    private static ModelDoc2? _ambient;

    public static IDisposable Push(ModelDoc2 doc) {
        var prev = _ambient;
        _ambient = doc;
        return new Pop(prev);
    }

    public static (Point3D Centroid, double Volume, double SurfaceArea) Compute(Body2 body) {
        var doc = _ambient
            ?? throw new InvalidOperationException(
                "MassProperties.Compute: no ambient ModelDoc — wrap the calling " +
                "entry point with `using var _ = MassProperties.Push(modelDoc);` " +
                "or pass the doc to the explicit overload.");
        return Compute(doc, body);
    }

    public static (Point3D Centroid, double Volume, double SurfaceArea) Compute(ModelDoc2 doc, Body2 body) {
        // See class header: high-accuracy IMassProperty2 was tried and
        // produced asymmetric fingerprints between source-capture (often
        // rolled-back → null → low-accuracy fallback) and target-resolve
        // (live → high-accuracy). Symmetric low-accuracy is more stable.
        _ = doc;
        var props = (double[])body.GetMassProperties(1.0);
        return (new Point3D(props[0], props[1], props[2]), props[3], props[4]);
    }

    private sealed class Pop(ModelDoc2? prev) : IDisposable {
        private bool _disposed;
        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            _ambient = prev;
        }
    }
}
