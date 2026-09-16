using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sldworks.Core.Definitions;
using Sldworks.Core.Utils;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swcommands;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core;

public partial class File {
    public JsonNode TranslateComponent(JsonNode path, JsonNode delta) {
        var point = delta.Deserialize<Point3D>(Definition.JsonOptions)
            ?? throw new ArgumentException("Translation requires a delta vector");
        return MoveComponent(path, [point.X, point.Y, point.Z], rotate: false);
    }

    public JsonNode RotateComponent(JsonNode path, JsonNode axis, double angle) {
        var direction = axis.Deserialize<Direction>(Definition.JsonOptions)
            ?? throw new ArgumentException("Rotation requires an axis direction");
        var length = Math.Sqrt(direction.X*direction.X + direction.Y*direction.Y + direction.Z*direction.Z);
        if (!double.IsFinite(length) || length == 0 || !double.IsFinite(angle))
            throw new ArgumentException("Axis must be finite and nonzero; angle must be finite");
        var x = direction.X/length; var y = direction.Y/length; var z = direction.Z/length;
        double[] values;
        if (new[] { x, y, z }.Count(value => value != 0) == 1) {
            values = [x*angle, y*angle, z*angle];
        } else {
            var c = Math.Cos(angle); var s = Math.Sin(angle); var d = 1-c;
            values = new RigidTransform(new Point3D(0, 0, 0), [
                [c+x*x*d, x*y*d-z*s, x*z*d+y*s],
                [y*x*d+z*s, c+y*y*d, y*z*d-x*s],
                [z*x*d-y*s, z*y*d+x*s, c+z*z*d],
            ]).RotationXyz();
        }
        return MoveComponent(path, values, rotate: true);
    }

    private JsonNode MoveComponent(JsonNode path, double[] values, bool rotate) {
        if (!values.All(double.IsFinite)) throw new ArgumentException("Movement values must be finite");
        var component = ResolveComponent(ReadComponentPath(path));
        if (component.IsSuppressed()) throw new InvalidOperationException("Unsuppress the component before moving it");
        if (_modelDoc.Extension.GetActivePropertyManagerPage() != "")
            throw new InvalidOperationException("Finish the active PropertyManager before moving a component");
        Activate(_modelDoc);
        var before = RigidTransform.Capture(component.Transform2);
        var wasLocked = IsLocked;
        if (wasLocked) Unlock();
        try {
            if (values.Any(value => Math.Abs(value) > 1e-10) && !component.IsFixed()
                    && component.GetConstrainedStatus() != (int)swConstrainedStatus_e.swFullyConstrained)
                ApplyMovement(component, values, rotate);
            if (!_modelDoc.ForceRebuild3(false)) throw new InvalidOperationException($"Rebuild failed in '{Name}'");
            var failed = Features().Where(feature => ReadFeatureStatus(feature) == "failed").Select(feature => feature.Name).ToArray();
            if (failed.Length != 0) throw new InvalidOperationException($"Features failed after movement: {string.Join(", ", failed)}");
            var difference = before.Difference(component.Transform2);
            var result = InspectComponent(component);
            result["moved"] = difference.Translation > 1e-9 || difference.Rotation > 1e-9;
            return result;
        } finally {
            try {
                if (IsMovementPage()) FinishMovement();
                _modelDoc.ClearSelection2(true);
            } finally { if (wasLocked) Lock(); }
        }
    }

    private bool IsMovementPage() => _modelDoc.Extension.GetActivePropertyManagerPage() is "DveMoveCompDlg" or "uiDveMoveCompDlg_c";

    private void ApplyMovement(Component2 component, double[] values, bool rotate) {
        _modelDoc.ClearSelection2(true);
        if (!Definition.SelectLive(this, component, 0)) throw new InvalidOperationException($"Cannot select {component.Name2}");
        if (rotate) AssemblyDoc.RotateComponent(); else AssemblyDoc.TranslateComponent();
        if (!IsMovementPage()) throw new InvalidOperationException("Native Move Component page did not open");
        var main = (IntPtr)_sldWorks.IFrameObject().GetHWndx64();
        NativeControls.SetChecked(MovementOption(main, 53401, 6330), true);
        NativeControls.SetChecked(MovementOption(main, 53403, 5157), true);
        NativeControls.SetChecked(NativeControls.Find(main, 53402), false);
        var modeId = rotate ? 5120 : 5119;
        NativeControls.SelectIndex(NativeControls.Find(main, modeId), modeId, rotate ? 2 : 3);
        var unit = (UserUnit)_modelDoc.GetUserUnit((int)(rotate ? swUserUnitsType_e.swAngleUnit : swUserUnitsType_e.swLengthUnit));
        var fields = rotate ? new[] { 4339, 4346, 4353 } : new[] { 4338, 4345, 4352 };
        for (var axis = 0; axis < 3; axis++) {
            var entry = values[axis].ToString("R", CultureInfo.InvariantCulture) + (rotate ? "rad" : "m");
            NativeControls.EnterNumber(NativeControls.Find(main, fields[axis]), entry, unit);
        }
        NativeControls.Send(NativeControls.Find(main, rotate ? 2405 : 2404), 0xF5);
        FinishMovement();
    }

    private static IntPtr MovementOption(IntPtr main, int section, int id) {
        var control = NativeControls.Find(main, id, visibleOnly: false);
        if (!NativeControls.IsWindowVisible(control)) NativeControls.Send(NativeControls.Find(main, section), 0xF5);
        return NativeControls.Find(main, id);
    }

    private void FinishMovement() {
        if (!_modelDoc.Extension.RunCommand((int)swCommands_e.swCommands_Ok_Command, "") || IsMovementPage())
            throw new InvalidOperationException("Native Move Component command did not close");
    }
}
