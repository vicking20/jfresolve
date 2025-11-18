using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Filters;
using Jfresolve.Providers;
using Jfresolve.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jfresolve;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        // Register core services
        services.AddSingleton<TmdbService>();
        services.AddSingleton<JfresolveManager>();
        services.AddSingleton<JfresolveSeriesProvider>();
        services.AddSingleton<SearchActionFilter>();
        services.AddSingleton<InsertActionFilter>();
        services.AddSingleton<ImageResourceFilter>();
        services.AddSingleton<DeleteResourceFilter>();

        // Register scheduled tasks
        services.AddSingleton<PurgeJfresolveTask>();
        services.AddSingleton<PopulateLibraryTask>();
        services.AddSingleton<UpdateSeriesTask>();

        // Register HttpClientFactory for TMDB API calls
        services.AddHttpClient();

        // Register FFmpeg configuration service (Gelato pattern)
        services.AddHostedService<JfresolveFFmpegConfigService>();

        // Register MVC filters
        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        {
            options.Filters.AddService<SearchActionFilter>(order: 1);
            options.Filters.AddService<InsertActionFilter>(order: 2);
            options.Filters.AddService<ImageResourceFilter>(order: 3);
            options.Filters.AddService<DeleteResourceFilter>(order: 4);
        });
    }
}

/// <summary>
/// Background service that applies FFmpeg configuration on startup (Gelato pattern)
/// </summary>
public class JfresolveFFmpegConfigService : IHostedService
{
    private readonly IConfiguration _config;
    private readonly ILogger<JfresolveFFmpegConfigService> _log;

    public JfresolveFFmpegConfigService(
        IConfiguration config,
        ILogger<JfresolveFFmpegConfigService> log)
    {
        _config = config;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var config = JfresolvePlugin.Instance?.Configuration;

        // Only apply custom FFmpeg settings if enabled
        if (config?.EnableCustomFFmpegSettings == true)
        {
            var analyze = config.FFmpegAnalyzeDuration ?? "5M";
            var probe = config.FFmpegProbeSize ?? "40M";

            _config["FFmpeg:probesize"] = probe;
            _config["FFmpeg:analyzeduration"] = analyze;

            _log.LogInformation(
                "Jfresolve: Custom FFmpeg settings enabled - Set FFmpeg:probesize={Probe}, FFmpeg:analyzeduration={Analyze}",
                probe,
                analyze
            );
        }
        else
        {
            _log.LogInformation(
                "Jfresolve: Using Jellyfin's default FFmpeg settings (custom settings disabled)"
            );
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// Extension methods for service decoration
public static class ServiceCollectionExtensions
{
    public static IServiceCollection Decorate<TInterface, TDecorator>(this IServiceCollection services)
        where TInterface : class
        where TDecorator : class, TInterface
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TInterface));
        if (descriptor == null)
        {
            throw new InvalidOperationException($"Service of type {typeof(TInterface).Name} is not registered.");
        }

        var decoratorDescriptor = new ServiceDescriptor(
            typeof(TInterface),
            provider =>
            {
                var inner = descriptor.ImplementationType != null
                    ? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType)
                    : descriptor.ImplementationFactory?.Invoke(provider)
                      ?? descriptor.ImplementationInstance
                      ?? throw new InvalidOperationException("Unable to resolve inner service.");

                return ActivatorUtilities.CreateInstance(provider, typeof(TDecorator), inner);
            },
            descriptor.Lifetime
        );

        services.Remove(descriptor);
        services.Add(decoratorDescriptor);

        return services;
    }
}
