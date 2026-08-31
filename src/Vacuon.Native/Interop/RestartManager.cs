using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Vacuon.Native.Interop;

/// <summary>One program holding a file open, as the Restart Manager reports it.</summary>
/// <param name="ProcessId">The process id. Zero when the holder is a service.</param>
/// <param name="Name">
/// What to call it on screen: the window title where there is one, the service name for a
/// service, otherwise the process name.
/// </param>
/// <param name="IsService">Services are closed and restarted differently, and say so.</param>
public readonly record struct FileHolder(int ProcessId, string Name, bool IsService);

/// <summary>
/// Who has a file open.
/// <para>
/// A copy that could not take a file says <em>why</em> — "in use by another program" — and
/// then leaves the person to guess which one, on a machine running forty of them. The
/// Restart Manager is the API Windows Installer uses to ask exactly this before an update:
/// register the files, ask which processes would have to be closed, and it answers with
/// names.
/// </para>
/// <para>
/// ⚠️ It only ever <b>reads</b> here. <c>RmShutdown</c> and <c>RmRestart</c> exist in this
/// API and are not bound: closing somebody's program to get at a file is a different
/// decision from telling them who has it, and this app does not make it for them.
/// </para>
/// <para>
/// The session is a machine-wide resource with a fixed key buffer of
/// <c>CCH_RM_SESSION_KEY + 1</c> characters, and it is always ended in a finally — a leaked
/// session outlives the process.
/// </para>
/// </summary>
public static partial class RestartManager
{
    private const int CCH_RM_MAX_APP_NAME = 255;
    private const int CCH_RM_MAX_SVC_NAME = 63;
    private const int CCH_RM_SESSION_KEY = 32;

    private const int ERROR_MORE_DATA = 234;
    private const int RmRebootReasonNone = 0;

    /// <summary>How many holders are worth naming. Past a handful the answer is "lots".</summary>
    public const int MaxHolders = 8;

    /// <summary>
    /// The programs holding <paramref name="path"/> open, or an empty list when nothing does,
    /// when the API is unavailable, or when it refuses to answer.
    /// </summary>
    /// <remarks>
    /// Never throws: this is decoration on a failure that has already happened, and a
    /// diagnostic that throws while explaining an error is worse than no diagnostic.
    /// </remarks>
    public static IReadOnlyList<FileHolder> WhoHolds(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];

        uint handle;
        var key = new char[CCH_RM_SESSION_KEY + 1];

        try
        {
            if (RmStartSession(out handle, 0, key) != 0) return [];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }

        try
        {
            if (RmRegisterResources(handle, 1, [path], 0, null, 0, null) != 0) return [];

            uint needed = 0;
            uint count = MaxHolders;
            var info = new RmProcessInfo[MaxHolders];
            uint reason = 0;

            int result = RmGetList(handle, out needed, ref count, info, ref reason);

            // The buffer was too small, which is an answer in itself: ask again with room
            // for what it said it needs, capped, because a list of forty names is not a
            // sentence anybody reads.
            if (result == ERROR_MORE_DATA)
            {
                count = Math.Min(needed, MaxHolders);
                if (count == 0) return [];

                info = new RmProcessInfo[count];
                result = RmGetList(handle, out needed, ref count, info, ref reason);
            }

            if (result != 0) return [];

            var holders = new List<FileHolder>((int)count);

            for (int i = 0; i < count && i < info.Length; i++)
            {
                RmProcessInfo entry = info[i];
                int pid = (int)entry.Process.dwProcessId;

                // ⚠️ A pid is reused. The Restart Manager hands back the start time of the
                // process it meant, so a pid that has since been recycled can be told from
                // the one that was registered — without this the window could name a
                // program that has nothing to do with the file.
                if (!StillTheSameProcess(pid, entry.Process.ProcessStartTime)) continue;

                holders.Add(new FileHolder(pid, DisplayName(entry, pid), entry.ApplicationType == 3));
            }

            return holders;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                        or ArgumentException)
        {
            return [];
        }
        finally
        {
            RmEndSession(handle);
        }
    }

    private static string DisplayName(RmProcessInfo entry, int pid)
    {
        if (entry.ApplicationType == 3 && entry.strServiceShortName.Length > 0)
            return entry.strServiceShortName;

        if (entry.strAppName.Length > 0) return entry.strAppName;

        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"#{pid}";
        }
    }

    private static bool StillTheSameProcess(int pid, FileTime started)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            long reported = ((long)started.dwHighDateTime << 32) | (uint)started.dwLowDateTime;

            // Within a second: the two clocks are the same clock, but the value travels
            // through two different APIs and rounding is not worth a false negative.
            return Math.Abs(process.StartTime.ToFileTime() - reported) < 10_000_000;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                        or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public uint dwProcessId;
        public FileTime ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags,
                                             [MarshalAs(UnmanagedType.LPArray)] char[] strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles,
                                                  string[] rgsFilenames,
                                                  uint nApplications, RmUniqueProcess[]? rgApplications,
                                                  uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded,
                                        ref uint pnProcInfo,
                                        [In, Out] RmProcessInfo[] rgAffectedApps,
                                        ref uint lpdwRebootReasons);
}
