using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RouterBalancing.Updater;

/// <summary>
/// Tra cứu process giữ lock file — updater dùng để chẩn đoán "ai giữ lock"
/// khi <see cref="UpdateStager.WaitUntilWritable"/> timeout (bug 2026-10-16:
/// orphan opencode-proxy.exe giữ lock file exe). Thử Restart Manager trước;
/// RM bị hạn chế trong process chạy dưới job object (testhost, service) nên
/// fallback scan MainModule khớp chính file — bắt đúng trường hợp real bug
/// (process thực thi từ file bị lock).
/// </summary>
internal static class RestartManager
{
    private const int ErrorMoreData = 234;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;

    /// <summary>
    /// Tìm process đang giữ lock <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">File cần truy locker.</param>
    /// <returns>Mô tả từng locker (dạng "PID {id} {exePath}"); rỗng khi không tra được.</returns>
    internal static IReadOnlyList<string> FindLockingProcesses(string filePath)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(filePath))
        {
            return [];
        }

        var viaRestartManager = FindViaRestartManager(filePath);
        if (viaRestartManager.Count > 0)
        {
            return viaRestartManager;
        }

        return FindProcessesExecuting(filePath);
    }

    /// <summary>
    /// Restart Manager: chính xác kể cả file chỉ bị load (DLL) — nhưng trả lỗi
    /// trong process chạy dưới job object (đã thấy ERROR_INVALID_PARAMETER=29
    /// trong testhost) — mọi lỗi trả rỗng để caller fallback.
    /// </summary>
    private static IReadOnlyList<string> FindViaRestartManager(string filePath)
    {
        var sessionKey = new StringBuilder(32);
        if (RmStartSession(out var session, 0, sessionKey) != 0)
        {
            return [];
        }

        try
        {
            if (RmRegisterResources(session, 1, [filePath]) != 0)
            {
                return [];
            }

            // Vòng 1: hỏi size; RmGetList trả ERROR_MORE_DATA + số process cần.
            uint needed = 0, count = 0, reason = 0;
            var result = RmGetList(session, out needed, ref count, null, ref reason);
            if (result != ErrorMoreData || needed == 0)
            {
                return [];
            }

            var infos = new RmProcessInfo[needed];
            count = needed;
            result = RmGetList(session, out needed, ref count, infos, ref reason);
            if (result != 0)
            {
                return [];
            }

            return infos
                .Take((int)count)
                .Select(info => $"PID {info.Process.dwProcessId} {info.strAppName}")
                .ToList();
        }
        finally
        {
            RmEndSession(session);
        }
    }

    /// <summary>
    /// Fallback: liệt kê process có executable TRÙNG chính <paramref name="filePath"/>
    /// — không bắt được lock thuần file (DLL load) nhưng đủ cho case sidecar
    /// giữ lock file exe của chính nó.
    /// </summary>
    private static IReadOnlyList<string> FindProcessesExecuting(string filePath)
    {
        var lockers = new List<string>();
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? fileName;
                try
                {
                    fileName = process.MainModule?.FileName;
                }
                catch
                {
                    // System process/quyền thấp không đọc được MainModule — bỏ qua.
                    continue;
                }

                if (fileName is not null
                    && string.Equals(Path.GetFullPath(fileName), fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    lockers.Add($"PID {process.Id} {fileName}");
                }
            }
        }

        return lockers;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RmApplicationType
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;
        public RmApplicationType ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern uint RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern uint RmRegisterResources(uint dwSessionHandle, uint nFiles, string[] rgsFilenames);

    [DllImport("rstrtmgr.dll")]
    private static extern uint RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps, ref uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern uint RmEndSession(uint pSessionHandle);
}
