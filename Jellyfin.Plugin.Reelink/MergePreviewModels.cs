namespace Jellyfin.Plugin.Reelink;

/// <summary>
/// A proposed group of library items that can be merged.
/// </summary>
public sealed record MergePreviewGroup(
    string GroupId,
    string Kind,
    IReadOnlyList<MatchedIdentity> MatchedIds,
    IReadOnlyList<MergePreviewItem> Items);

/// <summary>
/// An identity shared by at least two items in a proposed group.
/// </summary>
public sealed record MatchedIdentity(string Provider, string Value);

/// <summary>
/// A library item shown in a merge preview.
/// </summary>
public sealed record MergePreviewItem(
    Guid Id,
    string Title,
    int? Year,
    IReadOnlyDictionary<string, string> ProviderIds,
    bool IsProposedPrimary);

/// <summary>
/// A client-selected group of item IDs to merge.
/// </summary>
public sealed class SelectedMergeGroup
{
    /// <summary>
    /// Gets or sets the selected item IDs.
    /// </summary>
    public IReadOnlyList<Guid> ItemIds { get; set; } = [];
}

/// <summary>
/// A request containing one or more client-selected groups.
/// </summary>
public sealed class MergeSelectionRequest
{
    /// <summary>
    /// Gets or sets the selected groups.
    /// </summary>
    public IReadOnlyList<SelectedMergeGroup> Groups { get; set; } = [];
}

/// <summary>
/// Result returned after selected groups are merged.
/// </summary>
/// <param name="GroupsMerged">Number of groups the request merged.</param>
/// <param name="ItemsChanged">Number of library items changed.</param>
/// <param name="PreviewOnly">True when series preview-only mode prevented changes.</param>
public sealed record MergeSelectionResult(int GroupsMerged, int ItemsChanged, bool PreviewOnly);

/// <summary>
/// Error payload returned when a selected merge request cannot be applied.
/// </summary>
public sealed record MergeSelectionError(string Error);
