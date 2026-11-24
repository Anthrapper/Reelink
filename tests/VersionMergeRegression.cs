#:project ../Jellyfin.Plugin.Reelink/Jellyfin.Plugin.Reelink.csproj
#:package Jellyfin.Controller@12.0.0
#:package Jellyfin.Model@12.0.0
#:property Nullable=enable
#:property TreatWarningsAsErrors=true
#:property PublishAot=false

using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Reelink;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;

// Run: dotnet run --file tests/VersionMergeRegression.cs -c Release
// Uses Jellyfin's real scan mutation; the repository boundary is isolated in memory.
foreach (var kind in new[] { BaseItemKind.Movie, BaseItemKind.Episode })
{
    var library = DispatchProxy.Create<ILibraryManager, LibraryState>();
    var state = (LibraryState)(object)library;
    BaseItem.LibraryManager = library;
    var manager = new VideoVersionsManager(
        library,
        DispatchProxy.Create<IFileSystem, LibraryState>(),
        NullLogger<VideoVersionsManager>.Instance);
    var first = CreateVideo(kind, 1);
    var second = CreateVideo(kind, 2);
    var unselected = CreateVideo(kind, 3);
    state.Seed(first, second, unselected);

    await MergeSelectedAsync(manager, kind, first.Id, second.Id);
    await state.ScanAndReloadAsync();
    AssertGroup(state, first, second);
    Require(unselected.PrimaryVersionId is null && unselected.MediaSourceCount == 1,
        "A scan must not merge an unselected duplicate.");

    await MergeSelectedAsync(manager, kind, first.Id, second.Id);
    await state.ScanAndReloadAsync();
    AssertGroup(state, first, second);

    if (kind == BaseItemKind.Movie)
    {
        await manager.SplitMoviesAsync(null, CancellationToken.None);
    }
    else
    {
        await manager.SplitEpisodesAsync(null, CancellationToken.None);
    }

    await state.ScanAndReloadAsync();
    Require(state.DeletedIds.Count == 0, "Splitting must not delete any library records.");
    foreach (var video in new[] { first, second, unselected })
    {
        Require(video.PrimaryVersionId is null && video.OwnerId == Guid.Empty && video.MediaSourceCount == 1,
            "An explicitly split version must stay independent after a scan.");
    }

    // The new primary sorts before the old primary, whose owned child sorts last.
    // Clearing the old primary first must not let Jellyfin delete that child.
    var newPrimary = CreateVideo(kind, 4);
    var oldPrimary = CreateVideo(kind, 5);
    var oldAlternate = CreateVideo(kind, 6);
    oldPrimary.LocalAlternateVersions = [oldAlternate.Path];
    oldAlternate.SetPrimaryVersionId(oldPrimary.Id);
    oldAlternate.OwnerId = oldPrimary.Id;
    state.Seed(newPrimary, oldPrimary, oldAlternate);

    if (kind == BaseItemKind.Movie)
    {
        await manager.MergeMoviesAsync(null, CancellationToken.None);
    }
    else
    {
        await manager.MergeEpisodesAsync(null, CancellationToken.None);
    }

    await state.ScanAndReloadAsync();
    AssertGroup(state, newPrimary, oldPrimary, oldAlternate);

    // Selecting existing alternates must retain their primary and its other versions.
    await MergeSelectedAsync(manager, kind, oldPrimary.Id, oldAlternate.Id);
    await state.ScanAndReloadAsync();
    AssertGroup(state, newPrimary, oldPrimary, oldAlternate);
    Console.WriteLine($"PASS {kind}: selected merge, scan/reload, repeat merge, split, legacy migration, alternate-only merge");
}

