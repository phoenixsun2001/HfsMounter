using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace HfsMounter;

/// <summary>
/// Windows 服务（SCM）集成：安装/卸载/启停/状态查询 + 作为服务运行的分发器。
/// 只依赖 advapi32，不引入 System.ServiceProcess 包，发布产物更小。
/// </summary>
public static class ServiceRuntime
{
    public const string ServiceName = "HfsMounter";
    public const string DisplayName = "HFS+ 磁盘自动挂载服务";
    public const string Description =
        "监控 USB/外置磁盘，发现 HFS+/HFSX 分区即自动只读挂载到本地目录（默认 C:\\MacDisk）；磁盘拔出后自动卸载。";

    // ---------------- 常量 ----------------
    private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
    private const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
    private const uint SERVICE_ALL_ACCESS = 0xF01FF;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint SERVICE_DELETE = 0x10000;

    private const uint SERVICE_WIN32_OWN_PROCESS = 0x0000_0010;
    private const uint SERVICE_AUTO_START = 0x0000_0002;
    private const uint SERVICE_DEMAND_START = 0x0000_0003;
    private const uint SERVICE_ERROR_NORMAL = 0x0000_0001;
    private const uint SERVICE_NO_CHANGE = 0xFFFF_FFFF;

    private const uint SERVICE_CONTROL_STOP = 0x0000_0001;
    private const uint SERVICE_CONTROL_INTERROGATE = 0x0000_0004;
    private const uint SERVICE_CONTROL_SHUTDOWN = 0x0000_0005;
    private const uint SERVICE_CONTROL_PRESHUTDOWN = 0x0000_000F;

    private const uint SERVICE_ACCEPT_STOP = 0x0000_0001;
    private const uint SERVICE_ACCEPT_SHUTDOWN = 0x0000_0004;
    private const uint SERVICE_ACCEPT_PRESHUTDOWN = 0x0000_0100;

    private const uint SERVICE_STOPPED = 0x0000_0001;
    private const uint SERVICE_START_PENDING = 0x0000_0002;
    private const uint SERVICE_STOP_PENDING = 0x0000_0003;
    private const uint SERVICE_RUNNING = 0x0000_0004;

    private const int SC_STATUS_PROCESS_INFO = 0;
    private const int SERVICE_CONFIG_DESCRIPTION = 1;
    private const int SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const int SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    private const int SC_ACTION_RESTART = 1;
    private const int ERROR_SERVICE_EXISTS = 1073;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;
    private const int ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063;
    private const int ERROR_ACCESS_DENIED = 5;

