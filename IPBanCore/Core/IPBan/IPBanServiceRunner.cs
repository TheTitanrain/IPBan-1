﻿/*
MIT License

Copyright (c) 2012-present Digital Ruby, LLC - https://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DigitalRuby.IPBanCore
{
    /// <summary>
    /// IPBan service runner, assists with starting the service and make sure it runs
    /// properly if under a Windows service, systemd, etc.
    /// </summary>
    public sealed class IPBanServiceRunner : BackgroundService
    {
        private readonly CancellationTokenSource cancelToken = new CancellationTokenSource();
        private readonly Func<CancellationToken, Task> onRun;
        private readonly Func<CancellationToken, Task> onStop;
        private readonly IHost host;

        private int stopLock;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="onRun">Action to execute for run</param>
        /// <param name="onStop">Action to execute on stop</param>
        private IPBanServiceRunner(Func<CancellationToken, Task> onRun, Func<CancellationToken, Task> onStop)
        {
            Logger.Warn("Initializing service");
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            OSUtility.AddAppDomainExceptionHandlers(AppDomain.CurrentDomain);
            var hostBuilder = new HostBuilder()
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddHostedService<IPBanServiceRunner>(provider => this);
                });

            this.onRun = onRun;
            this.onStop = onStop;
            if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            {
                Logger.Warn("Running as a Windows service");
                hostBuilder.UseWindowsService();
            }
            else if (Microsoft.Extensions.Hosting.Systemd.SystemdHelpers.IsSystemdService())
            {
                Logger.Warn("Running as a systemd service");
                hostBuilder.UseSystemd();
            }
            else
            {
                // adding console lifetime wrecks things if actually running under a service
                Logger.Warn("Running as a console app");
                hostBuilder.UseConsoleLifetime();
            }
            hostBuilder.UseContentRoot(AppContext.BaseDirectory);
            host = hostBuilder.Build();
        }

        /// <summary>
        /// Run the service
        /// </summary>
        /// <returns>Task</returns>
        public async Task RunAsync()
        {
            Logger.Warn("Preparing to run service");
            try
            {
                await host.RunAsync(cancelToken.Token);
            }
            finally
            {
                host.Dispose();
            }
        }

        /// <summary>
        /// Run service helper method
        /// </summary>
        /// <param name="args">Args</param>
        /// <param name="onRun">Run</param>
        /// <param name="onStop">Stop</param>
        /// <returns>Task</returns>
#pragma warning disable IDE0060 // Remove unused parameter
        public static async Task<int> MainService(string[] args, Func<CancellationToken, Task> onRun, Func<CancellationToken, Task> onStop = null)
#pragma warning restore IDE0060 // Remove unused parameter
        {
            try
            {
                using IPBanServiceRunner runner = new IPBanServiceRunner(onRun, onStop);
                await runner.RunAsync();
            }
            catch (OperationCanceledException)
            {
                // don't care
            }
            catch (Exception ex)
            {
                ExtensionMethods.FileWriteAllTextWithRetry(System.IO.Path.Combine(AppContext.BaseDirectory, "service_error.txt"), ex.ToString());
                Logger.Fatal("Fatal error running service", ex);
                return ex.HResult;
            }
            return 0;
        }

        /// <inheritdoc />
        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            Logger.Warn("Starting service");
            await base.StartAsync(cancellationToken);
        }

        /// <inheritdoc />
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref stopLock) == 1)
            {
                Logger.Warn("Stopping service");
                cancelToken.Cancel();
                if (onStop != null)
                {
                    await onStop(cancellationToken);
                }
                await base.StopAsync(cancellationToken);
                await host.StopAsync(cancellationToken);
            }
        }

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.Warn("Running service");

            // fire off run event if there is one
            if (onRun != null)
            {
                try
                {
                    await onRun(cancelToken.Token);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }

                // send a stop event since we are done running
                StopAsync(stoppingToken).GetAwaiter();
            }

            // else it is up to the caller of this class to call StopAsync
        }
    }
}
