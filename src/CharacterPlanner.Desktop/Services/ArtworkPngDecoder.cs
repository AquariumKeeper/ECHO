using Avalonia.Media.Imaging;
using CharacterPlanner.Application.Artwork;
using System.Runtime.InteropServices;

namespace CharacterPlanner.Desktop.Services;

/// <summary>Decodes preflighted PNGs using the same image implementation as the artwork views, without retaining bitmaps.</summary>
public sealed class ArtworkPngDecoder : IArtworkImageDecoder
{
    public ValueTask<ArtworkImageSize?> DecodeAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken) => new(Task.Run<ArtworkImageSize?>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var bytes = MemoryMarshal.TryGetArray(png, out var segment) ? segment : new ArraySegment<byte>(png.ToArray());
            using var stream = new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false);
            using var bitmap = new Bitmap(stream);
            cancellationToken.ThrowIfCancellationRequested();
            return new(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        { return null; }
    }, cancellationToken));
}
