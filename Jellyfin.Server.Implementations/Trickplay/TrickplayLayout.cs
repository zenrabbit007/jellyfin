using System;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Server.Implementations.Trickplay;

/// <summary>
/// Calculations for trickplay tiles that are generated on demand.
/// </summary>
internal static class TrickplayLayout
{
    /// <summary>
    /// Creates the trickplay information of a tile set that is generated on demand.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="runtime">The runtime of the video.</param>
    /// <param name="videoStream">The video stream the thumbnails are extracted from.</param>
    /// <param name="requestedWidth">The width of a single thumbnail.</param>
    /// <param name="options">The trickplay options.</param>
    /// <returns>The trickplay information, or <c>null</c> when no thumbnails can be generated.</returns>
    public static TrickplayInfo? CreateOnDemandInfo(Guid itemId, TimeSpan runtime, MediaStream videoStream, int requestedWidth, TrickplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(videoStream);
        ArgumentNullException.ThrowIfNull(options);

        if (requestedWidth <= 0
            || options.Interval <= 0
            || options.TileWidth <= 0
            || options.TileHeight <= 0
            || runtime <= TimeSpan.Zero)
        {
            return null;
        }

        var (width, height) = GetThumbnailSize(videoStream, requestedWidth);
        if (!width.HasValue || !height.HasValue)
        {
            return null;
        }

        // The amount of thumbnails has to stay in sync with the calculation used by the clients to map
        // a playback position to a tile and an offset inside that tile.
        var thumbnailCount = (int)Math.Ceiling(runtime.TotalMilliseconds / options.Interval);
        if (thumbnailCount <= 0)
        {
            return null;
        }

        return new TrickplayInfo
        {
            ItemId = itemId,
            Width = width.Value,
            Height = height.Value,
            TileWidth = options.TileWidth,
            TileHeight = options.TileHeight,
            ThumbnailCount = thumbnailCount,
            Interval = options.Interval,
            Bandwidth = 0
        };
    }

    /// <summary>
    /// Gets the size of a single thumbnail as produced by the extraction.
    /// </summary>
    /// <param name="videoStream">The video stream the thumbnails are extracted from.</param>
    /// <param name="requestedWidth">The width of a single thumbnail.</param>
    /// <returns>The size of a single thumbnail.</returns>
    public static (int? Width, int? Height) GetThumbnailSize(MediaStream videoStream, int requestedWidth)
    {
        ArgumentNullException.ThrowIfNull(videoStream);

        // A rotated video is stored with swapped dimensions, the scaler outputs it the right way around.
        var rotated = Math.Abs(videoStream.Rotation ?? 0) == 90;
        var sourceWidth = rotated ? videoStream.Height : videoStream.Width;
        var sourceHeight = rotated ? videoStream.Width : videoStream.Height;

        // Use the same scaling as the extraction, so the metadata matches the generated tiles.
        return EncodingHelper.GetFixedOutputSize(sourceWidth, sourceHeight, null, null, requestedWidth, null);
    }

    /// <summary>
    /// Gets the thumbnails and the time window a tile with the given index is made of.
    /// </summary>
    /// <param name="info">The trickplay information.</param>
    /// <param name="index">The index of the tile.</param>
    /// <param name="range">The thumbnails and time window of the tile.</param>
    /// <returns><c>true</c> if the tile index is in range, <c>false</c> otherwise.</returns>
    public static bool TryGetTileRange(TrickplayInfo info, int index, out TrickplayTileRange range)
    {
        range = default;

        if (info is null
            || index < 0
            || info.TileWidth <= 0
            || info.TileHeight <= 0
            || info.Interval <= 0
            || info.ThumbnailCount <= 0)
        {
            return false;
        }

        var thumbnailsPerTile = (long)info.TileWidth * info.TileHeight;
        var firstThumbnail = index * thumbnailsPerTile;
        if (firstThumbnail >= info.ThumbnailCount)
        {
            return false;
        }

        var thumbnailCount = (int)Math.Min(thumbnailsPerTile, info.ThumbnailCount - firstThumbnail);
        var start = TimeSpan.FromMilliseconds(firstThumbnail * info.Interval);
        var end = TimeSpan.FromMilliseconds((firstThumbnail + thumbnailCount) * info.Interval);

        range = new TrickplayTileRange((int)firstThumbnail, thumbnailCount, start, end);
        return true;
    }
}