static Video CreateVideo(BaseItemKind kind, int number)
{
    Video video = kind == BaseItemKind.Movie
        ? new Movie()
        : new Episode { SeriesName = "Regression", ParentIndexNumber = 1, IndexNumber = 1 };
    video.Id = new Guid(number, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    video.Name = "Regression";
    video.Path = $"/library/{number}/Regression.mkv";
    video.ProviderIds["Tmdb"] = "42";
    return video;
}

static Task<MergeSelectionResult> MergeSelectedAsync(
    VideoVersionsManager manager, BaseItemKind kind, params Guid[] ids)
{
    SelectedMergeGroup[] selection = [new() { ItemIds = ids }];
    return kind == BaseItemKind.Movie
        ? manager.MergeSelectedMoviesAsync(selection, CancellationToken.None)
        : manager.MergeSelectedEpisodesAsync(selection, CancellationToken.None);
}

static void AssertGroup(LibraryState state, Video primary, params Video[] alternates)
{
    Require(state.DeletedIds.Count == 0, "Merging must never delete library records.");
    Require(primary.PrimaryVersionId is null && primary.MediaSourceCount == alternates.Length + 1,
        "The primary must expose every playback version after a scan/reload.");
    foreach (var video in alternates.Prepend(primary))
    {
        Require(state.Items.ContainsKey(video.Id) && video.OwnerId == Guid.Empty,
            "A linked version must remain an independent library record.");
        Require(video.PrimaryVersionId == (video.Id == primary.Id ? null : primary.Id),
            "All alternates must point at the same primary.");
        Require(primary.GetAlternateVersion(video.Id)?.Id == video.Id,
            "Every member must remain a selectable playback version.");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

public class LibraryState : DispatchProxy
{
    private static readonly MethodInfo ScanUpdate = typeof(Video).GetMethod(
        "UpdateFromResolvedItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly Dictionary<Guid, SavedVersion> _saved = [];
    public Dictionary<Guid, Video> Items { get; } = [];
    public List<Guid> DeletedIds { get; } = [];

    public void Seed(params Video[] videos)
    {
        Items.Clear();
        _saved.Clear();
        DeletedIds.Clear();
        foreach (var video in videos)
        {
            Items.Add(video.Id, video);
        }

        foreach (var video in videos)
        {
            Save(video);
        }
    }

    public async Task ScanAndReloadAsync()
    {
        foreach (var video in Items.Values.ToArray())
        {
            Video resolved = video is Movie ? new Movie() : new Episode();
            var update = (ItemUpdateType)ScanUpdate.Invoke(video, [resolved])!;
            await video.UpdateToRepositoryAsync(update, CancellationToken.None);
        }

        foreach (var (id, saved) in _saved)
        {
            var video = Items[id];
            video.OwnerId = saved.OwnerId;
            video.SetPrimaryVersionId(saved.PrimaryId);
            video.LocalAlternateVersions = [.. saved.LocalPaths];
            video.LinkedAlternateVersions = CopyLinks(saved.Linked);
        }
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod!.Name)
        {
            case "GetItemList":
                var query = (InternalItemsQuery)args![0]!;
                return Items.Values
                    .Where(video => video.OwnerId == Guid.Empty
                        && query.IncludeItemTypes.Contains(video is Movie ? BaseItemKind.Movie : BaseItemKind.Episode))
                    .Cast<BaseItem>().ToList();
            case "GetItemById":
                return Items.GetValueOrDefault((Guid)args![0]!);
            case "GetLocalAlternateVersionIds":
                return LocalIds((Video)args![0]!).ToArray();
            case "GetLinkedAlternateVersions":
                return _saved[((Video)args![0]!).Id].Linked.Select(link => Items[link.ItemId!.Value]).ToArray();
            case "UpdateItemAsync":
                ((CancellationToken)args![3]!).ThrowIfCancellationRequested();
                Save((Video)args[0]!);
                return Task.CompletedTask;
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }

    private IEnumerable<Guid> LocalIds(Video video)
        => _saved.TryGetValue(video.Id, out var saved)
            ? saved.LocalPaths.Select(path => Items.Values.Single(item => item.Path == path).Id)
            : [];

    private void Save(Video video)
    {
        var keptIds = video.LocalAlternateVersions.Select(path => Items.Values.Single(item => item.Path == path).Id)
            .Concat(video.LinkedAlternateVersions.Select(link => link.ItemId!.Value)).ToHashSet();
        // Mirrors Jellyfin 12's orphaned-local-version deletion at the persistence boundary.
        foreach (var id in LocalIds(video).Where(id => !keptIds.Contains(id)).ToArray())
        {
            if (_saved.TryGetValue(id, out var saved) && saved.OwnerId == video.Id)
            {
                DeletedIds.Add(id);
                Items.Remove(id);
                _saved.Remove(id);
            }
        }

        _saved[video.Id] = new SavedVersion(
            video.OwnerId, video.PrimaryVersionId, [.. video.LocalAlternateVersions], CopyLinks(video.LinkedAlternateVersions));
    }

    private static LinkedChild[] CopyLinks(LinkedChild[] links)
        => links.Select(link => new LinkedChild { ItemId = link.ItemId, Type = link.Type }).ToArray();

    private sealed record SavedVersion(Guid OwnerId, Guid? PrimaryId, string[] LocalPaths, LinkedChild[] Linked);
}
