using Jellyfin.Plugin.Reelink.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Reelink.Api;

/// <summary>
/// API endpoints for previewing, merging, and splitting duplicate shows, movies, and episodes.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Reelink")]
[Produces("application/json")]
public sealed class ReelinkController : ControllerBase
{
    private readonly VideoVersionsManager _manager;
    private readonly ShowMergeManager _showManager;
    private readonly ILogger<ReelinkController> _logger;

    public ReelinkController(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ILogger<VideoVersionsManager> managerLogger,
        ILogger<ShowMergeManager> showManagerLogger,
        ILogger<ReelinkController> logger)
    {
        _manager = new VideoVersionsManager(libraryManager, fileSystem, managerLogger);
        _showManager = new ShowMergeManager(libraryManager, showManagerLogger);
        _logger = logger;
    }

    /// <summary>
    /// Returns the duplicate series groups that currently satisfy the matching rules.
    /// </summary>
    /// <returns>The proposed series groups with matched IDs.</returns>
    [HttpGet("PreviewSeries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MergePreviewGroup>> PreviewSeries()
        => Ok(_showManager.PreviewSeries());

    /// <summary>
    /// Returns the duplicate movie groups that currently satisfy the matching rules.
    /// </summary>
    /// <returns>The proposed movie groups with matched IDs.</returns>
    [HttpGet("PreviewMovies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MergePreviewGroup>> PreviewMovies()
        => Ok(_manager.PreviewMovies());

    /// <summary>
    /// Returns the duplicate episode groups that currently satisfy the matching rules.
    /// </summary>
    /// <returns>The proposed episode groups with matched IDs.</returns>
    [HttpGet("PreviewEpisodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MergePreviewGroup>> PreviewEpisodes()
        => Ok(_manager.PreviewEpisodes());

    /// <summary>
    /// Groups only the client-selected duplicate series.
    /// </summary>
    /// <param name="request">The selected groups of series IDs.</param>
    /// <returns>The merge result.</returns>
    [HttpPost("MergeSelectedSeries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MergeSelectionResult>> MergeSelectedSeries([FromBody] MergeSelectionRequest request)
    {
        _logger.LogInformation("Manual selected series merge requested: {GroupCount} groups", request.Groups.Count);
        try
        {
            var result = await _showManager
                .MergeSelectedSeriesAsync(request.Groups, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            LogMergeSelectionResult("series", result);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new MergeSelectionError(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new MergeSelectionError(ex.Message));
        }
    }

    /// <summary>
    /// Merges only the client-selected movie groups.
    /// </summary>
    /// <param name="request">The selected groups of movie IDs.</param>
    /// <returns>The merge result.</returns>
    [HttpPost("MergeSelectedMovies")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MergeSelectionResult>> MergeSelectedMovies([FromBody] MergeSelectionRequest request)
    {
        _logger.LogInformation("Manual selected movie merge requested: {GroupCount} groups", request.Groups.Count);
        try
        {
            var result = await _manager
                .MergeSelectedMoviesAsync(request.Groups, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            LogMergeSelectionResult("movie", result);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new MergeSelectionError(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new MergeSelectionError(ex.Message));
        }
    }

    /// <summary>
    /// Merges only the client-selected episode groups.
    /// </summary>
    /// <param name="request">The selected groups of episode IDs.</param>
    /// <returns>The merge result.</returns>
    [HttpPost("MergeSelectedEpisodes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MergeSelectionResult>> MergeSelectedEpisodes([FromBody] MergeSelectionRequest request)
    {
        _logger.LogInformation("Manual selected episode merge requested: {GroupCount} groups", request.Groups.Count);
        try
        {
            var result = await _manager
                .MergeSelectedEpisodesAsync(request.Groups, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            LogMergeSelectionResult("episode", result);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new MergeSelectionError(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new MergeSelectionError(ex.Message));
        }
    }

    private void LogMergeSelectionResult(string kind, MergeSelectionResult result)
    {
        _logger.LogInformation(
            "Selected {Kind} merge complete: {GroupsMerged} groups merged, {ItemsChanged} items changed, preview only: {PreviewOnly}",
            kind,
            result.GroupsMerged,
            result.ItemsChanged,
            result.PreviewOnly);
    }

    /// <summary>
    /// Groups duplicate TV series that share configured provider IDs.
    /// </summary>
    /// <returns>A <see cref="NoContentResult"/> when grouping completes.</returns>
    [HttpPost("MergeSeries")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> MergeSeries()
    {
        _logger.LogInformation("Manual duplicate-show merge requested");
        var summary = await _showManager.MergeAsync(null, HttpContext.RequestAborted).ConfigureAwait(false);
        _logger.LogInformation(
            "Duplicate-show merge complete: {DuplicateGroups} groups and {SeriesChanged} series {Action}",
            summary.DuplicateGroups,
            summary.SeriesChanged,
            summary.PreviewOnly ? "would be changed" : "changed");
        return NoContent();
    }

    /// <summary>
    /// Separates TV series previously grouped by this plugin.
    /// </summary>
    /// <returns>A <see cref="NoContentResult"/> when splitting completes.</returns>
    [HttpPost("SplitSeries")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> SplitSeries()
    {
        _logger.LogInformation("Manual duplicate-show split requested");
        var changed = await _showManager.SplitAsync(null, HttpContext.RequestAborted).ConfigureAwait(false);
        _logger.LogInformation("Duplicate-show split complete: {Changed} series separated", changed);
        return NoContent();
    }

    /// <summary>
    /// Scans all movies and merges repeated ones into version groups.
    /// </summary>
    /// <returns>A <see cref="NoContentResult"/> when the merge completes.</returns>
    [HttpPost("MergeMovies")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> MergeMovies()
    {
        _logger.LogInformation("Manual movie merge requested");
        var changed = await _manager.MergeMoviesAsync(null, HttpContext.RequestAborted).ConfigureAwait(false);
        _logger.LogInformation("Movie merge complete: {Changed} movies grouped", changed);
        return NoContent();
    }

    /// <summary>
    /// Scans all episodes and merges repeated ones into version groups.
    /// </summary>
    /// <returns>A <see cref="NoContentResult"/> when the merge completes.</returns>
    [HttpPost("MergeEpisodes")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> MergeEpisodes()
    {
        _logger.LogInformation("Manual episode merge requested");
        var changed = await _manager.MergeEpisodesAsync(null, HttpContext.RequestAborted).ConfigureAwait(false);
        _logger.LogInformation("Episode merge complete: {Changed} episodes grouped", changed);
        return NoContent();
    }

    /// <summary>
    /// Splits all movie version groups back into individual items.
    /// </summary>
    /// <returns>A <see cref="NoContentResult"/> when the split completes.</returns>
    [HttpPost("SplitMovies")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> SplitMovies()
    {
        _logger.LogInformation("Manual movie split requested");
        var changed = await _manager.SplitMoviesAsync(null, HttpContext.RequestAborted).ConfigureAwait(false);
        _logger.LogInformation("Movie split complete: {Changed} movies separated", changed);
        return NoContent();
    }

    /// <summary>
    /// Splits all episode version groups back into individual items.
    /// </summary>
    /// <returns>A <see cref="NoContentResult"/> when the split completes.</returns>
    [HttpPost("SplitEpisodes")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> SplitEpisodes()
    {
        _logger.LogInformation("Manual episode split requested");
        var changed = await _manager.SplitEpisodesAsync(null, HttpContext.RequestAborted).ConfigureAwait(false);
        _logger.LogInformation("Episode split complete: {Changed} episodes separated", changed);
        return NoContent();
    }
}
