using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.SubtitleFixer.Worker;

/// <summary>
/// A Windows Job Object with "kill on job close": every process in it (and every child it starts) is ended by the
/// OS when the last handle closes, which includes Jellyfin itself dying. Verified need (live, 2026-11-11): killing
/// jellyfin.exe left subsync-cli.exe and its python.exe children running, so an orphaned run could overwrite a
/// subtitle after startup recovery had restored it.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly IntPtr _handle;

    private KillOnCloseJob(IntPtr handle)
    {
        _handle = handle;
    }

    /// <summary>Creates a job, or returns null where Job Objects are unavailable (not Windows, or refused).</summary>
    /// <returns>The job or null.</returns>
    public static KillOnCloseJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var info = new JobObjectExtendedLimitInformationStruct
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        var length = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, buffer, (uint)length))
            {
                CloseHandle(handle);
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return new KillOnCloseJob(handle);
    }

    /// <summary>Puts a started process into the job. Its children join automatically.</summary>
    /// <param name="process">Process.</param>
    /// <returns>True when assigned.</returns>
    public bool TryAdd(Process process)
    {
        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Closes the job: anything still in it is killed.</summary>
    public void Dispose()
    {
        CloseHandle(_handle);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
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
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

/// <summary>
/// Remembers the running SubSync process in a small file, so the next Jellyfin start can end a run that outlived
/// a crash (second line of defence behind <see cref="KillOnCloseJob"/>). The file holds "pid|startTicksUtc".
/// </summary>
public static class RunningWorkerFile
{
    /// <summary>Gets the file path.</summary>
    public static string PathName { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SubtitleFixer", "running-subsync.txt");

    /// <summary>Records a started process.</summary>
    /// <param name="process">Process.</param>
    public static void Write(Process process)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            // Best effort: the Job Object is the main protection.
        }
    }

    /// <summary>Forgets the process after it ended normally.</summary>
    public static void Clear()
    {
        try
        {
            File.Delete(PathName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Kills the recorded process tree if it is still the same process (same id and start time).</summary>
    /// <returns>True when a leftover run was killed.</returns>
    public static bool KillLeftover()
    {
        string text;
        try
        {
            if (!File.Exists(PathName))
            {
                return false;
            }

            text = File.ReadAllText(PathName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length != 2
            || !int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ticks))
        {
            Clear();
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            // Same id and start time: it is our run, not a reused id.
            if (process.StartTime.ToUniversalTime().Ticks != ticks)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(30_000);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false; // Not running any more.
        }
        finally
        {
            Clear();
        }
    }
}
