using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using J2N.Collections.Generic.Extensions;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Trickplay;

/// <summary>
/// ITrickplayManager implementation.
/// </summary>
public partial class TrickplayManager : ITrickplayManager
{
    // Preview frames are only a few kilobytes, keeping the last ones in memory avoids running
    // ffmpeg again when a client moves the mouse back and forth over the same positions.
    private const int MaxCachedFrames = 512;

    private static readonly Dictionary<string, TrickplayFrame> _frameCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> _frameCacheOrder = new();
    private static readonly object _frameCacheLock = new();

    private static readonly AsyncNonKeyedLocker _resourcePool = new(1);
    private static readonly AsyncKeyedLocker<string> _onDemandTileLocks = new(o =>
    {
        o.PoolSize = 20;
        o.PoolInitialFill = 1;
    });

    private static readonly AsyncKeyedLocker<string> _onDemandFrameLocks = new(o =>
    {
        o.PoolSize = 20;
        o.PoolInitialFill = 1;
    });

    private readonly ILogger<TrickplayManager> _logger;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IFileSystem _fileSystem;
    private readonly EncodingHelper _encodingHelper;
    private readonly IServerConfigurationManager _config;
    private readonly IImageEncoder _imageEncoder;
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IApplicationPaths _appPaths;
    private readonly IPathManager _pathManager;
    private readonly ILibraryManager _libraryManager;

