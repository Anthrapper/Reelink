using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Reelink.ScheduledTasks;

/// <summary>
/// Runs duplicate-show grouping after a library scan when enabled.
/// </summary>
public sealed class ReelinkPostScanTask : ILibraryPostScanTask
{
    private readonly ShowMergeManager _manager;
    private readonly ILogger<ShowMergeManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReelinkPostScanTask"/> class.
    /// </summary>
    public ReelinkPostScanTask(
        ILibraryManager libraryManager,
        ILogger<ShowMergeManager> logger)
    {
        _manager = new ShowMergeManager(libraryManager, logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.CurrentConfiguration.RunAfterLibraryScan != true)
        {
            _logger.LogDebug("Skipping post-scan duplicate-show merge because it is disabled in plugin configuration");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Post-library-scan duplicate-show merge started");
        var summary = await _manager.MergeAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Post-library-scan duplicate-show merge finished: {SeriesScanned} scanned, {DuplicateGroups} groups, {SeriesChanged} series {Action}",
            summary.SeriesScanned,
            summary.DuplicateGroups,
            summary.SeriesChanged,
            summary.PreviewOnly ? "would be changed" : "changed");
    }
}
