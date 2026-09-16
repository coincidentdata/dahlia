using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Sldworks.Core.Definitions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swcommands;
using SolidWorks.Interop.swconst;

namespace Sldworks.Core.Handlers.Shared;

// SW quirk: PromptBodiesToKeepNotify only fires on PartDoc (cast required); handler returns 1 to continue / 0 to cancel.
internal sealed class BodiesToKeepScope : IDisposable {
    // P/Invoke surface for posting an Enter keystroke at SW's currently-focused
    // window. Targets SW directly via ISldWorks.IFrameObject().GetHWndx64() +
    // GetGUIThreadInfo on SW's UI thread, so it works regardless of which app
    // has the system foreground. Used by FixupCutSweepViaEditOk to dismiss the
    // CutSweep property page and any follow-up bodies-to-keep modal.
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP   = 0x0101;
    private const int VK_RETURN   = 0x0D;

    // Post Enter WM_KEYDOWN/WM_KEYUP to SW's currently-focused window inside
    // its UI thread. Doesn't require SW to be the foreground app — the
    // message is queued to the specific hwnd regardless of focus state.
    private static void PostEnterToSolidWorks(File file) {
        IntPtr swMain;
        try {
            swMain = (IntPtr)file.SldWorks.IFrameObject().GetHWndx64();
        } catch (Exception ex) {
            SldworksLog.Information("PostEnterToSolidWorks: IFrameObject lookup failed: {Type}: {Msg}",
                ex.GetType().Name, ex.Message);
            return;
        }
        if (swMain == IntPtr.Zero) return;
        var threadId = GetWindowThreadProcessId(swMain, IntPtr.Zero);
        var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        IntPtr target = swMain;
        if (GetGUIThreadInfo(threadId, ref gti)) {
            if (gti.hwndFocus != IntPtr.Zero) target = gti.hwndFocus;
            else if (gti.hwndActive != IntPtr.Zero) target = gti.hwndActive;
        }
        SldworksLog.Information(
            "PostEnterToSolidWorks: posting Enter to hwnd {Hwnd} (main={Main}, focus={Focus}, active={Active})",
            target, swMain, gti.hwndFocus, gti.hwndActive);
        PostMessage(target, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
        PostMessage(target, WM_KEYUP,   (IntPtr)VK_RETURN, IntPtr.Zero);
    }

    private readonly PartDoc _partDoc;
    private readonly DPartDocEvents_PromptBodiesToKeepNotifyEventHandler _handler;
    private bool _disposed;

    private BodiesToKeepScope(PartDoc partDoc, DPartDocEvents_PromptBodiesToKeepNotifyEventHandler handler) {
        _partDoc = partDoc;
        _handler = handler;
    }

    internal static BodiesToKeepScope Register(File file) {
        var partDoc = (PartDoc)file.ModelDoc;
        var keepers = file.PendingBodiesToKeep;

        DPartDocEvents_PromptBodiesToKeepNotifyEventHandler handler = (object swFeat, ref object bodiesObj) => {
            var feature = (Feature)swFeat;
            var allBodies = (object[])bodiesObj;

            // SW quirk: SetBodiesToKeep needs DispatchWrapper-wrapped bodies for COM marshalling as IDispatch.
            List<DispatchWrapper> toKeep;
            if (keepers is null || keepers.Count == 0) {
                toKeep = allBodies.Select(b => new DispatchWrapper(b)).ToList();
            } else {
                // Match by COM identity, fall back to centroid+volume when the handle has been invalidated by a rebuild.
                var keepDefs = keepers
                    .Select(b => DefinitionCapture.Capture(b))
                    .ToList();
                toKeep = [];
                foreach (var bodyObj in allBodies) {
                    var body = (Body2)bodyObj;
                    if (keepers.Contains(body)
                        || keepDefs.Any(kd => BodyDefMatches(kd, DefinitionCapture.Capture(body)))) {
                        toKeep.Add(new DispatchWrapper(bodyObj));
                    }
                }
            }

            feature.SetBodiesToKeep(false, toKeep.ToArray(),
                (int)swInConfigurationOpts_e.swThisConfiguration, null);
            return 1;
        };

        partDoc.PromptBodiesToKeepNotify += handler;
        return new BodiesToKeepScope(partDoc, handler);
    }

    // Apply a list of probe rays against an already-committed Cut* feature:
    // re-fires the cut's PromptBodiesToKeepNotify, ray-resolves each ray
    // against the candidate bodies, and replaces the keep-list so the targeted
    // fragments are discarded. The cut itself was added with all bodies kept;
    // this is the second step that actually performs the drop.
    //
    // SW quirk: IModifyDefinition2 triggers the rebuild + notify. On Cut-Sweep
    // it leaves the feature in a dirty-rebuild state (mass-props drift, stale
    // getters); callers fix this with FixupCutSweepViaEditOk afterward.
    //
    // Throws loudly on:
    //   - ray axis >10 mm from every candidate centroid (probe-vs-target geometry mismatch)
    //   - two probes resolve to the same target body (cluster too tight for the per-attempt margin)
    internal static void ApplyDropBodies(File file, Feature feature, IReadOnlyList<Ray> rays) {
        var partDoc = file.ModelDoc as PartDoc
            ?? throw new InvalidOperationException("ApplyDropBodies: not a PartDoc");
        if (rays.Count == 0) return;

        DPartDocEvents_PromptBodiesToKeepNotifyEventHandler handler = (object swFeat, ref object bodiesObj) => {
            var innerFeature = (Feature)swFeat;
            var allBodiesRaw = (object[])bodiesObj;
            var allBodies = allBodiesRaw.OfType<Body2>().ToList();
            SldworksLog.Information(
                "ApplyDropBodies: PromptBodiesToKeepNotify fired for '{Feature}' with {Total} candidate bodies ({Drops} probes)",
                innerFeature.Name, allBodies.Count, rays.Count);

            var discardSet = new HashSet<Body2>(ReferenceEqualityComparer.Instance);
            foreach (var ray in rays) {
                var hit = File.ResolveBodyByRay(allBodies, ray);
                if (!discardSet.Add(hit)) {
                    throw new InvalidOperationException(
                        "ApplyDropBodies: two probes resolved to the same candidate body — " +
                        "likely a fragment cluster below the per-attempt disambiguation margin");
                }
            }

            var toKeep = new List<DispatchWrapper>();
            foreach (var body in allBodies) {
                if (!discardSet.Contains(body)) {
                    toKeep.Add(new DispatchWrapper(body));
                }
            }
            SldworksLog.Information(
                "ApplyDropBodies: keeping {Keep}/{Total}", toKeep.Count, allBodies.Count);

            innerFeature.SetBodiesToKeep(false, toKeep.ToArray(),
                (int)swInConfigurationOpts_e.swThisConfiguration, null);
            return 1;
        };

        partDoc.PromptBodiesToKeepNotify += handler;
        try {
            var data = feature.GetDefinition()
                ?? throw new InvalidOperationException(
                    $"ApplyDropBodies: feature '{feature.Name}' returned null GetDefinition");
            feature.IModifyDefinition2(data, file.ModelDoc, null);
        } finally {
            partDoc.PromptBodiesToKeepNotify -= handler;
        }
    }

    // On-demand probe generator for a committed Cut* feature. Returns a
    // JsonArray of `{body, ray}` BodyProbe objects, one per discarded fragment
    // (empty for a single-body cut that dropped nothing). The runner calls
    // this AFTER Cut* Inspect and pipes the result straight into
    // `target.drop_bodies(name, probes)`.
    //
    // Why on-demand (not in Inspect): the only way to recover the discarded
    // fragments' geometry is to re-fire `IModifyDefinition2`, which (a) costs
    // a Parasolid rebuild and (b) shifts the feature's surviving body
    // mass-properties by ~1e-7. Non-roundtrip Inspect callers (model authors
    // browsing the feature tree, diff tooling) shouldn't pay that price.
    //
    // SW quirk: the UI-path alternative (FeatEditDef + Ok_Command) blocks on a
    // follow-up dialog the API can't dismiss reliably, so we accept the mass-
    // props drift and live with `IModifyDefinition2` here.
    internal static JsonArray ProbeBodiesToDiscard(File file, Feature feature) {
        var probes = new JsonArray();
        var partDoc = file.ModelDoc as PartDoc;
        if (partDoc is null) return probes;

        DPartDocEvents_PromptBodiesToKeepNotifyEventHandler? handler = null;

        try {
            file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToAfterFeature, feature.Name);

            var survivors = new List<BodyDefinition>();
            var raw = ((IPartDoc)file.ModelDoc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (raw is not null) {
                foreach (Body2 body in raw) {
                    if (body is not null) survivors.Add(DefinitionCapture.Capture(body));
                }
            }

            handler = (object swFeat, ref object bodiesObj) => {
                var innerFeature = (Feature)swFeat;
                var allBodiesRaw = (object[])bodiesObj;
                var allBodies = allBodiesRaw.OfType<Body2>().ToList();
                SldworksLog.Information(
                    "ProbeBodiesToDiscard: PromptBodiesToKeepNotify fired for '{Feature}' with {Count} bodies",
                    innerFeature.Name, allBodies.Count);
                var toKeep = new List<DispatchWrapper>();
                var localDiscards = new List<Body2>();
                foreach (var body in allBodies) {
                    var bodyDef = DefinitionCapture.Capture(body);
                    var alive = survivors.Any(s => BodyDefMatches(s, bodyDef));
                    if (alive) {
                        toKeep.Add(new DispatchWrapper(body));
                    } else {
                        localDiscards.Add(body);
                    }
                }
                // Build one probe ray per discarded body, disambiguating against
                // the full allBodies list (not just other discards) so target's
                // ray hits the intended fragment even when survivors sit close
                // by along the same cardinal direction.
                foreach (var discard in localDiscards) {
                    var ray = File.BuildBodyProbeAgainstSiblings(discard, allBodies);
                    var bodyDef = DefinitionCapture.Capture(discard);
                    // SW quirk: STJ doesn't auto-prepend the `kind` discriminator on
                    // Definition subtypes — `Definition.ToJson()` adds it manually.
                    // Building the wire dict directly (vs serializing BodyProbe via
                    // SerializeToNode) ensures the body field carries
                    // `kind: "body"` so downstream `Definition.FromJson` callers
                    // (e.g. `target.probe(ray, BODY, expected=body)`) can parse it.
                    probes.Add(new JsonObject {
                        ["body"] = bodyDef.ToJson(),
                        ["ray"] = JsonSerializer.SerializeToNode(ray, Definition.JsonOptions),
                    });
                }
                SldworksLog.Information(
                    "ProbeBodiesToDiscard: keeping {Keep}, discarding {Discard}",
                    toKeep.Count, localDiscards.Count);
                innerFeature.SetBodiesToKeep(false, toKeep.ToArray(),
                    (int)swInConfigurationOpts_e.swThisConfiguration, null);
                return 1;
            };
            partDoc.PromptBodiesToKeepNotify += handler;

            var data = feature.GetDefinition();
            if (data is not null) {
                feature.IModifyDefinition2(data, file.ModelDoc, null);
            }
        } finally {
            if (handler is not null) {
                partDoc.PromptBodiesToKeepNotify -= handler;
            }
            // Restore rollback-bar position so downstream feature inspects
            // see the full feature tree active.
            file.MoveRollbackBar(swMoveRollbackBarTo_e.swMoveRollbackBarToEnd);
        }

        return probes;
    }

    // CutSweep fixup: SW leaves the feature in a dirty-rebuild state where
    // getters return stale values. Manual Edit-Feature → OK fixes it; this
    // mirrors that flow programmatically.
    //
    // FeatEditDef opens the property page; RunCommand(Ok_Command) drives the
    // real commit path (firing PromptBodiesToKeepNotify if applicable). But
    // that RunCommand blocks indefinitely on the follow-up bodies-to-keep
    // confirmation modal — so a background thread polls for an active
    // PropertyManagerPage and posts an Enter keystroke targeted at SW's UI
    // thread (via GetGUIThreadInfo + PostMessage) to dismiss it. The
    // targeting works even when SW is in the background.
    internal static void FixupCutSweepViaEditOk(File file, Feature feature) {
        SldworksLog.Information(
            "FixupCutSweepViaEditOk: FeatEditDef + Ok_Command + bg Enter on '{Feature}'", feature.Name);
        file.ModelDoc.ClearSelection2(true);
        feature.Select2(false, -1);
        var wasLocked = file.IsLocked;
        if (wasLocked) file.Unlock();

        using var stop = new CancellationTokenSource();
        var poller = new Thread(() => {
            // Post Enter every 100ms while a PMP is active. Empirically
            // confirmed: `CommandInProgress` reports `false` even during a
            // blocked RunCommand(Ok_Command) waiting on the bodies-to-keep
            // modal, so we don't gate on it. Scoping protection comes from the
            // poller's lifetime — it only runs between this thread's Start and
            // the finally-block Cancel below, which brackets our own
            // FeatEditDef + RunCommand call. Any active PMP we see in that
            // window is one we (or a follow-up modal from our flow) opened.
            //
            // 100ms (not 50ms): gives Ok_Command a wider window to complete
            // and clear the page BEFORE the next Enter post; a too-tight loop
            // can race with Ok closing the page and accidentally land an Enter
            // on whatever comes next.
            try {
                while (!stop.IsCancellationRequested) {
                    Thread.Sleep(100);
                    if (stop.IsCancellationRequested) break;
                    try {
                        var page = file.ModelDoc.Extension.GetActivePropertyManagerPage();
                        if (string.IsNullOrEmpty(page)) continue;
                        SldworksLog.Information(
                            "FixupCutSweepViaEditOk[poller]: posting Enter (page='{Page}')", page);
                        PostEnterToSolidWorks(file);
                    } catch (Exception ex) {
                        SldworksLog.Information(
                            "FixupCutSweepViaEditOk[poller]: ignored {Type}: {Msg}",
                            ex.GetType().Name, ex.Message);
                    }
                }
            } catch (Exception ex) {
                SldworksLog.Information(
                    "FixupCutSweepViaEditOk[poller]: thread exited on {Type}: {Msg}",
                    ex.GetType().Name, ex.Message);
            }
        }) { IsBackground = true, Name = "CutSweepFixupPoller" };
        poller.Start();

        try {
            file.ModelDoc.FeatEditDef();
            var initialPage = file.ModelDoc.Extension.GetActivePropertyManagerPage();
            SldworksLog.Information(
                "FixupCutSweepViaEditOk: FeatEditDef opened page '{Page}'",
                string.IsNullOrEmpty(initialPage) ? "<empty>" : initialPage);
            // Keep firing Ok_Command until the property page actually closes —
            // a single RunCommand can fail to commit if SW shows a follow-up
            // page (e.g. the bodies-to-keep dialog), and the next call
            // dismisses whatever stacked on top. The background poller above
            // handles modal Enter-presses in parallel; this loop handles the
            // property-page Ok specifically. Capped at 5 iterations as a
            // safety net so a stuck page can't infinite-loop.
            const int MaxOks = 5;
            int attempt;
            for (attempt = 0; attempt < MaxOks; attempt++) {
                var pageBefore = file.ModelDoc.Extension.GetActivePropertyManagerPage();
                if (string.IsNullOrEmpty(pageBefore)) {
                    SldworksLog.Information(
                        "FixupCutSweepViaEditOk: page closed after {N} Ok attempt(s)", attempt);
                    break;
                }
                SldworksLog.Information(
                    "FixupCutSweepViaEditOk: Ok_Command attempt {N}/{Max}, page='{Page}'",
                    attempt + 1, MaxOks, pageBefore);
                file.ModelDoc.Extension.RunCommand((int)swCommands_e.swCommands_Ok_Command, "");
                Thread.Sleep(50);
                var pageAfter = file.ModelDoc.Extension.GetActivePropertyManagerPage();
                SldworksLog.Information(
                    "FixupCutSweepViaEditOk: after Ok_Command #{N}, page='{Page}'",
                    attempt + 1, string.IsNullOrEmpty(pageAfter) ? "<empty>" : pageAfter);
            }
            if (attempt >= MaxOks) {
                var stillActive = file.ModelDoc.Extension.GetActivePropertyManagerPage();
                SldworksLog.Warning(
                    "FixupCutSweepViaEditOk: hit {Max}-iteration cap; page still='{Page}' — manual intervention may be required",
                    MaxOks, string.IsNullOrEmpty(stillActive) ? "<empty>" : stillActive);
            }
        } finally {
            stop.Cancel();
            poller.Join();
            if (wasLocked) file.Lock();
        }
        SldworksLog.Information("FixupCutSweepViaEditOk: done");
    }

    private static bool BodyDefMatches(BodyDefinition a, BodyDefinition b) {
        const double tol = Flags.GeometryTolerance;
        return Math.Abs(a.Centroid.X - b.Centroid.X) <= tol
            && Math.Abs(a.Centroid.Y - b.Centroid.Y) <= tol
            && Math.Abs(a.Centroid.Z - b.Centroid.Z) <= tol
            && Math.Abs(a.Volume - b.Volume) <= tol;
    }

    public void Dispose() {
        if (_disposed) return;
        _partDoc.PromptBodiesToKeepNotify -= _handler;
        _disposed = true;
    }
}
