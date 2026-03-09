using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UninstallTools.Uninstaller;

namespace BulkCrapUninstallerTests.UninstallTools
{
    [TestClass]
    public class BulkUninstallEntryTests
    {
        #region GetPerformanceCounterTargets

        [TestCategory("StallDetection")]
        [TestMethod]
        public void GetPerformanceCounterTargets_ResolvesDistinctCounterInstancesForDuplicateProcessNames()
        {
            using var firstProcess = StartSleepProcess();
            using var secondProcess = StartSleepProcess();

            try
            {
                var resolvedBothProcesses = SpinWait.SpinUntil(() =>
                {
                    var targets = BulkUninstallEntry.GetPerformanceCounterTargets(new[] { firstProcess, secondProcess }).ToList();

                    return targets.Any(x => x.ProcessId == firstProcess.Id) &&
                           targets.Any(x => x.ProcessId == secondProcess.Id);
                }, TimeSpan.FromSeconds(5));

                Assert.IsTrue(resolvedBothProcesses,
                    "Expected both duplicate-name processes to resolve to distinct performance counter instances.");
            }
            finally
            {
                TryKill(firstProcess);
                TryKill(secondProcess);
            }
        }

        #endregion

        #region EvaluateCounterReadings

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_NoReadings_ReturnsDefault()
        {
            var result = BulkUninstallEntry.EvaluateCounterReadings(Array.Empty<(float, float)>());

            Assert.IsFalse(result.HasReadings, "Empty readings should not count as having data");
            Assert.IsFalse(result.IsIdle);
            Assert.IsFalse(result.IsIoIdle);
            Assert.AreEqual(0f, result.AggregateCpu);
            Assert.AreEqual(0f, result.AggregateIo);
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_NoReadings_DoesNotTriggerSteadyState()
        {
            // Simulates the false-positive scenario: repeated empty readings should not
            // allow IsSteadyState to accumulate, because HasReadings is false.
            var emptyResult = BulkUninstallEntry.EvaluateCounterReadings(Array.Empty<(float, float)>());
            Assert.IsFalse(emptyResult.HasReadings);

            // Even though 0→0 would pass IsSteadyState, the caller must skip
            // steady-state tracking when HasReadings is false.
            Assert.IsTrue(BulkUninstallEntry.IsSteadyState(0f, 0f, 0f, 0f),
                "IsSteadyState itself returns true for 0/0 — the guard must be on HasReadings");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_AllIdle_ReturnsIdleAndIoIdle()
        {
            // CPU ≤ 1% and I/O ≤ 10240 for all
            var readings = new (float, float)[] { (0.5f, 1000f), (1f, 10240f) };
            var result = BulkUninstallEntry.EvaluateCounterReadings(readings);

            Assert.IsTrue(result.IsIdle, "Both CPU and I/O low → should be idle");
            Assert.IsTrue(result.IsIoIdle, "I/O low for all → should be I/O idle");
            Assert.AreEqual(1.5f, result.AggregateCpu, 0.001f);
            Assert.AreEqual(11240f, result.AggregateIo, 0.001f);
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_HighCpuZeroIo_NotIdleButIoIdle()
        {
            // Issue #579 scenario: CPU high, I/O zero
            var readings = new (float, float)[] { (15f, 0f) };
            var result = BulkUninstallEntry.EvaluateCounterReadings(readings);

            Assert.IsFalse(result.IsIdle, "CPU > 1% → not fully idle");
            Assert.IsTrue(result.IsIoIdle, "I/O = 0 → I/O idle");
            Assert.AreEqual(15f, result.AggregateCpu, 0.001f);
            Assert.AreEqual(0f, result.AggregateIo, 0.001f);
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_HighCpuHighIo_NotIdleNotIoIdle()
        {
            // Actively working process
            var readings = new (float, float)[] { (50f, 500000f) };
            var result = BulkUninstallEntry.EvaluateCounterReadings(readings);

            Assert.IsFalse(result.IsIdle);
            Assert.IsFalse(result.IsIoIdle);
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_MixedProcesses_AnyWorkingMeansNotIdle()
        {
            // One idle, one working → not idle
            var readings = new (float, float)[] { (0.5f, 100f), (10f, 50000f) };
            var result = BulkUninstallEntry.EvaluateCounterReadings(readings);

            Assert.IsFalse(result.IsIdle, "One working process → not idle");
            Assert.IsFalse(result.IsIoIdle, "One process with high I/O → not I/O idle");
            Assert.AreEqual(10.5f, result.AggregateCpu, 0.001f);
            Assert.AreEqual(50100f, result.AggregateIo, 0.001f);
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_HighCpuLowIo_MultipleProcesses_IoIdleButNotIdle()
        {
            // Multiple processes with high CPU but all I/O under threshold
            var readings = new (float, float)[] { (10f, 5000f), (8f, 3000f) };
            var result = BulkUninstallEntry.EvaluateCounterReadings(readings);

            Assert.IsFalse(result.IsIdle, "CPU > 1% → not fully idle");
            Assert.IsTrue(result.IsIoIdle, "All I/O ≤ 10240 → I/O idle");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_OneProcessIoIdleOneNot_NotIoIdle()
        {
            // Only one process exceeding I/O threshold breaks allIoIdle
            var readings = new (float, float)[] { (5f, 100f), (5f, 20000f) };
            var result = BulkUninstallEntry.EvaluateCounterReadings(readings);

            Assert.IsFalse(result.IsIoIdle, "One process with I/O > 10240 → not I/O idle");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_BoundaryValues_CpuExactly1_IoExactly10240_IsIdle()
        {
            // Exactly at threshold → still idle (uses ≤ comparison)
            var readings = new (float, float)[] { (1f, 10240f) };
            var result = BulkUninstallEntry.EvaluateCounterReadings(readings);

            Assert.IsTrue(result.IsIdle, "CPU = 1 and I/O = 10240 → at boundary, should be idle");
            Assert.IsTrue(result.IsIoIdle, "I/O = 10240 → at boundary, should be I/O idle");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_BoundaryValues_CpuSlightlyAbove1_NotIdle()
        {
            var readings = new (float, float)[] { (1.01f, 0f) };
            var result = BulkUninstallEntry.EvaluateCounterReadings(readings);

            Assert.IsFalse(result.IsIdle, "CPU > 1 → not idle");
            Assert.IsTrue(result.IsIoIdle, "I/O = 0 → I/O idle");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void EvaluateCounterReadings_BoundaryValues_IoSlightlyAbove10240_NotIoIdle()
        {
            var readings = new (float, float)[] { (0f, 10241f) };
            var result = BulkUninstallEntry.EvaluateCounterReadings(readings);

            Assert.IsFalse(result.IsIdle, "I/O > 10240 → not idle");
            Assert.IsFalse(result.IsIoIdle, "I/O > 10240 → not I/O idle");
        }

        #endregion

        #region IsCpuStable

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsCpuStable_NoPreviousReading_ReturnsFalse()
        {
            Assert.IsFalse(BulkUninstallEntry.IsCpuStable(-1f, 10f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsCpuStable_IdenticalValues_ReturnsTrue()
        {
            Assert.IsTrue(BulkUninstallEntry.IsCpuStable(10f, 10f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsCpuStable_WithinTolerance_ReturnsTrue()
        {
            Assert.IsTrue(BulkUninstallEntry.IsCpuStable(10f, 15f)); // exactly at ±5
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsCpuStable_ExceedsTolerance_ReturnsFalse()
        {
            Assert.IsFalse(BulkUninstallEntry.IsCpuStable(10f, 16f)); // 6 > 5
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsCpuStable_Issue579Scenario_StuckDialogIsStable()
        {
            // Stuck msiexec dialog: CPU ~10% each tick, stable → should return true
            Assert.IsTrue(BulkUninstallEntry.IsCpuStable(10f, 12f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsCpuStable_LegitWorkload_VaryingCpuIsUnstable()
        {
            // Legitimate CPU-heavy work: CPU jumps from 30 to 60 → should return false
            Assert.IsFalse(BulkUninstallEntry.IsCpuStable(30f, 60f));
        }

        #endregion

        #region IsSteadyState

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsSteadyState_NoPreviousReading_ReturnsFalse()
        {
            Assert.IsFalse(BulkUninstallEntry.IsSteadyState(-1f, 0f, 10f, 5000f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsSteadyState_IdenticalValues_ReturnsTrue()
        {
            Assert.IsTrue(BulkUninstallEntry.IsSteadyState(10f, 5000f, 10f, 5000f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsSteadyState_ValuesWithinTolerance_ReturnsTrue()
        {
            // CPU within ±5, I/O within ±20480, I/O < 524288
            Assert.IsTrue(BulkUninstallEntry.IsSteadyState(10f, 50000f, 14f, 60000f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsSteadyState_CpuExceedsTolerance_ReturnsFalse()
        {
            // CPU change of 6 > SteadyCpuTolerance(5)
            Assert.IsFalse(BulkUninstallEntry.IsSteadyState(10f, 5000f, 16f, 5000f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsSteadyState_IoExceedsTolerance_ReturnsFalse()
        {
            // I/O change of 30000 > SteadyIoTolerance(20480)
            Assert.IsFalse(BulkUninstallEntry.IsSteadyState(10f, 50000f, 10f, 80000f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsSteadyState_IoAboveMaxThreshold_ReturnsFalse()
        {
            // I/O at 600000 > SteadyIoMaxForStall(524288), even though values are steady
            Assert.IsFalse(BulkUninstallEntry.IsSteadyState(10f, 600000f, 10f, 600000f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsSteadyState_IoJustBelowMaxThreshold_ReturnsTrue()
        {
            float justBelow = BulkUninstallEntry.SteadyIoMaxForStall - 1;
            Assert.IsTrue(BulkUninstallEntry.IsSteadyState(10f, justBelow, 10f, justBelow));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsSteadyState_CpuAtExactTolerance_ReturnsTrue()
        {
            // Exactly at ±5 → within tolerance (uses ≤)
            Assert.IsTrue(BulkUninstallEntry.IsSteadyState(10f, 5000f, 15f, 5000f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsSteadyState_IoAtExactTolerance_ReturnsTrue()
        {
            // Exactly at ±20480 → within tolerance (uses ≤)
            Assert.IsTrue(BulkUninstallEntry.IsSteadyState(10f, 50000f, 10f, 70480f));
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void IsSteadyState_ZeroValues_ReturnsTrue()
        {
            // Both zero → steady (and below max threshold)
            Assert.IsTrue(BulkUninstallEntry.IsSteadyState(0f, 0f, 0f, 0f));
        }

        #endregion

        #region Threshold constants sanity

        [TestCategory("StallDetection")]
        [TestMethod]
        public void Thresholds_IoIdleLongerThanFullIdle()
        {
            Assert.IsTrue(BulkUninstallEntry.IoIdleThreshold > BulkUninstallEntry.FullIdleThreshold,
                "I/O-idle timeout must be longer than full-idle timeout (less certain signal)");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void Thresholds_SteadyStateLongerThanIoIdle()
        {
            Assert.IsTrue(BulkUninstallEntry.SteadyStateThreshold > BulkUninstallEntry.IoIdleThreshold,
                "Steady-state timeout must be longer than I/O-idle timeout (least certain signal)");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void Thresholds_NoReadingsAtLeastAsSteadyState()
        {
            Assert.IsTrue(BulkUninstallEntry.NoReadingsThreshold >= BulkUninstallEntry.SteadyStateThreshold,
                "No-readings timeout should be at least as long as steady-state timeout (last-resort safety net)");
        }

        #endregion

        #region StallTestResult struct

        [TestCategory("StallDetection")]
        [TestMethod]
        public void StallTestResult_Default_AllFalseAndZero()
        {
            var result = default(BulkUninstallEntry.StallTestResult);

            Assert.IsFalse(result.HasReadings, "Default should have no readings");
            Assert.IsFalse(result.HasRawReadings, "Default should have no raw readings");
            Assert.IsFalse(result.IsIdle);
            Assert.IsFalse(result.IsIoIdle);
            Assert.AreEqual(0f, result.AggregateCpu);
            Assert.AreEqual(0f, result.AggregateIo);
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void StallTestResult_Constructed_HasReadingsTrue()
        {
            var result = BulkUninstallEntry.EvaluateCounterReadings(new (float, float)[] { (5f, 1000f) });

            Assert.IsTrue(result.HasReadings, "Result from actual readings should have HasReadings=true");
            Assert.IsTrue(result.HasRawReadings, "Result from actual readings should have HasRawReadings=true");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void StallTestResult_GracePeriod_HasRawReadingsButNotHasReadings()
        {
            var result = new BulkUninstallEntry.StallTestResult(hasRawReadings: true);

            Assert.IsFalse(result.HasReadings, "Grace period result should not have usable readings");
            Assert.IsTrue(result.HasRawReadings, "Grace period result should indicate raw data exists");
            Assert.IsFalse(result.IsIdle);
            Assert.IsFalse(result.IsIoIdle);
            Assert.AreEqual(0f, result.AggregateCpu);
            Assert.AreEqual(0f, result.AggregateIo);
        }

        #endregion

        #region Counter reset behavior (documents loop invariants)

        /// <summary>
        /// Simulates the stall-detection counter logic from UninstallThread to verify
        /// that all counters reset correctly on PID set change, no-reading gaps, and
        /// partial-reading ticks. This tests the algorithm, not the live loop.
        /// </summary>
        private static void SimulateStallLoop(
            IReadOnlyList<(HashSet<int> pids, (float Cpu, float Io)[] readings)> ticks,
            out int idleCounter, out int ioIdleCounter, out int steadyStateCounter)
        {
            SimulateStallLoop(ticks, out idleCounter, out ioIdleCounter, out steadyStateCounter, out _);
        }

        private static void SimulateStallLoop(
            IReadOnlyList<(HashSet<int> pids, (float Cpu, float Io)[] readings)> ticks,
            out int idleCounter, out int ioIdleCounter, out int steadyStateCounter,
            out int noReadingsCounter)
        {
            idleCounter = 0;
            ioIdleCounter = 0;
            steadyStateCounter = 0;
            noReadingsCounter = 0;
            var prevAggregateCpu = -1f;
            var prevAggregateIo = -1f;
            var prevWatchedPidSet = new HashSet<int>();
            var partialReadingTicks = 0;

            foreach (var (pids, readings) in ticks)
            {
                if (!pids.SetEquals(prevWatchedPidSet))
                {
                    prevWatchedPidSet = new HashSet<int>(pids);
                }

                // Mirror the partial-reading grace period in TestUninstallerForStalls:
                // one-time grace window, then use available readings.
                BulkUninstallEntry.StallTestResult stallResult;
                if (readings.Length < pids.Count)
                {
                    if (readings.Length == 0)
                    {
                        stallResult = default;
                    }
                    else
                    {
                        partialReadingTicks++;
                        stallResult = partialReadingTicks <= BulkUninstallEntry.PartialReadingGraceTicks
                            ? new BulkUninstallEntry.StallTestResult(hasRawReadings: true)
                            : BulkUninstallEntry.EvaluateCounterReadings(readings);
                    }
                }
                else
                {
                    stallResult = BulkUninstallEntry.EvaluateCounterReadings(readings);
                }

                // Three reading states: full, partial-raw (grace), and zero.
                if (stallResult.HasRawReadings)
                    noReadingsCounter = 0;
                else
                    noReadingsCounter++;

                var isGracePeriod = stallResult.HasRawReadings && !stallResult.HasReadings;
                if (!isGracePeriod)
                {
                    if (stallResult.IsIdle)
                        idleCounter++;
                    else
                        idleCounter = 0;

                    if (stallResult.IsIoIdle && stallResult.HasReadings
                                             && BulkUninstallEntry.IsCpuStable(prevAggregateCpu, stallResult.AggregateCpu))
                        ioIdleCounter++;
                    else
                        ioIdleCounter = 0;

                    if (stallResult.HasReadings)
                    {
                        if (BulkUninstallEntry.IsSteadyState(prevAggregateCpu, prevAggregateIo,
                                stallResult.AggregateCpu, stallResult.AggregateIo))
                            steadyStateCounter++;
                        else
                            steadyStateCounter = 0;

                        prevAggregateCpu = stallResult.AggregateCpu;
                        prevAggregateIo = stallResult.AggregateIo;
                    }
                    else
                    {
                        steadyStateCounter = 0;
                        prevAggregateCpu = -1f;
                        prevAggregateIo = -1f;
                    }
                }
            }
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_PidSetChange_CountersContinueWhenConditionsHold()
        {
            var pidsA = new HashSet<int> { 100 };
            var pidsB = new HashSet<int> { 200 };

            // 5 ticks of idle with pidsA, then 5 with pidsB — same values
            var ticks = new List<(HashSet<int>, (float, float)[])>();
            for (int i = 0; i < 5; i++)
                ticks.Add((pidsA, new[] { (0.5f, 100f) }));
            for (int i = 0; i < 5; i++)
                ticks.Add((pidsB, new[] { (0.5f, 100f) }));

            SimulateStallLoop(ticks, out var idle, out var ioIdle, out var steady);

            Assert.AreEqual(10, idle,
                "idleCounter should continue across PID change when new set is also idle");
            // Tick 1: no prev → ioIdle=0. Ticks 2-5: +4. Tick 6: prev stays stable → +1. Ticks 7-10: +4. = 9
            Assert.AreEqual(9, ioIdle,
                "ioIdleCounter should continue across PID change when aggregate CPU is stable");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_PidSetChange_CountersResetNaturallyOnCpuJump()
        {
            var pidsA = new HashSet<int> { 100 };
            var pidsB = new HashSet<int> { 200 };

            // 5 idle ticks with pidsA, then pidsB with high CPU (not idle, CPU jumps)
            var ticks = new List<(HashSet<int>, (float, float)[])>();
            for (int i = 0; i < 5; i++)
                ticks.Add((pidsA, new[] { (0.5f, 100f) }));
            ticks.Add((pidsB, new[] { (50f, 0f) }));

            SimulateStallLoop(ticks, out var idle, out var ioIdle, out var steady);

            Assert.AreEqual(0, idle,
                "idleCounter should reset naturally when new PID set is not idle");
            Assert.AreEqual(0, ioIdle,
                "ioIdleCounter should reset naturally when CPU jumps on PID change");
            Assert.AreEqual(0, steady,
                "steadyStateCounter should reset naturally when values jump on PID change");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_PidChurn_DoesNotSuppressIdleDetection()
        {
            // Simulate PID set changing every 10 ticks while all processes are idle.
            // This must NOT prevent idleCounter from reaching the threshold.
            var ticks = new List<(HashSet<int>, (float, float)[])>();
            for (int pidBase = 100; pidBase <= 400; pidBase += 100)
            {
                var pids = new HashSet<int> { pidBase };
                for (int i = 0; i < 10; i++)
                    ticks.Add((pids, new[] { (0.5f, 100f) }));
            }

            SimulateStallLoop(ticks, out var idle, out _, out _);

            Assert.AreEqual(40, idle,
                "idleCounter should accumulate across PID changes when all sets are idle");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_NoReadingGap_BreaksSteadyStateStreak()
        {
            var pids = new HashSet<int> { 100 };

            // 5 ticks of steady state, then 1 tick with no readings, then 5 more steady ticks
            var ticks = new List<(HashSet<int>, (float, float)[])>();
            for (int i = 0; i < 5; i++)
                ticks.Add((pids, new[] { (10f, 5000f) }));
            ticks.Add((pids, Array.Empty<(float, float)>())); // no-reading gap
            for (int i = 0; i < 5; i++)
                ticks.Add((pids, new[] { (10f, 5000f) }));

            SimulateStallLoop(ticks, out _, out _, out var steady);

            // After the gap, prevAggregateCpu resets to -1 so first post-gap tick can't be steady.
            // Then 4 more consecutive steady ticks = 4.
            Assert.AreEqual(4, steady,
                "Steady-state streak must break on no-reading gap (gap resets prev aggregates)");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_NoReadingGap_DoesNotAccumulateSteadyState()
        {
            var pids = new HashSet<int> { 100 };

            // Alternating: 1 real tick, 1 no-reading tick — steady state should never accumulate
            var ticks = new List<(HashSet<int>, (float, float)[])>();
            for (int i = 0; i < 20; i++)
            {
                ticks.Add((pids, new[] { (10f, 5000f) }));
                ticks.Add((pids, Array.Empty<(float, float)>()));
            }

            SimulateStallLoop(ticks, out _, out _, out var steady);

            Assert.AreEqual(0, steady,
                "Alternating real/empty ticks must never build a steady-state streak");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_IoIdleResetsWhenCpuUnstable()
        {
            var pids = new HashSet<int> { 100 };

            // Tick 1: establish baseline (prevCpu = -1, so IsCpuStable = false → ioIdle = 0)
            // Tick 2: I/O idle + CPU stable relative to tick 1 → ioIdle = 1
            // Tick 3: I/O idle but CPU jumps → ioIdle must reset to 0
            // Tick 4: I/O idle + CPU stable relative to tick 3 → ioIdle = 1
            var ticks = new List<(HashSet<int>, (float, float)[])>
            {
                (pids, new[] { (10f, 0f) }),     // baseline
                (pids, new[] { (12f, 0f) }),     // stable CPU (±2 ≤ 5)
                (pids, new[] { (50f, 0f) }),     // CPU jump (38 > 5)
                (pids, new[] { (52f, 0f) }),     // stable again (±2 ≤ 5)
            };

            SimulateStallLoop(ticks, out _, out var ioIdle, out _);

            Assert.AreEqual(1, ioIdle,
                "ioIdleCounter must reset when CPU is unstable, not carry across gaps");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_PartialReadings_DoNotAdvanceCountersDuringGracePeriod()
        {
            // 2 watched PIDs but only 1 reading per tick → partial readings.
            // During the grace period, stall counters must not advance.
            var pids = new HashSet<int> { 100, 200 };

            var ticks = new List<(HashSet<int>, (float, float)[])>();
            for (int i = 0; i < BulkUninstallEntry.PartialReadingGraceTicks; i++)
                ticks.Add((pids, new[] { (0.5f, 100f) })); // only 1 reading for 2 PIDs

            SimulateStallLoop(ticks, out var idle, out var ioIdle, out var steady);

            Assert.AreEqual(0, idle,
                "idleCounter must not advance during partial-reading grace period");
            Assert.AreEqual(0, ioIdle,
                "ioIdleCounter must not advance during partial-reading grace period");
            Assert.AreEqual(0, steady,
                "steadyStateCounter must not advance during partial-reading grace period");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_PartialReadings_AdvanceAfterGracePeriodExpires()
        {
            // 2 watched PIDs but only 1 reading per tick → partial.
            // After the grace period, counters should advance using available readings
            // so that an unreadable PID doesn't suppress stall detection indefinitely.
            var pids = new HashSet<int> { 100, 200 };
            var postGraceTicks = 5;

            var ticks = new List<(HashSet<int>, (float, float)[])>();
            for (int i = 0; i < BulkUninstallEntry.PartialReadingGraceTicks + postGraceTicks; i++)
                ticks.Add((pids, new[] { (0.5f, 100f) })); // idle readings, partial

            SimulateStallLoop(ticks, out var idle, out var ioIdle, out var steady);

            Assert.AreEqual(postGraceTicks, idle,
                "idleCounter must advance once grace period expires");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_PartialReadings_GraceIsOneTimeWindow()
        {
            // Grace period is a one-time startup window that does NOT re-arm
            // when full readings appear. Once the total partial-reading tick budget
            // is consumed, all subsequent partial readings are evaluated normally.
            var pids = new HashSet<int> { 100, 200 };

            var ticks = new List<(HashSet<int>, (float, float)[])>();
            // Nearly exhaust grace period (9 of 10 partial ticks)
            for (int i = 0; i < BulkUninstallEntry.PartialReadingGraceTicks - 1; i++)
                ticks.Add((pids, new[] { (0.5f, 100f) }));
            // Full reading — does NOT reset grace counter
            ticks.Add((pids, new[] { (0.5f, 100f), (0.5f, 100f) }));
            // One more partial tick consumes the last grace tick (#10)
            ticks.Add((pids, new[] { (0.5f, 100f) }));
            // Further partial ticks are post-grace → evaluated normally
            var postGraceTicks = 5;
            for (int i = 0; i < postGraceTicks; i++)
                ticks.Add((pids, new[] { (0.5f, 100f) }));

            SimulateStallLoop(ticks, out var idle, out _, out _);

            // 9 grace ticks (frozen, idle=0) + 1 full tick (idle=1) + 1 grace tick
            // (frozen, idle=1) + 5 evaluated partial ticks (idle=2..6) = idle 6.
            // If grace had re-armed on the full reading, all 6 post-full partial
            // ticks would be grace (frozen) and idle would be only 1.
            Assert.AreEqual(1 + postGraceTicks, idle,
                "Grace must not re-arm on full reading; post-grace partial readings must advance counters");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_NoReadings_IncrementsContinuously()
        {
            var pids = new HashSet<int> { 100 };
            var tickCount = 50;

            // All ticks have zero readings (PID is watched but unreadable)
            var ticks = new List<(HashSet<int>, (float, float)[])>();
            for (int i = 0; i < tickCount; i++)
                ticks.Add((pids, Array.Empty<(float, float)>()));

            SimulateStallLoop(ticks, out _, out _, out _, out var noReadings);

            Assert.AreEqual(tickCount, noReadings,
                "noReadingsCounter should increment every tick with zero readings");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_NoReadings_ResetsOnValidReadings()
        {
            var pids = new HashSet<int> { 100 };

            // 20 ticks with no readings, then 1 tick with valid readings
            var ticks = new List<(HashSet<int>, (float, float)[])>();
            for (int i = 0; i < 20; i++)
                ticks.Add((pids, Array.Empty<(float, float)>()));
            ticks.Add((pids, new[] { (10f, 5000f) }));

            SimulateStallLoop(ticks, out _, out _, out _, out var noReadings);

            Assert.AreEqual(0, noReadings,
                "noReadingsCounter should reset to 0 when valid readings appear");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_PartialReadingsGrace_DoesNotIncrementNoReadingsCounter()
        {
            // Regression test: PID churn with partial readings must not accumulate
            // noReadingsCounter, since raw data IS being produced (HasRawReadings=true).
            var ticks = new List<(HashSet<int>, (float, float)[])>();

            // 35 churn cycles × PartialReadingGraceTicks(10) = 350 ticks > NoReadingsThreshold(300)
            for (int cycle = 0; cycle < 35; cycle++)
            {
                var pids = new HashSet<int> { 100, 200 + cycle }; // PID churn each cycle
                for (int i = 0; i < BulkUninstallEntry.PartialReadingGraceTicks; i++)
                    ticks.Add((pids, new[] { (10f, 5000f) })); // 1 reading for 2 PIDs = partial
            }

            SimulateStallLoop(ticks, out _, out _, out _, out var noReadings);

            Assert.AreEqual(0, noReadings,
                "noReadingsCounter must not accumulate during partial-reading grace when raw data exists");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_PidChurn_WithPartialReadings_CountersAdvanceAfterInitialGrace()
        {
            // Regression test: PID churn with persistent partial readings must NOT keep
            // the grace period active forever. The grace is a one-time startup window;
            // after it expires, partial readings feed into EvaluateCounterReadings so
            // stall counters can advance. Without this fix, resetting _partialReadingTicks
            // on every PID change would suppress all stall detection indefinitely.
            var ticks = new List<(HashSet<int>, (float, float)[])>();

            // 5 churn cycles × 10 ticks = 50 total. All readings are idle + partial.
            for (int cycle = 0; cycle < 5; cycle++)
            {
                var pids = new HashSet<int> { 100, 200 + cycle };
                for (int i = 0; i < 10; i++)
                    ticks.Add((pids, new[] { (0.5f, 100f) })); // 1 idle reading for 2 PIDs
            }

            SimulateStallLoop(ticks, out var idle, out _, out _);

            // First 10 ticks: grace active → IsIdle=false on grace result → idleCounter=0.
            // First 10 ticks: grace active → counters frozen at 0.
            // Ticks 10–49 (40 ticks): grace expired → EvaluateCounterReadings → IsIdle=true.
            Assert.AreEqual(40, idle,
                "idleCounter must advance after initial grace period despite ongoing PID churn with partial readings");
        }

        [TestCategory("StallDetection")]
        [TestMethod]
        public void CounterReset_FullPartialOscillation_CountersAccumulate()
        {
            // Regression test: alternating full and partial readings for a stalled
            // process must allow ioIdleCounter and steadyStateCounter to accumulate.
            // Before the one-time grace fix, full→partial transitions re-armed grace,
            // which reset history-dependent stall state on every cycle.
            var pids = new HashSet<int> { 100, 200 };

            var ticks = new List<(HashSet<int>, (float, float)[])>();
            // Consume grace period (10 partial ticks)
            for (int i = 0; i < BulkUninstallEntry.PartialReadingGraceTicks; i++)
                ticks.Add((pids, new[] { (0.5f, 100f) }));
            // Alternate 5 full + 5 partial for 6 cycles (60 ticks post-grace)
            for (int cycle = 0; cycle < 6; cycle++)
            {
                for (int i = 0; i < 5; i++)
                    ticks.Add((pids, new[] { (0.5f, 100f), (0.5f, 100f) })); // full
                for (int i = 0; i < 5; i++)
                    ticks.Add((pids, new[] { (0.5f, 100f) })); // partial (post-grace)
            }

            SimulateStallLoop(ticks, out _, out var ioIdle, out var steady);

            // All 60 post-grace ticks produce evaluated readings (grace is one-time).
            // The first post-grace tick has prevCpu=-1 so IsCpuStable fails → 59 qualifying ticks.
            Assert.IsTrue(ioIdle > 50,
                $"ioIdleCounter ({ioIdle}) must accumulate across full/partial oscillation");
            Assert.IsTrue(steady > 50,
                $"steadyStateCounter ({steady}) must accumulate across full/partial oscillation");
        }

        #endregion

        #region Helpers

        private static Process StartSleepProcess()
        {
            var powershellPath = Environment.ExpandEnvironmentVariables(
                @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe");

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = powershellPath,
                Arguments = "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.IsNotNull(process);
            return process;
        }

        private static void TryKill(Process process)
        {
            if (process == null)
                return;

            try
            {
                if (process.HasExited)
                    return;

                process.Kill(true);
                process.WaitForExit(2000);
            }
            catch (InvalidOperationException)
            {
            }
            catch (NotSupportedException)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        #endregion
    }
}
