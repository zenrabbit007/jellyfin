using System;

namespace Jellyfin.Server.Implementations.Trickplay;

/// <summary>
/// The thumbnails and the time window a single trickplay tile is made of.
/// </summary>
/// <param name="FirstThumbnail">The index of the first thumbnail of the tile.</param>
/// <param name="ThumbnailCount">The amount of thumbnails of the tile.</param>
/// <param name="Start">The start of the time window covered by the tile.</param>
/// <param name="End">The end of the time window covered by the tile.</param>
internal readonly record struct TrickplayTileRange(int FirstThumbnail, int ThumbnailCount, TimeSpan Start, TimeSpan End);
