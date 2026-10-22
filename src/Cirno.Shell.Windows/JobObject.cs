using System.ComponentModel;

namespace Cirno.Shell.Windows;

/// <summary>
/// 子进程 Job Object：句柄关闭时整棵进程树被终止。
/// 这样即使 Cirno 崩溃（不会走优雅退出），也不会留下 claude / codex 孤儿进程。
/// </summary>
internal sealed class JobObject : IDisposable
{
    private readonly IntPtr _handle;

    public JobObject()
    {
        _handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "无法创建 Job Object");
        }

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        if (!NativeMethods.SetInformationJobObject(
                _handle,
                NativeMethods.JobObjectExtendedLimitInformation,
                ref info,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "无法设置 Job Object 限制");
        }
    }

    /// <summary>把进程加入作业。失败返回 false（例如进程已在别的作业里）。</summary>
    public bool Assign(int processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_TERMINATE, false, processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return NativeMethods.AssignProcessToJobObject(_handle, handle);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_handle);
        }
    }
}
