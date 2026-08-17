namespace Core.Contracts;

/// <summary>
/// Published by the Edge pipeline for every plate read. Deliberately excludes the crop
/// image: embedding base64 image data here would bloat the hot-path message and blow the
/// &lt;300ms budget. The image is uploaded out-of-band and referenced by <see cref="ImageReference"/>.
/// </summary>
public sealed record PlateReadEvent
{
    public Guid EventId { get; init; }
    public string PlateText { get; init; } = default!;
    public string CameraId { get; init; } = default!;
    public DateTime TimestampUtc { get; init; }
    public double Confidence { get; init; }

    /// <summary>Key/URL of the plate crop image, uploaded asynchronously by the Edge node. Null until available.</summary>
    public string? ImageReference { get; init; }
}
