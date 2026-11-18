using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.Implementations.Tests.Item;
using Jellyfin.Server.Implementations.Trickplay;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Trickplay;

public sealed class TrickplayManagerOnDemandTests : SqliteDbTestFixture
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "jmp-trickplay-" + Guid.NewGuid().ToString("N"));
    private readonly List<(TimeSpan? Start, TimeSpan? End, int? FrameLimit)> _extractions = new();

    private readonly Mock<IMediaEncoder> _mediaEncoder = new();
    private readonly Mock<IImageEncoder> _imageEncoder = new();
    private readonly Mock<IFileSystem> _fileSystem = new();

    public TrickplayManagerOnDemandTests()
    {
        Directory.CreateDirectory(_tempDirectory);

        _mediaEncoder
            .Setup(e => e.ExtractVideoImagesOnIntervalAccelerated(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<MediaSourceInfo>(),
                It.IsAny<MediaStream>(),
                It.IsAny<int>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<ProcessPriorityClass?>(),
                It.IsAny<bool>(),
                It.IsAny<EncodingHelper>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<int?>()))
            .Returns((
                string inputFile,
                string container,
                MediaSourceInfo mediaSource,
                MediaStream videoStream,
                int maxWidth,
                TimeSpan interval,
                bool allowHwAccel,
                bool enableHwEncoding,
                int? threads,
                int? qualityScale,
                ProcessPriorityClass? priority,
                bool enableKeyFrameOnlyExtraction,
                EncodingHelper encodingHelper,
                CancellationToken cancellationToken,
                TimeSpan? startTime,
                TimeSpan? endTime,
                int? frameLimit) =>
            {
                _extractions.Add((startTime, endTime, frameLimit));

                var directory = Path.Combine(_tempDirectory, "extract-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);

                var window = (endTime ?? TimeSpan.Zero) - (startTime ?? TimeSpan.Zero);
                var count = frameLimit ?? Math.Max(1, (int)Math.Round(window.TotalMilliseconds / interval.TotalMilliseconds));
                for (var i = 1; i <= count; i++)
                {
                    WriteFile(Path.Combine(directory, i.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".jpg"));
                }

                return Task.FromResult(directory);
            });

        _imageEncoder
            .Setup(e => e.CreateTrickplayTile(It.IsAny<ImageCollageOptions>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int?>()))
            .Returns((ImageCollageOptions options, int quality, int imgWidth, int? imgHeight) =>
            {
                WriteFile(options.OutputPath);
                return 180;
            });

        _fileSystem
            .Setup(f => f.GetFiles(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns((string path, IReadOnlyList<string>? extensions, bool enableCaseSensitiveExtensions, bool recursive) =>
                Directory.GetFiles(path).Select(file => new FileSystemMetadata { FullName = file }).ToList());
    }

    [Fact]
    public async Task GetTrickplayTilePathOnDemandAsync_GeneratesTileForRequestedWindow()
    {
        var (manager, video) = CreateManager();

        var path = await manager.GetTrickplayTilePathOnDemandAsync(video, 320, 0, false, CancellationToken.None);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(Path.Combine(_tempDirectory, "320 - 10x10", "0.jpg"), path);

        var extraction = Assert.Single(_extractions);
        Assert.Equal(TimeSpan.Zero, extraction.Start!.Value);
        Assert.Equal(TimeSpan.FromSeconds(600), extraction.End!.Value);
    }

    [Fact]
    public async Task GetTrickplayTilePathOnDemandAsync_GeneratesOnlyOncePerTile()
    {
        var (manager, video) = CreateManager();

        var first = await manager.GetTrickplayTilePathOnDemandAsync(video, 320, 0, false, CancellationToken.None);
        var second = await manager.GetTrickplayTilePathOnDemandAsync(video, 320, 0, false, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Single(_extractions);
    }

    [Fact]
    public async Task GetTrickplayTilePathOnDemandAsync_ReturnsNullForTileOutsideTheVideo()
    {
        var (manager, video) = CreateManager();

        var path = await manager.GetTrickplayTilePathOnDemandAsync(video, 320, 1, false, CancellationToken.None);

        Assert.Null(path);
        Assert.Empty(_extractions);
    }

    [Fact]
    public async Task GetTrickplayTilePathOnDemandAsync_ReturnsNullForUnconfiguredWidth()
    {
        var (manager, video) = CreateManager();

        var path = await manager.GetTrickplayTilePathOnDemandAsync(video, 640, 0, false, CancellationToken.None);

        Assert.Null(path);
        Assert.Empty(_extractions);
    }

    [Fact]
    public async Task GetTrickplayFrameAsync_ExtractsFrameAtThumbnailPosition()
    {
        var (manager, video) = CreateManager();

        var frame = await manager.GetTrickplayFrameAsync(video, 320, TimeSpan.FromSeconds(12.5), CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(TimeSpan.FromSeconds(10), frame.Position);
        Assert.NotEmpty(frame.Data.ToArray());

        var extraction = Assert.Single(_extractions);
        Assert.Equal(TimeSpan.FromSeconds(10), extraction.Start!.Value);
        Assert.Null(extraction.End);
        Assert.Equal(1, extraction.FrameLimit);
    }

    [Fact]
    public async Task GetTrickplayFrameAsync_ExtractsOnlyOncePerThumbnail()
    {
        var (manager, video) = CreateManager();

        var first = await manager.GetTrickplayFrameAsync(video, 320, TimeSpan.FromSeconds(11), CancellationToken.None);
        var second = await manager.GetTrickplayFrameAsync(video, 320, TimeSpan.FromSeconds(19), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Position, second.Position);
        Assert.Single(_extractions);
    }

    [Fact]
    public async Task GetTrickplayFrameAsync_UsesThePreviousThumbnailNearTheEnd()
    {
        var (manager, video) = CreateManager(runtime: TimeSpan.FromSeconds(605));

        // The last bucket starts at 600s, but only 5s of video are left at that point, so the
        // extraction has to happen at the thumbnail before it.
        var frame = await manager.GetTrickplayFrameAsync(video, 320, TimeSpan.FromMinutes(20), CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(TimeSpan.FromSeconds(590), frame.Position);
        Assert.Equal(TimeSpan.FromSeconds(590), Assert.Single(_extractions).Start!.Value);
    }

    [Fact]
    public async Task GetTrickplayFrameAsync_IsEnabledWithoutConfiguration()
    {
        var (manager, video) = CreateManager(enableFrameExtraction: null);

        var frame = await manager.GetTrickplayFrameAsync(video, 320, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(frame);
    }

    [Fact]
    public async Task GetTrickplayFrameAsync_DoesNotRequireTheLibraryTrickplaySetting()
    {
        var (manager, video) = CreateManager(enableFrameExtraction: true, enableTileExtraction: false, libraryAllowsTrickplay: false);

        var frame = await manager.GetTrickplayFrameAsync(video, 320, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(frame);
    }

    [Fact]
    public async Task GetTrickplayManifest_AdvertisesFramesForALibraryWithoutTrickplay()
    {
        var (manager, video) = CreateManager(enableFrameExtraction: true, enableTileExtraction: false, libraryAllowsTrickplay: false);

        var manifest = await manager.GetTrickplayManifest(video);

        Assert.True(manifest.TryGetValue(video.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture), out var resolutions));
        Assert.True(resolutions.ContainsKey(320));
    }

    [Fact]
    public async Task GetTrickplayTilePathOnDemandAsync_RequiresTheLibraryTrickplaySetting()
    {
        var (manager, video) = CreateManager(enableFrameExtraction: true, enableTileExtraction: true, libraryAllowsTrickplay: false);

        var path = await manager.GetTrickplayTilePathOnDemandAsync(video, 320, 0, false, CancellationToken.None);

        Assert.Null(path);
        Assert.Empty(_extractions);
    }

    [Fact]
    public async Task GetTrickplayFrameAsync_DoesNotRequireOnDemandTiles()
    {
        var (manager, video) = CreateManager(enableFrameExtraction: true, enableTileExtraction: false);

        var frame = await manager.GetTrickplayFrameAsync(video, 320, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(frame);
    }

    [Fact]
    public async Task GetTrickplayManifest_AdvertisesResolutionsWithoutOnDemandTiles()
    {
        var (manager, video) = CreateManager(enableFrameExtraction: true, enableTileExtraction: false);

        var manifest = await manager.GetTrickplayManifest(video);

        Assert.True(manifest.TryGetValue(video.Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture), out var resolutions));
        Assert.True(resolutions.ContainsKey(320));
    }

    [Fact]
    public async Task GetTrickplayManifest_IsEmptyWithoutAnyOnDemandExtraction()
    {
        var (manager, video) = CreateManager(enableFrameExtraction: false, enableTileExtraction: false);

        var manifest = await manager.GetTrickplayManifest(video);

        Assert.Empty(manifest);
    }

    [Fact]
    public async Task GetTrickplayFrameAsync_ReturnsNullWhenFrameExtractionIsDisabled()
    {
        var (manager, video) = CreateManager(enableFrameExtraction: false);

        var frame = await manager.GetTrickplayFrameAsync(video, 320, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Null(frame);
        Assert.Empty(_extractions);
    }

    [Fact]
    public async Task GetTrickplayFrameAsync_ReturnsNullForUnconfiguredWidth()
    {
        var (manager, video) = CreateManager();

        var frame = await manager.GetTrickplayFrameAsync(video, 640, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Null(frame);
        Assert.Empty(_extractions);
    }

    private (TrickplayManager Manager, Video Video) CreateManager(
        bool? enableFrameExtraction = true,
        bool enableTileExtraction = true,
        bool libraryAllowsTrickplay = true,
        TimeSpan? runtime = null)
    {
        var itemId = Guid.NewGuid();
        var videoStream = new MediaStream { Type = MediaStreamType.Video, Width = 1920, Height = 1080 };

        var mediaSource = new MediaSourceInfo
        {
            Id = itemId.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
            Path = Path.Combine(_tempDirectory, "video.mp4"),
            Container = "mp4",
            MediaStreams = new[] { videoStream }
        };
        WriteFile(mediaSource.Path);

        var video = new Mock<Video>();
        video.Object.Id = itemId;
        video.Object.VideoType = VideoType.VideoFile;
        video.Object.RunTimeTicks = (runtime ?? TimeSpan.FromMinutes(10)).Ticks;

        video.Setup(v => v.GetMediaSources(It.IsAny<bool>())).Returns(new[] { mediaSource });
        video.Setup(v => v.GetMediaStreams()).Returns(new[] { videoStream });

        var trickplayOptions = new TrickplayOptions
        {
            EnableOnDemandExtraction = enableTileExtraction,
            Interval = 10000,
            TileWidth = 10,
            TileHeight = 10,
            WidthResolutions = [320]
        };

        if (enableFrameExtraction.HasValue)
        {
            trickplayOptions.EnableOnDemandFrameExtraction = enableFrameExtraction.Value;
        }

        var configuration = new ServerConfiguration
        {
            TrickplayOptions = trickplayOptions
        };

        var configManager = new Mock<IServerConfigurationManager>();
        configManager.Setup(c => c.Configuration).Returns(configuration);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>()))
            .Returns(new LibraryOptions { EnableTrickplayImageExtraction = libraryAllowsTrickplay });

        var pathManager = new Mock<IPathManager>();
        pathManager.Setup(p => p.GetTrickplayDirectory(It.IsAny<BaseItem>(), It.IsAny<bool>())).Returns(_tempDirectory);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.SetupGet(a => a.TempDirectory).Returns(_tempDirectory);

        var dbFactory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => CreateDbContext());

        var manager = new TrickplayManager(
            NullLogger<TrickplayManager>.Instance,
            _mediaEncoder.Object,
            _fileSystem.Object,
            null!,
            configManager.Object,
            _imageEncoder.Object,
            dbFactory.Object,
            appPaths.Object,
            pathManager.Object,
            libraryManager.Object);

        return (manager, video.Object);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
    }
}