    private static readonly string[] _trickplayImgExtensions = [".jpg"];

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayManager"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="encodingHelper">The encoding helper.</param>
    /// <param name="config">The server configuration manager.</param>
    /// <param name="imageEncoder">The image encoder.</param>
    /// <param name="dbProvider">The database provider.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="pathManager">The path manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    public TrickplayManager(
        ILogger<TrickplayManager> logger,
        IMediaEncoder mediaEncoder,
        IFileSystem fileSystem,
        EncodingHelper encodingHelper,
        IServerConfigurationManager config,
        IImageEncoder imageEncoder,
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IApplicationPaths appPaths,
        IPathManager pathManager,
        ILibraryManager libraryManager)
    {
        _logger = logger;
        _mediaEncoder = mediaEncoder;
        _fileSystem = fileSystem;
        _encodingHelper = encodingHelper;
        _config = config;
        _imageEncoder = imageEncoder;
        _dbProvider = dbProvider;
        _appPaths = appPaths;
        _pathManager = pathManager;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public async Task MoveGeneratedTrickplayDataAsync(Video video, LibraryOptions libraryOptions, CancellationToken cancellationToken)
    {
        var options = _config.Configuration.TrickplayOptions;
        if (libraryOptions is null || !libraryOptions.EnableTrickplayImageExtraction || !CanGenerateTrickplay(video, options.Interval))
        {
            return;
        }

        var existingTrickplayResolutions = await GetTrickplayResolutions(video.Id).ConfigureAwait(false);
        foreach (var resolution in existingTrickplayResolutions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingResolution = resolution.Key;
            var tileWidth = resolution.Value.TileWidth;
            var tileHeight = resolution.Value.TileHeight;
            var shouldBeSavedWithMedia = libraryOptions is not null && libraryOptions.SaveTrickplayWithMedia;
            var localOutputDir = new DirectoryInfo(GetTrickplayDirectory(video, tileWidth, tileHeight, existingResolution, false));
            var mediaOutputDir = new DirectoryInfo(GetTrickplayDirectory(video, tileWidth, tileHeight, existingResolution, true));
            if (shouldBeSavedWithMedia && localOutputDir.Exists)
            {
                var localDirFiles = localOutputDir.EnumerateFiles();
                var mediaDirExists = mediaOutputDir.Exists;
                if (localDirFiles.Any() && ((mediaDirExists && mediaOutputDir.EnumerateFiles().Any()) || !mediaDirExists))
                {
                    // Move images from local dir to media dir
                    MoveContent(localOutputDir.FullName, mediaOutputDir.FullName);
                    _logger.LogInformation("Moved trickplay images for {ItemName} to {Location}", video.Name, mediaOutputDir);
                }
            }
            else if (!shouldBeSavedWithMedia && mediaOutputDir.Exists)
            {
                var mediaDirFiles = mediaOutputDir.EnumerateFiles();
                var localDirExists = localOutputDir.Exists;
                if (mediaDirFiles.Any() && ((localDirExists && localOutputDir.EnumerateFiles().Any()) || !localDirExists))
                {
                    // Move images from media dir to local dir
                    MoveContent(mediaOutputDir.FullName, localOutputDir.FullName);
                    _logger.LogInformation("Moved trickplay images for {ItemName} to {Location}", video.Name, localOutputDir);
                }
            }
        }
    }

    private void MoveContent(string sourceFolder, string destinationFolder)
    {
        _fileSystem.MoveDirectory(sourceFolder, destinationFolder);
        var parent = Directory.GetParent(sourceFolder);
        if (parent is not null)
        {
            var parentContent = parent.EnumerateDirectories();
            if (!parentContent.Any())
            {
                parent.Delete();
            }
        }
    }

    private async Task DiscoverExistingTrickplayAsync(Video video, bool saveWithMedia, CancellationToken cancellationToken)
    {
        var options = _config.Configuration.TrickplayOptions;
        var existing = await GetTrickplayResolutions(video.Id).ConfigureAwait(false);

        // Remove DB rows whose on-disk folder no longer exists in either possible location.
        // Checking both locations avoids dropping rows mid-`SaveTrickplayWithMedia` migration.
        var orphanedWidths = new List<int>();
        foreach (var (width, info) in existing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localDir = GetTrickplayDirectory(video, info.TileWidth, info.TileHeight, info.Width, false);
            var mediaDir = GetTrickplayDirectory(video, info.TileWidth, info.TileHeight, info.Width, true);
            if (!HasTrickplayTiles(localDir) && !HasTrickplayTiles(mediaDir))
            {
                orphanedWidths.Add(width);
            }
        }

        if (orphanedWidths.Count > 0)
        {
            var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (dbContext.ConfigureAwait(false))
            {
                await dbContext.TrickplayInfos
                    .Where(i => i.ItemId.Equals(video.Id) && orphanedWidths.Contains(i.Width))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var width in orphanedWidths)
            {
                _logger.LogInformation("Removed orphaned trickplay DB entry width={Width} for {Path}", width, video.Path);
                existing.Remove(width);
            }
        }

        var trickplayDirectory = _pathManager.GetTrickplayDirectory(video, saveWithMedia);
        if (!Directory.Exists(trickplayDirectory))
        {
            return;
        }

        foreach (var subdir in new DirectoryInfo(trickplayDirectory).EnumerateDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var match = TrickplaySubdirRegex().Match(subdir.Name);
            if (!match.Success)
            {
                continue;
            }

            var width = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var tileWidth = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var tileHeight = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

            if (existing.ContainsKey(width))
            {
                continue;
            }

            var tiles = subdir.GetFiles("*.jpg")
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();
            if (tiles.Length == 0)
            {
                continue;
            }

            // On demand extraction creates a folder as soon as a single tile is requested, those folders
            // are not complete. Adopting them would store metadata that hides all the other tiles.
            if (options.EnableOnDemandExtraction
                && tiles.Length < TrickplayLayout.GetExpectedTileCount(TimeSpan.FromTicks(video.RunTimeTicks ?? 0), options.Interval, tileWidth, tileHeight))
            {
                _logger.LogDebug("Skipping incomplete trickplay tile folder {Folder} for {ItemId}", subdir.Name, video.Id);
                continue;
            }

            // The encoder pads the last tile to a full TileWidth*TileHeight grid, so the real
            // thumbnail count cannot be read from tile dimensions. Instead, bound the count from
            // the tile count and per-tile capacity, then pick an interval consistent with the
            // video runtime - snapping to the server's configured interval when it fits.
            var thumbsPerTile = tileWidth * tileHeight;
            var maxThumbs = tiles.Length * thumbsPerTile;
            var minThumbs = tiles.Length > 1 ? ((tiles.Length - 1) * thumbsPerTile) + 1 : 1;

            int interval;
            int thumbnailCount;
            if (video.RunTimeTicks is long ticks)
            {
                var runtimeMs = ticks / TimeSpan.TicksPerMillisecond;
                var minInterval = Math.Max(1000L, (long)Math.Ceiling(runtimeMs / (double)maxThumbs));
                var maxInterval = Math.Max(minInterval, (long)Math.Floor(runtimeMs / (double)minThumbs));

                if (options.Interval >= minInterval && options.Interval <= maxInterval)
                {
                    interval = options.Interval;
                }
                else
                {
                    var midpoint = (minInterval + maxInterval) / 2.0;
                    var snapped = (long)Math.Round(midpoint / 1000d) * 1000L;
                    interval = (int)Math.Clamp(snapped, minInterval, maxInterval);
                }

                thumbnailCount = Math.Clamp(
                    (int)Math.Round(runtimeMs / (double)interval),
                    minThumbs,
                    maxThumbs);
            }
            else
            {
                interval = Math.Max(1000, options.Interval);
                thumbnailCount = maxThumbs;
            }

            var firstSize = _imageEncoder.GetImageSize(tiles[0].FullName);
            var thumbPxH = Math.Max(1, (int)Math.Ceiling((double)firstSize.Height / tileHeight));

            var info = new TrickplayInfo
            {
                ItemId = video.Id,
                Width = width,
                Interval = interval,
                TileWidth = tileWidth,
                TileHeight = tileHeight,
                ThumbnailCount = thumbnailCount,
                Height = thumbPxH,
                Bandwidth = 0,
            };

            foreach (var tile in tiles)
            {
                var bitrate = (int)Math.Ceiling((decimal)tile.Length * 8 / tileWidth / tileHeight / (interval / 1000m));
                info.Bandwidth = Math.Max(info.Bandwidth, bitrate);
            }

            await SaveTrickplayInfo(info).ConfigureAwait(false);
            _logger.LogInformation(
                "Discovered existing trickplay {Width} - {TileWidth}x{TileHeight} ({ThumbnailCount} thumbnails, {Interval}ms interval) for {Path}",
                width,
                tileWidth,
                tileHeight,
                thumbnailCount,
                interval,
                video.Path);
        }
    }

    /// <inheritdoc />
    public async Task RefreshTrickplayDataAsync(Video video, bool replace, LibraryOptions libraryOptions, CancellationToken cancellationToken)
    {
        var options = _config.Configuration.TrickplayOptions;
        if (!CanGenerateTrickplay(video, options.Interval) || libraryOptions is null)
        {
            return;
        }

        var saveWithMedia = libraryOptions.SaveTrickplayWithMedia;

        // Catalog any existing trickplay folders on disk before any prune/generate. This picks up
        // user-placed files even when their (width, tile dims) don't match the server's configured values.
        if (!replace)
        {
            await DiscoverExistingTrickplayAsync(video, saveWithMedia, cancellationToken).ConfigureAwait(false);
        }

        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var trickplayDirectory = _pathManager.GetTrickplayDirectory(video, saveWithMedia);

            // When extraction is disabled and files live next to media, treat them as user-managed:
            // discovery above already catalogued whatever is on disk, leave it alone.
            if (!libraryOptions.EnableTrickplayImageExtraction && !replace && saveWithMedia)
            {
                return;
            }

            if (!libraryOptions.EnableTrickplayImageExtraction || replace)
            {
                // Prune existing data
                if (Directory.Exists(trickplayDirectory))
                {
                    try
                    {
                        Directory.Delete(trickplayDirectory, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Unable to clear trickplay directory: {Directory}: {Exception}", trickplayDirectory, ex);
                    }
                }

                await dbContext.TrickplayInfos
                        .Where(i => i.ItemId.Equals(video.Id))
                        .ExecuteDeleteAsync(cancellationToken)
                        .ConfigureAwait(false);

                if (!replace)
                {
                    return;
                }
            }

            _logger.LogDebug("Trickplay refresh for {ItemId} (replace existing: {Replace})", video.Id, replace);

            if (options.Interval < 1000)
            {
                _logger.LogWarning("Trickplay image interval {Interval} is too small, reset to the minimum valid value of 1000", options.Interval);
                options.Interval = 1000;
            }

            foreach (var width in options.WidthResolutions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RefreshTrickplayDataInternal(
                    video,
                    replace,
                    width,
                    options,
                    saveWithMedia,
                    cancellationToken).ConfigureAwait(false);
            }

            // Cleanup old trickplay files
            if (Directory.Exists(trickplayDirectory))
            {
                var existingFolders = Directory.GetDirectories(trickplayDirectory);
                var trickplayInfos = await dbContext.TrickplayInfos
                        .AsNoTracking()
                        .Where(i => i.ItemId.Equals(video.Id))
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                var expectedFolders = trickplayInfos.Select(i => GetTrickplayDirectory(video, i.TileWidth, i.TileHeight, i.Width, saveWithMedia));
                var foldersToRemove = existingFolders.Except(expectedFolders);
                foreach (var folder in foldersToRemove)
                {
                    try
                    {
                        _logger.LogWarning("Pruning trickplay files for {Item}", video.Path);
                        Directory.Delete(folder, true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Unable to remove trickplay directory: {Directory}: {Exception}", folder, ex);
                    }
                }
            }
        }
    }

    private async Task RefreshTrickplayDataInternal(
        Video video,
        bool replace,
        int width,
        TrickplayOptions options,
        bool saveWithMedia,
        CancellationToken cancellationToken)
    {
        var imgTempDir = string.Empty;

        using (await _resourcePool.LockAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                // Extract images
                // Note: Media sources under parent items exist as their own video/item as well. Only use this video stream for trickplay.
                var mediaSource = video.GetMediaSources(false).FirstOrDefault(source => Guid.Parse(source.Id).Equals(video.Id));

                if (mediaSource is null)
                {
                    _logger.LogDebug("Found no matching media source for item {ItemId}", video.Id);
                    return;
                }

                var mediaPath = mediaSource.Path;
                if (!File.Exists(mediaPath))
                {
                    _logger.LogWarning("Media not found at {Path} for item {ItemID}", mediaPath, video.Id);
                    return;
                }

                // We support video backdrops, but we should not generate trickplay images for them
                var parentDirectory = Directory.GetParent(video.Path);
                if (parentDirectory is not null && string.Equals(parentDirectory.Name, "backdrops", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Ignoring backdrop media found at {Path} for item {ItemID}", video.Path, video.Id);
                    return;
                }

                // The width has to be even, otherwise a lot of filters will not be able to sample it
                var actualWidth = 2 * (width / 2);

                // Force using the video width when the trickplay setting has a too large width
                if (mediaSource.VideoStream.Width is not null && mediaSource.VideoStream.Width < width)
                {
                    _logger.LogWarning("Video width {VideoWidth} is smaller than trickplay setting {TrickPlayWidth}, using video width for thumbnails", mediaSource.VideoStream.Width, width);
                    actualWidth = 2 * ((int)mediaSource.VideoStream.Width / 2);
                }

                var tileWidth = options.TileWidth;
                var tileHeight = options.TileHeight;
                var outputDir = new DirectoryInfo(GetTrickplayDirectory(video, tileWidth, tileHeight, actualWidth, saveWithMedia));

                // Import existing trickplay tiles
                if (!replace && outputDir.Exists)
                {
                    var existingFiles = outputDir.GetFiles();
                    if (existingFiles.Length > 0)
                    {
                        var hasTrickplayResolution = await HasTrickplayResolutionAsync(video.Id, actualWidth).ConfigureAwait(false);
                        if (hasTrickplayResolution)
                        {
                            _logger.LogDebug("Found existing trickplay files for {ItemId}.", video.Id);
                            return;
                        }

                        // Tiles that were generated on demand are only a part of the set, importing them would
                        // store metadata that makes the missing tiles unreachable.
                        var expectedTiles = TrickplayLayout.GetExpectedTileCount(TimeSpan.FromTicks(video.RunTimeTicks ?? 0), options);
                        if (expectedTiles > existingFiles.Length)
                        {
                            _logger.LogDebug(
                                "Found only {TileCount} of {ExpectedTileCount} trickplay tiles for {ItemId}, generating the remaining tiles.",
                                existingFiles.Length,
                                expectedTiles,
                                video.Id);
                        }
                        else
                        {
                            // Import tiles
                            var localTrickplayInfo = new TrickplayInfo
                            {
                                ItemId = video.Id,
                                Width = width,
                                Interval = options.Interval,
                                TileWidth = options.TileWidth,
                                TileHeight = options.TileHeight,
                                ThumbnailCount = existingFiles.Length,
                                Height = 0,
                                Bandwidth = 0
                            };

                            foreach (var tile in existingFiles)
                            {
                                var image = _imageEncoder.GetImageSize(tile.FullName);
                                localTrickplayInfo.Height = Math.Max(localTrickplayInfo.Height, (int)Math.Ceiling((double)image.Height / localTrickplayInfo.TileHeight));
                                var bitrate = (int)Math.Ceiling((decimal)tile.Length * 8 / localTrickplayInfo.TileWidth / localTrickplayInfo.TileHeight / (localTrickplayInfo.Interval / 1000));
                                localTrickplayInfo.Bandwidth = Math.Max(localTrickplayInfo.Bandwidth, bitrate);
                            }

                            await SaveTrickplayInfo(localTrickplayInfo).ConfigureAwait(false);

                            _logger.LogDebug("Imported existing trickplay files for {ItemId}.", video.Id);
                            return;
                        }
                    }
                }

                // Generate trickplay tiles
                var mediaStream = mediaSource.VideoStream;
                var container = mediaSource.Container;

                // Checks for write permission before generating images
                if (saveWithMedia)
                {
                    _logger.LogDebug("Verifying write permission in media directory {OutputDir}", outputDir);
                    Directory.CreateDirectory(outputDir.FullName);
                    FileHelper.CreateEmpty(Path.Combine(outputDir.FullName, ".jellyfin-trickplay"));
                }

                _logger.LogInformation("Creating trickplay files at {Width} width, for {Path} [ID: {ItemId}]", actualWidth, mediaPath, video.Id);
                imgTempDir = await _mediaEncoder.ExtractVideoImagesOnIntervalAccelerated(
                    mediaPath,
                    container,
                    mediaSource,
                    mediaStream,
                    actualWidth,
                    TimeSpan.FromMilliseconds(options.Interval),
                    options.EnableHwAcceleration,
                    options.EnableHwEncoding,
                    options.ProcessThreads,
                    options.Qscale,
                    options.ProcessPriority,
                    options.EnableKeyFrameOnlyExtraction,
                    _encodingHelper,
                    cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrEmpty(imgTempDir) || !Directory.Exists(imgTempDir))
                {
                    throw new InvalidOperationException("Null or invalid directory from media encoder.");
                }

                var images = _fileSystem.GetFiles(imgTempDir, _trickplayImgExtensions, false, false)
                    .Select(i => i.FullName)
                    .OrderBy(i => i)
                    .ToList();

                // Create tiles
                var trickplayInfo = CreateTiles(images, actualWidth, options, outputDir.FullName);

                // Save tiles info
                try
                {
                    if (trickplayInfo is not null)
                    {
                        trickplayInfo.ItemId = video.Id;
                        await SaveTrickplayInfo(trickplayInfo).ConfigureAwait(false);

                        _logger.LogInformation("Finished creation of trickplay files for {0}", mediaPath);
                    }
                    else
                    {
                        throw new InvalidOperationException("Null trickplay tiles info from CreateTiles.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while saving trickplay tiles info.");

                    // Make sure no files stay in metadata folders on failure
                    // if tiles info wasn't saved.
                    outputDir.Delete(true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating trickplay images.");
            }
            finally
            {
                if (!string.IsNullOrEmpty(imgTempDir))
                {
                    Directory.Delete(imgTempDir, true);
                }
            }
        }
    }

    /// <inheritdoc />
    public TrickplayInfo CreateTiles(IReadOnlyList<string> images, int width, TrickplayOptions options, string outputDir)
    {
        if (images.Count == 0)
        {
            throw new ArgumentException("Can't create trickplay from 0 images.");
        }

        var workDir = Path.Combine(_appPaths.TempDirectory, "trickplay_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var trickplayInfo = new TrickplayInfo
            {
                Width = width,
                Interval = options.Interval,
                TileWidth = options.TileWidth,
                TileHeight = options.TileHeight,
                ThumbnailCount = images.Count,
                // Set during image generation
                Height = 0,
                Bandwidth = 0
            };

            /*
             * Generate trickplay tiles from sets of thumbnails
             */
            var imageOptions = new ImageCollageOptions
            {
                Width = trickplayInfo.TileWidth,
                Height = trickplayInfo.TileHeight
            };

            var thumbnailsPerTile = trickplayInfo.TileWidth * trickplayInfo.TileHeight;
            var requiredTiles = (int)Math.Ceiling((double)images.Count / thumbnailsPerTile);

            for (int i = 0; i < requiredTiles; i++)
            {
                // Set output/input paths
                var tilePath = Path.Combine(workDir, $"{i}.jpg");

                imageOptions.OutputPath = tilePath;
                imageOptions.InputPaths = images.Skip(i * thumbnailsPerTile).Take(Math.Min(thumbnailsPerTile, images.Count - (i * thumbnailsPerTile))).ToList();

                // Generate image and use returned height for tiles info
                var height = _imageEncoder.CreateTrickplayTile(imageOptions, options.JpegQuality, trickplayInfo.Width, trickplayInfo.Height != 0 ? trickplayInfo.Height : null);
                if (trickplayInfo.Height == 0)
                {
                    trickplayInfo.Height = height;
                }

                // Update bitrate
                var bitrate = (int)Math.Ceiling(new FileInfo(tilePath).Length * 8m / trickplayInfo.TileWidth / trickplayInfo.TileHeight / (trickplayInfo.Interval / 1000m));
                trickplayInfo.Bandwidth = Math.Max(trickplayInfo.Bandwidth, bitrate);
            }

            /*
             * Move trickplay tiles to output directory
             */
            Directory.CreateDirectory(Directory.GetParent(outputDir)!.FullName);

            // Replace existing tiles if they already exist
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }

            _fileSystem.MoveDirectory(workDir, outputDir);

            return trickplayInfo;
        }
        catch
        {
            Directory.Delete(workDir, true);
            throw;
        }
    }

    private bool CanGenerateTrickplay(Video video, int interval)
    {
        var videoType = video.VideoType;
        if (videoType == VideoType.Iso || videoType == VideoType.Dvd || videoType == VideoType.BluRay)
        {
            return false;
        }

        if (video.IsPlaceHolder)
        {
            return false;
        }

        if (video.IsShortcut)
        {
            return false;
        }

        if (!video.IsCompleteMedia)
        {
            return false;
        }

        if (!video.RunTimeTicks.HasValue || video.RunTimeTicks.Value < TimeSpan.FromMilliseconds(interval).Ticks)
        {
            return false;
        }

        // Can't extract images if there are no video streams
        return video.GetMediaStreams().Count > 0;
    }

    /// <inheritdoc />
    public async Task<Dictionary<int, TrickplayInfo>> GetTrickplayResolutions(Guid itemId)
    {
        var trickplayResolutions = new Dictionary<int, TrickplayInfo>();

        var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var trickplayInfos = await dbContext.TrickplayInfos
                .AsNoTracking()
                .Where(i => i.ItemId.Equals(itemId))
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (var info in trickplayInfos)
            {
                trickplayResolutions[info.Width] = info;
            }
        }

        return trickplayResolutions;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrickplayInfo>> GetTrickplayItemsAsync(int limit, int offset)
    {
        IReadOnlyList<TrickplayInfo> trickplayItems;

        var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            trickplayItems = await dbContext.TrickplayInfos
                .AsNoTracking()
                .OrderBy(i => i.ItemId)
                .Skip(offset)
                .Take(limit)
                .ToListAsync()
                .ConfigureAwait(false);
        }

        return trickplayItems;
    }

    /// <inheritdoc />
    public async Task SaveTrickplayInfo(TrickplayInfo info)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var oldInfo = await dbContext.TrickplayInfos.FindAsync(info.ItemId, info.Width).ConfigureAwait(false);
            if (oldInfo is not null)
            {
                dbContext.TrickplayInfos.Remove(oldInfo);
            }

            dbContext.Add(info);

            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DeleteTrickplayDataAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            await dbContext.TrickplayInfos.Where(i => i.ItemId.Equals(itemId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, Dictionary<int, TrickplayInfo>>> GetTrickplayManifest(BaseItem item)
    {
        var trickplayManifest = new Dictionary<string, Dictionary<int, TrickplayInfo>>();
        var mediaSources = item.GetMediaSources(false);
        var onDemandResolutions = CreateOnDemandResolutions(item, mediaSources);

        foreach (var mediaSource in mediaSources)
        {
            if (mediaSource.IsRemote || !Guid.TryParse(mediaSource.Id, out var mediaSourceId))
            {
                continue;
            }

            var trickplayResolutions = await GetTrickplayResolutions(mediaSourceId).ConfigureAwait(false);

            if (onDemandResolutions.TryGetValue(mediaSource.Id, out var onDemandResolution))
            {
                foreach (var (width, info) in onDemandResolution)
                {
                    trickplayResolutions.TryAdd(width, info);
                }
            }

            if (trickplayResolutions.Count > 0)
            {
                trickplayManifest[mediaSource.Id] = trickplayResolutions;
            }
        }

        return trickplayManifest;
    }

    /// <inheritdoc />
    public async Task<string> GetTrickplayTilePathAsync(BaseItem item, int width, int index, bool saveWithMedia)
    {
        var trickplayResolutions = await GetTrickplayResolutions(item.Id).ConfigureAwait(false);
        if (trickplayResolutions is not null && trickplayResolutions.TryGetValue(width, out var trickplayInfo))
        {
            return Path.Combine(GetTrickplayDirectory(item, trickplayInfo.TileWidth, trickplayInfo.TileHeight, width, saveWithMedia), index + ".jpg");
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public async Task<string?> GetTrickplayTilePathOnDemandAsync(BaseItem item, int width, int index, bool saveWithMedia, CancellationToken cancellationToken)
    {
        if (item is not Video video || !CanGenerateOnDemandTrickplay(video))
        {
            return null;
        }

        var options = _config.Configuration.TrickplayOptions;
        var trickplayInfo = await GetOnDemandInfoAsync(video, width).ConfigureAwait(false);
        if (trickplayInfo is null)
        {
            return null;
        }

        var path = Path.Combine(
            GetTrickplayDirectory(video, trickplayInfo.TileWidth, trickplayInfo.TileHeight, trickplayInfo.Width, saveWithMedia),
            index.ToString(CultureInfo.InvariantCulture) + ".jpg");

        if (File.Exists(path))
        {
            return path;
        }

        if (!TrickplayLayout.TryGetTileRange(trickplayInfo, index, out var range))
        {
            return null;
        }

        // Several requests (or several clients) can ask for the same tile at the same time, make sure it's only extracted once.
        using (await _onDemandTileLocks.LockAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (File.Exists(path))
            {
                return path;
            }

            try
            {
                return await GenerateTrickplayTileAsync(video, trickplayInfo, range, path, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating trickplay tile {Path} on demand", path);
                return null;
            }
        }
    }

    /// <inheritdoc />
    public async Task<TrickplayFrame?> GetTrickplayFrameAsync(BaseItem item, int width, TimeSpan position, CancellationToken cancellationToken)
    {
        if (item is not Video video || !CanGenerateOnDemandFrames(video))
        {
            return null;
        }

        var trickplayInfo = await GetOnDemandInfoAsync(video, width).ConfigureAwait(false);
        if (trickplayInfo is null)
        {
            return null;
        }

        // Snap the position to the thumbnail grid, so that a client that moves the mouse around
        // keeps requesting the same frame and the extraction plus the cache can be shared.
        var framePosition = TrickplayLayout.GetThumbnailPosition(trickplayInfo, position);
        var cacheKey = string.Create(
            CultureInfo.InvariantCulture,
            $"{video.Id:N}:{trickplayInfo.Width}:{framePosition.TotalMilliseconds:0}");

        if (TryGetCachedFrame(cacheKey, out var cachedFrame))
        {
            return cachedFrame;
        }

        // Moving the mouse over a slider can request the same frame several times at once.
        using (await _onDemandFrameLocks.LockAsync(cacheKey, cancellationToken).ConfigureAwait(false))
        {
            if (TryGetCachedFrame(cacheKey, out cachedFrame))
            {
                return cachedFrame;
            }

            try
            {
                var frame = await GenerateTrickplayFrameAsync(video, trickplayInfo, framePosition, cancellationToken).ConfigureAwait(false);
                if (frame is not null)
                {
                    AddFrameToCache(cacheKey, frame);
                }

                return frame;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating trickplay frame for {ItemId} at {Position}", video.Id, framePosition);
                return null;
            }
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetHlsPlaylist(Guid itemId, int width, string? apiKey)
    {
        var trickplayResolutions = await GetTrickplayResolutions(itemId).ConfigureAwait(false);
        if (trickplayResolutions is not null && trickplayResolutions.TryGetValue(width, out var trickplayInfo))
        {
            var builder = new StringBuilder(128);

            if (trickplayInfo.ThumbnailCount > 0)
            {
                const string urlFormat = "{0}.jpg?MediaSourceId={1}&ApiKey={2}";
                const string decimalFormat = "{0:0.###}";

                var resolution = $"{trickplayInfo.Width}x{trickplayInfo.Height}";
                var layout = $"{trickplayInfo.TileWidth}x{trickplayInfo.TileHeight}";
                var thumbnailsPerTile = trickplayInfo.TileWidth * trickplayInfo.TileHeight;
                var thumbnailDuration = trickplayInfo.Interval / 1000d;
                var infDuration = thumbnailDuration * thumbnailsPerTile;
                var tileCount = (int)Math.Ceiling((decimal)trickplayInfo.ThumbnailCount / thumbnailsPerTile);

                builder
                    .AppendLine("#EXTM3U")
                    .Append("#EXT-X-TARGETDURATION:")
                    .AppendLine(tileCount.ToString(CultureInfo.InvariantCulture))
                    .AppendLine("#EXT-X-VERSION:7")
                    .AppendLine("#EXT-X-MEDIA-SEQUENCE:1")
                    .AppendLine("#EXT-X-PLAYLIST-TYPE:VOD")
                    .AppendLine("#EXT-X-IMAGES-ONLY");

                for (int i = 0; i < tileCount; i++)
                {
                    // All tiles prior to the last must contain full amount of thumbnails (no black).
                    if (i == tileCount - 1)
                    {
                        thumbnailsPerTile = trickplayInfo.ThumbnailCount - (i * thumbnailsPerTile);
                        infDuration = thumbnailDuration * thumbnailsPerTile;
                    }

                    // EXTINF
                    builder
                        .Append("#EXTINF:")
                        .AppendFormat(CultureInfo.InvariantCulture, decimalFormat, infDuration)
                        .AppendLine(",");

                    // EXT-X-TILES
                    builder
                        .Append("#EXT-X-TILES:RESOLUTION=")
                        .Append(resolution)
                        .Append(",LAYOUT=")
                        .Append(layout)
                        .Append(",DURATION=")
                        .AppendFormat(CultureInfo.InvariantCulture, decimalFormat, thumbnailDuration)
                        .AppendLine();

                    // URL
                    builder
                        .AppendFormat(
                            CultureInfo.InvariantCulture,
                            urlFormat,
                            i.ToString(CultureInfo.InvariantCulture),
                            itemId.ToString("N"),
                            apiKey)
                        .AppendLine();
                }

                builder.AppendLine("#EXT-X-ENDLIST");
                return builder.ToString();
            }
        }

        return null;
    }

    /// <inheritdoc />
    public string GetTrickplayDirectory(BaseItem item, int tileWidth, int tileHeight, int width, bool saveWithMedia = false)
    {
        var path = _pathManager.GetTrickplayDirectory(item, saveWithMedia);
        var subdirectory = string.Format(
            CultureInfo.InvariantCulture,
            "{0} - {1}x{2}",
            width.ToString(CultureInfo.InvariantCulture),
            tileWidth.ToString(CultureInfo.InvariantCulture),
            tileHeight.ToString(CultureInfo.InvariantCulture));

        return Path.Combine(path, subdirectory);
    }

    [GeneratedRegex(@"^(\d+) - (\d+)x(\d+)$")]
    private static partial Regex TrickplaySubdirRegex();

    private static bool HasTrickplayTiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        return new DirectoryInfo(directory).EnumerateFiles("*.jpg").Any();
    }

    private async Task<bool> HasTrickplayResolutionAsync(Guid itemId, int width)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            return await dbContext.TrickplayInfos
                .AsNoTracking()
                .Where(i => i.ItemId.Equals(itemId))
                .AnyAsync(i => i.Width == width)
                .ConfigureAwait(false);
        }
    }

    private bool CanGenerateOnDemandTrickplay(Video video)
    {
        var options = _config.Configuration.TrickplayOptions;
        return options.EnableOnDemandExtraction
            && CanGenerateTrickplay(video, options.Interval)
            && _libraryManager.GetLibraryOptions(video).EnableTrickplayImageExtraction;
    }

    private bool CanGenerateOnDemandFrames(Video video)
    {
        var options = _config.Configuration.TrickplayOptions;

        // Frame extraction is an addition on top of on demand tiles, the tiles are what tells the
        // clients which resolutions exist, so both have to be enabled.
        return options.EnableOnDemandFrameExtraction && CanGenerateOnDemandTrickplay(video);
    }

    /// <summary>
    /// Gets the trickplay information of an item for a width, falling back to the information of the
    /// tiles that would be generated on demand when no tiles have been extracted yet.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="width">The width of a single thumbnail.</param>
    /// <returns>The trickplay information, or <c>null</c> when the width is not available.</returns>
    private async Task<TrickplayInfo?> GetOnDemandInfoAsync(Video video, int width)
    {
        var trickplayResolutions = await GetTrickplayResolutions(video.Id).ConfigureAwait(false);
        if (trickplayResolutions.TryGetValue(width, out var trickplayInfo))
        {
            return trickplayInfo;
        }

        var videoStream = GetOwnMediaSource(video)?.VideoStream;
        if (videoStream is null || video.RunTimeTicks is null)
        {
            return null;
        }

        return CreateOnDemandInfo(
            video.Id,
            TimeSpan.FromTicks(video.RunTimeTicks.Value),
            videoStream,
            width,
            _config.Configuration.TrickplayOptions);
    }

    /// <summary>
    /// Creates the resolutions that will be generated on demand for an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="mediaSources">The media sources of the item.</param>
    /// <returns>A map of media source id to a map of tile width to the not yet generated trickplay information.</returns>
    private Dictionary<string, Dictionary<int, TrickplayInfo>> CreateOnDemandResolutions(BaseItem item, IReadOnlyList<MediaSourceInfo> mediaSources)
    {
        var resolutions = new Dictionary<string, Dictionary<int, TrickplayInfo>>();
        var options = _config.Configuration.TrickplayOptions;
        if (!options.EnableOnDemandExtraction
            || item is not Video video
            || video.RunTimeTicks is null
            || !CanGenerateTrickplay(video, options.Interval)
            || !_libraryManager.GetLibraryOptions(video).EnableTrickplayImageExtraction)
        {
            return resolutions;
        }

        var ownMediaSource = FindOwnMediaSource(mediaSources, video.Id);
        var videoStream = ownMediaSource?.VideoStream;
        if (ownMediaSource is null || videoStream is null)
        {
            return resolutions;
        }

        var runtime = TimeSpan.FromTicks(video.RunTimeTicks.Value);
        var widths = new Dictionary<int, TrickplayInfo>();
        foreach (var width in options.WidthResolutions)
        {
            var info = CreateOnDemandInfo(video.Id, runtime, videoStream, width, options);
            if (info is not null)
            {
                widths.TryAdd(info.Width, info);
            }
        }

        if (widths.Count > 0)
        {
            resolutions[ownMediaSource.Id] = widths;
        }

        return resolutions;
    }

    /// <summary>
    /// Creates the trickplay information of tiles that are generated on demand.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="runtime">The runtime of the video.</param>
    /// <param name="videoStream">The video stream the thumbnails are extracted from.</param>
    /// <param name="requestedWidth">The width of a single thumbnail.</param>
    /// <param name="options">The trickplay options.</param>
    /// <returns>The trickplay information or <c>null</c> when the width is not configured.</returns>
    private static TrickplayInfo? CreateOnDemandInfo(Guid itemId, TimeSpan runtime, MediaStream videoStream, int requestedWidth, TrickplayOptions options)
    {
        var info = TrickplayLayout.CreateOnDemandInfo(itemId, runtime, videoStream, requestedWidth, options);
        if (info is null)
        {
            return null;
        }

        // Only expose the widths the server is configured for, so that arbitrary widths can't be used
        // to generate (and store) tiles that no client would ever request.
        var isConfiguredWidth = options.WidthResolutions
            .Select(configuredWidth => TrickplayLayout.GetThumbnailSize(videoStream, configuredWidth).Width)
            .Contains(info.Width);

        return isConfiguredWidth ? info : null;
    }

    /// <summary>
    /// Gets the media source that belongs to the item itself, the sources of the other versions are not used for trickplay.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <returns>The media source or <c>null</c>.</returns>
    private static MediaSourceInfo? GetOwnMediaSource(Video video)
    {
        return FindOwnMediaSource(video.GetMediaSources(false), video.Id);
    }

    private static MediaSourceInfo? FindOwnMediaSource(IReadOnlyList<MediaSourceInfo> mediaSources, Guid itemId)
    {
        return mediaSources.FirstOrDefault(source => Guid.TryParse(source.Id, out var sourceId) && sourceId.Equals(itemId));
    }

    private async Task<string?> GenerateTrickplayTileAsync(
        Video video,
        TrickplayInfo trickplayInfo,
        TrickplayTileRange range,
        string outputPath,
        TrickplayOptions options,
        CancellationToken cancellationToken)
    {
        var mediaSource = GetOwnMediaSource(video);
        var videoStream = mediaSource?.VideoStream;
        if (mediaSource is null
            || videoStream is null
            || string.IsNullOrEmpty(mediaSource.Path)
            || !File.Exists(mediaSource.Path))
        {
            _logger.LogDebug("Unable to generate trickplay tile {Path}, no playable media source found for {ItemId}", outputPath, video.Id);
            return null;
        }

        _logger.LogDebug(
            "Generating trickplay tile {Path} for {ItemId} on demand, thumbnails {FirstThumbnail}-{LastThumbnail}",
            outputPath,
            video.Id,
            range.FirstThumbnail,
            range.FirstThumbnail + range.ThumbnailCount - 1);

        var imagesDirectory = await _mediaEncoder.ExtractVideoImagesOnIntervalAccelerated(
            mediaSource.Path,
            mediaSource.Container,
            mediaSource,
            videoStream,
            trickplayInfo.Width,
            TimeSpan.FromMilliseconds(trickplayInfo.Interval),
            options.EnableHwAcceleration,
            options.EnableHwEncoding,
            options.ProcessThreads,
            options.Qscale,
            options.ProcessPriority,
            options.EnableKeyFrameOnlyExtraction,
            _encodingHelper,
            cancellationToken,
            range.Start,
            range.End).ConfigureAwait(false);

        try
        {
            if (string.IsNullOrEmpty(imagesDirectory) || !Directory.Exists(imagesDirectory))
            {
                _logger.LogWarning("No images were extracted for trickplay tile {Path}", outputPath);
                return null;
            }

            var images = _fileSystem.GetFiles(imagesDirectory, _trickplayImgExtensions, false, false)
                .Select(i => i.FullName)
                .OrderBy(i => i, StringComparer.Ordinal)
                .Take(range.ThumbnailCount)
                .ToList();

            if (images.Count == 0)
            {
                _logger.LogWarning("No images were extracted for trickplay tile {Path}", outputPath);
                return null;
            }

            if (images.Count < range.ThumbnailCount)
            {
                _logger.LogDebug(
                    "Only {ImageCount} of {ThumbnailCount} thumbnails were extracted for trickplay tile {Path}",
                    images.Count,
                    range.ThumbnailCount,
                    outputPath);
            }

            var workDirectory = Path.Combine(_appPaths.TempDirectory, "trickplay_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            try
            {
                var tilePath = Path.Combine(workDirectory, "tile.jpg");
                var tileHeight = _imageEncoder.CreateTrickplayTile(
                    new ImageCollageOptions
                    {
                        Width = trickplayInfo.TileWidth,
                        Height = trickplayInfo.TileHeight,
                        OutputPath = tilePath,
                        InputPaths = images
                    },
                    options.JpegQuality,
                    trickplayInfo.Width,
                    null);

                if (tileHeight != trickplayInfo.Height)
                {
                    _logger.LogWarning(
                        "Generated trickplay tile {Path} with a thumbnail height of {TileHeight} instead of the announced {AnnouncedHeight}",
                        outputPath,
                        tileHeight,
                        trickplayInfo.Height);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

                // Move the tile into place in a single operation, clients should never read a partially written tile.
                File.Move(tilePath, outputPath, true);
            }
            finally
            {
                TryDeleteDirectory(workDirectory);
            }

            return outputPath;
        }
        finally
        {
            TryDeleteDirectory(imagesDirectory);
        }
    }

    /// <summary>
    /// Extracts a single thumbnail for the requested position.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="trickplayInfo">The trickplay information.</param>
    /// <param name="position">The position of the thumbnail to extract.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The extracted frame, or <c>null</c> when nothing could be extracted.</returns>
    private async Task<TrickplayFrame?> GenerateTrickplayFrameAsync(
        Video video,
        TrickplayInfo trickplayInfo,
        TimeSpan position,
        CancellationToken cancellationToken)
    {
        var options = _config.Configuration.TrickplayOptions;
        var mediaSource = GetOwnMediaSource(video);
        var videoStream = mediaSource?.VideoStream;
        if (mediaSource is null
            || videoStream is null
            || string.IsNullOrEmpty(mediaSource.Path)
            || !File.Exists(mediaSource.Path))
        {
            _logger.LogDebug("Unable to generate trickplay frame for {ItemId}, no playable media source found", video.Id);
            return null;
        }

        _logger.LogDebug("Generating trickplay frame for {ItemId} at {Position} on demand", video.Id, position);

        var imagesDirectory = await _mediaEncoder.ExtractVideoImagesOnIntervalAccelerated(
            mediaSource.Path,
            mediaSource.Container,
            mediaSource,
            videoStream,
            trickplayInfo.Width,
            TimeSpan.FromMilliseconds(trickplayInfo.Interval),
            options.EnableHwAcceleration,
            options.EnableHwEncoding,
            options.ProcessThreads,
            options.Qscale,
            options.ProcessPriority,
            options.EnableKeyFrameOnlyExtraction,
            _encodingHelper,
            cancellationToken,
            position,
            null,
            1).ConfigureAwait(false);

        try
        {
            if (string.IsNullOrEmpty(imagesDirectory) || !Directory.Exists(imagesDirectory))
            {
                _logger.LogDebug("No image was extracted for trickplay frame of {ItemId} at {Position}", video.Id, position);
                return null;
            }

            var imagePath = _fileSystem.GetFiles(imagesDirectory, _trickplayImgExtensions, false, false)
                .Select(i => i.FullName)
                .OrderBy(i => i, StringComparer.Ordinal)
                .FirstOrDefault();

            if (imagePath is null)
            {
                _logger.LogDebug("No image was extracted for trickplay frame of {ItemId} at {Position}", video.Id, position);
                return null;
            }

            var data = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
            return new TrickplayFrame(data, position);
        }
        finally
        {
            TryDeleteDirectory(imagesDirectory);
        }
    }

    private static bool TryGetCachedFrame(string key, out TrickplayFrame? frame)
    {
        lock (_frameCacheLock)
        {
            return _frameCache.TryGetValue(key, out frame);
        }
    }

    private static void AddFrameToCache(string key, TrickplayFrame frame)
    {
        lock (_frameCacheLock)
        {
            if (_frameCache.ContainsKey(key))
            {
                return;
            }

            _frameCache[key] = frame;
            _frameCacheOrder.Enqueue(key);

            while (_frameCacheOrder.Count > MaxCachedFrames && _frameCacheOrder.TryDequeue(out var oldest))
            {
                _frameCache.Remove(oldest);
            }
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Unable to delete directory {Path}: {Exception}", path, ex);
        }
    }
}
