using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ASD;

/// <summary>
/// WinUI 3 has no public API to set an element's mouse cursor (FrameworkElement has no
/// "Cursor" property like WPF, and UIElement.ProtectedCursor is protected). This helper
/// works around that:
///   - Hand cursor: uses the built-in system "Hand" shape, no file needed.
///   - Custom pointer: loads any .cur/.ani file via a small native interop call.
/// In both cases the cursor is actually applied through reflection on the protected
/// ProtectedCursor property, which is the standard, well-known workaround for this gap.
/// </summary>
public static class CursorHelper
{
    // Cursor files in Assets/Cursors:
    //   Li_pointer.cur / Li_hand.cur  = LIGHT (white) cursors  → used on dark backgrounds
    //   Dr_pointer.cur / Dr_hand.cur  = DARK  (black) cursors  → used on light backgrounds
    private static InputCursor? _ptrLight, _ptrDark, _handLight, _handDark;
    private static InputCursor _ptr = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private static InputCursor _hand = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private static bool _dark;

    // Raw HCURSOR handles: the window frame (title-bar drag area, min/max/close) is not XAML,
    // so it can only be given a cursor through the native WM_SETCURSOR message.
    private static nint _ptrLightH, _ptrDarkH, _handLightH, _handDarkH;

    private static WeakReference<UIElement>? _root;
    private static readonly List<WeakReference<UIElement>> _handTargets = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, object> _seen = new();

    /// <summary>
    /// Loads the four cursors once at startup (any file may be missing: it falls back to the
    /// other colour, then to the system arrow / hand). Call once from MainWindow's constructor.
    /// </summary>
    public static void Initialize(UIElement rootElement, string cursorsFolder)
    {
        _root = new WeakReference<UIElement>(rootElement);

        _ptrLight = TryLoad(cursorsFolder, out _ptrLightH, "Li_pointer.cur", "Li_poitner.cur", "pointer.cur");
        _ptrDark  = TryLoad(cursorsFolder, out _ptrDarkH, "Dr_pointer.cur", "Dr_poitner.cur", "pointer.cur");
        _handLight = TryLoad(cursorsFolder, out _handLightH, "Li_hand.cur", "hand.cur");
        _handDark  = TryLoad(cursorsFolder, out _handDarkH, "Dr_hand.cur", "hand.cur");

        Apply();
    }

    /// <summary>
    /// Switches between the white cursors (dark theme) and the black cursors (light theme)
    /// and updates every element that already has a cursor.
    /// </summary>
    public static void SetDarkTheme(bool dark)
    {
        _dark = dark;
        Apply();
    }

