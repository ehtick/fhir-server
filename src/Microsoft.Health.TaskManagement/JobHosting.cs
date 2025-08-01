﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Extensions.DependencyInjection;
using Newtonsoft.Json;

namespace Microsoft.Health.JobManagement
{
    public class JobHosting
    {
        private static readonly ActivitySource JobHostingActivitySource = new ActivitySource(nameof(JobHosting));
        private readonly IQueueClient _queueClient;
        private readonly IJobFactory _jobFactory;
        private readonly ILogger<JobHosting> _logger;
        private DateTime _lastHeartbeatLog;

        public JobHosting(IQueueClient queueClient, IJobFactory jobFactory, ILogger<JobHosting> logger)
        {
            EnsureArg.IsNotNull(queueClient, nameof(queueClient));
            EnsureArg.IsNotNull(jobFactory, nameof(jobFactory));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _queueClient = queueClient;
            _jobFactory = jobFactory;
            _logger = logger;
        }

        public int PollingFrequencyInSeconds { get; set; } = Constants.DefaultPollingFrequencyInSeconds;

        public int JobHeartbeatTimeoutThresholdInSeconds { get; set; } = Constants.DefaultJobHeartbeatTimeoutThresholdInSeconds;

        public double JobHeartbeatIntervalInSeconds { get; set; } = Constants.DefaultJobHeartbeatIntervalInSeconds;

        public async Task ExecuteAsync(byte queueType, short runningJobCount, string workerName, CancellationTokenSource cancellationTokenSource)
        {
            var workers = new List<Task>();
            _lastHeartbeatLog = DateTime.UtcNow;

            _logger.LogInformation("Queue {QueueType} is starting.", queueType);

            // parallel dequeue
            for (var thread = 0; thread < runningJobCount; thread++)
            {
                workers.Add(Task.Run(async () =>
                {
                    // random delay to avoid convoys
                    await Task.Delay(TimeSpan.FromSeconds(RandomNumberGenerator.GetInt32(100) / 100.0 * PollingFrequencyInSeconds));

                    var checkTimeoutJobStopwatch = Stopwatch.StartNew();

                    while (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        if (DateTime.UtcNow - _lastHeartbeatLog > TimeSpan.FromHours(1))
                        {
                            _lastHeartbeatLog = DateTime.UtcNow;
                            _logger.LogInformation("{QueueType} working is running.", queueType);
                        }

                        JobInfo nextJob = null;
                        if (_queueClient.IsInitialized())
                        {
                            try
                            {
                                _logger.LogDebug("Dequeuing next job for {QueueType}.", queueType);

                                if (checkTimeoutJobStopwatch.Elapsed.TotalSeconds > 600)
                                {
                                    checkTimeoutJobStopwatch.Restart();
                                    nextJob = await _queueClient.DequeueAsync(queueType, workerName, JobHeartbeatTimeoutThresholdInSeconds, cancellationTokenSource.Token, null, true);
                                }

                                nextJob ??= await _queueClient.DequeueAsync(queueType, workerName, JobHeartbeatTimeoutThresholdInSeconds, cancellationTokenSource.Token);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to dequeue new job for {QueueType}.", queueType);
                            }
                        }

                        if (nextJob != null)
                        {
                            using (Activity activity = JobHostingActivitySource.StartActivity(
                                JobHostingActivitySource.Name,
                                ActivityKind.Server))
                            {
                                if (activity == null)
                                {
                                    _logger.LogWarning("Failed to start an activity.");
                                }

                                activity?.SetTag("CreateDate", nextJob.CreateDate);
                                activity?.SetTag("HeartbeatDateTime", nextJob.HeartbeatDateTime);
                                activity?.SetTag("Id", nextJob.Id);
                                activity?.SetTag("QueueType", nextJob.QueueType);
                                activity?.SetTag("Version", nextJob.Version);

                                _logger.LogJobInformation(nextJob, "Job dequeued.");
                                await ExecuteJobAsync(nextJob);
                            }
                        }
                        else
                        {
                            try
                            {
                                _logger.LogDebug("Empty queue {QueueType}. Delaying until next iteration.", queueType);
                                await Task.Delay(TimeSpan.FromSeconds(PollingFrequencyInSeconds), cancellationTokenSource.Token);
                            }
                            catch (TaskCanceledException)
                            {
                                _logger.LogInformation("Queue {QueueType} is stopping, worker is shutting down.", queueType);
                            }
                        }
                    }
                }));
            }

            try
            {
                // If any worker crashes or complete after cancellation due to shutdown,
                // cancel all workers and wait for completion so they don't crash unnecessarily.
                await Task.WhenAny(workers.ToArray());
#if NET6_0
                cancellationTokenSource.Cancel();
#else
                await cancellationTokenSource.CancelAsync();
#endif
                await Task.WhenAll(workers.ToArray());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job failed to execute. Queue type: {QueueType}", queueType);
            }
        }