    // ---------------- 结构 ----------------
    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_TABLE_ENTRY
    {
        public IntPtr lpServiceName;
        public IntPtr lpServiceProc;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_DESCRIPTION
    {
        public string lpDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION
    {
        public int Type;
        public int Delay;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public int dwResetPeriod;
        public string? lpRebootMsg;
        public string? lpCommand;
        public int cActions;
        public IntPtr lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_DELAYED_AUTO_START_INFO
    {
        public int fDelayedAutostart;
    }

    // ---------------- P/Invoke ----------------
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateServiceW(IntPtr scm, string serviceName, string displayName,
        uint desiredAccess, uint serviceType, uint startType, uint errorControl, string binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? startName, string? password);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType,
        uint errorControl, string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies,
        string? startName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, int infoLevel, ref SERVICE_DESCRIPTION info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, int infoLevel, ref SERVICE_FAILURE_ACTIONS info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, int infoLevel, ref SERVICE_DELAYED_AUTO_START_INFO info);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, ref SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceW(IntPtr service, int argCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel,
        ref SERVICE_STATUS_PROCESS status, int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcherW(IntPtr serviceTable);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, HandlerExDelegate handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr statusHandle, ref SERVICE_STATUS status);

    private delegate void ServiceMainDelegate(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerExDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);

    // ---------------- 状态 ----------------
    private static readonly object StatusGate = new();
    private static IntPtr _statusHandle = IntPtr.Zero;
    private static uint _currentState = SERVICE_STOPPED;
    private static uint _exitCode;
    private static uint _checkPoint;
    private static CancellationTokenSource? _shutdown;

    // 必须用静态字段持有委托，否则会被 GC 回收导致 SCM 回调野指针
    private static readonly ServiceMainDelegate ServiceMainCallback = ServiceMain;
    private static readonly HandlerExDelegate HandlerCallback = Handler;

    public enum InstallState
    {
        NotInstalled,
        Stopped,
        StartPending,
        StopPending,
        Running,
        Other,
    }

    public sealed record ServiceState(InstallState State, uint RawState, int ProcessId);

    // ---------------- 安装 / 卸载 ----------------

    public static int Install(string exePath, AppConfig config)
    {
        string binaryPath = $"\"{exePath}\" --service";

        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CREATE_SERVICE);
        if (scm == IntPtr.Zero) return Fail("打开服务控制管理器失败", Marshal.GetLastWin32Error());

        IntPtr service = IntPtr.Zero;
        try
        {
            service = CreateServiceW(scm, ServiceName, DisplayName, SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, binaryPath,
                null, IntPtr.Zero, null, null, null);

            int createError = Marshal.GetLastWin32Error();
            bool existing = service == IntPtr.Zero && createError == ERROR_SERVICE_EXISTS;

            if (existing)
            {
                service = OpenServiceW(scm, ServiceName, SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero) return Fail("打开已有服务失败", Marshal.GetLastWin32Error());
                if (!ChangeServiceConfigW(service, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
                        SERVICE_ERROR_NORMAL, binaryPath, null, IntPtr.Zero, null, null, null, DisplayName))
                    return Fail("更新已有服务配置失败", Marshal.GetLastWin32Error());
                Console.WriteLine("服务已存在，已更新其可执行路径与启动类型。");
            }
            else if (service == IntPtr.Zero)
            {
                return Fail("创建服务失败", createError);
            }
            else
            {
                Console.WriteLine($"服务已创建: {DisplayName} ({ServiceName})");
            }

            Console.WriteLine($"  可执行文件: {binaryPath}");
            Console.WriteLine($"  启动类型:   自动（延迟启动）");
            Console.WriteLine($"  运行账户:   LocalSystem");

            SetDescription(service);
            SetFailureActions(service);
            SetDelayedAutoStart(service);

            if (Start(service) == 0)
                Console.WriteLine("服务已启动。");

            Console.WriteLine($"  挂载根目录: {config.MountRoot}");
            Console.WriteLine($"  日志目录:   {AppPaths.LogDir}");
            return 0;
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(scm);
        }
    }

    private static void SetDescription(IntPtr service)
    {
        var description = new SERVICE_DESCRIPTION { lpDescription = Description };
        if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, ref description))
            Log.Warn($"设置服务描述失败: {Marshal.GetLastWin32Error()}");
    }

    private static void SetDelayedAutoStart(IntPtr service)
    {
        var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = 1 };
        // 失败不影响功能，只是开机会更早启动
        ChangeServiceConfig2W(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info);
    }

    private static void SetFailureActions(IntPtr service)
    {
        SC_ACTION[] actions =
        {
            new() { Type = SC_ACTION_RESTART, Delay = 5000 },
            new() { Type = SC_ACTION_RESTART, Delay = 15000 },
            new() { Type = SC_ACTION_RESTART, Delay = 60000 },
        };

        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<SC_ACTION>() * actions.Length);
        try
        {
            for (int i = 0; i < actions.Length; i++)
                Marshal.StructureToPtr(actions[i], buffer + i * Marshal.SizeOf<SC_ACTION>(), false);

            var failure = new SERVICE_FAILURE_ACTIONS
            {
                dwResetPeriod = 86400,
                lpRebootMsg = null,
                lpCommand = null,
                cActions = actions.Length,
                lpsaActions = buffer,
            };
            if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS, ref failure))
                Log.Warn($"设置服务失败重启策略失败: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static int Uninstall()
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return Fail("打开服务控制管理器失败", Marshal.GetLastWin32Error());

        try
        {
            IntPtr service = OpenServiceW(scm, ServiceName, SERVICE_STOP | SERVICE_QUERY_STATUS | SERVICE_DELETE);
            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_SERVICE_DOES_NOT_EXIST)
                {
                    Console.WriteLine("服务未安装，无需卸载。");
                    return 0;
                }
                return Fail("打开服务失败", error);
            }

            try
            {
                if (Query(service) is { State: InstallState.Running or InstallState.StartPending })
                {
                    Console.WriteLine("正在停止服务…");
                    Stop(service);
                    WaitForState(service, SERVICE_STOPPED, TimeSpan.FromSeconds(20));
                }

                if (!DeleteService(service))
                    return Fail("删除服务失败（可能有其他进程正在使用）", Marshal.GetLastWin32Error());

                Console.WriteLine("服务已删除。");
                return 0;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    // ---------------- 启停 / 查询 ----------------

    public static int StartServiceByName()
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return Fail("打开服务控制管理器失败", Marshal.GetLastWin32Error());
        try
        {
            IntPtr service = OpenServiceW(scm, ServiceName, SERVICE_START | SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero) return Fail("打开服务失败（是否已安装？）", Marshal.GetLastWin32Error());
            try
            {
                int rc = Start(service);
                return rc;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public static int StopServiceByName()
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (scm == IntPtr.Zero) return Fail("打开服务控制管理器失败", Marshal.GetLastWin32Error());
        try
        {
            IntPtr service = OpenServiceW(scm, ServiceName, SERVICE_STOP | SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero) return Fail("打开服务失败（是否已安装？）", Marshal.GetLastWin32Error());
            try
            {
                return Stop(service);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static int Start(IntPtr service)
    {
        ServiceState? state = Query(service);
        if (state?.State == InstallState.Running)
        {
            Console.WriteLine("服务已经在运行。");
            return 0;
        }

        if (!StartServiceW(service, 0, IntPtr.Zero))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ERROR_SERVICE_ALREADY_RUNNING)
            {
                Console.WriteLine("服务已经在运行。");
                return 0;
            }
            return Fail("启动服务失败", error);
        }

        WaitForState(service, SERVICE_RUNNING, TimeSpan.FromSeconds(20));
        ServiceState? after = Query(service);
        Console.WriteLine(after?.State == InstallState.Running ? "服务正在运行。" : $"服务状态: {after?.State}");
        return 0;
    }

    private static int Stop(IntPtr service)
    {
        var status = new SERVICE_STATUS();
        if (!ControlService(service, SERVICE_CONTROL_STOP, ref status))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ERROR_SERVICE_NOT_ACTIVE)
            {
                Console.WriteLine("服务未在运行。");
                return 0;
            }
            return Fail("停止服务失败", error);
        }

        WaitForState(service, SERVICE_STOPPED, TimeSpan.FromSeconds(20));
        Console.WriteLine("服务已停止。");
        return 0;
    }

    private static void WaitForState(IntPtr service, uint wanted, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ServiceState? state = Query(service);
            if (state == null || state.RawState == wanted || state.State == InstallState.NotInstalled) return;
            Thread.Sleep(250);
        }
        Log.Warn("等待服务状态变化超时");
    }

    public static ServiceState? QueryService() => Query(null);

    private static ServiceState? Query(IntPtr? opened)
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr service = opened ?? IntPtr.Zero;
        bool ownService = opened == null;
        try
        {
            if (ownService)
            {
                scm = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
                if (scm == IntPtr.Zero) return null;
                service = OpenServiceW(scm, ServiceName, SERVICE_QUERY_STATUS);
                if (service == IntPtr.Zero)
                {
                    return Marshal.GetLastWin32Error() == ERROR_SERVICE_DOES_NOT_EXIST
                        ? new ServiceState(InstallState.NotInstalled, 0, 0)
                        : null;
                }
            }

            var status = new SERVICE_STATUS_PROCESS();
            if (!QueryServiceStatusEx(service, SC_STATUS_PROCESS_INFO, ref status,
                    Marshal.SizeOf<SERVICE_STATUS_PROCESS>(), out _))
                return null;

            InstallState state = status.dwCurrentState switch
            {
                SERVICE_STOPPED => InstallState.Stopped,
                SERVICE_START_PENDING => InstallState.StartPending,
                SERVICE_STOP_PENDING => InstallState.StopPending,
                SERVICE_RUNNING => InstallState.Running,
                _ => InstallState.Other,
            };
            return new ServiceState(state, status.dwCurrentState, (int)status.dwProcessId);
        }
        finally
        {
            if (ownService && service != IntPtr.Zero) CloseServiceHandle(service);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    // ---------------- 作为服务运行 ----------------

    /// <summary>
    /// 由 SCM 启动时进入服务模式；body 在服务工作线程里阻塞运行，收到停止/关机请求时通过 token 取消。
    /// </summary>
    public static int RunAsService(Func<CancellationToken, int> body)
    {
        _serviceBody = body;
        int entrySize = Marshal.SizeOf<SERVICE_TABLE_ENTRY>();
        IntPtr table = Marshal.AllocHGlobal(entrySize * 2);
        IntPtr namePtr = Marshal.StringToHGlobalUni(ServiceName);
        try
        {
            Marshal.StructureToPtr(new SERVICE_TABLE_ENTRY
            {
                lpServiceName = namePtr,
                lpServiceProc = Marshal.GetFunctionPointerForDelegate(ServiceMainCallback),
            }, table, false);
            Marshal.StructureToPtr(new SERVICE_TABLE_ENTRY
            {
                lpServiceName = IntPtr.Zero,
                lpServiceProc = IntPtr.Zero,
            }, table + entrySize, false);

            if (!StartServiceCtrlDispatcherW(table))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT)
                {
                    Console.Error.WriteLine("本程序必须由服务控制管理器（SCM）启动。");
                    Console.Error.WriteLine("  安装服务:  HfsMounter.exe --install-service");
                    Console.Error.WriteLine("  前台调试:  HfsMounter.exe --daemon");
                    return 2;
                }
                return Fail("启动服务分发器失败", error);
            }
            return (int)_exitCode;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
            Marshal.FreeHGlobal(namePtr);
        }
    }

    /// <summary>服务主体：由 RunAsService 注册，SCM 在线程池线程里调用。</summary>
    private static Func<CancellationToken, int>? _serviceBody;

    public static void SetServiceBody(Func<CancellationToken, int> body) => _serviceBody = body;

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerExW(ServiceName, HandlerCallback, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            Log.Error($"注册服务控制处理程序失败: {Marshal.GetLastWin32Error()}");
            return;
        }

        ReportStatus(SERVICE_START_PENDING, 5000, 1);
        _shutdown = new CancellationTokenSource();
        int exitCode = 0;

        try
        {
            ReportStatus(SERVICE_RUNNING, 0, 0,
                SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN | SERVICE_ACCEPT_PRESHUTDOWN);
            exitCode = _serviceBody?.Invoke(_shutdown.Token) ?? 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("服务主体异常退出", ex);
            exitCode = 1;
        }
        finally
        {
            _exitCode = (uint)exitCode;
            ReportStatus(SERVICE_STOPPED, 0, 0);
            _statusHandle = IntPtr.Zero;
        }
    }

    private static uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case SERVICE_CONTROL_STOP:
            case SERVICE_CONTROL_SHUTDOWN:
            case SERVICE_CONTROL_PRESHUTDOWN:
                ReportStatus(SERVICE_STOP_PENDING, 30000, ++_checkPoint,
                    SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN | SERVICE_ACCEPT_PRESHUTDOWN);
                try { _shutdown?.Cancel(); }
                catch (ObjectDisposedException) { }
                return 0;

            case SERVICE_CONTROL_INTERROGATE:
                ReportStatus(_currentState, 0, _checkPoint,
                    SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN | SERVICE_ACCEPT_PRESHUTDOWN);
                return 0;

            default:
                return 0;
        }
    }

    private static void ReportStatus(uint state, uint waitHint, uint checkPoint, uint accepted = 0)
    {
        lock (StatusGate)
        {
            if (_statusHandle == IntPtr.Zero) return;
            _currentState = state;
            _checkPoint = checkPoint;
            var status = new SERVICE_STATUS
            {
                dwServiceType = SERVICE_WIN32_OWN_PROCESS,
                dwCurrentState = state,
                dwControlsAccepted = accepted,
                dwWin32ExitCode = state == SERVICE_STOPPED ? _exitCode : 0,
                dwServiceSpecificExitCode = 0,
                dwCheckPoint = checkPoint,
                dwWaitHint = waitHint,
            };
            SetServiceStatus(_statusHandle, ref status);
        }
    }

    private static int Fail(string what, int error)
    {
        string message = new Win32Exception(error).Message;
        Console.Error.WriteLine($"{what}: {message}（错误码 {error}）");
        if (error == ERROR_ACCESS_DENIED)
            Console.Error.WriteLine("提示: 安装/卸载服务需要管理员权限，请右键以管理员身份运行。");
        return 1;
    }
}
