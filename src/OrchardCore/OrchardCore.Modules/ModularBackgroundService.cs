using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrchardCore.BackgroundTasks;
using OrchardCore.Environment.Shell;
using OrchardCore.Environment.Shell.Descriptor.Models;
using OrchardCore.Environment.Shell.Models;
using OrchardCore.Hosting.ShellBuilders;

namespace OrchardCore.Modules
{
    internal class ModularBackgroundService : Internal.BackgroundService,
        IModularBackgroundService, IShellDescriptorManagerEventHandler
    {
        private static TimeSpan PollingTime = TimeSpan.FromMinutes(1);
        private static TimeSpan MinIdleTime = TimeSpan.FromSeconds(10);

        private readonly ConcurrentDictionary<string, BackgroundTaskScheduler> _schedulers =
            new ConcurrentDictionary<string, BackgroundTaskScheduler>();

        private readonly IShellHost _shellHost;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public ModularBackgroundService(
            IShellHost shellHost,
            IHttpContextAccessor httpContextAccessor,
            ILogger<ModularBackgroundService> logger)
        {
            _shellHost = shellHost;
            _httpContextAccessor = httpContextAccessor;
            Logger = logger;
        }

        public ILogger Logger { get; set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.Register(() =>
            {
                Logger.LogDebug($"{nameof(ModularBackgroundService)} is stopping.");
                StopBackgroundTaskSchedulers();
            });

            var referenceTime = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                var pollingDelay = Task.Delay(PollingTime, stoppingToken);

                var shells = GetRunningShells();

                await shells.ForEachAsync(async shell =>
                {
                    if (shell.Released || stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }

                    IEnumerable<Type> taskTypes;

                    using (var scope = shell.EnterServiceScope())
                    {
                        taskTypes = scope.GetBackgroundTaskTypes();
                    }

                    var tenant = shell.Settings?.Name;

                    if (taskTypes.Count() > 0)
                    {
                        _httpContextAccessor.HttpContext = shell.GetBackgroundHttpContext();
                    }

                    foreach (var taskType in taskTypes)
                    {
                        var taskName = taskType.FullName;

                        if (shell.Released || stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }

                        using (var scope = shell.EnterServiceScope())
                        {
                            var task = scope.GetBackgroundTaskOfType(taskType);

                            if (task == null)
                            {
                                continue;
                            }

                            if (!_schedulers.TryGetValue(tenant + taskName, out BackgroundTaskScheduler scheduler))
                            {
                                _schedulers[tenant + taskName] = scheduler = new BackgroundTaskScheduler(tenant, taskName, referenceTime);
                            }

                            try
                            {
                                var settings = await scope.GetBackgroundTaskSettingsAsync(taskType);

                                if (settings != BackgroundTaskSettings.None)
                                {
                                    scheduler.Settings.Enable = settings.Enable;
                                    scheduler.Settings.Schedule = settings.Schedule;
                                }

                                if (!scheduler.CanRun())
                                {
                                    continue;
                                }

                                if (Logger.IsEnabled(LogLevel.Information))
                                {
                                    Logger.LogInformation(
                                        "Start processing background task \"{0}\" on tenant \"{1}\".",
                                        tenant, taskName);
                                }

                                scheduler.Run();

                                await task.DoWorkAsync(scope.ServiceProvider, stoppingToken);

                                scheduler.Idle();

                                if (Logger.IsEnabled(LogLevel.Information))
                                {
                                    Logger.LogInformation(
                                        "Finished processing background task \"{0}\" on tenant \"{1}\".",
                                        tenant, taskName);
                                }
                            }

                            catch (Exception ex)
                            {
                                scheduler.Fault(ex);

                                if (Logger.IsEnabled(LogLevel.Error))
                                {
                                    Logger.LogError(ex,
                                        "Error while processing background task \"{0}\" on tenant \"{1}\".",
                                        tenant, taskName);
                                }
                            }
                        }
                    }
                });

                referenceTime = DateTime.UtcNow;
                await Task.Delay(MinIdleTime, stoppingToken);
                await pollingDelay;
            }

            Logger.LogDebug($"{nameof(ModularBackgroundService)} is stopping.");
            StopBackgroundTaskSchedulers();
        }

