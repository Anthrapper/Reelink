using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Reelink;

/// <summary>
/// A proposed group of library items that can be merged.
/// </summary>
public sealed record MergePreviewGroup(
    [property: JsonPropertyName("groupId")] string GroupId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("matchedIds")] IReadOnlyList<MatchedIdentity> MatchedIds,
    [property: JsonPropertyName("items")] IReadOnlyList<MergePreviewItem> Items);

/// <summary>
/// An identity shared by at least two items in a proposed group.
/// </summary>
public sealed record MatchedIdentity(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("value")] string Value);

/// <summary>
/// A library item shown in a merge preview.
/// </summary>
public sealed record MergePreviewItem(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("providerIds")] IReadOnlyDictionary<string, string> ProviderIds,
    [property: JsonPropertyName("isProposedPrimary")] bool IsProposedPrimary);

/// <summary>
/// A client-selected group of item IDs to merge.
/// </summary>
public sealed class SelectedMergeGroup
{
    /// <summary>
    /// Gets or sets the selected item IDs.
    /// </summary>
    [JsonPropertyName("itemIds")]
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
    [JsonPropertyName("groups")]
    public IReadOnlyList<SelectedMergeGroup> Groups { get; set; } = [];
}

/// <summary>
/// Result returned after selected groups are merged.
/// </summary>
/// <param name="GroupsMerged">Number of groups the request merged.</param>
/// <param name="ItemsChanged">Number of library items changed.</param>
/// <param name="PreviewOnly">True when series preview-only mode prevented changes.</param>
public sealed record MergeSelectionResult(
    [property: JsonPropertyName("groupsMerged")] int GroupsMerged,
    [property: JsonPropertyName("itemsChanged")] int ItemsChanged,
    [property: JsonPropertyName("previewOnly")] bool PreviewOnly);

/// <summary>
/// Error payload returned when a selected merge request cannot be applied.
/// </summary>
public sealed record MergeSelectionError(
    [property: JsonPropertyName("error")] string Error);
