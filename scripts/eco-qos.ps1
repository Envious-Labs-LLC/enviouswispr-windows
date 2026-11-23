# EcoQoS / Power Throttling opt-out helper for Windows builds and tests.
# Ref: GitHub issue #68.
#
# Under SSH or background service sessions, Windows places processes into EcoQoS
# (Efficiency Mode), scheduling them onto efficiency cores (E-cores) while performance cores (P-cores)
# sit idle. Setting ProcessPowerThrottling with PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0
# opts out of this throttling. Because child processes (MSBuild worker nodes, Roslyn compiler, test hosts)
# do not automatically inherit power throttling opt-outs, this helper uses a Win32 Job Object with
# an I/O completion port to detect and opt out all spawned descendant processes instantly.

param(
    [switch]$OptOutCurrent,
    [switch]$Verify,
    [switch]$ShowStatus
)

$csharpCode = @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

public static class EcoQoSManager {
    public const int ProcessPowerThrottling = 4;
    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    public const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    public const uint HIGH_PRIORITY_CLASS = 0x00000080;
    public const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint IDLE_PRIORITY_CLASS = 0x00000040;

    public const int JobObjectAssociateCompletionPortInformation = 7;
    public const uint JOB_OBJECT_MSG_NEW_PROCESS = 6;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_ASSOCIATE_COMPLETION_PORT {
        public IntPtr CompletionKey;
        public IntPtr CompletionPort;
    }

