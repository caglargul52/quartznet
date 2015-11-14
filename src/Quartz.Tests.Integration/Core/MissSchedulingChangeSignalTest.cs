﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;

using NUnit.Framework;

using Quartz.Impl;
using Quartz.Logging;
using Quartz.Simpl;
using Quartz.Spi;

namespace Quartz.Tests.Integration.Core
{
    public class MissSchedulingChangeSignalTest
    {
        private static readonly ILog log = LogProvider.GetLogger(typeof (MissSchedulingChangeSignalTest));

        [Test]
        [Explicit]
        public async Task SimpleScheduleAlwaysFiredUnder20S()
        {
            NameValueCollection properties = new NameValueCollection();
            // Use a custom RAMJobStore to produce context switches leading to the race condition
            properties["quartz.jobStore.type"] = typeof (SlowRAMJobStore).AssemblyQualifiedName;
            ISchedulerFactory sf = new StdSchedulerFactory(properties);
            IScheduler sched = await sf.GetScheduler();
            log.Info("------- Initialization Complete -----------");

            log.Info("------- Scheduling Job  -------------------");

            IJobDetail job = JobBuilder.Create<CollectDurationBetweenFireTimesJob>().WithIdentity("job", "group").Build();

            ITrigger trigger = TriggerBuilder.Create()
                .WithIdentity("trigger1", "group1")
                .StartAt(DateTime.UtcNow.AddSeconds(1))
                .WithSimpleSchedule(x => x
                    .WithIntervalInSeconds(1)
                    .RepeatForever()
                    .WithMisfireHandlingInstructionIgnoreMisfires())
                .Build();

            await sched.ScheduleJobAsync(job, trigger);

            // Start up the scheduler (nothing can actually run until the
            // scheduler has been started)
            await sched.StartAsync();

            log.Info("------- Scheduler Started -----------------");

            // wait long enough so that the scheduler has an opportunity to
            // run the job in theory around 50 times
            await Task.Delay(50000);

            List<TimeSpan> durationBetweenFireTimesInMillis = CollectDurationBetweenFireTimesJob.Durations;

            Assert.False(durationBetweenFireTimesInMillis.Count == 0, "Job was not executed once!");

            // Let's check that every call for around 1 second and not between 23 and 30 seconds
            // which would be the case if the scheduling change signal were not checked
            foreach (TimeSpan durationInMillis in durationBetweenFireTimesInMillis)
            {
                Assert.True(durationInMillis.TotalMilliseconds < 20000, "Missed an execution with one duration being between two fires: " + durationInMillis + " (all: "
                                                                        + durationBetweenFireTimesInMillis + ")");
            }
        }
    }

    /// <summary>
    /// A simple job for collecting fire times in order to check that we did not miss one call, for having the race
    ///  condition the job must be real quick and not allowing concurrent executions.
    /// </summary>
    public class CollectDurationBetweenFireTimesJob : IJob
    {
        private static DateTime? lastFireTime;
        private static readonly List<TimeSpan> durationBetweenFireTimes = new List<TimeSpan>();
        private static readonly ILog log = LogProvider.GetLogger(typeof (CollectDurationBetweenFireTimesJob));

        public void Execute(IJobExecutionContext context)
        {
            DateTime now = DateTime.UtcNow;
            log.Info("Fire time: " + now);
            if (lastFireTime != null)
            {
                durationBetweenFireTimes.Add(now - lastFireTime.Value);
            }

            lastFireTime = now;
        }

        public static List<TimeSpan> Durations => durationBetweenFireTimes;
    }

    /// <summary>
    /// Custom RAMJobStore for producing context switches.
    /// </summary>
    public class SlowRAMJobStore : RAMJobStore
    {
        public override async Task<IReadOnlyList<IOperableTrigger>> AcquireNextTriggersAsync(DateTimeOffset noLaterThan, int maxCount, TimeSpan timeWindow)
        {
            var nextTriggers = await base.AcquireNextTriggersAsync(noLaterThan, maxCount, timeWindow);

            // Wait just a bit for hopefully having a context switch leading to the race condition
            await Task.Delay(10);

            return nextTriggers;
        }
    }
}