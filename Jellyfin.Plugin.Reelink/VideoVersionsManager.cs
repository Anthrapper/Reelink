using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Reelink;

/// <summary>
/// Groups duplicate movies and episodes into version groups, and splits them apart again.
/// Mirrors the capabilities of the Merge Versions plugin on top of Jellyfin 12's
/// linked-alternate-version persistence.
/// </summary>
public sealed class VideoVersionsManager
{
    private static readonly HashSet<string> MovieIdentityProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tmdb",
        "Imdb",
        "Tvdb"
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<VideoVersionsManager> _logger;

    public VideoVersionsManager(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ILogger<VideoVersionsManager> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Returns the movie groups that currently satisfy Reelink's matching rules.
    /// </summary>
    public IReadOnlyList<MergePreviewGroup> PreviewMovies()
    {
        var groups = FindMovieGroups();
        return groups.Select(group => CreatePreviewGroup(group, "movie")).ToArray();
    }

    /// <summary>
    /// Returns the episode groups that currently satisfy Reelink's matching rules.
    /// </summary>
    public IReadOnlyList<MergePreviewGroup> PreviewEpisodes()
    {
        var groups = FindEpisodeGroups();
        return groups.Select(group => CreatePreviewGroup(group, "episode")).ToArray();
    }

    /// <summary>
    /// Merges only the selected movie groups after revalidating each group against the live library.
    /// </summary>
    public Task<MergeSelectionResult> MergeSelectedMoviesAsync(
        IReadOnlyList<SelectedMergeGroup> selections,
        CancellationToken cancellationToken)
        => MergeSelectedVideosAsync(BaseItemKind.Movie, "movie", selections, FindMovieGroups, cancellationToken);

    /// <summary>
    /// Merges only the selected episode groups after revalidating each group against the live library.
    /// </summary>
    public Task<MergeSelectionResult> MergeSelectedEpisodesAsync(
        IReadOnlyList<SelectedMergeGroup> selections,
        CancellationToken cancellationToken)
        => MergeSelectedVideosAsync(BaseItemKind.Episode, "episode", selections, FindEpisodeGroups, cancellationToken);

    /// <summary>
    /// Merges repeated movies that share a provider ID into version groups.
    /// </summary>
    public async Task<int> MergeMoviesAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        return await MergeVideosAsync(
            BaseItemKind.Movie,
            "movie",
            _ => FindMovieGroups(),
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Merges repeated episodes that share a provider ID, or the same series and episode position, into version groups.
    /// </summary>
    public async Task<int> MergeEpisodesAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        return await MergeVideosAsync(
            BaseItemKind.Episode,
            "episode",
            _ => FindEpisodeGroups(),
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits all movie version groups back into individual library items.
    /// </summary>
    public async Task<int> SplitMoviesAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        return await SplitItemsAsync(GetLibraryVideos(BaseItemKind.Movie), "movie", progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits all episode version groups back into individual library items.
    /// </summary>
    public async Task<int> SplitEpisodesAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        return await SplitItemsAsync(GetLibraryVideos(BaseItemKind.Episode), "episode", progress, cancellationToken).ConfigureAwait(false);
    }

    private List<DuplicateVideoGroup> FindMovieGroups()
        => GetDuplicateGroups(
            GetLibraryVideos(BaseItemKind.Movie).OfType<Movie>().ToArray(),
            HasMovieGroupKey,
            GroupMoviesByKey);

    private List<DuplicateVideoGroup> FindEpisodeGroups()
        => GetDuplicateGroups(
            GetLibraryVideos(BaseItemKind.Episode).OfType<Episode>().ToArray(),
            HasProviderId,
            GroupEpisodesByKey);

    private async Task<MergeSelectionResult> MergeSelectedVideosAsync(
        BaseItemKind itemKind,
        string kind,
        IReadOnlyList<SelectedMergeGroup> selections,
        Func<List<DuplicateVideoGroup>> findGroups,
        CancellationToken cancellationToken)
    {
        if (selections.Count == 0)
        {
            return new MergeSelectionResult(0, 0, PreviewOnly: false);
        }

        var submittedGroups = selections
            .Select(selection => selection.ItemIds.Distinct().Order().ToArray())
            .Where(ids => ids.Length > 0)
            .ToArray();
        if (submittedGroups.Any(ids => ids.Length < 2))
        {
            throw new ArgumentException("Every selected group must contain at least two distinct item IDs.", nameof(selections));
        }

        if (submittedGroups.SelectMany(ids => ids).GroupBy(id => id).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("An item cannot be selected in more than one group.", nameof(selections));
        }

        var liveItems = GetLibraryVideos(itemKind).ToDictionary(item => item.Id);
        var validGroups = findGroups();
        var changed = 0;
        var merged = 0;

        foreach (var submittedIds in submittedGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var containingGroup = validGroups.SingleOrDefault(group =>
                submittedIds.All(id => group.Items.Any(item => item.Id.Equals(id))));
            if (containingGroup is null)
            {
                throw new InvalidOperationException(
                    $"The selected {kind} items no longer form a valid Reelink group. Refresh the preview and try again.");
            }

            var selectedItems = submittedIds.Select(id =>
            {
                if (!liveItems.TryGetValue(id, out var item))
                {
                    throw new InvalidOperationException(
                        $"The selected {kind} item {id} is no longer eligible. Refresh the preview and try again.");
                }

                return item;
            }).ToArray();

            var revalidated = itemKind == BaseItemKind.Movie
                ? GetDuplicateGroups(selectedItems.OfType<Movie>().ToArray(), HasMovieGroupKey, GroupMoviesByKey)
                : GetDuplicateGroups(selectedItems.OfType<Episode>().ToArray(), HasProviderId, GroupEpisodesByKey);
            if (revalidated.Count != 1 || revalidated[0].Items.Count != selectedItems.Length)
            {
                throw new InvalidOperationException(
                    $"The selected {kind} items do not share valid matching IDs. Refresh the preview and try again.");
            }

            _logger.LogInformation(
                "Merging selected {Kind} group; matching keys: {MatchingKeys}; item IDs: {ItemIds}",
                kind,
                string.Join(",", revalidated[0].MatchingKeys),
                string.Join(",", submittedIds));
            changed += await MergeGroupAsync(selectedItems, cancellationToken).ConfigureAwait(false);
            merged++;
        }

        return new MergeSelectionResult(merged, changed, PreviewOnly: false);
    }

    private static MergePreviewGroup CreatePreviewGroup(DuplicateVideoGroup group, string kind)
    {
        var primary = SelectPrimaryVersion(group.Items);
        var matchedIds = group.MatchingKeys.Select(ParseMatchingKey).ToArray();
        var items = group.Items
            .Select(item => new MergePreviewItem(
                item.Id,
                item.Name,
                item.ProductionYear,
                new Dictionary<string, string>(item.ProviderIds, StringComparer.OrdinalIgnoreCase),
                item.Id.Equals(primary.Id)))
            .ToArray();
        var groupId = string.Join("-", group.Items.Select(item => item.Id).Order());
        return new MergePreviewGroup(groupId, kind, matchedIds, items);
    }

    private static MatchedIdentity ParseMatchingKey(string key)
    {
        if (key.StartsWith("position:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = key.Split(':');
            return parts.Length == 4
                ? new MatchedIdentity("Position", $"{parts[1]} S{parts[2]}E{parts[3]}")
                : new MatchedIdentity("Position", key);
        }

        if (key.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = key.LastIndexOf(':');
            var title = key["title:".Length..separator];
            var year = key[(separator + 1)..];
            return new MatchedIdentity("Title", $"{title} ({year})");
        }

        var parts2 = key.Split(':', 3);
        return parts2.Length == 3
            ? new MatchedIdentity(parts2[1], parts2[2])
            : new MatchedIdentity(parts2[0], parts2.Length > 1 ? parts2[1] : key);
    }

    private async Task<int> MergeVideosAsync(
        BaseItemKind itemKind,
        string kind,
        Func<IReadOnlyList<Video>, List<DuplicateVideoGroup>> findGroups,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        using var scope = _logger.BeginScope(
            new Dictionary<string, object?>
            {
                ["ReelinkOperation"] = $"Merge{kind}",
                ["ReelinkOperationId"] = operationId
            });

        _logger.LogInformation("Starting {Kind} version merge {OperationId}", kind, operationId);
        var items = GetLibraryVideos(itemKind).ToArray();
        var groups = findGroups(items);
        _logger.LogInformation(
            "Scanned {ItemCount} eligible {Kind} items and found {GroupCount} duplicate groups",
            items.Length,
            kind,
            groups.Count);

        var changed = 0;
        try
        {
            for (var index = 0; index < groups.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var group = groups[index];
                _logger.LogInformation(
                    "Processing {Kind} group {GroupNumber}/{GroupCount}; matching keys: {MatchingKeys}; item IDs: {ItemIds}",
                    kind,
                    index + 1,
                    groups.Count,
                    string.Join(",", group.MatchingKeys),
                    string.Join(",", group.Items.Select(item => item.Id)));
                _logger.LogDebug(
                    "{Kind} group {GroupNumber}/{GroupCount} paths: {Paths}",
                    kind,
                    index + 1,
                    groups.Count,
                    string.Join(" | ", group.Items.Select(item => item.Path)));

                try
                {
                    changed += await MergeGroupAsync(group.Items, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(
                        ex,
                        "Failed to merge {Kind} group {GroupNumber}/{GroupCount}; matching keys: {MatchingKeys}; item IDs: {ItemIds}",
                        kind,
                        index + 1,
                        groups.Count,
                        string.Join(",", group.MatchingKeys),
                        string.Join(",", group.Items.Select(item => item.Id)));
                    throw;
                }

                progress?.Report((index + 1) * 100d / groups.Count);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "{Kind} version merge {OperationId} was cancelled after changing {ChangedCount} items",
                kind,
                operationId,
                changed);
            throw;
        }

        progress?.Report(100);
        _logger.LogInformation(
            "Completed {Kind} version merge {OperationId}: {GroupCount} groups and {ChangedCount} items changed",
            kind,
            operationId,
            groups.Count,
            changed);
        return changed;
    }

    private static List<DuplicateVideoGroup> GetDuplicateGroups<T>(
        IReadOnlyList<T> items,
        Func<Video, bool> hasGroupKey,
        Func<T, IEnumerable<string>> keysOf)
        where T : Video
    {
        // Union-find over shared merge keys so overlapping keys (multiple providers,
        // or provider plus position) yield one group per connected set.
        var parent = new int[items.Count];
        var itemKeys = new IReadOnlyList<string>[items.Count];
        for (var i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
            itemKeys[i] = [];
        }

        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
            {
                parent[rb] = ra;
            }
        }

        var buckets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            if (!hasGroupKey(items[i]))
            {
                continue;
            }

            itemKeys[i] = keysOf(items[i])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var key in itemKeys[i])
            {
                if (buckets.TryGetValue(key, out var other))
                {
                    Union(i, other);
                }
                else
                {
                    buckets[key] = i;
                }
            }
        }

        return items.Select((item, index) => (Item: (Video)item, Index: index, Root: Find(index)))
            .GroupBy(entry => entry.Root)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var entries = group.ToArray();
                var matchingKeys = entries
                    .SelectMany(entry => itemKeys[entry.Index])
                    .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .Where(keys => keys.Count() > 1)
                    .Select(keys => keys.Key)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var groupItems = entries
                    .Select(entry => entry.Item)
                    .OrderBy(item => item.Id)
                    .ToArray();
                return new DuplicateVideoGroup(groupItems, matchingKeys);
            })
            .ToList();
    }

