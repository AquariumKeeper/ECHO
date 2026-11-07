using System.Buffers.Binary;

namespace CharacterPlanner.Application.Artwork;

/// <summary>Shared allocation bounds for user PNGs; full container checks and decoding are separate.</summary>
public static class ArtworkPngHeader
{
    public static ArtworkImageSize? Inspect(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10]) ||
            BinaryPrimitives.ReadUInt32BigEndian(png[8..]) != 13 || !png.Slice(12, 4).SequenceEqual("IHDR"u8)) { return null; }
        var width = BinaryPrimitives.ReadUInt32BigEndian(png[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(png[20..]);
        return width is 0 or > ArtworkLimits.ImageDimension || height is 0 or > ArtworkLimits.ImageDimension ||
            (long)width * height > ArtworkLimits.ImagePixels ? null : new((int)width, (int)height);
    }
}
