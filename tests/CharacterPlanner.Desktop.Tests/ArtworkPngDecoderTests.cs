using Avalonia.Headless.XUnit;
using CharacterPlanner.Application.Artwork;
using CharacterPlanner.Desktop.Services;
using CharacterPlanner.Core.Packs;
using CharacterPlanner.Infrastructure.Artwork;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CharacterPlanner.Desktop.Tests;

public sealed class ArtworkPngDecoderTests
{
    [AvaloniaFact]
    public async Task DecodesTheExamplePngWithoutRetainingABitmap()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP4X8/wHwAGfQJ+unzLBwAAAABJRU5ErkJggg==");
        Assert.Equal(new ArtworkImageSize(1, 1), await new ArtworkPngDecoder().DecodeAsync(png, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task CancellationPropagatesBeforeDecoding()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArtworkPngDecoder().DecodeAsync(ReadOnlyMemory<byte>.Empty, cancellation.Token).AsTask());
    }

    [AvaloniaFact]
    public async Task InstallerAndDesktopDecoderValidateAPreparedZipTogether()
    {
        var root = Path.Combine(Path.GetTempPath(), "artwork-decoder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP4X8/wHwAGfQJ+unzLBwAAAABJRU5ErkJggg==");
            var manifest = new
            {
                schemaVersion = 1,
                id = "example.native",
                name = "Native decoder smoke",
                version = "1",
                author = "Example",
                gamePackId = "division_2",
                credits = new[] { new { author = "Example", license = "MIT", licenseFile = "licenses/LICENSE.txt" } },
                assets = new[] { new { id = "image", path = "assets/image.png", sha256 = Convert.ToHexStringLower(SHA256.HashData(png)) } },
                bindings = new { icons = new { slots = new { mask = "image" } } }
            };
            var files = new Dictionary<string, byte[]>
            {
                ["artwork.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest),
                ["assets/image.png"] = png,
                ["licenses/LICENSE.txt"] = Encoding.UTF8.GetBytes("MIT fixture notice")
            };
            var path = Path.Combine(root, "sample.zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var (name, bytes) in files)
                { using var output = archive.CreateEntry(name).Open(); output.Write(bytes); }
            }
            var result = await new ArtworkPackInstaller(Path.Combine(root, "store"), new ArtworkPngDecoder()).InstallAsync(path,
                new GamePack { ID = "division_2", Slots = [new() { ID = "mask", Name = "Mask" }] }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(value => value.Message)));
            Assert.Equal(png, File.ReadAllBytes(Path.Combine(result.Installed!.Directory, "assets/image.png")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
