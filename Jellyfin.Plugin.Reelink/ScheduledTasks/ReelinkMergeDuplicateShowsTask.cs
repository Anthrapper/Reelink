using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Reelink.ScheduledTasks;

/// <summary>
/// Scheduled task that groups duplicate TV series.
/// </summary>
public sealed class ReelinkMergeDuplicateShowsTask : IScheduledTask
{
    private readonly ShowMergeManager _manager;
    private readonly ILogger<ShowMergeManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReelinkMergeDuplicateShowsTask"/> class.
    /// </summary>
    public ReelinkMergeDuplicateShowsTask(
        ILibraryManager libraryManager,
        ILogger<ShowMergeManager> logger)
    {
        _manager = new ShowMergeManager(libraryManager, logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Merge Duplicate Shows";

    /// <inheritdoc />
    public string Key => "ReelinkMergeDuplicateShows";

    /// <inheritdoc />
    public string Description => "Groups duplicate TV shows that share configured external provider IDs.";

    /// <inheritdoc />
    public string Category => "Reelink";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Scheduled duplicate-show merge started");
        var summary = await _manager.MergeAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Scheduled duplicate-show merge finished: {SeriesScanned} scanned, {DuplicateGroups} groups, {SeriesChanged} series {Action}",
            summary.SeriesScanned,
            summary.DuplicateGroups,
            summary.SeriesChanged,
            summary.PreviewOnly ? "would be changed" : "changed");
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
