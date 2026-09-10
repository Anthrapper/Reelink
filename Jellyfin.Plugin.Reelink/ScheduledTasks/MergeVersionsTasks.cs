using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Reelink.ScheduledTasks;

/// <summary>
/// Scheduled task that merges repeated movies into version groups.
/// </summary>
public sealed class MergeMoviesTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<VideoVersionsManager> _logger;

    public MergeMoviesTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ILogger<VideoVersionsManager> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Merge All Movies";

    /// <inheritdoc />
    public string Key => "ReelinkMergeMovies";

    /// <inheritdoc />
    public string Description => "Scans all libraries and merges repeated movies into version groups.";

    /// <inheritdoc />
    public string Category => "Reelink";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var manager = new VideoVersionsManager(_libraryManager, _fileSystem, _logger);
        var changed = await manager.MergeMoviesAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Merged {Changed} movies into version groups", changed);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }
}

/// <summary>
/// Scheduled task that merges repeated episodes into version groups.
/// </summary>
public sealed class MergeEpisodesTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<VideoVersionsManager> _logger;

    public MergeEpisodesTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ILogger<VideoVersionsManager> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Merge All Episodes";

    /// <inheritdoc />
    public string Key => "ReelinkMergeEpisodes";

    /// <inheritdoc />
    public string Description => "Scans all libraries and merges repeated episodes into version groups.";

    /// <inheritdoc />
    public string Category => "Reelink";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var manager = new VideoVersionsManager(_libraryManager, _fileSystem, _logger);
        var changed = await manager.MergeEpisodesAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Merged {Changed} episodes into version groups", changed);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        ];
    }
}
