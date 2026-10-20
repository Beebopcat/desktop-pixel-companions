using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using AzerothCompanion.Core;

namespace AzerothCompanion.Services;

public sealed record GameExecutableIdentity(long Bytes, DateTimeOffset LastWriteUtc, string FileIdentity);
public sealed record GameExecutableFingerprint(GameExecutableIdentity Identity, string Sha256);
public sealed record UserConfirmedGameProfile(string Id, string Title, string ExecutablePath, string ExecutableSha256,
    GameExecutableIdentity ExecutableIdentity, DateTimeOffset ConfirmedUtc);
public enum GameProfileState { Ready, Missing, Changed, Unavailable }
public sealed record GameProfileValidation(GameProfileState State, string Message, GameExecutableIdentity? CurrentIdentity = null);

/// <summary>Injected file access lets fixtures verify fingerprints without opening any user game.</summary>
public interface IGameProfileFileSystem
{
    GameExecutableIdentity ReadIdentity(string path);
    Task<string> HashAsync(string path, long maximumBytes, TimeSpan timeout, CancellationToken ct);
    async Task<GameExecutableFingerprint> FingerprintAsync(string path, long maximumBytes, TimeSpan timeout, CancellationToken ct)
    {
        var before = ReadIdentity(path);
        var hash = await HashAsync(path, maximumBytes, timeout, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (ReadIdentity(path) != before) throw new IOException("Executable changed while fingerprinting.");
        return new(before, hash);
    }
}

/// <summary>Local opt-in title/executable pins. Reading a missing store creates nothing.</summary>
public sealed class LocalGameProfileStore
{
    public const long MaximumExecutableBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan HashTimeout = TimeSpan.FromSeconds(10);
    private readonly string _path;
    private readonly IGameProfileFileSystem _files;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed record Document(int Schema, IReadOnlyList<UserConfirmedGameProfile> Profiles);

