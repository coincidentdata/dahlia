using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Utils;

internal static class MathUtils {
    // Returns 4×3 [X basis, Y basis, Z basis (normal), origin] from a MathTransform.
    // SW quirk: ArrayData = 9 rotation (column-major) + 3 translation + 1 scale + 3 unused.
    internal static double[][] GetTransformMatrix(MathTransform transform) {
        var arr = (double[])transform.ArrayData;
        double[] x = [arr[0], arr[3], arr[6]];
        double[] y = [arr[1], arr[4], arr[7]];
        double[] z = [arr[2], arr[5], arr[8]];
        double[] translate = [arr[9], arr[10], arr[11]];
        var origin = new[] {
            x[0] * -translate[0] + y[0] * -translate[1] + z[0] * -translate[2],
            x[1] * -translate[0] + y[1] * -translate[1] + z[1] * -translate[2],
            x[2] * -translate[0] + y[2] * -translate[1] + z[2] * -translate[2],
        };
        return [x, y, z, origin];
    }

    // Project a model-space 3D point through `axes` (from GetTransformMatrix
    // on the sketch's `ModelToSketchTransform`) to its sketch-plane 2D coords.
    // Inline dot products — no COM call, no allocations. Use in hot loops
    // where IMultiplyTransform-per-point is too slow (e.g. tessellation
    // sample projection).
    internal static (double x, double y) ProjectToSketchPlane(
            double[][] axes, double worldX, double worldY, double worldZ) {
        var xb = axes[0]; var yb = axes[1]; var origin = axes[3];
        var dx = worldX - origin[0]; var dy = worldY - origin[1]; var dz = worldZ - origin[2];
        return (xb[0] * dx + xb[1] * dy + xb[2] * dz,
                yb[0] * dx + yb[1] * dy + yb[2] * dz);
    }

    internal static double DotProduct(double[] a, double[] b) {
        if (a.Length != b.Length) {
            throw new ArgumentException($"DotProduct length mismatch: {a.Length} vs {b.Length}");
        }
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++) {
            sum += a[i] * b[i];
        }
        return sum;
    }

    // Wrap angle to [0, 2π).
    internal static double NormalizeAngle(double angle) {
        return ((angle % (Math.PI * 2)) + (Math.PI * 2)) % (Math.PI * 2);
    }
}
