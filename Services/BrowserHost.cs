using System.Runtime.InteropServices;

namespace Daylane.Services;

/// <summary>Reads the site host of the foreground browser tab, and nothing else.
///
/// Ported from Hindsight's <c>capture/browser_url/windows.rs</c>, including the mistakes its
/// comments record. Two of them are load-bearing here:
///
/// <list type="bullet">
/// <item>The tree search must be <b>descendants</b>, not children. Hindsight's matcher defaulted
/// to direct children, found nothing, and looked like "no browser is open" for months: address
/// bars sit deep in the subtree.</item>
/// <item>Candidates are <b>not</b> scored. Trying to identify "the real address bar" breaks the
/// day Chrome renames its omnibox class, or on <c>chrome://</c> URLs that carry no scheme. The
/// first value that parses as a URL wins; an occasional false positive costs one wrong host on
/// one segment, which is cheaper than a matcher that silently stops matching.</item>
/// </list></summary>
internal static class BrowserHost
{
    /// A fixed list rather than a heuristic: guessing wrong means running an expensive UI
    /// Automation walk against every non-browser window. Adding a browser is a one-line change.
    private static readonly string[] KnownBrowsers =
        ["chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "zen"];

    internal static bool IsBrowser(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        string name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return KnownBrowsers.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The longest a host may be and still be a valid DNS name. Anything longer is a
    /// crafted authority, not a site: a URL may carry tens of thousands of labels and still parse,
    /// and the result is persisted verbatim to an uncapped TEXT column once per segment.
    /// WindowTitle is bounded by ForegroundTracker's 512-char buffer; UrlHost has no such natural
    /// limit, so it needs an explicit one.</summary>
    private const int MaxHostLength = 253;

    /// <summary>Host only — never the path, query or fragment. The rest is not truncated at
    /// display time, it is never persisted.</summary>
    internal static string? HostFromUrl(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        // Empty host is not merely uninteresting, it is dangerous to return: "about:blank" and
        // "file:///C:/x" parse as absolute URIs with Host == "", and an empty string would count
        // as a hit and stop the walk before it ever reached the address bar.
        if (string.IsNullOrWhiteSpace(uri.Host) || uri.Host.Length > MaxHostLength)
        {
            return null;
        }

        return uri.Host;
    }

    /// <summary>Walks the foreground window's UI Automation tree for the first Edit or Document
    /// control whose value parses as a URL, and returns its host.
    ///
    /// Costs tens of milliseconds, so the caller must invoke it <b>only at segment open, only for
    /// a known browser, only when the user enabled the setting</b> — never on the one-second poll
    /// path. Returns null for every failure, including a COM failure: capture is best-effort and
    /// must never be able to take the tracker down.</summary>
    internal static string? TryGetForegroundHost() => HostForWindow();

    private static string? HostForWindow()
    {
        try
        {
            // Inside the try with everything else. A missing user32.dll export is vanishingly
            // unlikely, but this runs on a threadpool timer callback where an escaping exception
            // is an unhandled process crash -- which is the entire class of failure the catch
            // below exists to prevent.
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }

            // The RCWs below are released by the finalizer, not deterministically. Measured over
            // 30 consecutive walks this cost 41 KB of heap growth with no leak, so it stays a
            // note rather than a Marshal.FinalReleaseComObject rewrite -- which would have to
            // release every element in the array and is easy to get wrong.
            Type? comType = Type.GetTypeFromCLSID(CLSID_CUIAutomation);
            if (comType is null || Activator.CreateInstance(comType) is not IUIAutomation automation)
            {
                return null;
            }

            IUIAutomationElement root = automation.ElementFromHandle(hwnd);

            // Edit OR Document: Chromium's omnibox is an Edit, Firefox's is a Document on some
            // builds, and page content is a Document. One search, not two -- a descendant walk is
            // the expensive part and doing it twice doubles the cost for no extra coverage.
            IUIAutomationCondition urlBearing = automation.CreateOrCondition(
                automation.CreatePropertyCondition(ControlTypePropertyId, EditControlTypeId),
                automation.CreatePropertyCondition(ControlTypePropertyId, DocumentControlTypeId));

            IUIAutomationElementArray found = root.FindAll(TreeScopeDescendants, urlBearing);
            int length = found.GetLength();

            for (int i = 0; i < length; i++)
            {
                IUIAutomationElement element = found.GetElement(i);

                foreach (string? candidate in Candidates(element))
                {
                    string? host = HostFromUrl(candidate);
                    if (host is not null)
                    {
                        return host;
                    }
                }
            }

            return null;
        }
        catch (Exception)
        {
            // Deliberately total. The automation server can be missing, busy, in a different
            // apartment, or dying along with the window we are asking about; none of that is
            // worth a single lost segment, let alone a crash.
            return null;
        }
    }

    /// <summary>Value pattern, then legacy IAccessible value, then the element name — the same
    /// order and the same fallbacks Hindsight settled on. Each is independently optional: a
    /// control that does not support a pattern returns null rather than throwing, and a control
    /// that throws anyway is skipped rather than aborting the walk.</summary>
    private static IEnumerable<string?> Candidates(IUIAutomationElement element)
    {
        yield return TryRead(() =>
            (element.GetCurrentPattern(ValuePatternId) as IUIAutomationValuePattern)
                ?.GetCurrentValue());

        yield return TryRead(() =>
            (element.GetCurrentPattern(LegacyIAccessiblePatternId)
                as IUIAutomationLegacyIAccessiblePattern)?.GetCurrentValue());

        yield return TryRead(element.GetCurrentName);
    }

    private static string? TryRead(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // ---------------------------------------------------------------------------------------
    // UI Automation COM interop.
    //
    // Hand-written rather than generated: a <COMReference> to the registered UIAutomationClient
    // type library needs the .NET Framework MSBuild ResolveComReference task, which the .NET SDK
    // does not have (MSB4803), so `dotnet build` cannot produce it. WPF's
    // System.Windows.Automation is not an option either -- this is an Avalonia app.
    //
    // That makes vtable ORDER load-bearing and silent when wrong: a mis-ordered slot calls a
    // different function, and the catch above would report it as "no browser open". The order
    // and the IIDs below were dumped from the registered type library in
    // C:\Windows\System32\UIAutomationCore.dll, not from memory. Every method up to and
    // including the last one called must be declared, in that exact order; the placeholders are
    // there to occupy their slots and MUST NOT be called. Re-check with:
    //   ITypeLib on UIAutomationCore.dll, ordering FUNCDESC by oVft (slot = oVft / IntPtr.Size,
    //   the first interface method being slot 3, after IUnknown).
    // ---------------------------------------------------------------------------------------

    private static readonly Guid CLSID_CUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");

    private const int TreeScopeDescendants = 4;
    private const int ControlTypePropertyId = 30003;
    private const int EditControlTypeId = 50004;
    private const int DocumentControlTypeId = 50030;
    private const int ValuePatternId = 10002;
    private const int LegacyIAccessiblePatternId = 10018;

    [ComImport]
    [Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();                     // 1
        void CompareRuntimeIds();                   // 2
        void GetRootElement();                      // 3

        IUIAutomationElement ElementFromHandle(IntPtr hwnd);   // 4

        void ElementFromPoint();                    // 5
        void GetFocusedElement();                   // 6
        void GetRootElementBuildCache();            // 7
        void ElementFromHandleBuildCache();         // 8
        void ElementFromPointBuildCache();          // 9
        void GetFocusedElementBuildCache();         // 10
        void CreateTreeWalker();                    // 11
        void GetControlViewWalker();                // 12
        void GetContentViewWalker();                // 13
        void GetRawViewWalker();                    // 14
        void GetRawViewCondition();                 // 15
        void GetControlViewCondition();             // 16
        void GetContentViewCondition();             // 17
        void CreateCacheRequest();                  // 18
        void CreateTrueCondition();                 // 19
        void CreateFalseCondition();                // 20

        IUIAutomationCondition CreatePropertyCondition(       // 21
            int propertyId,
            [MarshalAs(UnmanagedType.Struct)] object value);

        void CreatePropertyConditionEx();           // 22
        void CreateAndCondition();                  // 23
        void CreateAndConditionFromArray();         // 24
        void CreateAndConditionFromNativeArray();   // 25

        IUIAutomationCondition CreateOrCondition(             // 26
            IUIAutomationCondition condition1,
            IUIAutomationCondition condition2);
    }

    [ComImport]
    [Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();                            // 1
        void GetRuntimeId();                        // 2
        void FindFirst();                           // 3

        IUIAutomationElementArray FindAll(                    // 4
            int scope,
            IUIAutomationCondition condition);

        void FindFirstBuildCache();                 // 5
        void FindAllBuildCache();                   // 6
        void BuildUpdatedCache();                   // 7
        void GetCurrentPropertyValue();             // 8
        void GetCurrentPropertyValueEx();           // 9
        void GetCachedPropertyValue();              // 10
        void GetCachedPropertyValueEx();            // 11
        void GetCurrentPatternAs();                 // 12
        void GetCachedPatternAs();                  // 13

        /// <summary>Returns null, not an error, when the pattern is unsupported.</summary>
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetCurrentPattern(int patternId);             // 14

        void GetCachedPattern();                    // 15
        void GetCachedParent();                     // 16
        void GetCachedChildren();                   // 17
        void GetCurrentProcessId();                 // 18
        void GetCurrentControlType();               // 19
        void GetCurrentLocalizedControlType();      // 20

        [return: MarshalAs(UnmanagedType.BStr)]
        string? GetCurrentName();                             // 21
    }

    [ComImport]
    [Guid("14314595-b4bc-4055-95f2-58f2e42c9855")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int GetLength();                                      // 1

        IUIAutomationElement GetElement(int index);           // 2
    }

    /// <summary>Opaque marker: the real interface declares no methods of its own.</summary>
    [ComImport]
    [Guid("352ffba8-0973-437c-a61f-f64cafd81df9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition
    {
    }

    [ComImport]
    [Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationValuePattern
    {
        void SetValue();                            // 1

        [return: MarshalAs(UnmanagedType.BStr)]
        string? GetCurrentValue();                            // 2
    }

    [ComImport]
    [Guid("828055ad-355b-4435-86d5-3b51c14a9b1b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationLegacyIAccessiblePattern
    {
        void Select();                              // 1
        void DoDefaultAction();                     // 2
        void SetValue();                            // 3
        void GetCurrentChildId();                   // 4
        void GetCurrentName();                      // 5

        [return: MarshalAs(UnmanagedType.BStr)]
        string? GetCurrentValue();                            // 6
    }
}