    public struct ProcessSavedState {
        public int Pid;
        public uint Priority;
        public uint ControlMask;
        public uint StateMask;
        public bool HasEcoQoS;
        public long CreationTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(
        IntPtr hProcess,
        out long lpCreationTime,
        out long lpExitTime,
        out long lpKernelTime,
        out long lpUserTime
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetInformationJobObject(
        IntPtr hJob,
        int JobObjectInformationClass,
        ref JOBOBJECT_ASSOCIATE_COMPLETION_PORT lpJobObjectInfo,
        uint cbJobObjectInfoLength
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateIoCompletionPort(
        IntPtr FileHandle,
        IntPtr ExistingCompletionPort,
        UIntPtr CompletionKey,
        uint NumberOfConcurrentThreads
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetQueuedCompletionStatus(
        IntPtr CompletionPort,
        out uint lpNumberOfBytesTransferred,
        out UIntPtr lpCompletionKey,
        out IntPtr lpOverlapped,
        uint dwMilliseconds
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool PostQueuedCompletionStatus(
        IntPtr CompletionPort,
        uint dwNumberOfBytesTransferred,
        UIntPtr dwCompletionKey,
        IntPtr lpOverlapped
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        uint ProcessInformationSize
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        uint ProcessInformationSize
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    public static bool OptOutProcess(IntPtr hProcess, bool setHighPriority = false) {
        var state = new PROCESS_POWER_THROTTLING_STATE {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
            StateMask = 0
        };
        bool ok = SetProcessInformation(hProcess, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf(state));
        if (setHighPriority) {
            SetPriorityClass(hProcess, HIGH_PRIORITY_CLASS);
        }
        return ok;
    }

    public static bool ForceThrottling(IntPtr hProcess) {
        var state = new PROCESS_POWER_THROTTLING_STATE {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
            StateMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION
        };
        bool ok = SetProcessInformation(hProcess, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf(state));
        SetPriorityClass(hProcess, IDLE_PRIORITY_CLASS);
        return ok;
    }

    public static bool QueryProcess(IntPtr hProcess, out uint controlMask, out uint stateMask, out uint priorityClass) {
        var state = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION };
        bool ok = GetProcessInformation(hProcess, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf(state));
        controlMask = state.ControlMask;
        stateMask = state.StateMask;
        priorityClass = GetPriorityClass(hProcess);
        return ok;
    }

    public sealed class ProcessTreeTracker : IDisposable {
        private IntPtr _job = IntPtr.Zero;
        private IntPtr _iocp = IntPtr.Zero;
        private Thread _worker;
        private volatile bool _running = false;
        private readonly ConcurrentDictionary<int, ProcessSavedState> _savedStates = new ConcurrentDictionary<int, ProcessSavedState>();

        private uint _originalPriority = NORMAL_PRIORITY_CLASS;
        private bool _savedPriority = false;
        private uint _originalControlMask = 0;
        private uint _originalStateMask = 0;
        private bool _savedEcoQoS = false;

        public bool IsJobAttached { get; private set; }
        public int FailedOptOutCount;
        public ICollection<int> OptedPids { get { return _savedStates.Keys; } }

        public bool Start() {
            IntPtr cur = GetCurrentProcess();
            _originalPriority = GetPriorityClass(cur);
            _savedPriority = true;

            var origState = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION };
            if (GetProcessInformation(cur, ProcessPowerThrottling, ref origState, (uint)Marshal.SizeOf(origState))) {
                _originalControlMask = origState.ControlMask;
                _originalStateMask = origState.StateMask;
                _savedEcoQoS = true;
            }

            if (!OptOutProcess(cur, setHighPriority: false)) {
                return false;
            }

            _iocp = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, UIntPtr.Zero, 1);
            if (_iocp == IntPtr.Zero) {
                IsJobAttached = false;
                return false;
            }

            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) {
                CloseHandle(_iocp);
                _iocp = IntPtr.Zero;
                IsJobAttached = false;
                return false;
            }

            var assoc = new JOBOBJECT_ASSOCIATE_COMPLETION_PORT {
                CompletionKey = _job,
                CompletionPort = _iocp
            };

            if (!SetInformationJobObject(_job, JobObjectAssociateCompletionPortInformation, ref assoc, (uint)Marshal.SizeOf(assoc))) {
                CloseHandle(_job);
                CloseHandle(_iocp);
                _job = IntPtr.Zero;
                _iocp = IntPtr.Zero;
                IsJobAttached = false;
                return false;
            }

            if (!AssignProcessToJobObject(_job, cur)) {
                CloseHandle(_job);
                CloseHandle(_iocp);
                _job = IntPtr.Zero;
                _iocp = IntPtr.Zero;
                IsJobAttached = false;
                return false;
            }

            IsJobAttached = true;
            _running = true;
            _worker = new Thread(ListenLoop);
            _worker.IsBackground = true;
            _worker.Name = "EcoQoSTracker";
            _worker.Start();
            return true;
        }

        private bool OptOutWithSave(int pid) {
            IntPtr hProc = OpenProcess(0x0200 /* SET_INFO */ | 0x0400 /* QUERY_INFO */ | 0x1000, false, pid);
            if (hProc == IntPtr.Zero) {
                try {
                    using (Process p = Process.GetProcessById(pid)) {
                        if (!p.HasExited) {
                            Interlocked.Increment(ref FailedOptOutCount);
                            return false;
                        }
                    }
                } catch {
                    return true;
                }
                return true;
            }
            try {
                if (!_savedStates.ContainsKey(pid)) {
                    uint origPriority = GetPriorityClass(hProc);
                    var origState = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION };
                    bool hasEco = GetProcessInformation(hProc, ProcessPowerThrottling, ref origState, (uint)Marshal.SizeOf(origState));
                    long creationTime = 0, exitTime, kernelTime, userTime;
                    GetProcessTimes(hProc, out creationTime, out exitTime, out kernelTime, out userTime);
                    _savedStates.TryAdd(pid, new ProcessSavedState {
                        Pid = pid,
                        Priority = origPriority,
                        ControlMask = origState.ControlMask,
                        StateMask = origState.StateMask,
                        HasEcoQoS = hasEco,
                        CreationTime = creationTime
                    });
                }
                bool ok = OptOutProcess(hProc, setHighPriority: false);
                if (!ok) {
                    Interlocked.Increment(ref FailedOptOutCount);
                }
                return ok;
            } finally {
                CloseHandle(hProc);
            }
        }

        private void ListenLoop() {
            while (_running) {
                uint msgId;
                UIntPtr key;
                IntPtr overlapped;
                if (GetQueuedCompletionStatus(_iocp, out msgId, out key, out overlapped, 500)) {
                    if (msgId == JOB_OBJECT_MSG_NEW_PROCESS) {
                        int pid = overlapped.ToInt32();
                        OptOutWithSave(pid);
                    }
                }
            }
        }

        public int StopAndGetFailedCount() {
            Dispose();
            return FailedOptOutCount;
        }

        public void Dispose() {
            _running = false;
            if (_iocp != IntPtr.Zero) {
                PostQueuedCompletionStatus(_iocp, 0, UIntPtr.Zero, IntPtr.Zero);
            }
            if (_worker != null && _worker.IsAlive) {
                _worker.Join(2000);
            }
            if (_iocp != IntPtr.Zero) {
                CloseHandle(_iocp);
                _iocp = IntPtr.Zero;
            }
            if (_job != IntPtr.Zero) {
                CloseHandle(_job);
                _job = IntPtr.Zero;
            }

            // Restore all surviving child processes to their original scheduling state.
            // Check process creation time to ensure the PID was not recycled by an unrelated process.
            foreach (KeyValuePair<int, ProcessSavedState> kvp in _savedStates) {
                int pid = kvp.Key;
                ProcessSavedState saved = kvp.Value;
                try {
                    IntPtr hProc = OpenProcess(0x0200 /* SET_INFO */ | 0x1000 /* QUERY_LIMITED */, false, pid);
                    if (hProc != IntPtr.Zero) {
                        try {
                            long creationTime = 0, exitTime, kernelTime, userTime;
                            if (GetProcessTimes(hProc, out creationTime, out exitTime, out kernelTime, out userTime) &&
                                (saved.CreationTime == 0 || creationTime == saved.CreationTime)) {
                                if (saved.Priority != 0 && saved.Priority != GetPriorityClass(hProc)) {
                                    SetPriorityClass(hProc, saved.Priority);
                                }
                                if (saved.HasEcoQoS) {
                                    var restore = new PROCESS_POWER_THROTTLING_STATE {
                                        Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                                        ControlMask = saved.ControlMask,
                                        StateMask = saved.StateMask
                                    };
                                    SetProcessInformation(hProc, ProcessPowerThrottling, ref restore, (uint)Marshal.SizeOf(restore));
                                }
                            }
                        } finally {
                            CloseHandle(hProc);
                        }
                    }
                } catch {}
            }
            _savedStates.Clear();

            // Restore the host process priority and EcoQoS state
            IntPtr cur = GetCurrentProcess();
            if (_savedPriority && _originalPriority != 0) {
                SetPriorityClass(cur, _originalPriority);
            }
            if (_savedEcoQoS) {
                var restoreState = new PROCESS_POWER_THROTTLING_STATE {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = _originalControlMask,
                    StateMask = _originalStateMask
                };
                SetProcessInformation(cur, ProcessPowerThrottling, ref restoreState, (uint)Marshal.SizeOf(restoreState));
            }
        }
    }
}
'@

if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
    if (-not ([System.Management.Automation.PSTypeName]'EcoQoSManager').Type) {
        Add-Type -TypeDefinition $csharpCode -ErrorAction Stop
    }
}

