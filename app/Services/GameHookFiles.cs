using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AzerothCompanion.Services;

internal static class GameHookFiles
{
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool Id(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out _);
    internal static string LocalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith("\\", StringComparison.Ordinal)
            || value.Split(['\\', '/']).Any(p => p is "." or "..")) throw new InvalidDataException("Direct-text paths must be exact local absolute paths.");
        var full = Path.GetFullPath(value);
        if (full[Path.GetPathRoot(full)!.Length..].Contains(':')) throw new InvalidDataException("Alternate data streams are not direct-text paths.");
        return full;
    }
    internal static void NoReparse(string path)
    {
        var root = Path.GetPathRoot(path)!;
        if (new DriveInfo(root).DriveType is DriveType.Network or DriveType.Unknown) throw new IOException("Direct-text paths must be on a local volume.");
        for (string? at = path; at != null; at = Path.GetDirectoryName(at))
        {
            if (File.Exists(at) || Directory.Exists(at))
                if ((File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0) throw new IOException("Direct-text data/config paths may not redirect through links.");
            if (string.Equals(at, root, StringComparison.OrdinalIgnoreCase)) break;
        }
    }
    internal static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!seen.Add(property.Name)) throw new JsonException("Duplicate direct-text metadata."); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    internal static async Task<byte[]> ReadAsync(string path, int maximumBytes, CancellationToken ct)
    {
        path = LocalPath(path); NoReparse(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is <= 0 || file.Length > maximumBytes) throw new InvalidDataException("Direct-text metadata exceeds its bound.");
        byte[] bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        _ = new UTF8Encoding(false, true).GetString(bytes); return bytes;
    }
    internal static JsonDocument Json(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try { Unique(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    internal static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
