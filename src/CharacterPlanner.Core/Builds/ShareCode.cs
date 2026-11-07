using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace CharacterPlanner.Core.Builds;

public sealed class ShareCodeException(string message, Exception? inner = null) : FormatException(message, inner);

/// <summary>
/// The app's own share code (ADR 0002): <c>D2P1.</c> followed by base64url (no padding) of zlib-compressed,
/// canonical JSON that names everything by id. The zlib trailer's Adler-32 checksum rejects damaged pastes.
/// </summary>
/// <remarks>
/// The payload is the canonical build object (<see cref="BuildJson"/>) without the party: compact, keys in
/// ordinal order, empty collections omitted and text escaped only where JSON requires, so equal builds give
/// equal payloads. A slot's item or skill is written whenever the build has one, even an empty one, and
/// upgrades, Prototypes and skill grades or mods record their own item or skill id only when it differs from
/// the slot's selection, which keeps the code lossless for the resolver to diagnose. The compressed bytes may
/// differ between runtimes; the payload does not.
/// </remarks>
public static class ShareCode
{
    public const string Prefix = "D2P";
    public const int Version = 1;
    public const int MaxCodeLength = ShareCodeWrapping.MaxCodeLength;
    public const int MaxPayloadBytes = ShareCodeWrapping.MaxPayloadBytes;

    /// <summary>True when the text is in this app's format (any version), rather than a legacy code: base64url never contains a dot.</summary>
    public static bool IsAppCode(string code)
    {
        var text = code.AsSpan().Trim();
        var dot = text.IndexOf('.');
        return dot > Prefix.Length && text.StartsWith(Prefix, StringComparison.Ordinal) && !text[Prefix.Length..dot].ContainsAnyExceptInRange('0', '9');
    }

    /// <summary>Encodes a build. A build without a pack id, or with a non-finite number or invalid text, is a caller error.</summary>
    /// <exception cref="ArgumentException">The build can't be shared; the message names the field.</exception>
    public static string Encode(BuildSnapshot build)
    {
        ArgumentNullException.ThrowIfNull(build);
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize)) { zlib.Write(Payload(build)); }
        return $"{Prefix}{Version}." + Base64Url.EncodeToString(output.ToArray());
    }

    /// <summary>Decodes a code in this app's format; surrounding whitespace from a paste is ignored.</summary>
    public static BuildSnapshot Decode(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var text = ShareCodeWrapping.Trim(code);
        if (!IsAppCode(text)) { throw Error($"share code does not start with {Prefix}{Version}."); }
        var dot = text.IndexOf('.');
        var digits = text[Prefix.Length..dot];
        if (digits != Version.ToString(CultureInfo.InvariantCulture))
        {
            var newer = digits[0] != '0' && (digits.Length > 9 || int.Parse(digits, CultureInfo.InvariantCulture) > Version);
            throw Error(newer ? "share code was made by a newer version of the planner" : $"unsupported share code version {digits}");
        }
        return Read(Inflate(ShareCodeWrapping.Base64(text.AsSpan(dot + 1))));
    }

    /// <summary>The canonical JSON payload, exposed for tests and diagnostics.</summary>
    /// <exception cref="ArgumentException">The build can't be shared; the message names the field.</exception>
    public static byte[] Payload(BuildSnapshot build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var json = new StringBuilder();
        BuildJson.WriteCanonical(json, BuildJson.Write(build, party: false), "build", indented: false);
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    private static ShareCodeException Error(string message) => new(message);

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        var payload = ShareCodeWrapping.Inflate(input, "share code is damaged");
        // ZLibStream returns what it could inflate from a truncated stream, so the trailer is checked here:
        // a cut or extended paste no longer ends with the payload's checksum.
        if (compressed.Length < 6 || BinaryPrimitives.ReadUInt32BigEndian(compressed.AsSpan(compressed.Length - 4)) != Adler32(payload))
        { throw Error("share code is damaged"); }
        return payload;
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint Modulus = 65521;
        uint a = 1, b = 0;
        foreach (var value in data) { a = (a + value) % Modulus; b = (b + a) % Modulus; }
        return (b << 16) | a;
    }

    private static BuildSnapshot Read(byte[] payload)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new() { MaxDepth = 16 }); }
        catch (JsonException exception) { throw new ShareCodeException("share code is damaged", exception); }
        using (document) { return BuildJson.Read(document.RootElement, party: false, (message, inner) => new ShareCodeException("share code " + message, inner)); }
    }
}
