using System.Buffers.Binary;
using CharacterPlanner.Application.Artwork;

namespace CharacterPlanner.Infrastructure.Artwork;

/// <summary>Bounds dimensions and checks the complete static PNG container before a decoder allocates pixels.</summary>
internal static class ArtworkPngPreflight
{
    public static ArtworkImageSize? Inspect(ReadOnlySpan<byte> png)
    {
        var header = ArtworkPngHeader.Inspect(png);
        if (png.Length < 33 || header is null) { return null; }
        var position = 8;
        ArtworkImageSize? size = null;
        var hasData = false;
        while (position <= png.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            if (length > png.Length - position - 12) { return null; }
            var count = (int)length;
            var chunk = png.Slice(position + 4, count + 4);
            var kind = chunk[..4];
            if (ArtworkCrc32.Calculate(chunk) != BinaryPrimitives.ReadUInt32BigEndian(png[(position + 8 + count)..])) { return null; }
            if (size is null)
            {
                if (!kind.SequenceEqual("IHDR"u8) || count != 13) { return null; }
                size = header;
            }
            else if (kind.SequenceEqual("IHDR"u8)) { return null; }
            if (kind.SequenceEqual("acTL"u8) || kind.SequenceEqual("fcTL"u8) || kind.SequenceEqual("fdAT"u8)) { return null; }
            if (kind.SequenceEqual("IDAT"u8)) { hasData = true; }
            position += count + 12;
            if (kind.SequenceEqual("IEND"u8)) { return count == 0 && position == png.Length && hasData ? size : null; }
        }
        return null;
    }

}

internal static class ArtworkCrc32
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static uint Calculate(ReadOnlySpan<byte> bytes)
    {
        var value = uint.MaxValue;
        foreach (var item in bytes) { value = CrcTable[(value ^ item) & 255] ^ (value >> 8); }
        return value ^ uint.MaxValue;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) { value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320u; }
            table[index] = value;
        }
        return table;
    }
}