        private async Task ExecuteJobAsync(JobInfo jobInfo)
        {
            EnsureArg.IsNotNull(jobInfo, nameof(jobInfo));
            using var jobCancellationToken = new CancellationTokenSource();

            using IScoped<IJob> job = _jobFactory.Create(jobInfo);

            if (job?.Value == null)
            {
                _logger.LogJobWarning(jobInfo, "Not supported job type.", jobInfo.Id);
                return;
            }

            try
            {
                _logger.LogJobInformation(jobInfo, "Job starting.", jobInfo.Id, jobInfo.QueueType);

                if (jobInfo.CancelRequested)
                {
                    // For cancelled job, try to execute it for potential cleanup.
#if NET6_0
                    jobCancellationToken.Cancel();
#else
                    await jobCancellationToken.CancelAsync();
#endif
                }

                Task<string> runningJob = ExecuteJobWithHeartbeatsAsync(
                                    _queueClient,
                                    jobInfo.QueueType,
                                    jobInfo.Id,
                                    jobInfo.Version,
                                    cancellationSource => job.Value.ExecuteAsync(jobInfo, cancellationSource.Token),
                                    TimeSpan.FromSeconds(JobHeartbeatIntervalInSeconds),
                                    jobCancellationToken);

                jobInfo.Result = await runningJob;
            }
            catch (JobExecutionException ex)
            {
                if (ex.IsCustomerCaused)
                {
                    _logger.LogJobWarning(ex, jobInfo, "Job failed due to customer caused issue.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
                }
                else
                {
                    _logger.LogJobError(ex, jobInfo, "Job failed.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
                }

                jobInfo.Result = JsonConvert.SerializeObject(ex.Error);
                jobInfo.Status = JobStatus.Failed;

                try
                {
                    await _queueClient.CompleteJobAsync(jobInfo, true, CancellationToken.None);
                }
                catch (Exception completeEx)
                {
                    _logger.LogJobError(completeEx, jobInfo, "Job failed to complete.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
                }

                return;
            }
            catch (JobExecutionSoftFailureException ex)
            {
                if (ex.IsCustomerCaused)
                {
                    _logger.LogJobWarning(ex, jobInfo, "Job soft failed due to customer caused issue.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
                }
                else
                {
                    _logger.LogJobError(ex, jobInfo, "Job soft failed.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
                }

                jobInfo.Result = JsonConvert.SerializeObject(ex.Error);
                jobInfo.Status = JobStatus.Failed;

                try
                {
                    await _queueClient.CompleteJobAsync(jobInfo, false, CancellationToken.None);
                }
                catch (Exception completeEx)
                {
                    _logger.LogJobError(completeEx, jobInfo, "Job failed to complete on soft job failure.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
                }

                return;
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogJobWarning(ex, jobInfo, "Job canceled due to unhandled cancellation exception.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
                jobInfo.Status = JobStatus.Cancelled;

                try
                {
                    await _queueClient.CompleteJobAsync(jobInfo, true, CancellationToken.None);
                }
                catch (Exception completeEx)
                {
                    _logger.LogJobError(completeEx, jobInfo, "Job failed to complete.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
                }

                return;
            }
            catch (Exception ex)
            {
                _logger.LogJobError(ex, jobInfo, "Job failed with generic exception.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);

                object error = new { message = ex.Message, stackTrace = ex.StackTrace };
                jobInfo.Result = JsonConvert.SerializeObject(error);
                jobInfo.Status = JobStatus.Failed;

                try
                {
                    await _queueClient.CompleteJobAsync(jobInfo, true, CancellationToken.None);
                }
                catch (Exception completeEx)
                {
                    _logger.LogJobError(completeEx, jobInfo, "Job failed to complete.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
                }

                return;
            }

            try
            {
                jobInfo.Status = JobStatus.Completed;
                await _queueClient.CompleteJobAsync(jobInfo, true, CancellationToken.None);
                _logger.LogJobInformation(jobInfo, "Job completed.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
            }
            catch (Exception completeEx)
            {
                _logger.LogJobError(completeEx, jobInfo, "Job failed to complete.", jobInfo.Id, jobInfo.GroupId, jobInfo.QueueType);
            }
        }

        public static async Task<string> ExecuteJobWithHeartbeatsAsync(IQueueClient queueClient, byte queueType, long jobId, long version, Func<CancellationTokenSource, Task<string>> action, TimeSpan heartbeatPeriod, CancellationTokenSource cancellationTokenSource)
        {
            EnsureArg.IsNotNull(queueClient, nameof(queueClient));
            EnsureArg.IsNotNull(action, nameof(action));

            var jobInfo = new JobInfo { QueueType = queueType, Id = jobId, Version = version }; // not other data points

            // WARNING: Avoid using 'async' lambda when delegate type returns 'void'
            await using (new Timer(async _ => await PutJobHeartbeatAsync(queueClient, jobInfo, cancellationTokenSource), null, TimeSpan.FromSeconds(RandomNumberGenerator.GetInt32(100) / 100.0 * heartbeatPeriod.TotalSeconds), heartbeatPeriod))
            {
                return await action(cancellationTokenSource);
            }
        }

        private static async Task PutJobHeartbeatAsync(IQueueClient queueClient, JobInfo jobInfo, CancellationTokenSource cancellationTokenSource)
        {
            try // this try/catch is redundant with try/catch in queueClient.PutJobHeartbeatAsync, but it is extra guarantee
            {
                var cancel = await queueClient.PutJobHeartbeatAsync(jobInfo, cancellationTokenSource.Token);
                if (cancel)
                {
#if NET6_0
                    cancellationTokenSource.Cancel();
#else
                    await cancellationTokenSource.CancelAsync();
#endif
                }
            }
            catch
            {
                // do nothing
            }
        }
    }
}
