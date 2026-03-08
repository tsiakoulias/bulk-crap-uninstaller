/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)
    Apache License Version 2.0
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Klocman.Extensions;
using Klocman.IO;
using Klocman.Tools;
using UninstallTools.Factory;
using UninstallTools.Properties;

namespace UninstallTools.Uninstaller
{
    public class BulkUninstallEntry
    {
        private static readonly string[] NamesOfIgnoredProcesses =
            WindowsTools.GetInstalledWebBrowsers().Select(s =>
            {
                try
                {
                    return Path.GetFileNameWithoutExtension(s);
                }
                catch (ArgumentException)
                {
                    try
                    {
                        var dash = s.LastIndexOf('\\');
                        return s.Substring(dash + 1, s.LastIndexOf('.') - dash - 1);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }).Where(x => !string.IsNullOrEmpty(x)).Concat(new[] { "explorer" }).Distinct().ToArray();

        // Number of consecutive idle checks (both CPU and I/O low) before killing the process.
        // Kill fires on tick threshold+1 due to > comparison (~34s at ~1.1s/tick).
        internal const int FullIdleThreshold = 30;
        // Number of consecutive I/O-idle checks before killing, even if CPU is active.
        // Catches processes stuck showing a dialog that spin the CPU but perform no I/O (issue #579).
        // Kill fires on tick threshold+1 (~2.2min).
        internal const int IoIdleThreshold = 120;
        // Number of consecutive steady-state checks before killing.
        // Catches processes in a busy loop with consistent, low-level activity (e.g., polling for a file).
        // Kill fires on tick threshold+1 (~5.5min).
        internal const int SteadyStateThreshold = 300;
        // CPU variation tolerance for steady-state detection (±5 percentage points across all processes)
        internal const float SteadyCpuTolerance = 5f;
        // I/O variation tolerance for steady-state detection (±20KB/s)
        internal const float SteadyIoTolerance = 20480f;
        // Maximum aggregate I/O rate for steady-state stall detection (512KB/s).
        // Above this, the process is likely doing real work even if values are stable.
        internal const float SteadyIoMaxForStall = 524288f;
        // Number of consecutive partial-reading ticks to tolerate before using available readings.
        // Gives counter resolution time to catch up with newly spawned processes (~11s).
        internal const int PartialReadingGraceTicks = 10;
        // Number of consecutive ticks with zero counter readings before killing.
        // Acts as a safety net when performance counter resolution completely fails,
        // preventing a stuck process from running indefinitely unmonitored.
        // Kill fires on tick threshold+1 (~5.5min).
        internal const int NoReadingsThreshold = 300;

        private readonly object _operationLock = new();

        private readonly Dictionary<int, PerfCounterEntry> _perfCounterBuffer = new();
        private int _partialReadingTicks;

        private bool _canRetry = true;
        private SkipCurrentLevel _skipLevel = SkipCurrentLevel.None;
        private Thread _worker;

        public BulkUninstallEntry(ApplicationUninstallerEntry uninstallerEntry, bool isSilentPossible,
            UninstallStatus startingStatus)
        {
            CurrentStatus = startingStatus;
            IsSilentPossible = isSilentPossible;
            UninstallerEntry = uninstallerEntry;
        }

        public Exception CurrentError { get; private set; }

        public UninstallStatus CurrentStatus { get; private set; }

        public bool Finished { get; private set; }

        public int Id { get; internal set; }

        public bool IsRunning
        {
            get
            {
                lock (_operationLock)
                    return _worker != null && _worker.IsAlive;
            }
        }

        public bool IsSilentPossible { get; set; }

        public ApplicationUninstallerEntry UninstallerEntry { get; }

        private static void KillProcesses(IEnumerable<Process> processes)
        {
            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
            }
        }

        public void Reset()
        {
            //bug handle already running
            CurrentError = null;
            CurrentStatus = UninstallStatus.Waiting;
            Finished = false;
        }

        /// <summary>
        ///     Run the uninstaller on a new thread.
        /// </summary>
        internal void RunUninstaller(RunUninstallerOptions options)
        {
            lock (_operationLock)
            {
                if (Finished || IsRunning || CurrentStatus != UninstallStatus.Waiting)
                    return;

                if (UninstallerEntry.IsRegistered && !UninstallerEntry.RegKeyStillExists())
                {
                    CurrentStatus = UninstallStatus.Completed;
                    Finished = true;
                    return;
                }

                if (UninstallerEntry.UninstallerKind == UninstallerType.Msiexec)
                {
                    var uninstallString = IsSilentPossible && UninstallerEntry.QuietUninstallPossible
                        ? UninstallerEntry.QuietUninstallString
                        : UninstallerEntry.UninstallString;

                    // Always reenumerate products in case any were uninstalled
                    if (ApplicationEntryTools.PathPointsToMsiExec(uninstallString) &&
                        MsiTools.MsiEnumProducts().All(g => !g.Equals(UninstallerEntry.BundleProviderKey)))
                    {
                        CurrentStatus = UninstallStatus.Completed;
                        Finished = true;
                        return;
                    }
                }

                CurrentStatus = UninstallStatus.Uninstalling;

                try
                {
                    _worker = new Thread(UninstallThread) { Name = "RunBulkUninstall_Worker", IsBackground = false };
                    _worker.Start(options);
                }
                catch
                {
                    CurrentStatus = UninstallStatus.Failed;
                    Finished = true;
                    throw;
                }
            }
        }

        public void SkipWaiting(bool terminate)
        {
            lock (_operationLock)
            {
                if (Finished)
                    return;

                if (!IsRunning && CurrentStatus == UninstallStatus.Waiting)
                    CurrentStatus = UninstallStatus.Skipped;

                // Do not allow skipping of Msiexec uninstallers because they will hold up the rest of Msiexec uninstallers in the task
                if (CurrentStatus == UninstallStatus.Uninstalling &&
                    UninstallerEntry.UninstallerKind == UninstallerType.Msiexec &&
                    !terminate)
                    return;

                _skipLevel = terminate ? SkipCurrentLevel.Terminate : SkipCurrentLevel.Skip;
            }
        }

        /// <summary>
        /// Try to mark this entry as finished. If it is running and can't be safely skipped, mark it for termination instead.
        /// Do not use unless the entry was uninstalled externally.
        /// </summary>
        public void ForceFinished()
        {
            lock (_operationLock)
            {
                if (Finished)
                    return;

                if (IsRunning)
                {
                    // Do not allow skipping of Msiexec uninstallers because they will hold up the rest of Msiexec uninstallers in the task
                    if (CurrentStatus == UninstallStatus.Uninstalling &&
                        UninstallerEntry.UninstallerKind == UninstallerType.Msiexec)
                    {
                        SkipWaiting(true);
                        return;
                    }
                }

                CurrentStatus = UninstallStatus.Completed;
                Finished = true;
            }
        }

        /// <summary>
        ///     Tests whether uninstaller processes appear stalled. Blocks for ~1100ms to gather data.
        /// </summary>
        private StallTestResult TestUninstallerForStalls(IEnumerable<Process> childProcesses)
        {
            var processList = childProcesses as IList<Process> ?? childProcesses.ToList();

            // Determine which PIDs need new counter instances
            var watchedPids = new HashSet<int>();
            foreach (var p in processList)
            {
                var pid = SafeGetProcessId(p);
                if (pid > 0) watchedPids.Add(pid);
            }

            // Remove counters for processes no longer being watched
            foreach (var perfCounterEntry in _perfCounterBuffer.ToList())
            {
                if (!watchedPids.Contains(perfCounterEntry.Key))
                {
                    _perfCounterBuffer.Remove(perfCounterEntry.Key);
                    perfCounterEntry.Value.Dispose();
                }
            }

            // Only perform the expensive category scan if there are new PIDs not yet in the buffer
            var newPids = watchedPids.Where(pid => !_perfCounterBuffer.ContainsKey(pid)).ToList();
            if (newPids.Count > 0)
            {
                var counterTargets = GetPerformanceCounterTargets(processList).ToList();
                foreach (var counterTarget in counterTargets)
                {
                    if (_perfCounterBuffer.ContainsKey(counterTarget.ProcessId))
                        continue;

                    PerformanceCounter[] perfCounters = null;
                    try
                    {
                        perfCounters = new[]
                        {
                            new PerformanceCounter("Process", "% Processor Time", counterTarget.CounterInstanceName, true),
                            new PerformanceCounter("Process", "IO Data Bytes/sec", counterTarget.CounterInstanceName, true)
                        };
                        _perfCounterBuffer.Add(counterTarget.ProcessId, new PerfCounterEntry(
                            perfCounters, new[] { perfCounters[0].NextSample(), perfCounters[1].NextSample() }));
                    }
                    catch
                    {
                        if (perfCounters != null && perfCounters.Length == 2)
                        {
                            perfCounters[0].Dispose();
                            perfCounters[1].Dispose();
                        }
                    }
                }
            }

            // Let the counters gather some data
            Thread.Sleep(1100);

            var readings = new List<(float Cpu, float Io)>();

            foreach (var perfCounterEntry in _perfCounterBuffer.ToList())
            {
                try
                {
                    var new0 = perfCounterEntry.Value.Counter[0].NextSample();
                    var new1 = perfCounterEntry.Value.Counter[1].NextSample();
                    var c0 = CounterSample.Calculate(perfCounterEntry.Value.Sample[0], new0);
                    var c1 = CounterSample.Calculate(perfCounterEntry.Value.Sample[1], new1);
                    perfCounterEntry.Value.Sample[0] = new0;
                    perfCounterEntry.Value.Sample[1] = new1;

                    Debug.WriteLine("CPU " + c0 + "%, IO " + c1 + "B");

                    readings.Add((c0, c1));
                }
                catch
                {
                    perfCounterEntry.Value.Dispose();
                    _perfCounterBuffer.Remove(perfCounterEntry.Key);
                }
            }

            // Grace period for partial readings: tolerate missing PIDs briefly
            // (counter resolution can lag behind process creation), but after the
            // grace period, use available readings to avoid suppressing stall
            // detection indefinitely when a PID is persistently unreadable.
            if (readings.Count < watchedPids.Count)
            {
                if (readings.Count == 0)
                    return default;

                _partialReadingTicks++;
                if (_partialReadingTicks <= PartialReadingGraceTicks)
                    return new StallTestResult(hasRawReadings: true);
            }
            else
            {
                _partialReadingTicks = 0;
            }

            return EvaluateCounterReadings(readings);
        }

        /// <summary>
        /// Evaluates per-process CPU/I/O readings to determine idle, I/O-idle, and aggregate states.
        /// </summary>
        internal static StallTestResult EvaluateCounterReadings(IReadOnlyList<(float Cpu, float Io)> readings)
        {
            if (readings.Count == 0)
                return default;

            var anyWorking = false;
            var allIoIdle = true;
            var totalCpu = 0f;
            var totalIo = 0f;

            foreach (var (cpu, io) in readings)
            {
                totalCpu += cpu;
                totalIo += io;

                if (io > 10240)
                    allIoIdle = false;

                if (cpu > 1 || io > 10240)
                    anyWorking = true;
            }

            return new StallTestResult(
                isIdle: !anyWorking,
                isIoIdle: allIoIdle,
                aggregateCpu: totalCpu,
                aggregateIo: totalIo);
        }

        /// <summary>
        /// Determines if aggregate CPU is roughly stable compared to the previous reading.
        /// Used to gate the I/O-idle path so that legitimately CPU-heavy (but I/O-quiet) work is not killed.
        /// </summary>
        internal static bool IsCpuStable(float prevCpu, float currentCpu)
        {
            if (prevCpu < 0)
                return false; // no valid previous reading

            return Math.Abs(currentCpu - prevCpu) <= SteadyCpuTolerance;
        }

        /// <summary>
        /// Determines if aggregate counter readings indicate a steady state (values not changing significantly).
        /// </summary>
        internal static bool IsSteadyState(float prevCpu, float prevIo, float currentCpu, float currentIo)
        {
            if (prevCpu < 0)
                return false; // no valid previous reading

            return Math.Abs(currentCpu - prevCpu) <= SteadyCpuTolerance &&
                   Math.Abs(currentIo - prevIo) <= SteadyIoTolerance &&
                   currentIo < SteadyIoMaxForStall;
        }

        private void UninstallThread(object parameters)
        {
            var options = parameters as RunUninstallerOptions;
            Debug.Assert(options != null, "options != null");

            Exception error = null;
            var retry = false;
            try
            {
                var processSnapshot = Process.GetProcesses().Select(x => x.Id).ToArray();

                using (var uninstaller = UninstallerEntry.RunUninstaller(options.PreferQuiet, options.Simulate, _canRetry))
                {
                    // Can be null during simulation
                    if (uninstaller == null) return;

                    if (options.PreferQuiet && UninstallerEntry.QuietUninstallPossible)
                    {
                        try
                        {
                            uninstaller.PriorityClass = ProcessPriorityClass.BelowNormal;
                        }
                        catch
                        {
                            // Don't care if setting this fails
                        }
                    }

                    var checkCounters = options.PreferQuiet && options.AutoKillStuckQuiet &&
                                        UninstallerEntry.QuietUninstallPossible;

                    var watchedProcesses = new List<Process> { uninstaller };
                    int[] previousWatchedProcessIds = { };

                    var idleCounter = 0;
                    var ioIdleCounter = 0;
                    var steadyStateCounter = 0;
                    var prevAggregateCpu = -1f;
                    var prevAggregateIo = -1f;
                    var prevWatchedPidSet = new HashSet<int>();
                    var noReadingsCounter = 0;

                    // Reset instance-level stall state for this attempt (survives retries otherwise)
                    _partialReadingTicks = 0;
                    _perfCounterBuffer.Clear();

                    while (true)
                    {
                        if (_skipLevel == SkipCurrentLevel.Skip)
                            break;

                        foreach (var watchedProcess in watchedProcesses.ToList())
                            watchedProcesses.AddRange(watchedProcess.GetChildProcesses());

                        if (UninstallerEntry.UninstallerKind == UninstallerType.Msiexec)
                        {
                            foreach (var watchedProcess in Process.GetProcessesByName("msiexec"))
                                watchedProcesses.AddRange(watchedProcess.GetChildProcesses());
                        }

                        watchedProcesses = CleanupDeadProcesses(watchedProcesses, processSnapshot).ToList();

                        // Check if we are done, or if there are some proceses left that we missed.
                        // We are done when the entry process and all of its spawns exit.
                        if (watchedProcesses.Count == 0)
                        {
                            if (string.IsNullOrEmpty(UninstallerEntry.InstallLocation))
                                break;

                            FindAndAddProcessesToWatch(watchedProcesses, processSnapshot);

                            if (watchedProcesses.Count == 0)
                                break;
                        }

                        // Only try to automate first try. If it fails, don't try to automate 
                        // the rerun in case user or app itself can resolve the issue.
                        if (IsSilentPossible && UninstallToolsGlobalConfig.UseQuietUninstallDaemon && _canRetry)
                        {
                            // There is no point in trying to automatize command line interface programs, or our own helpers
                            if (!UninstallerEntry.QuietUninstallerIsCLI() && !UninstallerEntry.QuietUninstallString
                                .Contains(UninstallToolsGlobalConfig.AppLocation, StringComparison.OrdinalIgnoreCase))
                            {
                                var processIds = SafeGetProcessIds(watchedProcesses).ToArray();

                                options.Owner.SendProcessesToWatchToDeamon(processIds.Except(previousWatchedProcessIds));

                                previousWatchedProcessIds = processIds;
                            }
                        }

                        // Check for deadlocks during silent uninstall. Prevents the task from getting stuck 
                        // indefinitely on stuck uninstallers and unrelated processes spawned by uninstallers.
                        if (checkCounters)
                        {
                            // Reset history-dependent counters when the watched process set changes,
                            // so that aggregate-based detectors don't carry stale state across PID turnover.
                            var currentPidSet = new HashSet<int>(watchedProcesses.Select(p =>
                            {
                                try { return p.Id; } catch { return -1; }
                            }));
                            currentPidSet.Remove(-1);

                            if (!currentPidSet.SetEquals(prevWatchedPidSet))
                            {
                                // Track the new PID set but do NOT reset _partialReadingTicks:
                                // the grace period is a one-time startup window. Resetting it
                                // on every PID change would let recurring child-process churn
                                // keep grace active forever, preventing all stall counters
                                // from advancing. Stall counters and prev-aggregate values are
                                // also preserved — counters naturally reset through
                                // else-branches when conditions don't hold, and keeping prev
                                // aggregates lets relative-change detectors (I/O-idle,
                                // steady-state) continue across child-process churn when
                                // aggregate values stay similar.
                                prevWatchedPidSet = currentPidSet;
                            }

                            var stallResult = TestUninstallerForStalls(watchedProcesses);

                            if (stallResult.HasRawReadings)
                                noReadingsCounter = 0;
                            else
                                noReadingsCounter++;

                            if (stallResult.IsIdle)
                                idleCounter++;
                            else
                                idleCounter = 0;

                            // I/O-idle path requires CPU stability to avoid killing legitimately
                            // CPU-heavy uninstallers that simply perform little I/O.
                            // Resets on ANY tick where the full condition is not met, preventing
                            // non-consecutive stable-CPU samples from accumulating.
                            if (stallResult.IsIoIdle && stallResult.HasReadings
                                                     && IsCpuStable(prevAggregateCpu, stallResult.AggregateCpu))
                                ioIdleCounter++;
                            else
                                ioIdleCounter = 0;

                            if (stallResult.HasReadings)
                            {
                                if (IsSteadyState(prevAggregateCpu, prevAggregateIo,
                                        stallResult.AggregateCpu, stallResult.AggregateIo))
                                    steadyStateCounter++;
                                else
                                    steadyStateCounter = 0;

                                prevAggregateCpu = stallResult.AggregateCpu;
                                prevAggregateIo = stallResult.AggregateIo;
                            }
                            else
                            {
                                // No readings available — break any consecutive steady-state streak
                                // and invalidate previous aggregates so the next reading starts fresh.
                                steadyStateCounter = 0;
                                prevAggregateCpu = -1f;
                                prevAggregateIo = -1f;
                            }

                            // Kill the uninstaller (and children) if they were idle/stalled for too long
                            if (idleCounter > FullIdleThreshold || ioIdleCounter > IoIdleThreshold
                                                                || steadyStateCounter > SteadyStateThreshold
                                                                || noReadingsCounter > NoReadingsThreshold)
                            {
                                KillProcesses(watchedProcesses);
                                throw new IOException(Localisation.UninstallError_UninstallerTimedOut);
                            }
                        }
                        else Thread.Sleep(1000);

                        // Kill the uninstaller (and children) if user told us to or if it was idle for too long
                        if (_skipLevel == SkipCurrentLevel.Terminate)
                        {
                            if (UninstallerEntry.UninstallerKind == UninstallerType.Msiexec)
                                watchedProcesses.AddRange(Process.GetProcessesByName("Msiexec"));

                            KillProcesses(watchedProcesses);
                            break;
                        }
                    }

                    if (_skipLevel == SkipCurrentLevel.None)
                    {
                        var exitVar = uninstaller.ExitCode;
                        if (exitVar != 0)
                        {
                            if (UninstallerEntry.UninstallerKind == UninstallerType.Msiexec &&
                                exitVar == 1602)
                            {
                                // 1602 ERROR_INSTALL_USEREXIT - The user has cancelled the installation.
                                _skipLevel = SkipCurrentLevel.Skip;
                            }
                            else if (UninstallerEntry.UninstallerKind == UninstallerType.Nsis &&
                                     (exitVar == 1 || exitVar == 2))
                            {
                                // 1 - Installation aborted by user (cancel button)
                                // 2 - Installation aborted by script (often after user clicks cancel)
                                _skipLevel = SkipCurrentLevel.Skip;
                            }
                            else if (UninstallerEntry.UninstallerKind == UninstallerType.Nsis &&
                                     exitVar == 1627)
                            {
                                // Nsis OK return code
                            }
                            else if (UninstallerEntry.UninstallerKind == UninstallerType.SimpleDelete &&
                                     exitVar == 1)
                            {
                                // 1 - Installation aborted by user (cancel button)
                                _skipLevel = SkipCurrentLevel.Skip;
                            }
                            else if (exitVar == -1073741510)
                            {
                                /* 3221225786 / 0xC000013A / -1073741510 
                                    The application terminated as a result of a CTRL+C. 
                                    Indicates that the application has been terminated either by user's 
                                    keyboard input CTRL+C or CTRL+Break or closing command prompt window. */
                                _skipLevel = SkipCurrentLevel.Terminate;
                            }
                            else
                            {
                                switch (exitVar)
                                {
                                    case 2:
                                        throw new Exception("The system cannot find the file specified. Indicates that the file can not be found in specified location.");
                                    case 3:
                                        throw new Exception("The system cannot find the path specified. Indicates that the specified path can not be found.");
                                    case 5:
                                        throw new Exception("Access is denied. Indicates that user has no access right to specified resource.");
                                    case 9009:
                                        throw new Exception("Program is not recognized as an internal or external command, operable program or batch file.");
                                    case -2147024846:
                                        throw new Exception("0x80070032 - This app is part of Windows and cannot be uninstalled on a per-user basis.");

                                    default:
                                        if (options.RetryFailedQuiet || (UninstallerEntry.UninstallerKind == UninstallerType.Nsis && !options.PreferQuiet))
                                            retry = true;
                                        throw new IOException(Localisation.UninstallError_UninstallerReturnedCode + exitVar);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex;
                Trace.WriteLine(@$"Exception when uninstalling {UninstallerEntry.DisplayName}: {ex}");
            }
            finally
            {
                try
                {
                    _perfCounterBuffer.ForEach(x => x.Value.Dispose());
                }
                catch
                {
                    // Ignore any errors to make sure rest of this code runs
                }
                _perfCounterBuffer.Clear();

                // Take care of the aftermath
                if (_skipLevel != SkipCurrentLevel.None)
                {
                    _skipLevel = SkipCurrentLevel.None;

                    CurrentStatus = UninstallStatus.Skipped;
                    CurrentError = new OperationCanceledException(Localisation.ManagerError_Skipped);
                }
                else if (error != null)
                {
                    //Localisation.ManagerError_PrematureWorkerStop is unused
                    CurrentStatus = UninstallStatus.Failed;
                    CurrentError = error;
                }
                else
                {
                    CurrentStatus = UninstallStatus.Completed;
                }

                if (retry && _canRetry)
                {
                    CurrentStatus = UninstallStatus.Waiting;
                    _canRetry = false;
                }
                else
                {
                    Finished = true;
                }
            }
        }

        internal static IEnumerable<ProcessCounterTarget> GetPerformanceCounterTargets(IEnumerable<Process> processes)
        {
            var remainingIds = processes.Select(SafeGetProcessId)
                .Where(x => x > 0)
                .ToHashSet();

            if (remainingIds.Count == 0)
                return Enumerable.Empty<ProcessCounterTarget>();

            var results = new List<ProcessCounterTarget>();

            string[] instanceNames;
            try
            {
                instanceNames = new PerformanceCounterCategory("Process").GetInstanceNames();
            }
            catch
            {
                return results;
            }

            foreach (var instanceName in instanceNames)
            {
                var processId = TryGetProcessIdFromCounterInstance(instanceName);
                if (!processId.HasValue || !remainingIds.Remove(processId.Value))
                    continue;

                results.Add(new ProcessCounterTarget(processId.Value, instanceName));
                if (remainingIds.Count == 0)
                    break;
            }

            return results;
        }

        private static int SafeGetProcessId(Process process)
        {
            try
            {
                return process.Id;
            }
            catch
            {
                return -1;
            }
        }

        private static int? TryGetProcessIdFromCounterInstance(string instanceName)
        {
            try
            {
                using (var counter = new PerformanceCounter("Process", "ID Process", instanceName, true))
                {
                    var rawValue = counter.RawValue;
                    return rawValue > 0 ? (int)rawValue : null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<int> SafeGetProcessIds(IEnumerable<Process> processes)
        {
            return processes.Select(x =>
            {
                try
                {
                    x.Refresh();
                    if (x.MainWindowHandle == IntPtr.Zero)
                        return -1;

                    Debug.WriteLine("Process ID " + x.Id + " is running: " + !Process.GetProcessById(x.Id).HasExited);
                    return x.Id;
                }
                catch
                {
                    // Ignore errors caused by processes that exited
                    return -1;
                }
            }).Where(x => x >= 0);
        }

        private void FindAndAddProcessesToWatch(ICollection<Process> watchedProcesses, int[] runningProcessIds)
        {
            var candidates = Process.GetProcesses().Where(x => !runningProcessIds.Contains(x.Id));
            foreach (var process in candidates)
            {
                try
                {
                    if (process.MainModule!.FileName!.Contains(
                            UninstallerEntry.InstallLocation, StringComparison.InvariantCultureIgnoreCase) ||
                        process.GetCommandLine().Contains(
                            UninstallerEntry.InstallLocation, StringComparison.InvariantCultureIgnoreCase))
                    {
                        watchedProcesses.Add(process);
                    }
                }
                catch
                {
                    // Ignore permission and access errors
                }
            }
        }

        /// <summary>
        /// Remove duplicate, dead, and blacklisted processes
        /// </summary>
        private static IEnumerable<Process> CleanupDeadProcesses(IEnumerable<Process> watchedProcesses, int[] runningProcessIds)
        {
            return watchedProcesses.DistinctBy(x => x.Id).Where(p =>
            {
                try
                {
                    if (p.HasExited)
                        return false;

                    var pName = p.ProcessName;
                    if (NamesOfIgnoredProcesses.Any(n =>
                        pName.Equals(n, StringComparison.InvariantCultureIgnoreCase)))
                        return false;
                }
                catch (Win32Exception)
                {
                    return false;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }

                return !runningProcessIds.Contains(p.Id);
            });
        }

        private sealed class PerfCounterEntry : IDisposable
        {
            public PerfCounterEntry(PerformanceCounter[] counter, CounterSample[] sample)
            {
                Counter = counter;
                Sample = sample;
            }

            public PerformanceCounter[] Counter { get; }

            public CounterSample[] Sample { get; }

            public void Dispose()
            {
                foreach (var performanceCounter in Counter)
                {
                    performanceCounter?.Dispose();
                }
            }
        }

        internal readonly struct StallTestResult
        {
            /// <summary>True when at least one process produced usable counter readings.</summary>
            public bool HasReadings { get; }
            /// <summary>True when at least one process produced counter data, even if
            /// suppressed by the partial-reading grace period. Used to prevent the
            /// no-readings safety net from firing when raw data does exist.</summary>
            public bool HasRawReadings { get; }
            /// <summary>Both CPU and I/O are below thresholds for all monitored processes.</summary>
            public bool IsIdle { get; }
            /// <summary>I/O is below threshold for all monitored processes (CPU may be active).</summary>
            public bool IsIoIdle { get; }
            /// <summary>Sum of CPU usage across all monitored processes.</summary>
            public float AggregateCpu { get; }
            /// <summary>Sum of I/O rates across all monitored processes.</summary>
            public float AggregateIo { get; }

            public StallTestResult(bool isIdle, bool isIoIdle, float aggregateCpu, float aggregateIo)
            {
                HasReadings = true;
                HasRawReadings = true;
                IsIdle = isIdle;
                IsIoIdle = isIoIdle;
                AggregateCpu = aggregateCpu;
                AggregateIo = aggregateIo;
            }

            /// <summary>Creates a result with no usable readings but indicates raw counter
            /// data existed (e.g. partial readings suppressed by grace period).</summary>
            internal StallTestResult(bool hasRawReadings)
            {
                HasRawReadings = hasRawReadings;
            }
        }

        internal sealed class ProcessCounterTarget
        {
            public ProcessCounterTarget(int processId, string counterInstanceName)
            {
                ProcessId = processId;
                CounterInstanceName = counterInstanceName;
            }

            public int ProcessId { get; }

            public string CounterInstanceName { get; }
        }

        internal sealed class RunUninstallerOptions
        {
            public RunUninstallerOptions(bool autoKillStuckQuiet, bool retryFailedQuiet, bool preferQuiet, bool simulate, BulkUninstallTask owner)
            {
                AutoKillStuckQuiet = autoKillStuckQuiet;
                RetryFailedQuiet = retryFailedQuiet;
                PreferQuiet = preferQuiet;
                Simulate = simulate;
                Owner = owner;
            }

            public bool AutoKillStuckQuiet { get; }

            public bool PreferQuiet { get; }

            public bool RetryFailedQuiet { get; }

            public bool Simulate { get; }

            public BulkUninstallTask Owner { get; }
        }

        internal enum SkipCurrentLevel
        {
            None = 0,
            Terminate,
            Skip
        }

        public void Pause()
        {
            lock (_operationLock)
            {
                if (CurrentStatus == UninstallStatus.Waiting)
                    CurrentStatus = UninstallStatus.Paused;
            }
        }

        public void Resume()
        {
            lock (_operationLock)
            {
                if (CurrentStatus == UninstallStatus.Paused)
                    CurrentStatus = UninstallStatus.Waiting;
            }
        }
    }
}