        public Task<IEnumerable<BackgroundTaskSettings>> GetSettingsAsync(string tenant)
        {
            return Task.FromResult(_schedulers.Where(kv => kv.Value.Tenant == tenant)
                .Select(kv => kv.Value.Settings));
        }

        public Task<IEnumerable<BackgroundTaskState>> GetStatesAsync(string tenant)
        {
            return Task.FromResult(_schedulers.Where(kv => kv.Value.Tenant == tenant)
                .Select(kv => kv.Value.State));
        }

        public Task<BackgroundTaskSettings> GetSettingsAsync(string tenant, string taskName)
        {
            if (_schedulers.TryGetValue(tenant + taskName, out BackgroundTaskScheduler scheduler))
            {
                return Task.FromResult(scheduler.Settings);
            }

            return Task.FromResult(BackgroundTaskSettings.None);
        }

        public Task<BackgroundTaskState> GetStateAsync(string tenant, string taskName)
        {
            if (_schedulers.TryGetValue(tenant + taskName, out BackgroundTaskScheduler scheduler))
            {
                return Task.FromResult(scheduler.State);
            }

            return Task.FromResult(BackgroundTaskState.Undefined);
        }

        Task IShellDescriptorManagerEventHandler.Changed(ShellDescriptor descriptor, string tenant)
        {
            CleanTenantBackgroundTaskSchedulers(tenant);
            return Task.CompletedTask;
        }

        private IEnumerable<ShellContext> GetRunningShells()
        {
            return _shellHost.ListShellContexts()?.Where(s => s.Settings?.State == TenantState.Running)
                .OrderBy(s => s.Settings?.Name).ToArray() ?? Enumerable.Empty<ShellContext>();
        }

        private void CleanTenantBackgroundTaskSchedulers(string tenant)
        {
            var keys = _schedulers.Where(kv => kv.Value.Tenant == tenant).Select(kv => kv.Key).ToArray();

            foreach (var key in keys)
            {
                _schedulers.TryRemove(key, out var scheduler);
            }
        }

        private void StopBackgroundTaskSchedulers()
        {
            foreach (var scheduler in _schedulers.Values)
            {
                scheduler.Stop();
            }
        }
    }

    internal static class EnumerableExtensions
    {
        public static Task ForEachAsync<T>(this IEnumerable<T> source, Func<T, Task> body)
        {
            var partitionCount = System.Environment.ProcessorCount;

            return Task.WhenAll(
                from partition in Partitioner.Create(source).GetPartitions(partitionCount)
                select Task.Run(async delegate
                {
                    using (partition)
                    {
                        while (partition.MoveNext())
                        {
                            await body(partition.Current);
                        }
                    }
                }));
        }
    }

    internal static class ShellExtensions
    {
        public static HttpContext GetBackgroundHttpContext(this ShellContext shell)
        {
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Host = new HostString(shell.Settings?.RequestUrlHost ?? "localhost");
            httpContext.Request.Path = "/" + shell.Settings?.RequestUrlPrefix ?? "";
            httpContext.Items["IsBackground"] = true;
            return httpContext;
        }
    }

    internal static class ServiceScopeExtensions
    {
        public static IEnumerable<Type> GetBackgroundTaskTypes(this IServiceScope scope)
        {
            return scope.ServiceProvider.GetServices<IBackgroundTask>().Select(t => t.GetType());
        }

        public static IBackgroundTask GetBackgroundTaskOfType(this IServiceScope scope, Type type)
        {
            return scope.ServiceProvider.GetServices<IBackgroundTask>().FirstOrDefault(t => t.GetType() == type);
        }

        public static async Task<BackgroundTaskSettings> GetBackgroundTaskSettingsAsync(this IServiceScope scope, Type type)
        {
            var providers = scope.ServiceProvider.GetService<IOptions<BackgroundTaskOptions>>()
                .Value.SettingsProviders;

            foreach (var provider in providers.OrderBy(p => p.Order))
            {
                var settings = await provider.GetSettingsAsync(type);

                if (settings != null && settings != BackgroundTaskSettings.None)
                {
                    return settings;
                }
            }

            return BackgroundTaskSettings.None;
        }
    }
}