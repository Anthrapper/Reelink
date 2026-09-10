using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Reelink.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Reelink;

/// <summary>
/// Detects duplicate series and assigns a shared presentation identity.
/// </summary>
public sealed class ShowMergeManager
{
    private const string PresentationKeyPrefix = "reelink-series-";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ShowMergeManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShowMergeManager"/> class.
    /// </summary>
    public ShowMergeManager(ILibraryManager libraryManager, ILogger<ShowMergeManager> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Groups duplicate series that have a configured number of matching provider IDs.
    /// </summary>
    public async Task<MergeSummary> MergeAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        using var scope = _logger.BeginScope(
            new Dictionary<string, object?>
            {
                ["ReelinkOperation"] = "MergeSeries",
                ["ReelinkOperationId"] = operationId
            });

        var configuration = Plugin.Instance?.CurrentConfiguration ?? new PluginConfiguration();
        var providers = ParseProviders(configuration.ProviderIds);
        var minimumMatches = Math.Clamp(configuration.MinimumProviderMatches, 1, providers.Count);

        _logger.LogInformation(
            "Starting duplicate-show scan {OperationId}; providers: {Providers}; minimum matches: {MinimumMatches}; preview only: {PreviewOnly}",
            operationId,
            string.Join(",", providers),
            minimumMatches,
            configuration.PreviewOnly);

        var series = GetSeries();
        var groups = DuplicateGroupFinder.Find(series, providers, minimumMatches);

        _logger.LogInformation(
            "Scanned {SeriesCount} TV shows and found {GroupCount} duplicate groups",
            series.Count,
            groups.Count);

        var changedSeries = 0;
        try
        {
            for (var index = 0; index < groups.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var group = groups[index];
                try
                {
                    changedSeries += await ApplyGroupAsync(group, providers, configuration.PreviewOnly, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(
                        ex,
                        "Failed to process duplicate-show group {GroupNumber}/{GroupCount}; series IDs: {SeriesIds}",
                        index + 1,
                        groups.Count,
                        string.Join(",", group.Select(item => item.Id)));
                    throw;
                }

                progress?.Report((index + 1) * 100d / groups.Count);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Duplicate-show scan {OperationId} was cancelled after changing {ChangedSeries} series",
                operationId,
                changedSeries);
            throw;
        }

        progress?.Report(100);
        _logger.LogInformation(
            "Completed duplicate-show scan {OperationId}: {SeriesCount} scanned, {GroupCount} groups, {ChangedSeries} series {Action}",
            operationId,
            series.Count,
            groups.Count,
            changedSeries,
            configuration.PreviewOnly ? "would be changed" : "changed");

        return new MergeSummary(series.Count, groups.Count, changedSeries, configuration.PreviewOnly);
    }

    /// <summary>
    /// Returns the series groups that currently satisfy Reelink's matching rules.
    /// </summary>
    public IReadOnlyList<MergePreviewGroup> PreviewSeries()
    {
        var (providers, minimumMatches) = GetMatchingSettings();
        var groups = DuplicateGroupFinder.Find(GetSeries(), providers, minimumMatches);
        return groups.Select(group => CreateSeriesPreviewGroup(group, providers)).ToArray();
    }

    /// <summary>
    /// Groups only the selected series after revalidating each selection against the live library.
    /// </summary>
    public async Task<MergeSelectionResult> MergeSelectedSeriesAsync(
        IReadOnlyList<SelectedMergeGroup> selections,
        CancellationToken cancellationToken)
    {
        var (providers, minimumMatches) = GetMatchingSettings();
        var previewOnly = Plugin.Instance?.CurrentConfiguration?.PreviewOnly == true;
        if (selections.Count == 0)
        {
            return new MergeSelectionResult(0, 0, previewOnly);
        }

        var submittedGroups = selections
            .Select(selection => selection.ItemIds.Distinct().Order().ToArray())
            .Where(ids => ids.Length > 0)
            .ToArray();
        if (submittedGroups.Any(ids => ids.Length < 2))
        {
            throw new ArgumentException("Every selected group must contain at least two distinct series IDs.", nameof(selections));
        }

        if (submittedGroups.SelectMany(ids => ids).GroupBy(id => id).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("A series cannot be selected in more than one group.", nameof(selections));
        }

        var series = GetSeries();
        var seriesById = series.ToDictionary(item => item.Id);
        var validGroups = DuplicateGroupFinder.Find(series, providers, minimumMatches);
        var changed = 0;
        var merged = 0;

        foreach (var submittedIds in submittedGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containingGroup = validGroups.SingleOrDefault(group =>
                submittedIds.All(id => group.Any(item => item.Id.Equals(id))));
            if (containingGroup is null)
            {
                throw new InvalidOperationException(
                    "The selected series no longer form a valid Reelink group. Refresh the preview and try again.");
            }

            var selectedSeries = submittedIds.Select(id =>
            {
                if (!seriesById.TryGetValue(id, out var item))
                {
                    throw new InvalidOperationException(
                        $"The selected series {id} is no longer eligible. Refresh the preview and try again.");
                }

                return item;
            }).ToArray();

            var revalidated = DuplicateGroupFinder.Find(selectedSeries, providers, minimumMatches);
            if (revalidated.Count != 1 || revalidated[0].Count != selectedSeries.Length)
            {
                throw new InvalidOperationException(
                    "The selected series do not satisfy the configured matching rules. Refresh the preview and try again.");
            }

            _logger.LogInformation(
                "Merging selected series group; matched IDs: {MatchedIds}; series IDs: {SeriesIds}; preview only: {PreviewOnly}",
                string.Join(",", GetSeriesMatchedIds(revalidated[0], providers).Select(identity => $"{identity.Provider}={identity.Value}")),
                string.Join(",", submittedIds),
                previewOnly);
            changed += await ApplyGroupAsync(revalidated[0], providers, previewOnly, cancellationToken)
                .ConfigureAwait(false);
            merged++;
        }

        return new MergeSelectionResult(merged, changed, previewOnly);
    }

    /// <summary>
    /// Separates series previously grouped by this plugin by assigning each physical series
    /// and its descendants a distinct presentation identity.
    /// </summary>
    public async Task<int> SplitAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        using var scope = _logger.BeginScope(
            new Dictionary<string, object?>
            {
                ["ReelinkOperation"] = "SplitSeries",
                ["ReelinkOperationId"] = operationId
            });

        var series = GetSeries()
            .Where(item => item.PresentationUniqueKey?.StartsWith(PresentationKeyPrefix, StringComparison.Ordinal) == true)
            .ToArray();

        _logger.LogInformation(
            "Starting duplicate-show split {OperationId}; found {SeriesCount} series grouped by Reelink",
            operationId,
            series.Length);

        var changedSeries = 0;
        try
        {
            for (var index = 0; index < series.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = series[index];
                var key = CreateIndividualPresentationKey(item);

                _logger.LogInformation(
                    "Separating show {Name} ({SeriesId}) at {Path} with presentation key {PresentationKey}",
                    item.Name,
                    item.Id,
                    item.Path,
                    key);

                if (!string.Equals(item.PresentationUniqueKey, key, StringComparison.Ordinal))
                {
                    item.PresentationUniqueKey = key;
                    await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                    changedSeries++;
                }

                await UpdateDescendantKeysAsync([item], key, cancellationToken).ConfigureAwait(false);
                progress?.Report((index + 1) * 100d / series.Length);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Duplicate-show split {OperationId} was cancelled after separating {ChangedSeries} series",
                operationId,
                changedSeries);
            throw;
        }

        progress?.Report(100);
        _logger.LogInformation(
            "Completed duplicate-show split {OperationId}: {ChangedSeries} series separated",
            operationId,
            changedSeries);

        return changedSeries;
    }

