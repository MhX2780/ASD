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

        _ptrLight = TryLoad(cursorsFolder, "Li_pointer.cur", "Li_poitner.cur", "pointer.cur");
        _ptrDark  = TryLoad(cursorsFolder, "Dr_pointer.cur", "Dr_poitner.cur", "pointer.cur");
        _handLight = TryLoad(cursorsFolder, "Li_hand.cur", "hand.cur");
        _handDark  = TryLoad(cursorsFolder, "Dr_hand.cur", "hand.cur");

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

    private static InputCursor? TryLoad(string folder, params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                var path = Path.Combine(folder, name);
                if (!File.Exists(path)) continue;
                var c = LoadCursorFromFile(path);
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
    /// Applies the hand cursor to every Button, CheckBox and ToggleSwitch found in the visual
    /// tree under <paramref name="root"/>. Call this when the visual tree is built (Loaded).
    /// </summary>
    public static void ApplyHandCursorToButtons(DependencyObject root)
    {
        foreach (var button in FindDescendants<Button>(root)) ApplyHand(button);
        foreach (var box in FindDescendants<CheckBox>(root)) ApplyHand(box);
        foreach (var sw in FindDescendants<ToggleSwitch>(root)) ApplyHand(sw);
    }

    /// <summary>
    /// Applies the hand cursor to a NavigationView's menu and footer items.
    /// </summary>
    public static void ApplyHandCursorToNavItems(NavigationView navView)
    {
        foreach (var item in navView.MenuItems.OfType<UIElement>()) ApplyHand(item);
        foreach (var item in navView.FooterMenuItems.OfType<UIElement>()) ApplyHand(item);
    }

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

    private static InputCursor? LoadCursorFromFile(string filePath)
    {
        var hcursor = LoadCursorFromFileW(filePath);
        if (hcursor == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return CreateCursorFromHCursor(hcursor);
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

    [DllImport("api-ms-win-core-winrt-string-l1-1-0", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string? sourceString, int length, out nint @string);

    [DllImport("api-ms-win-core-winrt-string-l1-1-0", CharSet = CharSet.Unicode)]
    private static extern int WindowsDeleteString(nint @string);
}
