using System.Text.Json;

namespace CharacterPlanner.Desktop.Services;

/// <summary>
/// The window's last size, kept between runs as the Go app kept it. A missing or unreadable file means the default
/// size; failing to save it never stops the app closing.
/// </summary>
public static class WindowSize
{
    private sealed record Saved(double Width, double Height);

    public static (double Width, double Height)? Load(string path)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path));
            return saved is { Width: >= 400 and <= 20000, Height: >= 300 and <= 20000 } ? (saved.Width, saved.Height) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Save(string path, double width, double height)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new Saved(width, height)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
