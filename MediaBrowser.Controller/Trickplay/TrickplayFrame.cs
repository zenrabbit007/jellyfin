using System;

namespace MediaBrowser.Controller.Trickplay;

/// <summary>
/// A preview frame that was extracted for a single playback position.
/// </summary>
public sealed class TrickplayFrame
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayFrame"/> class.
    /// </summary>
    /// <param name="data">The encoded jpeg data.</param>
    /// <param name="position">The position in the video the frame was extracted at.</param>
    public TrickplayFrame(ReadOnlyMemory<byte> data, TimeSpan position)
    {
        Data = data;
        Position = position;
    }

    /// <summary>
    /// Gets the encoded jpeg data.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// Gets the position in the video the frame was extracted at.
    /// </summary>
    public TimeSpan Position { get; }
}
