﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace DivvyUp
{
    public class Worker
    {
        private Service _service;
        public string[] Queues { get; }
        public string WorkerId { get; }
        private bool _shuttingDown;
        private int _running;
        public int CheckinInterval { get; set; } = 30;
        public int NoWorkCheckInterval { get; set; } = 5;
        public int DelayAfterInternalError { get; set; } = 5;

        public Worker(params string[] queues) : this(DivvyUp.Service, queues) { }
        public Worker(Service service, params string[] queues)
        {
            _service = service;
            Queues = queues;
            WorkerId = $"{Dns.GetHostName()}:{Guid.NewGuid()}";
        }

        public delegate void OnErrorDelegate(Exception exc);
        public event OnErrorDelegate OnError;

        private Logger Logger { get => LogManager.GetLogger($"DivvyUp.Worker.{WorkerId}"); }
        
        public async Task Work(bool forever = true)
        {
            _shuttingDown = false;
            lock(this) _running += 1;
            StartupBackgroundCheckin(forever);
            while (!_shuttingDown)
            {
                try
                {
                    Logger.Debug("Looking for work");
                    await RetrieveAndExecuteWork();
                }
                catch (Exception exc)
                {
                    Logger.Error(exc, "Error looking for work");
                    if (OnError != null) OnError(exc);
                    Thread.Sleep(DelayAfterInternalError * 1000);
                }
                if (!forever) break;
            }
            lock (this) _running -= 1;
        }

        private Thread _backgroundCheckinThread;
        private void StartupBackgroundCheckin(bool forever)
        {
            if (forever)
            {
                _backgroundCheckinThread = new Thread(() => BackgroundCheckin(forever));
                _backgroundCheckinThread.Start();
            }
            else
            {
                BackgroundCheckin(forever);
            }
        }

        private void BackgroundCheckin(bool forever)
        {
            lock (this) _running += 1;
            while (!_shuttingDown)
            {
                try
                {
                    Logger.Debug($"Checking In");
                    _service.Checkin(this).Wait();
                }
                catch (Exception exc)
                {
                    Logger.Error(exc, $"Error checking in");
                    if (OnError != null) OnError(exc);
                }
                if (!forever) break;
                Thread.Sleep(CheckinInterval * 1000);
            }
            lock (this) _running -= 1;
        }

        private async Task RetrieveAndExecuteWork()
        {
            var job = await _service.GetWork(this);
            if (job != null)
            {
                try
                {
                    Logger.Debug($"Starting work {job}");
                    await _service.StartWork(this, job);
                    Logger.Debug($"Executing");
                    await job.Execute();
                    Logger.Debug($"Completing");
                    await _service.CompleteWork(this, job);
                }
                catch (Exception exc)
                {
                    Logger.Error(exc, $"Error executing work");
                    await _service.FailWork(this, job, exc);
                    if (OnError != null) OnError(exc);
                }
            }
            else
            {
                Thread.Sleep(NoWorkCheckInterval * 1000);
            }
        }

        public void Shutdown()
        {
            _shuttingDown = true;
        }

        public Task Stop()
        {
            Shutdown();
            return Task.Run(() => {
                while (true)
                {
                    lock (this)
                    {
                        if (_running == 0) return;
                    }
                    Thread.Sleep(100);
                }
            });
        }
    }
}