    private static IEnumerable<string> GroupMoviesByKey(Movie movie)
        => movie.ProviderIds
            .Where(pair => MovieIdentityProviders.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"provider:{pair.Key}:{pair.Value.Trim()}");

    private static bool HasMovieGroupKey(Video video)
        => video.ProviderIds.Any(pair =>
            MovieIdentityProviders.Contains(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value));

    private static IEnumerable<string> GroupEpisodesByKey(Episode episode)
        => episode.ProviderIds
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"provider:{pair.Key}:{pair.Value}")
            .Concat(episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
                ? [$"position:{episode.SeriesName}:{episode.ParentIndexNumber}:{episode.IndexNumber}"]
                : TitleKeys(episode));

    private static IEnumerable<string> TitleKeys(Video video)
    {
        if (!string.IsNullOrWhiteSpace(video.Name))
        {
            yield return $"title:{video.Name}:{video.ProductionYear}";
        }
    }

    private static bool HasProviderId(Video video)
        => video.ProviderIds.Values.Any(v => !string.IsNullOrWhiteSpace(v));

    private IEnumerable<Video> GetLibraryVideos(BaseItemKind kind)
    {
        var items = _libraryManager.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [kind],
                    IsVirtualItem = false,
                    Recursive = true,
                    GroupByPresentationUniqueKey = false
                })
            .OfType<Video>()
            .ToArray();
        var eligible = items.Where(IsEligible).ToArray();

        _logger.LogDebug(
            "Loaded {TotalCount} {ItemKind} items; {EligibleCount} eligible and {ExcludedCount} excluded by configuration",
            items.Length,
            kind,
            eligible.Length,
            items.Length - eligible.Length);
        return eligible;
    }

    private async Task<int> MergeGroupAsync(IReadOnlyList<Video> group, CancellationToken cancellationToken)
    {
        // Flatten existing version groups so re-merging is idempotent.
        var items = GetAllVersionItems(group)
            .DistinctBy(v => v.Id)
            .OrderBy(v => v.Id)
            .ToList();

        if (items.Count < 2)
        {
            return 0;
        }

        var primary = SelectPrimaryVersion(items);
        var changed = 0;

        foreach (var alternate in items.Where(i => !i.Id.Equals(primary.Id)))
        {
            _logger.LogInformation(
                "Merging duplicate {Kind} \"{Name}\" ({Id}) into version group with primary {PrimaryId}",
                alternate is Movie ? "movie" : "episode",
                alternate.Name,
                alternate.Id,
                primary.Id);

            alternate.SetPrimaryVersionId(primary.Id);
            alternate.OwnerId = primary.Id;
            alternate.LocalAlternateVersions = [];
            alternate.LinkedAlternateVersions = [];

            await alternate.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            changed++;
        }

        primary.LocalAlternateVersions = items
            .Where(i => !i.Id.Equals(primary.Id))
            .Select(i => i.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        primary.LinkedAlternateVersions = [];
        primary.SetPrimaryVersionId(null);
        primary.OwnerId = Guid.Empty;

        await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

        return changed;
    }

    private async Task<int> SplitItemsAsync(IEnumerable<Video> items, string kind, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        using var scope = _logger.BeginScope(
            new Dictionary<string, object?>
            {
                ["ReelinkOperation"] = $"Split{kind}",
                ["ReelinkOperationId"] = operationId
            });

        var list = items.ToArray();
        _logger.LogInformation(
            "Starting {Kind} version split {OperationId}; scanning {ItemCount} eligible items",
            kind,
            operationId,
            list.Length);

        var changed = 0;
        try
        {
            for (var index = 0; index < list.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    changed += await SplitItemAsync(list[index], kind, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(
                        ex,
                        "Failed to split {Kind} item {ItemId} at path {Path}",
                        kind,
                        list[index].Id,
                        list[index].Path);
                    throw;
                }

                progress?.Report((index + 1) * 100d / list.Length);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "{Kind} version split {OperationId} was cancelled after changing {ChangedCount} items",
                kind,
                operationId,
                changed);
            throw;
        }

        progress?.Report(100);
        _logger.LogInformation(
            "Completed {Kind} version split {OperationId}: {ChangedCount} items separated",
            kind,
            operationId,
            changed);
        return changed;
    }

    private async Task<int> SplitItemAsync(Video item, string kind, CancellationToken cancellationToken)
    {
        if (item.PrimaryVersionId.HasValue)
        {
            // An alternate version: clearing its link detaches it; the primary keeps its other links.
            _logger.LogInformation(
                "Splitting alternate {Kind} \"{Name}\" ({Id}) from primary {PrimaryId}",
                kind,
                item.Name,
                item.Id,
                item.PrimaryVersionId.Value);

            item.SetPrimaryVersionId(null);
            item.OwnerId = Guid.Empty;
            item.LocalAlternateVersions = [];
            item.LinkedAlternateVersions = [];

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        // A primary with alternates: detach each alternate and clear the group.
        var alternateIds = _libraryManager.GetLocalAlternateVersionIds(item)
            .Concat(_libraryManager.GetLinkedAlternateVersions(item).Select(v => v.Id))
            .Where(id => !id.Equals(item.Id))
            .ToArray();

        if (alternateIds.Length == 0)
        {
            return 0;
        }

        foreach (var alternateId in alternateIds)
        {
            if (_libraryManager.GetItemById<Video>(alternateId) is not Video alternate)
            {
                continue;
            }

            _logger.LogInformation(
                "Splitting alternate {Kind} \"{Name}\" ({Id}) from primary {PrimaryId}",
                kind,
                alternate.Name,
                alternate.Id,
                item.Id);

            alternate.SetPrimaryVersionId(null);
            alternate.OwnerId = Guid.Empty;
            alternate.LocalAlternateVersions = [];
            alternate.LinkedAlternateVersions = [];

            await alternate.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }

        item.LocalAlternateVersions = [];
        item.LinkedAlternateVersions = [];
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);

        return alternateIds.Length;
    }

    private List<Video> GetAllVersionItems(IReadOnlyList<Video> initial)
    {
        var versions = new Dictionary<Guid, Video>();
        var pending = new Queue<Video>(initial);

        while (pending.Count > 0)
        {
            var version = pending.Dequeue();
            if (!versions.TryAdd(version.Id, version))
            {
                continue;
            }

            foreach (var alternateId in _libraryManager.GetLocalAlternateVersionIds(version))
            {
                if (_libraryManager.GetItemById<Video>(alternateId) is Video alternate)
                {
                    pending.Enqueue(alternate);
                }
            }

            foreach (var alternate in _libraryManager.GetLinkedAlternateVersions(version))
            {
                pending.Enqueue(alternate);
            }
        }

        return versions.Values.ToList();
    }

    private static Video SelectPrimaryVersion(IReadOnlyList<Video> items)
    {
        // Prefer the highest-resolution default video stream; fall back to stable id order.
        return items
            .OrderByDescending(i => i.GetDefaultVideoStream()?.Width ?? 0)
            .ThenBy(i => i.Id)
            .First();
    }

    private bool IsEligible(BaseItem item)
    {
        var excluded = Plugin.Instance?.CurrentConfiguration?.LocationsExcluded;
        return excluded is null || !IsInExcludedLocation(item.Path, excluded);
    }

    private bool IsInExcludedLocation(string? path, IReadOnlyList<string> excludedLocations)
    {
        return !string.IsNullOrWhiteSpace(path)
            && excludedLocations.Count > 0
            && excludedLocations.Any(s => _fileSystem.ContainsSubPath(s, path!));
    }

    private sealed record DuplicateVideoGroup(
        IReadOnlyList<Video> Items,
        IReadOnlyList<string> MatchingKeys);
}
