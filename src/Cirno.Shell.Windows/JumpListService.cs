using System.Runtime.InteropServices;

namespace Cirno.Shell.Windows;

/// <summary>
/// 跳转列表（Jump List）构造器。
/// <para>
/// 桌面应用要拿到任务栏右键菜单里的自定义项，必须：
/// 1) 进程启动早期调用 <c>SetCurrentProcessExplicitAppUserModelID</c> 声明 AUMID；
/// 2) 用 <c>ICustomDestinationList</c> 提交一份任务列表（每项是一个带参数的 IShellLink）。
/// </para>
/// <para>
/// 任务列表被系统缓存：即使应用没运行，右键菜单也显示上一次提交的版本。
/// 因此这里的任务必须是静态的、语义稳定的。
/// </para>
/// <para>
/// COM 参数封送的坑（踩过）：
/// <list type="bullet">
/// <item><c>AddUserTasks</c> 收的是 <c>IObjectArray*</c>，直接传 IObjectCollection 的 IUnknown 指针会让被调方按错 vtable 调用而崩溃；</item>
/// <item><c>IPropertyStore::SetValue</c> 的两个参数都是「引用」，必须按引用传；</item>
/// <item>接口 IID 必须以本机 Windows SDK 头文件为准——网上流传的 <c>ICustomDestinationList</c> IID 是错的。</item>
/// </list>
/// </para>
/// </summary>
public sealed class JumpListService
{
    private const string ClsidDestinationList = "77f10cf0-3db5-4966-b520-b7c54fd35ed6";
    private const string ClsidEnumerableObjectCollection = "2d3468c1-36a7-43b6-ac24-d3f02fd9607a";
    private const string ClsidShellLink = "00021401-0000-0000-c000-000000000046";

    private const string IidObjectArray = "92ca9dcd-5622-4bba-a805-5e9f541bd8c9";
    private const string IidCustomDestinationList = "6332debf-87b5-4670-90c0-5e57b408a49e";

    /// <summary>Cirno 的应用用户模型 id。任务栏分组与跳转列表都以此为准。</summary>
    public const string AppUserModelId = "Cirno.Desktop";

    private readonly string _executablePath;

    public JumpListService(string executablePath)
    {
        _executablePath = executablePath;
    }

    /// <summary>最近一次提交失败的原因（诊断用）。</summary>
    public string? LastError { get; private set; }

    /// <summary>分步诊断回调。传入 null 表示静默。</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>声明进程级 AUMID。必须在创建任何窗口之前调用。</summary>
    public static bool RegisterAppUserModelId()
    {
        try
        {
            return NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId) >= 0;
        }
        catch (COMException)
        {
            return false;
        }
    }

    /// <summary>
    /// 提交一份跳转列表：固定任务（新建会话）。
    /// 失败时静默降级——跳转列表是锦上添花，不能影响主功能。
    /// </summary>
    public bool TryCommit()
    {
        ICustomDestinationList? list = null;
        IObjectCollection? collection = null;

        try
        {
            Trace?.Invoke("jumplist: create destination list");
            list = (ICustomDestinationList)CreateCom(ClsidDestinationList, IidCustomDestinationList);
            list.SetAppID(AppUserModelId);
            Trace?.Invoke("jumplist: begin list");
            list.BeginList(out _, new Guid(IidObjectArray), out var removed);
            if (removed != IntPtr.Zero)
            {
                Marshal.Release(removed);
            }

            Trace?.Invoke("jumplist: create collection");
            collection = (IObjectCollection)CreateCom(ClsidEnumerableObjectCollection, IidObjectArray);

            foreach (var task in BuildTasks())
            {
                Trace?.Invoke($"jumplist: shell link {task.Title}");
                var link = CreateShellLink(task.Title, task.Arguments);
                if (link is null)
                {
                    continue;
                }

                collection.AddObject(link);
                Release(link);
            }

            Trace?.Invoke("jumplist: add user tasks");
            // AddUserTasks 要的是 IObjectArray，必须先 QueryInterface 到该接口再传
            var array = (IObjectArray)collection;
            list.AddUserTasks(array);
            Trace?.Invoke("jumplist: commit");
            list.CommitList();
            Trace?.Invoke("jumplist: done");
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NotSupportedException)
        {
            LastError = ex.Message;
            return false;
        }
        finally
        {
            Release(collection);
            Release(list);
        }
    }

    /// <summary>固定任务集合。必须静态：系统会缓存最后一次提交的版本。</summary>
    private static IEnumerable<(string Title, string Arguments)> BuildTasks()
    {
        yield return ("新建会话", "--new-session");
    }

    private static object CreateCom(string clsid, string iid)
        => Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(clsid))!, nonPublic: true)!;

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    private IShellLink? CreateShellLink(string title, string arguments)
    {
        try
        {
            Trace?.Invoke("jumplist: create shell link object");
            var link = (IShellLink)CreateCom(ClsidShellLink, "000214f9-0000-0000-c000-000000000046");
            Trace?.Invoke("jumplist: set path");
            link.SetPath(_executablePath);
            Trace?.Invoke("jumplist: set arguments");
            link.SetArguments(arguments);

            // 跳转列表的显示名只能通过 System.Title 属性设置，SetDescription 只是提示气泡。
            if (link is IPropertyStore store)
            {
                Trace?.Invoke("jumplist: set title");
                SetTitle(store, title);
                Trace?.Invoke("jumplist: title set");
            }

            return link;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static void SetTitle(IPropertyStore store, string title)
    {
        // PKEY_Title = {F29F85E0-4FF9-1068-AB91-08002B27B3D9}, 2
        var key = new PropertyKey(new Guid("f29f85e0-4ff9-1068-ab91-08002b27b3d9"), 2);

        var value = Marshal.StringToCoTaskMemUni(title);
        var variant = new PropVariant
        {
            ValueType = 31, // VT_LPWSTR
            Pointer = value,
        };

        try
        {
            store.SetValue(ref key, ref variant);
            store.Commit();
        }
        finally
        {
            // SetValue 不接管所有权，调用方负责释放
            Marshal.FreeCoTaskMem(value);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort ValueType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] out string pszFile, int cch, IntPtr pfd, int fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] out string pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] out string pszDir, int cch, IntPtr pfd, int fFlags);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] out string pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out ushort pwHotkey);

        void SetHotkey(ushort wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] out string pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);

        void Resolve(IntPtr hwnd, int fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);

        void GetAt(uint iProp, out PropertyKey pkey);

        void GetValue(ref PropertyKey key, out PropVariant pv);

        void SetValue(ref PropertyKey key, ref PropVariant propvar);

        void Commit();
    }

    [ComImport]
    [Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        void GetCount(out uint count);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetAt(uint index, in Guid iid);
    }

    [ComImport]
    [Guid("5632b1a4-e38a-400a-928a-d4cd63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectCollection
    {
        // IObjectArray
        void GetCount(out uint count);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetAt(uint index, in Guid iid);

        // IObjectCollection
        void AddObject([MarshalAs(UnmanagedType.IUnknown)] object pObject);

        void AddFromArray(IntPtr pArray);

        void Clear();

        void RemoveObjectAt(uint index);
    }

    [ComImport]
    [Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        void BeginList(out uint minSlots, in Guid riid, out IntPtr ppv);

        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string category, IntPtr destinations);

        void AppendKnownCategory(int category);

        void AddUserTasks(IObjectArray tasks);

        void CommitList();

        void GetRemovedDestinations(in Guid riid, out IntPtr ppv);

        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string appId);

        void AbortList();
    }
}