    private IReadOnlyList<Series> GetSeries()
    {
        return _libraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Series],
                    GroupByPresentationUniqueKey = false,
                    IsVirtualItem = false,
                    Recursive = true
                })
            .OfType<Series>()
            .Where(item => item.SourceType == SourceType.Library)
            .ToArray();
    }

    private async Task<int> ApplyGroupAsync(
        IReadOnlyList<Series> group,
        IReadOnlyList<string> providers,
        bool previewOnly,
        CancellationToken cancellationToken)
    {
        var key = CreatePresentationKey(group, providers);
        var changed = group.Where(item => !string.Equals(item.PresentationUniqueKey, key, StringComparison.Ordinal)).ToArray();

        _logger.LogInformation(
            "{Mode} duplicate show {Name}: {RecordCount} records, {ChangedCount} requiring updates, key {PresentationKey}",
            previewOnly ? "Would group" : "Grouping",
            group[0].Name,
            group.Count,
            changed.Length,
            key);
        _logger.LogDebug(
            "Duplicate-show group details: IDs {SeriesIds}; paths {Paths}; provider identities {ProviderIdentities}",
            string.Join(",", group.Select(item => item.Id)),
            string.Join(" | ", group.Select(item => item.Path)),
            string.Join(
                " | ",
                group.Select(item => string.Join(",", providers.Select(provider => $"{provider}={GetProviderId(item, provider) ?? "<none>"}")))));

        if (previewOnly || changed.Length == 0)
        {
            return changed.Length;
        }

        foreach (var item in changed)
        {
            item.PresentationUniqueKey = key;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        await UpdateDescendantKeysAsync(group, key, cancellationToken).ConfigureAwait(false);

        return changed.Length;
    }

    private async Task UpdateDescendantKeysAsync(
        IReadOnlyList<Series> group,
        string seriesKey,
        CancellationToken cancellationToken)
    {
        var descendants = _libraryManager.GetItemList(
            new InternalItemsQuery
            {
                AncestorIds = group.Select(item => item.Id).ToArray(),
                IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode],
                GroupByPresentationUniqueKey = false,
                IsVirtualItem = false,
                Recursive = true
            });

        _logger.LogDebug(
            "Updating presentation identities for {DescendantCount} descendants of series key {SeriesKey}",
            descendants.Count,
            seriesKey);

        foreach (var season in descendants.OfType<Season>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            season.SeriesPresentationUniqueKey = seriesKey;
            if (season.IndexNumber.HasValue)
            {
                season.PresentationUniqueKey = seriesKey + "-" + season.IndexNumber.Value.ToString("000", CultureInfo.InvariantCulture);
            }

            await season.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        foreach (var episode in descendants.OfType<Episode>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            episode.SeriesPresentationUniqueKey = seriesKey;
            await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }

    private (IReadOnlyList<string> Providers, int MinimumMatches) GetMatchingSettings()
    {
        var configuration = Plugin.Instance?.CurrentConfiguration ?? new PluginConfiguration();
        var providers = ParseProviders(configuration.ProviderIds);
        var minimumMatches = Math.Clamp(configuration.MinimumProviderMatches, 1, providers.Count);
        return (providers, minimumMatches);
    }

    private static MergePreviewGroup CreateSeriesPreviewGroup(
        IReadOnlyList<Series> group,
        IReadOnlyList<string> providers)
    {
        var items = group
            .Select(item => new MergePreviewItem(
                item.Id,
                item.Name,
                item.ProductionYear,
                new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
                IsProposedPrimary: false))
            .ToArray();
        var groupId = string.Join("-", group.Select(item => item.Id).Order());
        return new MergePreviewGroup(groupId, "series", GetSeriesMatchedIds(group, providers), items);
    }

    private static IReadOnlyList<MatchedIdentity> GetSeriesMatchedIds(
        IReadOnlyList<Series> group,
        IReadOnlyList<string> providers)
    {
        var matched = new List<MatchedIdentity>();
        foreach (var provider in providers)
        {
            var values = group
                .Select(item => GetProviderId(item, provider))
                .Where(value => value is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var itemCount = group.Count(item => GetProviderId(item, provider) is not null);
            if (values.Length == 1 && itemCount >= 2)
            {
                matched.Add(new MatchedIdentity(provider, values[0]));
            }
        }

        return matched;
    }

    internal static IReadOnlyList<string> ParseProviders(string configuredProviders)
    {
        var providers = configuredProviders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return providers.Length == 0 ? ["Tvdb", "Tmdb", "Imdb"] : providers;
    }

    internal static string CreatePresentationKey(
        IReadOnlyList<Series> group,
        IReadOnlyList<string> providers)
    {
        var identity = providers
            .Select(provider =>
            {
                var values = group
                    .Select(item => GetProviderId(item, provider))
                    .Where(value => value is not null)
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return values.Length == 1 ? $"{provider.ToLowerInvariant()}:{values[0].ToLowerInvariant()}" : null;
            })
            .FirstOrDefault(value => value is not null)
            ?? string.Join(',', group.Select(item => item.Id.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return PresentationKeyPrefix + Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    internal static string CreateIndividualPresentationKey(Series series)
        => PresentationKeyPrefix + "item-" + series.Id.ToString("N", CultureInfo.InvariantCulture);

    internal static string? GetProviderId(Series series, string provider)
    {
        if (!series.ProviderIds.TryGetValue(provider, out var value))
        {
            return null;
        }

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }
}

/// <summary>
/// Result of a duplicate-show scan.
/// </summary>
public sealed record MergeSummary(int SeriesScanned, int DuplicateGroups, int SeriesChanged, bool PreviewOnly);
