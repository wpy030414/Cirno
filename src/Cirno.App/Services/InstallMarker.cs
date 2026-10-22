using Microsoft.Win32;

namespace Cirno.App.Services;

/// <summary>
/// 安装状态标记。
/// <para>
/// 写入 <c>HKCU\Software\Cirno\ShutdownSupported</c>，供 MSI 判断
/// 「已安装的版本是否支持 shutdown 开关」。旧版本不认识这个开关，
/// 安装器若贸然调用，它会当成普通启动而弹窗。
/// </para>
/// </summary>
public static class InstallMarker
{
    private const string KeyPath = @"Software\Cirno";
    private const string ValueName = "ShutdownSupported";

    /// <summary>标记当前版本支持优雅关闭。启动时调用一次即可。</summary>
    public static void MarkShutdownSupported()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key?.SetValue(ValueName, 1, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            // 写不进去不该影响应用运行：只是让安装器少一条兜底路径
        }
    }
}
