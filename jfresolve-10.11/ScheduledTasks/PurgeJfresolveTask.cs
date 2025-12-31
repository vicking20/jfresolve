using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jfresolve.ScheduledTasks;

/// <summary>
/// Scheduled task to purge all Jfresolve items from the library
/// Accessible from Dashboard → Scheduled Tasks
/// </summary>
public sealed class PurgeJfresolveTask : IScheduledTask
{
    private readonly ILogger<PurgeJfresolveTask> _log;
    private readonly JfresolveManager _manager;
    private readonly ILibraryManager _library;

    public PurgeJfresolveTask(
        ILibraryManager libraryManager,
        ILogger<PurgeJfresolveTask> log,
        JfresolveManager manager
    )
    {
        _log = log;
        _library = libraryManager;
        _manager = manager;
    }

    public string Name => "Clear All Jfresolve Items";
    public string Key => "PurgeJfresolveTask";
    public string Description => "Removes all items added by Jfresolve plugin (movies, series, seasons, episodes)";
    public string Category => "Jfresolve";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken
    )
    {
        _log.LogInformation("Jfresolve: Starting purge of all Jfresolve items");

        var allItemIds = new HashSet<Guid>(); // Use HashSet to automatically deduplicate items by ID
        var allChildren = new List<MediaBrowser.Controller.Entities.BaseItem>();
        var processedFolders = new HashSet<Guid>(); // Track folders to avoid duplicate logging

        // Helper function to add folder items
        void AddFolderItems(MediaBrowser.Controller.Entities.Folder? folder, string folderType)
        {
            if (folder == null) return;

            bool isNewFolder = !processedFolders.Contains(folder.Id);
            if (isNewFolder)
            {
                processedFolders.Add(folder.Id);
            }

            var items = folder.GetRecursiveChildren();
            int addedCount = 0;

            foreach (var item in items)
            {
                if (allItemIds.Add(item.Id))
                {
                    allChildren.Add(item);
                    addedCount++;
                }
            }

            if (isNewFolder || addedCount > 0)
            {
                _log.LogInformation("Jfresolve: Checking {Count} items in {FolderType} folder '{Name}' ({Added} unique)",
                    items.Count, folderType, folder.Name, addedCount);
            }
        }

        // Check all possible folder paths (Simple and Advanced mode)
        // Simple mode / Auto-populate paths
        AddFolderItems(_manager.TryGetMovieFolderForAutoPopulate(), "movie auto-populate");
        AddFolderItems(_manager.TryGetSeriesFolderForAutoPopulate(), "series auto-populate");
        AddFolderItems(_manager.TryGetAnimeFolderForAutoPopulate(), "anime auto-populate");

        // Search paths (Advanced mode)
        AddFolderItems(_manager.TryGetMovieFolderForSearch(), "movie search");
        AddFolderItems(_manager.TryGetSeriesFolderForSearch(), "series search");
        AddFolderItems(_manager.TryGetAnimeFolderForSearch(), "anime search");

        int total = allChildren.Count;
        int deleted = 0;
        int skipped = 0;

        _log.LogInformation("Jfresolve: Found {Total} total items to check", total);

        foreach (var child in allChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_manager.IsJfresolve(child))
            {
                skipped++;
                continue;
            }

            try
            {
                _library.DeleteItem(
                    child,
                    new DeleteOptions { DeleteFileLocation = false },
                    true
                );
                deleted++;
                _log.LogDebug("Jfresolve: Deleted item {Name} (ID: {Id})", child.Name, child.Id);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Jfresolve: Failed to delete item {Name} (ID: {Id})", child.Name, child.Id);
            }

            // Report progress
            progress?.Report(Math.Min(100.0, (double)(deleted + skipped) / total * 100.0));
        }

        // Clear metadata cache
        _log.LogInformation("Jfresolve: Clearing metadata cache");
        // Note: Gelato has a ClearCache method, we could add one if needed

        progress?.Report(100.0);

        _log.LogInformation(
            "Jfresolve: Purge completed - Deleted {Deleted} items, Skipped {Skipped} non-Jfresolve items",
            deleted,
            skipped
        );

        await Task.CompletedTask;
    }
}
