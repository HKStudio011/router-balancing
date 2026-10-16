using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Windows Job Object với cờ <c>KILL_ON_JOB_CLOSE</c>: mọi process được gán vào
/// job sẽ bị OS kill khi mọi handle tới job đóng — kể cả khi app chết bất ngờ
/// (Quit/crash/Environment.Exit) mà managed Dispose không chạy.
/// Dùng cho Go sidecar opencode-proxy: sidecar là HTTP server sống lâu, không
/// monitor parent — nếu không có job, app thoát để lại orphan giữ lock file exe
/// trong appDir, updater không ghi đè được (bug "Timed out waiting for
/// application files to be released" 2026-10-16).
/// </summary>
internal sealed class WindowsProcessJob : IDisposable
{
    private const int InfoClassExtendedLimit = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeFileHandle? _handle;
    private bool _disposed;

    private WindowsProcessJob(SafeFileHandle handle) => _handle = handle;

    /// <summary>
    /// Tạo job object đã cấu hình KILL_ON_JOB_CLOSE.
    /// </summary>
    /// <returns>Job mới; <see langword="null"/> khi không phải Windows hoặc kernel từ chối tạo/set.</returns>
    internal static WindowsProcessJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        // KILL_ON_JOB_CLOSE nằm trong BasicLimitInformation.LimitFlags —
        // struct mở rộng không có LimitFlags ở cuối (size 144 trên x64).
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (!SetInformationJobObject(handle, InfoClassExtendedLimit, ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            handle.Dispose();
            return null;
        }

        return new WindowsProcessJob(handle);
    }

    /// <summary>
    /// Gán process vào job — từ giờ process chết khi job handle đóng.
    /// </summary>
    /// <param name="process">Process cần gán (phải chưa exit).</param>
    /// <returns><see langword="true"/> khi gán thành công; false khi job đã dispose, process đã thoát hoặc kernel từ chối (nested job policy).</returns>
    internal bool Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        // Dispose rồi thì handle đã đóng — trả false thay vì ném: caller chỉ log warn.
        if (_disposed)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(_handle!, process.Handle);
        }
        catch (InvalidOperationException)
        {
            // Process thoát giữa chừng — không có handle để gán, không phải lỗi hệ trọng.
            return false;
        }
    }

    /// <summary>Đóng job handle — OS kill mọi process còn trong job. Dispose lần 2 là no-op.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle?.Dispose();
    }

    // Layout khớp JOBOBJECT_BASIC_LIMIT_INFORMATION/JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    // trong winnt.h — sai một byte là SetInformationJobObject trả ERROR_BAD_LENGTH.
    // Extended KHÔNG có LimitFlags ở cuối: cờ nằm trong BasicLimitInformation.
    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public IntPtr MinimumWorkingSetSize;
        public IntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public IntPtr ProcessMemoryLimit;
        public IntPtr JobMemoryLimit;
        public IntPtr PeakProcessMemoryUsed;
        public IntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle hJob, int jobObjectInformationClass,
        ref JobObjectExtendedLimitInformation lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);
}
