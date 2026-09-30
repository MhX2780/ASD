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
    private static InputCursor? _handCursor;
    private static string _cursorsFolder = string.Empty;

    /// <summary>
    /// Loads both cursors once at startup:
    ///   - Assets/Cursors/pointer.cur → the app's default cursor (applied to <paramref name="rootElement"/>).
    ///   - Assets/Cursors/hand.cur    → used for Buttons/Nav items instead of the system hand shape.
    /// Either file can be missing — pointer falls back to the system arrow, hand falls back
    /// to the built-in system "Hand" shape. Call this once, from MainWindow's constructor,
    /// before ApplyHandCursorToButtons/ApplyHandCursorToNavItems are used anywhere.
    /// </summary>
    public static void Initialize(UIElement rootElement, string cursorsFolder)
    {
        _cursorsFolder = cursorsFolder;
        SetCustomPointer(rootElement, Path.Combine(cursorsFolder, "pointer.cur"));
        _handCursor = TryLoadCustomHand(cursorsFolder) ?? InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private static InputCursor? TryLoadCustomHand(string cursorsFolder)
    {
        try
        {
            var path = Path.Combine(cursorsFolder, "hand.cur");
            if (!File.Exists(path)) return null;
            return LoadCursorFromFile(path);
        }
        catch
        {
            return null; // fall back to the system hand shape
        }
    }

    private static InputCursor HandCursor => _handCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Hand);

    /// <summary>
    /// Sets the app's default ("normal") cursor from a .cur/.ani file. If the file is
    /// missing or invalid, this does nothing and the system arrow keeps being used.
    /// </summary>
    private static void SetCustomPointer(UIElement rootElement, string curFilePath)
    {
        try
        {
            if (!File.Exists(curFilePath)) return;
            var cursor = LoadCursorFromFile(curFilePath);
            if (cursor != null)
                ChangeCursor(rootElement, cursor);
        }
        catch
        {
            // Missing/invalid cursor file — fall back to the default arrow rather than crash.
        }
    }

    /// <summary>
    /// Applies the hand cursor to every Button found in the visual tree under <paramref name="root"/>.
    /// Call this from a Page's Loaded event (the visual tree must already be built).
    /// </summary>
    public static void ApplyHandCursorToButtons(DependencyObject root)
    {
        foreach (var button in FindDescendants<Button>(root))
            ChangeCursor(button, HandCursor);
    }

    /// <summary>
    /// Applies the hand cursor to a NavigationView's menu and footer items.
    /// </summary>
    public static void ApplyHandCursorToNavItems(NavigationView navView)
    {
        foreach (var item in navView.MenuItems.OfType<UIElement>())
            ChangeCursor(item, HandCursor);
        foreach (var item in navView.FooterMenuItems.OfType<UIElement>())
            ChangeCursor(item, HandCursor);
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
