namespace ASD;

/// <summary>
/// Progress bar on the app's taskbar button (the same thing WPF's TaskbarItemInfo or the old
/// TaskbarManager.SetProgressState / SetProgressValue do), through the Windows ITaskbarList3 interface.
/// Never throws: if the interface isn't available the calls are simply ignored.
/// </summary>
internal sealed class TaskbarProgress
{
    private enum Flag { NoProgress = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

    // vtable order matters: every method of ITaskbarList and ITaskbarList2 must be declared, in order,
    // before the two ITaskbarList3 methods that are actually used.
    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        // ITaskbarList3
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        [PreserveSig] int SetProgressState(IntPtr hwnd, Flag tbpFlags);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListCoClass { }

    private readonly IntPtr _hwnd;
    private readonly ITaskbarList3? _list;
    private Flag _state = Flag.NoProgress;
    private ulong _percent;

    /// <param name="hwnd">The window whose taskbar button shows the progress (must be a window that HAS a taskbar button).</param>
    public TaskbarProgress(IntPtr hwnd)
    {
        _hwnd = hwnd;
        try
        {
            var list = (ITaskbarList3)new TaskbarListCoClass();
            list.HrInit();
            _list = list;
        }
        catch { _list = null; }
    }

    /// <summary>Length unknown: green "marquee" on the taskbar button.</summary>
    public void Indeterminate() => SetState(Flag.Indeterminate);

    /// <summary>Known progress, 0-100.</summary>
    public void Value(double percent)
    {
        _percent = (ulong)Math.Clamp(Math.Round(percent), 0, 100);
        SetState(Flag.Normal);
        try { _list?.SetProgressValue(_hwnd, _percent, 100); } catch { }
    }

    /// <summary>Red bar (keeps the progress reached so far, or a full bar if there was none).</summary>
    public void Error()
    {
        try { _list?.SetProgressValue(_hwnd, _percent > 0 ? _percent : 100, 100); } catch { }
        SetState(Flag.Error);
    }

    /// <summary>Removes the progress from the taskbar button.</summary>
    public void Clear()
    {
        _percent = 0;
        SetState(Flag.NoProgress);
    }

    private void SetState(Flag flag)
    {
        if (_state == flag) return;
        _state = flag;
        try { _list?.SetProgressState(_hwnd, flag); } catch { }
    }
}
