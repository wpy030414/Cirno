using System.Runtime.InteropServices;

namespace Cirno.Agents.Claude;

/// <summary>
/// 把文件移入 Windows 回收站。
/// <para>
/// .NET 10 共享框架里没有 Microsoft.VisualBasic 的 FileIO 回收站 API（那在 WindowsDesktop
/// 框架 / VB NuGet 包里，会把 WinForms 拖进适配器），所以直接 P/Invoke SHFileOperationW。
/// 声明已对照本机 Windows SDK shellapi.h（字段序：hwnd/wFunc/pFrom/pTo/fFlags/
/// fAnyOperationsAborted/hNameMappings/lpszProgressTitle）。
/// </para>
/// </summary>
internal static class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>移入回收站。成功返回 true；任何失败（权限、占用、取消）返回 false，不抛异常。</summary>
    public static bool TrySend(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCTW
            {
                wFunc = FO_DELETE,
                // pFrom 是双 NUL 结尾的路径列表：字符串封送补一个 NUL，这里再补一个
                pFrom = path + "\0",
                pTo = null,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
            };

            // SHFileOperation 对线程单元敏感（内部走 OLE），专用 STA 线程跑完即收
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var hr = SHFileOperationW(ref op);
                    tcs.SetResult(hr == 0 && !op.fAnyOperationsAborted);
                }
                catch
                {
                    tcs.SetResult(false);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task.GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }
}
