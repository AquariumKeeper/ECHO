using System.Buffers.Text;

namespace CharacterPlanner.Core.Builds;

/// <summary>Limits and checks shared by the app's share code and the legacy (v1–v12) importer.</summary>
internal static class ShareCodeWrapping
{
    public const int MaxCodeLength = 64 * 1024;
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>The code without the whitespace a paste brings, rejected when empty or too long.</summary>
    public static string Trim(string code)
    {
        var text = code.Trim();
        if (text.Length == 0) { throw new ShareCodeException("share code is empty"); }
        return text.Length <= MaxCodeLength ? text : throw new ShareCodeException($"share code is longer than {MaxCodeLength} characters");
    }

    public static byte[] Base64(ReadOnlySpan<char> text)
    {
        try { return Base64Url.DecodeFromChars(text); }
        catch (FormatException exception) { throw new ShareCodeException("share code is not base64url text", exception); }
    }

    /// <summary>Reads a decompression stream to its end, refusing more than <see cref="MaxPayloadBytes"/>.</summary>
    public static byte[] Inflate(Stream decompressor, string damaged)
    {
        using var output = new MemoryStream();
        var chunk = new byte[16 * 1024];
        try
        {
            for (int read; output.Length <= MaxPayloadBytes && (read = decompressor.Read(chunk)) > 0;) { output.Write(chunk, 0, read); }
        }
        catch (InvalidDataException exception) { throw new ShareCodeException(damaged, exception); }
        if (output.Length > MaxPayloadBytes) { throw new ShareCodeException($"share code expands to more than {MaxPayloadBytes} bytes"); }
        return output.ToArray();
    }
}
