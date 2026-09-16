using System.Diagnostics;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace Sldworks.Core.Utils;

internal static class NativeControls {
    internal static IntPtr Find(IntPtr parent, int id, bool visibleOnly = true) {
        var matches = new List<IntPtr>();
        EnumChildWindows(parent, (window, _) => {
            if (GetDlgCtrlID(window) == id && (!visibleOnly || IsWindowVisible(window))) matches.Add(window);
            return true;
        }, IntPtr.Zero);
        return matches.Count == 1 ? matches[0]
            : throw new NotSupportedException($"Expected one native control {id}, found {matches.Count}");
    }

    internal static IntPtr Send(IntPtr window, uint message, IntPtr wParam = default, IntPtr lParam = default) {
        if (SendMessageTimeout(window, message, wParam, lParam, 2, 5000, out var result) == IntPtr.Zero)
            throw new InvalidOperationException($"Native control message {message:X} failed");
        return result;
    }

    internal static void SelectIndex(IntPtr combo, int id, int index) {
        if (Send(combo, 0x14E, (IntPtr)index) != (IntPtr)index)
            throw new InvalidOperationException($"Cannot select native option {id}:{index}");
        Send(GetParent(combo), 0x111, (IntPtr)((1 << 16) | id), combo);
    }

    internal static void SetChecked(IntPtr control, bool value) {
        if ((Send(control, 0xF0) != IntPtr.Zero) == value) return;
        if (!IsWindowEnabled(control)) throw new InvalidOperationException("Native checkbox or radio button is disabled");
        Send(control, 0xF5);
        if ((Send(control, 0xF0) != IntPtr.Zero) != value)
            throw new InvalidOperationException("Native checkbox or radio button did not update");
    }

    internal static void EnterNumber(IntPtr field, string entry, UserUnit unit) {
        if (!IsWindowEnabled(field)) throw new InvalidOperationException("Native numeric field is disabled");
        Send(field, 0x201, (IntPtr)1, (IntPtr)((5 << 16) | 5));
        Send(field, 0x202, IntPtr.Zero, (IntPtr)((5 << 16) | 5));
        Send(field, 0xB1, IntPtr.Zero, (IntPtr)(-1));
        foreach (var character in entry) Send(field, 0x102, (IntPtr)character, (IntPtr)1);
        if (!PostMessage(field, 0x100, (IntPtr)9, IntPtr.Zero) || !PostMessage(field, 0x101, (IntPtr)9, IntPtr.Zero))
            throw new InvalidOperationException("Cannot commit native numeric field");
        var timer = Stopwatch.StartNew();
        while (true) {
            // Posted Tab must run on SolidWorks' UI thread before Apply reads the value.
            System.Windows.Forms.Application.DoEvents();
            var text = Text(field);
            var value = 0.0;
            if (text != entry && unit.ConvertToSystemValue(text, ref value) && double.IsFinite(value)) return;
            if (timer.ElapsedMilliseconds > 2000)
                throw new InvalidOperationException($"Native numeric input did not commit: {text}");
            Thread.Sleep(10);
        }
    }

    private static string Text(IntPtr window) {
        var buffer = Marshal.AllocHGlobal(512);
        try {
            Send(window, 0xD, (IntPtr)256, buffer);
            return Marshal.PtrToStringUni(buffer) ?? "";
        } finally { Marshal.FreeHGlobal(buffer); }
    }

    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
}
