using System;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Trickplay;

public sealed class TrickplayLayoutTests
{
    private static readonly TrickplayOptions _options = new()
    {
        Interval = 10000,
        TileWidth = 10,
        TileHeight = 10,
        WidthResolutions = [320]
    };

    [Fact]
    public void CreateOnDemandInfo_CalculatesThumbnailsAndSize()
    {
        var info = TrickplayLayout.CreateOnDemandInfo(
            Guid.NewGuid(),
            TimeSpan.FromMinutes(10),
            new MediaStream { Width = 1920, Height = 1080 },
            320,
            _options);

        Assert.NotNull(info);
        Assert.Equal(320, info.Width);
        Assert.Equal(180, info.Height);
        Assert.Equal(60, info.ThumbnailCount);
        Assert.Equal(_options.Interval, info.Interval);
        Assert.Equal(_options.TileWidth, info.TileWidth);
        Assert.Equal(_options.TileHeight, info.TileHeight);
        Assert.Equal(0, info.Bandwidth);
    }

    [Fact]
    public void CreateOnDemandInfo_UsesVideoWidthWhenNarrowerThanRequested()
    {
        var info = TrickplayLayout.CreateOnDemandInfo(
            Guid.NewGuid(),
            TimeSpan.FromMinutes(1),
            new MediaStream { Width = 200, Height = 100 },
            320,
            _options);

        Assert.NotNull(info);
        Assert.Equal(200, info.Width);
        Assert.Equal(100, info.Height);
    }

    [Fact]
    public void CreateOnDemandInfo_SwapsDimensionsOfRotatedVideo()
    {
        var info = TrickplayLayout.CreateOnDemandInfo(
            Guid.NewGuid(),
            TimeSpan.FromMinutes(1),
            new MediaStream { Width = 1920, Height = 1080, Rotation = 90 },
            320,
            _options);

        Assert.NotNull(info);
        Assert.Equal(320, info.Width);
        Assert.Equal(568, info.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateOnDemandInfo_ReturnsNullWithoutRuntime(int runtimeSeconds)
    {
        var info = TrickplayLayout.CreateOnDemandInfo(
            Guid.NewGuid(),
            TimeSpan.FromSeconds(runtimeSeconds),
            new MediaStream { Width = 1920, Height = 1080 },
            320,
            _options);

        Assert.Null(info);
    }

    [Fact]
    public void CreateOnDemandInfo_ReturnsNullWithoutVideoDimensions()
    {
        var info = TrickplayLayout.CreateOnDemandInfo(
            Guid.NewGuid(),
            TimeSpan.FromMinutes(1),
            new MediaStream(),
            320,
            _options);

        Assert.Null(info);
    }

    [Fact]
    public void TryGetTileRange_ReturnsFullTile()
    {
        var info = CreateInfo(thumbnailCount: 1000);

        Assert.True(TrickplayLayout.TryGetTileRange(info, 1, out var range));
        Assert.Equal(100, range.FirstThumbnail);
        Assert.Equal(100, range.ThumbnailCount);
        Assert.Equal(TimeSpan.FromSeconds(1000), range.Start);
        Assert.Equal(TimeSpan.FromSeconds(2000), range.End);
    }

    [Fact]
    public void TryGetTileRange_ReturnsPartialLastTile()
    {
        var info = CreateInfo(thumbnailCount: 105);

        Assert.True(TrickplayLayout.TryGetTileRange(info, 1, out var range));
        Assert.Equal(100, range.FirstThumbnail);
        Assert.Equal(5, range.ThumbnailCount);
        Assert.Equal(TimeSpan.FromSeconds(1000), range.Start);
        Assert.Equal(TimeSpan.FromSeconds(1050), range.End);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10)]
    [InlineData(int.MaxValue)]
    public void TryGetTileRange_ReturnsFalseForOutOfRangeIndex(int index)
    {
        var info = CreateInfo(thumbnailCount: 1000);

        Assert.False(TrickplayLayout.TryGetTileRange(info, index, out _));
    }

    [Fact]
    public void TryGetTileRange_ReturnsFalseForInvalidInfo()
    {
        Assert.False(TrickplayLayout.TryGetTileRange(CreateInfo(thumbnailCount: 0), 0, out _));
        Assert.False(TrickplayLayout.TryGetTileRange(CreateInfo(thumbnailCount: 10, interval: 0), 0, out _));
    }

    private static TrickplayInfo CreateInfo(int thumbnailCount, int interval = 10000)
    {
        return new TrickplayInfo
        {
            ItemId = Guid.NewGuid(),
            Width = 320,
            Height = 180,
            TileWidth = 10,
            TileHeight = 10,
            ThumbnailCount = thumbnailCount,
            Interval = interval
        };
    }
}
