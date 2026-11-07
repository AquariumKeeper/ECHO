using CharacterPlanner.Desktop.Services;

namespace CharacterPlanner.Desktop.Tests;

/// <summary>The window's size kept between runs (F56), the Go app's only preference.</summary>
public sealed class WindowSizeTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "window-size-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(folder, "nested", "window.json");

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ASavedSizeComesBack()
    {
        WindowSize.Save(File, 1500, 900);
        Assert.Equal((1500, 900), WindowSize.Load(File));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"Width":10,"Height":900}""")]
    [InlineData("""{"Width":1500,"Height":99999}""")]
    public void AMissingUnreadableOrImplausibleSizeMeansTheDefault(string? content)
    {
        if (content is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, content);
        }
        Assert.Null(WindowSize.Load(File));
    }
}
