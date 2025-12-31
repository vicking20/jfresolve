using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jfresolve.Decorators;
using Jfresolve.Filters;
using Jfresolve.Providers;
using Jfresolve.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
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

        // Register decorators
        services.DecorateSingle<IItemRepository, JfresolveItemRepository>();
        services.DecorateSingle<IMediaSourceManager, MediaSourceManagerDecorator>();


        // Register MVC filters
        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        {
            options.Filters.AddService<SearchActionFilter>(order: 1);
            options.Filters.AddService<InsertActionFilter>(order: 2);
            options.Filters.AddService<ImageResourceFilter>(order: 4);
            options.Filters.AddService<DeleteResourceFilter>(order: 5);
        });
    }
}

/// <summary>
/// Background service that applies FFmpeg configuration on startup and initializes folders (Gelato pattern)
/// </summary>
public class JfresolveFFmpegConfigService : IHostedService
{
    private readonly IConfiguration _config;
    private readonly ILogger<JfresolveFFmpegConfigService> _log;
    private readonly JfresolveManager _manager;

    public JfresolveFFmpegConfigService(
        IConfiguration config,
        ILogger<JfresolveFFmpegConfigService> log,
        JfresolveManager manager)
    {
        _config = config;
        _log = log;
        _manager = manager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var config = JfresolvePlugin.Instance?.Configuration;

        // Initialize seed folders on startup
        InitializeFolders(config);

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

    /// <summary>
    /// Initialize seed folders for all configured library paths (supports Simple and Advanced modes)
    /// </summary>
    private void InitializeFolders(Configuration.PluginConfiguration? config)
    {
        if (config == null)
        {
            _log.LogWarning("Jfresolve: Plugin configuration is null, skipping folder initialization");
            return;
        }

        try
        {
            _log.LogInformation("Jfresolve: Initializing folders in {Mode} mode", config.PathMode);

            if (config.PathMode == Configuration.PathConfigMode.Simple)
            {
                // Simple mode: Initialize single paths for both search and auto-populate
                if (!string.IsNullOrWhiteSpace(config.MoviePath))
                {
                    JfresolveManager.SeedFolder(config.MoviePath);
                    _log.LogInformation("Jfresolve: [Simple] Initialized seed folder for movies at '{Path}'", config.MoviePath);
                }

                if (!string.IsNullOrWhiteSpace(config.SeriesPath))
                {
                    JfresolveManager.SeedFolder(config.SeriesPath);
                    _log.LogInformation("Jfresolve: [Simple] Initialized seed folder for series at '{Path}'", config.SeriesPath);
                }

                if (config.EnableAnimeFolder && !string.IsNullOrWhiteSpace(config.AnimePath))
                {
                    JfresolveManager.SeedFolder(config.AnimePath);
                    _log.LogInformation("Jfresolve: [Simple] Initialized seed folder for anime at '{Path}'", config.AnimePath);
                }
            }
            else
            {
                // Advanced mode: Initialize separate search and auto-populate paths
                // Search paths
                if (!string.IsNullOrWhiteSpace(config.MovieSearchPath))
                {
                    JfresolveManager.SeedFolder(config.MovieSearchPath);
                    _log.LogInformation("Jfresolve: [Advanced] Initialized seed folder for movie search at '{Path}'", config.MovieSearchPath);
                }

                if (!string.IsNullOrWhiteSpace(config.SeriesSearchPath))
                {
                    JfresolveManager.SeedFolder(config.SeriesSearchPath);
                    _log.LogInformation("Jfresolve: [Advanced] Initialized seed folder for series search at '{Path}'", config.SeriesSearchPath);
                }

                if (config.EnableAnimeFolderAdvanced && !string.IsNullOrWhiteSpace(config.AnimeSearchPath))
                {
                    JfresolveManager.SeedFolder(config.AnimeSearchPath);
                    _log.LogInformation("Jfresolve: [Advanced] Initialized seed folder for anime search at '{Path}'", config.AnimeSearchPath);
                }

                // Auto-populate paths
                if (!string.IsNullOrWhiteSpace(config.MovieAutoPopulatePath))
                {
                    JfresolveManager.SeedFolder(config.MovieAutoPopulatePath);
                    _log.LogInformation("Jfresolve: [Advanced] Initialized seed folder for movie auto-populate at '{Path}'", config.MovieAutoPopulatePath);
                }

                if (!string.IsNullOrWhiteSpace(config.SeriesAutoPopulatePath))
                {
                    JfresolveManager.SeedFolder(config.SeriesAutoPopulatePath);
                    _log.LogInformation("Jfresolve: [Advanced] Initialized seed folder for series auto-populate at '{Path}'", config.SeriesAutoPopulatePath);
                }

                if (config.EnableAnimeFolderAdvanced && !string.IsNullOrWhiteSpace(config.AnimeAutoPopulatePath))
                {
                    JfresolveManager.SeedFolder(config.AnimeAutoPopulatePath);
                    _log.LogInformation("Jfresolve: [Advanced] Initialized seed folder for anime auto-populate at '{Path}'", config.AnimeAutoPopulatePath);
                }
            }

            _log.LogInformation("Jfresolve: Folder initialization complete");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Failed to initialize seed folders");
        }
    }
}

// Extension methods for service decoration
public static class ServiceCollectionExtensions
{
    public static IServiceCollection DecorateSingle<TService, TDecorator>(
        this IServiceCollection services
    )
        where TDecorator : class, TService
    {
        var original = services.LastOrDefault(sd => sd.ServiceType == typeof(TService));
        if (original is null)
            return services;

        services.Remove(original);

        services.Add(
            new ServiceDescriptor(
                typeof(TService),
                sp =>
                {
                    var inner = (TService)BuildInner(sp, original);
                    return ActivatorUtilities.CreateInstance<TDecorator>(sp, inner);
                },
                original.Lifetime
            )
        );

        return services;
    }

    private static object BuildInner(IServiceProvider sp, ServiceDescriptor original)
    {
        if (original.ImplementationInstance is not null)
            return original.ImplementationInstance;

        if (original.ImplementationFactory is not null)
            return original.ImplementationFactory(sp);

        return ActivatorUtilities.GetServiceOrCreateInstance(sp, original.ImplementationType!);
    }
}
