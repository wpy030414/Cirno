using System.Runtime.InteropServices;

namespace Cirno.Shell.Windows;

/// <summary>任务栏进度态。数值与 Win32 TBPFLAG 一致。</summary>
public enum TaskbarProgressState
{
    NoProgress = 0x0,
    Indeterminate = 0x1,
    Normal = 0x2,
    Error = 0x4,
    Paused = 0x8,
}

/// <summary>
/// ITaskbarList3 的最小封装。
/// 必须在收到 `TaskbarButtonCreated` 之后初始化——在窗口激活时抢先调用会静默失败。
/// </summary>
internal sealed class TaskbarIntegration
{
    private const string TaskbarButtonCreatedMessage = "TaskbarButtonCreated";

    private ITaskbarList3? _list;

    public bool IsReady => _list is not null;

    /// <summary>注册窗口消息的 id。需在任何窗口创建前调用一次。</summary>
    public static uint RegisterCreatedMessage()
        => NativeMethods.RegisterWindowMessageW(TaskbarButtonCreatedMessage);

    public void Initialize(IntPtr hwnd)
    {
        if (_list is not null)
        {
            return;
        }

        try
        {
            _list = (ITaskbarList3)new TaskbarInstance();
            _list.HrInit();
            _list.AddTab(hwnd);
        }
        catch (COMException)
        {
            // 任务栏不可用（例如资源管理器重启中）：静默降级，不影响主功能
            _list = null;
        }
    }

    public bool SetProgress(IntPtr hwnd, TaskbarProgressState state, ulong completed = 0, ulong total = 0)
    {
        if (_list is null)
        {
            return false;
        }

        try
        {
            if (state is TaskbarProgressState.Normal or TaskbarProgressState.Error or TaskbarProgressState.Paused)
            {
                _list.SetProgressValue(hwnd, completed, total == 0 ? 1 : total);
            }

            _list.SetProgressState(hwnd, state);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public bool SetOverlay(IntPtr hwnd, IntPtr icon, string description)
    {
        if (_list is null)
        {
            return false;
        }

        try
        {
            _list.SetOverlayIcon(hwnd, icon, description);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public void Dispose(IntPtr hwnd)
    {
        if (_list is null)
        {
            return;
        }

        try
        {
            _list.SetProgressState(hwnd, TaskbarProgressState.NoProgress);
            _list.SetOverlayIcon(hwnd, IntPtr.Zero, string.Empty);
            _list.DeleteTab(hwnd);
        }
        catch (COMException)
        {
        }
        finally
        {
            Marshal.ReleaseComObject(_list);
            _list = null;
        }
    }

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();

        void AddTab(IntPtr hwnd);

        void DeleteTab(IntPtr hwnd);

        void ActivateTab(IntPtr hwnd);

        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);

        void SetProgressState(IntPtr hwnd, TaskbarProgressState flags);

        void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);

        void UnregisterTab(IntPtr hwndTab);

        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);

        void SetTabActive(IntPtr hwndTab, IntPtr hwndInsertBefore, uint dwReserved);

        void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);

        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);

        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);

        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarInstance
    {
    }
}