function Enable-ProcessTreeEcoQoSOptOut {
    param(
        [switch]$RequireDescendantTracking
    )
    if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
        return $null
    }

    # Disable out-of-job compiler daemon reuse so all compilation workers spawn inside this tracked job
    $script:prevUseSharedCompilation = $env:UseSharedCompilation
    $script:prevMsBuildDisableNodeReuse = $env:MSBUILDDISABLENODEREUSE
    $script:prevDotnetCliDoNotUseMsBuildServer = $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER
    $env:UseSharedCompilation = "false"
    $env:MSBUILDDISABLENODEREUSE = "1"
    $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = "1"

    $tracker = New-Object EcoQoSManager+ProcessTreeTracker
    $ok = $tracker.Start()
    if (-not $ok -or -not $tracker.IsJobAttached) {
        $hostOpted = [EcoQoSManager]::OptOutProcess([EcoQoSManager]::GetCurrentProcess(), $false)
        if ($RequireDescendantTracking) {
            Disable-ProcessTreeEcoQoSOptOut -Tracker $tracker -CheckFailures:$false
            throw "Process-tree EcoQoS tracking failed to start or attach to the Win32 Job Object. Child compiler and test nodes cannot be guaranteed to opt out of efficiency cores."
        }
        if (-not $hostOpted) {
            Disable-ProcessTreeEcoQoSOptOut -Tracker $tracker -CheckFailures:$false
            throw "Host process failed to opt out of EcoQoS efficiency-core throttling."
        }
    }
    $status = Test-IsEcoQoSOptedOut
    if (-not $status.IsOptedOut) {
        Disable-ProcessTreeEcoQoSOptOut -Tracker $tracker -CheckFailures:$false
        throw "Host process failed to verify EcoQoS opt-out."
    }
    return $tracker
}