    private static void Apply()
    {
        _ptr = (_dark ? _ptrLight ?? _ptrDark : _ptrDark ?? _ptrLight)
               ?? InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        _hand = (_dark ? _handLight ?? _handDark : _handDark ?? _handLight)
                ?? InputSystemCursor.Create(InputSystemCursorShape.Hand);

        if (_root != null && _root.TryGetTarget(out var root)) ChangeCursor(root, _ptr);

        _handTargets.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in _handTargets.ToList())
            if (w.TryGetTarget(out var el)) ChangeCursor(el, _hand);
    }

    private static InputCursor? TryLoad(string folder, out nint handle, params string[] names)
    {
        handle = 0;
        foreach (var name in names)
        {
            try
            {
                var path = Path.Combine(folder, name);
                if (!File.Exists(path)) continue;
                var h = LoadCursorFromFileW(path);
                if (h == 0) continue;
                handle = h;
                var c = CreateCursorFromHCursor(h);
                if (c != null) return c;
            }
            catch { /* try the next name */ }
        }
        return null;
    }

    private static void ApplyHand(UIElement element)
    {
        if (!_seen.TryGetValue(element, out _))
        {
            _seen.Add(element, new object());
            _handTargets.Add(new WeakReference<UIElement>(element));
        }
        ChangeCursor(element, _hand);
    }

    /// <summary>
    /// Applies the hand cursor to every Button, ToggleButton, RadioButton, CheckBox, ToggleSwitch and Slider found in the visual
    /// tree under <paramref name="root"/>. Call this when the visual tree is built (Loaded).
    /// </summary>
    public static void ApplyHandCursorToButtons(DependencyObject root)
    {
        foreach (var button in FindDescendants<Button>(root)) ApplyHand(button);
        // ToggleButton also covers CheckBox and RadioButton (they derive from it)
        foreach (var toggle in FindDescendants<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>(root)) ApplyHand(toggle);
        foreach (var sw in FindDescendants<ToggleSwitch>(root)) ApplyHand(sw);
        foreach (var slider in FindDescendants<Slider>(root)) ApplyHand(slider);
    }

    /// <summary>
    /// Applies the hand cursor to a NavigationView's menu and footer items.
    /// </summary>
    public static void ApplyHandCursorToNavItems(NavigationView navView)
    {
        foreach (var item in navView.MenuItems.OfType<UIElement>()) ApplyHand(item);
        foreach (var item in navView.FooterMenuItems.OfType<UIElement>()) ApplyHand(item);
    }

    /// <summary>Gives an element (e.g. the root of a secondary window) the app's pointer cursor.</summary>
    public static void ApplyPointer(UIElement element) => ChangeCursor(element, _ptr);

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindDescendants<T>(child))
                yield return descendant;
        }
    }

    // ───────── window frame (title-bar drag area + caption buttons) ─────────
    // With ExtendsContentIntoTitleBar the empty part of the title bar and the min/max/close
    // buttons belong to the non-client area. Windows owns the cursor there, so XAML's
    // ProtectedCursor has no effect. We intercept WM_SETCURSOR on the top-level window instead.
    private const uint WM_SETCURSOR = 0x0020;
    private const int HTCAPTION = 2, HTSYSMENU = 3, HTMINBUTTON = 8, HTMAXBUTTON = 9, HTCLOSE = 20;
    private const int IDC_ARROW = 32512, IDC_HAND = 32649;

    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nint id, nint data);
    private static readonly List<SubclassProc> _subclassProcs = new();   // static: must stay alive as long as the windows
    private static nint _nextSubclassId = 1;

    private static nint PointerHandle()
    {
        var h = _dark ? (_ptrLightH != 0 ? _ptrLightH : _ptrDarkH) : (_ptrDarkH != 0 ? _ptrDarkH : _ptrLightH);
        return h != 0 ? h : LoadCursorW(0, IDC_ARROW);
    }

    private static nint HandHandle()
    {
        var h = _dark ? (_handLightH != 0 ? _handLightH : _handDarkH) : (_handDarkH != 0 ? _handDarkH : _handLightH);
        return h != 0 ? h : LoadCursorW(0, IDC_HAND);
    }

    /// <summary>Gives the title-bar drag area your pointer, and the caption buttons your hand cursor.</summary>
    public static void HookWindowFrame(nint hwnd)
    {
        SubclassProc proc = (h, msg, wParam, lParam, id, data) =>
        {
            if (msg == WM_SETCURSOR)
            {
                int hit = (int)((long)lParam & 0xFFFF);
                nint cursor = hit switch
                {
                    HTCAPTION or HTSYSMENU => PointerHandle(),
                    HTMINBUTTON or HTMAXBUTTON or HTCLOSE => HandHandle(),
                    _ => 0,
                };
                if (cursor != 0) { SetCursor(cursor); return 1; }
            }
            return DefSubclassProc(h, msg, wParam, lParam);
        };
        _subclassProcs.Add(proc);
        SetWindowSubclass(hwnd, proc, _nextSubclassId++, 0);
    }

    // ProtectedCursor is protected on UIElement with no public equivalent, so it's set via
    // reflection. This is the community-standard workaround (also used by Microsoft's own
    // samples for this exact gap in the WinAppSDK).
    private static void ChangeCursor(UIElement element, InputCursor? cursor)
    {
        try
        {
            typeof(UIElement).InvokeMember(
                "ProtectedCursor",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty,
                null, element, new object?[] { cursor });
        }
        catch
        {
            // Cosmetic only: never let a cursor failure crash the app.
        }
    }

    private static InputCursor? CreateCursorFromHCursor(nint hcursor)
    {
        if (hcursor == 0) return null;

        const string classId = "Microsoft.UI.Input.InputCursor";
        _ = WindowsCreateString(classId, classId.Length, out var hs);
        _ = RoGetActivationFactory(hs, typeof(IActivationFactory).GUID, out var factory);
        _ = WindowsDeleteString(hs);

        if (factory is not IInputCursorStaticsInterop interop)
            return null;

        interop.CreateFromHCursor(hcursor, out var cursorAbi);
        if (cursorAbi == 0) return null;

        return WinRT.MarshalInspectable<InputCursor>.FromAbi(cursorAbi);
    }

    [ComImport, Guid("ac6f5065-90c4-46ce-beb7-05e138e54117"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInputCursorStaticsInterop
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();

        [PreserveSig]
        int CreateFromHCursor(nint hcursor, out nint inputCursor);
    }

    [ComImport, Guid("00000035-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivationFactory
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();

        [PreserveSig]
        int ActivateInstance(out nint instance);
    }

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")]
    private static extern int RoGetActivationFactory(nint runtimeClassId, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IActivationFactory factory);

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadCursorFromFileW(string name);

    [DllImport("user32", SetLastError = true)]
    private static extern nint LoadCursorW(nint hInstance, nint lpCursorName);

    [DllImport("user32")]
    private static extern nint SetCursor(nint hCursor);

    [DllImport("comctl32", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nint uIdSubclass, nint dwRefData);

    [DllImport("comctl32")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string? sourceString, int length, out nint @string);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0", CharSet = CharSet.Unicode)]
    private static extern int WindowsDeleteString(nint @string);
}
