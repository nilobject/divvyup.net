﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using StackExchange.Redis;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;

namespace DivvyUp
{
    public class Service
    {
        private IDatabase _redis;
        private string _namespace;
        public Service(IDatabase redis = null, string ns = null)
        {
            _redis = redis ?? DivvyUp.RedisDatabase;
            _namespace = ns ?? DivvyUp.Namespace;
        }

        private Logger Logger { get => LogManager.GetCurrentClassLogger(); }

        public Task Enqueue(Job job, bool retry = false)
        {
            return Task.WhenAll(
                _redis.SetAddAsync($"{_namespace}::queues", job.Queue),
                _redis.ListRightPushAsync($"{_namespace}::queue::{job.Queue}", SerializeWork(job, retry))
            );
        }

        public async Task<IEnumerable<string>> AllQueues()
        {
            var queues = await _redis.SetMembersAsync($"{_namespace}::queues");
            return queues.Select(q => q.ToString());
        }

        public async Task<IEnumerable<JobStatus>> AllJobsInQueue(string queue)
        {
            var jobs = await _redis.ListRangeAsync($"{_namespace}::queue::{queue}");
            return jobs.Select(j => JsonConvert.DeserializeObject<JobStatus>(j));
        }

        public async Task<IEnumerable<FailedJob>> AllFailedJobs()
        {
            var jobs = await _redis.ListRangeAsync($"{_namespace}::failed");
            return jobs.Select(j => JsonConvert.DeserializeObject<FailedJob>(j));
        }

        public async Task<IEnumerable<WorkerStatus>> AllWorkers()
        {
            var result = new List<WorkerStatus>();
            foreach (var worker in await _redis.HashGetAllAsync($"{_namespace}::workers"))
            {
                var status = new WorkerStatus();
                status.Id = worker.Name;
                status.LastCheckIn = DateTimeOffset.FromUnixTimeSeconds(int.Parse(worker.Value));
                status.Queues = JsonConvert.DeserializeObject<string[]>(await _redis.HashGetAsync($"{_namespace}::worker::{status.Id}", "queues"));
                var job = await _redis.HashGetAllAsync($"{_namespace}::worker::{status.Id}::job");
                if (job.Length > 0)
                {
                    status.Job = job.Where(k => k.Name == "work").Select(v => JsonConvert.DeserializeObject<JobStatus>(v.Value)).FirstOrDefault() ?? new JobStatus();
                    status.Job.StartedAt = DateTimeOffset.FromUnixTimeSeconds(job.Where(k => k.Name == "started_at").Select(v => int.Parse(v.Value)).FirstOrDefault());
                }
                result.Add(status);
            }

            return result;
        }

        private object GetWork(Job job, bool retry)
        {
            return new
            {
                @class = job.GetType().FullName,
                queue = job.Queue,
                args = job.Arguments,
                retries = (retry ? job.Retries - 1 : job.Retries),
            };
        }

        private string SerializeWork(Job job, bool retry)
        {
            return JsonConvert.SerializeObject(GetWork(job, retry));
        }

        internal Task Checkin(Worker worker)
        {
            return Task.WhenAll(
                _redis.HashSetAsync($"{_namespace}::workers", worker.WorkerId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                _redis.HashSetAsync($"{_namespace}::worker::{worker.WorkerId}", "queues", JsonConvert.SerializeObject(worker.Queues))
            );
        }

        internal async Task<Job> GetWork(Worker worker)
        {
            await ReclaimStuckWork(worker);
            var payload = await RetrieveNewWork(worker);
            if (payload == null) return null;

            var jobCls = DivvyUp.GetWorker(payload["class"].ToString());
            if (jobCls == null) throw new TypeLoadException($"{payload["class"]} not found.");

            var jsonArguments = payload["args"] as JArray;
            foreach (var constructor in jobCls.GetConstructors())
            {
                var parameters = constructor.GetParameters();
                var arguments = new object[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i < jsonArguments.Count)
                    {
                        arguments[i] = jsonArguments[i].ToObject(parameters[i].ParameterType);
                    }
                    else if (parameters[i].HasDefaultValue)
                    {
                        arguments[i] = parameters[i].RawDefaultValue;
                    }
                    else
                    {
                        throw new ArgumentException($"No value provided for parameter {parameters[i].Name}");
                    }
                }

                var job = (Job)constructor.Invoke(arguments);
                job.Retries = (payload["retries"] != null ? (int)payload["retries"] : 0);
                return job;
            }
            throw new TargetException($"Could not find constructor for {jobCls}");
        }

        internal Task StartWork(Worker worker, Job job)
        {
            return Task.WhenAll(
                _redis.HashSetAsync($"{_namespace}::worker::{worker.WorkerId}::job", "started_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                _redis.HashSetAsync($"{_namespace}::worker::{worker.WorkerId}::job", "work", SerializeWork(job, false))
            );
        }

        internal Task CompleteWork(Worker worker, Job job)
        {
            return _redis.KeyDeleteAsync($"{_namespace}::worker::{worker.WorkerId}::job");
        }

        internal async Task FailWork(Worker worker, Job job, Exception exc)
        {
            if (job.Retries > 0)
            {
                await CompleteWork(worker, job);
                await Enqueue(job, true);
            }
            else
            {
                await _redis.ListRightPushAsync($"{_namespace}::failed", JsonConvert.SerializeObject(new
                {
                    work = GetWork(job, false),
                    worker = worker.WorkerId,
                    message = exc.Message,
                    backtrace = exc.StackTrace.Split(new string[] { Environment.NewLine }, StringSplitOptions.None),
                }));
            }
        }

        private async Task ReclaimStuckWork(Worker worker)
        {
            var checkinThreshold = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30 * 10;
            foreach (var entry in await _redis.HashGetAllAsync($"{_namespace}::workers"))
            {
                long lastCheckin;
                if (entry.Value.TryParse(out lastCheckin) && lastCheckin < checkinThreshold)
                {
                    await ReapWorker(entry.Name);
                }
            }
        }

        private async Task ReapWorker(string workerId)
        {
            var jobJson = await _redis.HashGetAsync($"{_namespace}::worker::{workerId}::job", "work");
            if (jobJson.HasValue)
            {
                var job = JObject.Parse(jobJson);
                await _redis.ListRightPushAsync($"{_namespace}::queue::{job["queue"]}", jobJson);
            }
            await Task.WhenAll(
                _redis.KeyDeleteAsync($"{_namespace}::worker::{workerId}::job"),
                _redis.KeyDeleteAsync($"{_namespace}::worker::{workerId}"),
                _redis.HashDeleteAsync($"{_namespace}::workers", workerId)
            );
        }

        private async Task<JObject> RetrieveNewWork(Worker worker)
        {
            foreach (var queue in worker.Queues)
            {
                var work = await _redis.ListLeftPopAsync($"{_namespace}::queue::{queue}");
                if (!work.HasValue) continue;
                return JObject.Parse(work.ToString());
            }
            return null;
        }
    }
}