function Disable-ProcessTreeEcoQoSOptOut {
    param(
        [Parameter(Mandatory = $false)]$Tracker,
        [bool]$CheckFailures = $true
    )
    $failedCount = 0
    if ($Tracker) {
        if ($Tracker -is [EcoQoSManager+ProcessTreeTracker]) {
            $failedCount = $Tracker.StopAndGetFailedCount()
        }
        elseif ($Tracker -is [System.IDisposable]) {
            $Tracker.Dispose()
            if ($Tracker.FailedOptOutCount) {
                $failedCount = $Tracker.FailedOptOutCount
            }
        }
    }

    # Restore compiler reuse environment variables
    $env:UseSharedCompilation = $script:prevUseSharedCompilation
    $env:MSBUILDDISABLENODEREUSE = $script:prevMsBuildDisableNodeReuse
    $env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = $script:prevDotnetCliDoNotUseMsBuildServer

    if ($CheckFailures -and $failedCount -gt 0) {
        throw "EcoQoS opt-out failed for $failedCount child process(es)."
    }
}

function Test-IsEcoQoSOptedOut {
    param([IntPtr]$ProcessHandle = [IntPtr]::Zero)
    if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
        return [pscustomobject]@{
            Success = $true
            IsOptedOut = $true
            ControlMask = 0
            StateMask = 0
            PriorityClass = 0
        }
    }
    if ($ProcessHandle -eq [IntPtr]::Zero) {
        $ProcessHandle = [EcoQoSManager]::GetCurrentProcess()
    }
    $cMask = 0
    $sMask = 0
    $pClass = 0
    $ok = [EcoQoSManager]::QueryProcess($ProcessHandle, [ref]$cMask, [ref]$sMask, [ref]$pClass)
    $optedOut = $ok -and (($cMask -band 1) -eq 1) -and (($sMask -band 1) -eq 0)
    return [pscustomobject]@{
        Success = $ok
        IsOptedOut = $optedOut
        ControlMask = $cMask
        StateMask = $sMask
        PriorityClass = $pClass
    }
}

function OptOut-CurrentProcess {
    if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
        return $true
    }
    return [EcoQoSManager]::OptOutProcess([EcoQoSManager]::GetCurrentProcess(), $true)
}

function OptOut-ProcessTree {
    param([Parameter(Mandatory = $true)][int]$ProcessId)
    if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
        return
    }

    $hProc = [EcoQoSManager]::OpenProcess(0x0200 -bor 0x0400 -bor 0x1000, $false, $ProcessId)
    if ($hProc -ne [IntPtr]::Zero) {
        try {
            [EcoQoSManager]::OptOutProcess($hProc, $true) | Out-Null
        }
        finally {
            [EcoQoSManager]::CloseHandle($hProc) | Out-Null
        }
    }

    try {
        $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue
        foreach ($child in $children) {
            OptOut-ProcessTree -ProcessId $child.ProcessId
        }
    }
    catch {
    }
}

if ($OptOutCurrent) {
    $res = OptOut-CurrentProcess
    Write-Host "Opted out current process: $res"
}

if ($ShowStatus -or $Verify) {
    $state = Test-IsEcoQoSOptedOut
    Write-Host "EcoQoS status: OptedOut=$($state.IsOptedOut), Control=0x$($state.ControlMask.ToString('X')), State=0x$($state.StateMask.ToString('X')), Priority=0x$($state.PriorityClass.ToString('X'))"
    if ($Verify -and -not $state.IsOptedOut) {
        throw "EcoQoS opt-out verification failed."
    }
}