    public LocalGameProfileStore(string? path = null, IGameProfileFileSystem? files = null, Func<DateTimeOffset>? clock = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AzerothCompanion", "user-confirmed-game-profiles.json"));
        _files = files ?? new LocalExecutableFileSystem();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }
    public string StorePath => _path;
    public async Task<IReadOnlyList<UserConfirmedGameProfile>> ListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 256 * 1024) throw new InvalidDataException("Game profile file exceeds 256 KiB. Review the local profile file; nothing was accepted.");
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 65536, leaveOpen: true);
            var document = JsonSerializer.Deserialize<Document>(await reader.ReadToEndAsync(ct).ConfigureAwait(false), Json);
            if (document is not { Schema: 1 } || document.Profiles is null || document.Profiles.Count > 128)
                throw new InvalidDataException("Unsupported or oversized game profile document. No profiles were accepted.");
            foreach (var profile in document.Profiles)
                if (!ValidProfile(profile)) throw new InvalidDataException("Invalid game profile. Reconfirm the public title and exact executable; nothing was accepted.");
            if (document.Profiles.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1) ||
                document.Profiles.GroupBy(p => p.ExecutablePath, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
                throw new InvalidDataException("Ambiguous duplicate game profile ID or executable path. Remove the duplicate explicitly; nothing was accepted.");
            return document.Profiles;
        }
        catch (JsonException error) { throw new InvalidDataException("Malformed local game profile JSON. Review the file; nothing was accepted.", error); }
    }
    public async Task<UserConfirmedGameProfile> ConfirmAsync(string publicTitle, string executablePath, CancellationToken ct = default, Func<CancellationToken, Task>? validateSelection = null)
    {
        var title = GameKnowledgeText.ValidateTitle(publicTitle);
        if (IsToolTitle(title)) throw new ArgumentException("Choose a game title, not an editor, engine, launcher or tool.", nameof(publicTitle));
        var path = NormalizeExecutablePath(executablePath);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Mutations occur only after this explicit call. File-share locking
            // serializes other app instances and releases on process failure.
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using var writeLock = await AcquireWriteLockAsync(ct).ConfigureAwait(false);
            var profiles = (await ListAsync(ct).ConfigureAwait(false)).ToList();
            var existing = profiles.SingleOrDefault(p => string.Equals(p.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing is null && profiles.Count >= 128) throw new InvalidOperationException("Maximum 128 remembered games. Remove an explicit profile before adding another.");
            var before = _files.ReadIdentity(path);
            if (before.Bytes is <= 0 or > MaximumExecutableBytes) throw new InvalidOperationException("Executable exceeds the 512 MiB fingerprint limit or is empty. Use manual game selection; no profile was saved.");
            GameExecutableFingerprint fingerprint;
            try { fingerprint = await _files.FingerprintAsync(path, MaximumExecutableBytes, HashTimeout, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("Executable fingerprint exceeded 10 seconds. Try again when the disk is idle or use manual selection; no profile was saved."); }
            ct.ThrowIfCancellationRequested();
            if (fingerprint.Identity != before || _files.ReadIdentity(path) != before) throw new IOException("Executable changed during confirmation. Retry after its update finishes; no profile was saved.");
            var profile = new UserConfirmedGameProfile(existing?.Id ?? Guid.NewGuid().ToString("N"), title, path, fingerprint.Sha256.ToLowerInvariant(), before, _clock());
            if (!ValidProfile(profile)) throw new InvalidDataException("Executable fingerprint is invalid; no profile was saved.");
            if (validateSelection is not null) await validateSelection(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            profiles.RemoveAll(p => p.Id == profile.Id); profiles.Add(profile);
            await SaveAsync(profiles, ct).ConfigureAwait(false);
            return profile;
        }
        finally { _gate.Release(); }
    }
    public async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        if (!ValidId(id)) throw new ArgumentException("Remove requires the exact profile ID.", nameof(id));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return false;
            await using var writeLock = await AcquireWriteLockAsync(ct).ConfigureAwait(false);
            var profiles = (await ListAsync(ct).ConfigureAwait(false)).ToList();
            if (profiles.RemoveAll(p => p.Id == id) == 0) return false;
            await SaveAsync(profiles, ct).ConfigureAwait(false); return true;
        }
        finally { _gate.Release(); }
    }
    public GameProfileValidation CheckMetadata(UserConfirmedGameProfile profile)
    {
        try
        {
            var identity = _files.ReadIdentity(profile.ExecutablePath);
            return identity == profile.ExecutableIdentity ? new(GameProfileState.Ready, "Pinned executable metadata unchanged; cold hash validation still required.", identity) :
                new(GameProfileState.Changed, "Remembered game executable changed or was replaced. Explicitly reconfirm it before automatic detection.", identity);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new(GameProfileState.Missing, "Remembered game executable is missing. Locate and explicitly reconfirm it."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(GameProfileState.Unavailable, "Executable cannot be checked: " + error.Message); }
    }
    public async Task<GameProfileValidation> ValidateAsync(UserConfirmedGameProfile profile, CancellationToken ct = default)
    {
        var metadata = CheckMetadata(profile); if (metadata.State != GameProfileState.Ready) return metadata;
        try
        {
            var fingerprint = await _files.FingerprintAsync(profile.ExecutablePath, MaximumExecutableBytes, HashTimeout, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (fingerprint.Identity != profile.ExecutableIdentity || _files.ReadIdentity(profile.ExecutablePath) != profile.ExecutableIdentity || !string.Equals(fingerprint.Sha256, profile.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                return new(GameProfileState.Changed, "Remembered game executable fingerprint changed. Explicitly reconfirm it before automatic detection.");
            return new(GameProfileState.Ready, "User-confirmed public title and exact unchanged executable; not store metadata.", profile.ExecutableIdentity);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(GameProfileState.Unavailable, "Cold executable validation exceeded 10 seconds. Use manual selection or reconfirm when the disk is idle."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(GameProfileState.Unavailable, "Executable fingerprint could not be checked: " + error.Message); }
    }
    public static string NormalizeExecutablePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.Split(['/', '\\']).Any(part => part is "." or "..")) throw new ArgumentException("Choose the exact fully qualified local game executable path; relative/network/traversal paths are not accepted.", nameof(path));
        var full = Path.GetFullPath(path);
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || full.AsSpan(Path.GetPathRoot(full)!.Length).Contains(':') ||
            IsToolExecutable(Path.GetFileNameWithoutExtension(full))) throw new ArgumentException("Browser, editor, launcher, service and tool executables cannot be remembered as games.", nameof(path));
        return full;
    }
    internal static bool IsToolExecutable(string name)
    {
        name = GameLaunchDetector.NormalizeProcessName(name);
        return GameLaunchDetector.IsIgnoredProcess(name) ||
        new[] { "unrealeditor", "unity", "godot", "blender", "obs32", "obs64", "devenv", "rider64", "quixelbridge", "bridge", "fab", "python", "pythonw", "java", "node", "mossplayer", "kokoroplayer", "autohotkey64", "wscript", "cscript" }.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        new[] { "editor", "webview", "dedicatedserver", "sdk", "helper" }.Any(term => name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsToolTitle(string title) => GameLaunchDetector.IsToolTitle(title) ||
        new[] { "Unreal Engine", "Unity Editor", "Godot Engine", "Fab UE Plugin", "Quixel Bridge" }.Any(term => title.Contains(term, StringComparison.OrdinalIgnoreCase));
    private static bool ValidId(string? id) => id is not null && Regex.IsMatch(id, "^[a-f0-9]{32}$", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static bool ValidProfile(UserConfirmedGameProfile? p)
    {
        if (p is null || !ValidId(p.Id) || p.ExecutableIdentity is null || p.ExecutableIdentity.Bytes is <= 0 or > MaximumExecutableBytes ||
            string.IsNullOrWhiteSpace(p.ExecutableIdentity.FileIdentity) || p.ExecutableIdentity.FileIdentity.Length > 160 ||
            p.ConfirmedUtc == default || p.ExecutableIdentity.LastWriteUtc == default || p.ExecutableSha256 is null || !Regex.IsMatch(p.ExecutableSha256, "^[a-f0-9]{64}$", RegexOptions.None, TimeSpan.FromSeconds(1))) return false;
        try { return GameKnowledgeText.ValidateTitle(p.Title) == p.Title && !IsToolTitle(p.Title) && NormalizeExecutablePath(p.ExecutablePath) == p.ExecutablePath; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }
    private async Task<FileStream> AcquireWriteLockAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(_path + ".write-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < until) { await Task.Delay(50, ct).ConfigureAwait(false); }
            catch (IOException error) { throw new IOException("Another app is saving game profiles. Try again; no profile was changed.", error); }
        }
    }
    private async Task SaveAsync(IReadOnlyList<UserConfirmedGameProfile> profiles, CancellationToken ct)
    {
        var contents = JsonSerializer.Serialize(new Document(1, profiles), Json);
        if (Encoding.UTF8.GetByteCount(contents) > 256 * 1024)
            throw new InvalidOperationException("Game profile file would exceed 256 KiB. Remove explicit unused profiles; no change was saved.");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, contents, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

