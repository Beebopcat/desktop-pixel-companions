using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using AzerothCompanion.Core;

namespace AzerothCompanion.Services;

public enum GameLaunchProvenance { Unknown, SteamManifest, UserConfirmedExecutable, ManualSelection }
public sealed record GameLaunchIdentity(string Title, uint ProcessId, string ProcessName, nint WindowHandle,
    string? StoreId, string? ProcessPath = null, bool IsVerified = true,
    GameLaunchProvenance Provenance = GameLaunchProvenance.Unknown, string? SourceId = null, long ProfileEpoch = 0);
public sealed record GameProcessWindow(uint ProcessId, string ProcessName, nint WindowHandle, string? ProcessPath);
public sealed record SteamGameInstallation(string Title, string AppId, string InstallationPath);

/// <summary>Steam metadata or explicitly confirmed local executable pins. Never creates profiles, hooks or captures a game.</summary>
public sealed class GameLaunchDetector
{
    private readonly IReadOnlyList<string> _steamRoots;
    private readonly Func<IReadOnlyList<GameProcessWindow>> _windowProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly LocalGameProfileStore _profileStore;
    private IReadOnlyList<UserConfirmedGameProfile> _profiles = [];
    private readonly Dictionary<string, (UserConfirmedGameProfile Profile, GameProfileValidation Validation, DateTimeOffset RetryAfter)> _profileValidation = new();
    private int _refreshProfiles;
    private readonly object _epochSync = new();
    private long _profileEpoch, _lastEpoch = -1;
    private CancellationTokenSource _epochCancellation = new();
    private string? _storeDiagnostic, _pollDiagnostic;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Epoch, UserConfirmedGameProfile Profile)> _readyProfiles = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<SteamGameInstallation> _index = [];
    private IReadOnlyList<GameLaunchIdentity> _last = [];
    private DateTimeOffset _nextIndexRefresh = DateTimeOffset.MinValue, _nextPoll = DateTimeOffset.MinValue;
    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    { "steam", "steamwebhelper", "epicgameslauncher", "battle.net", "goggalaxy", "ubisoftconnect", "upc", "eadesktop", "ealauncher",
      "explorer", "dwm", "chrome", "msedge", "firefox", "code", "pwsh", "powershell", "cmd", "notepad", "AzerothCompanion" };
    public GameLaunchDetector(IEnumerable<string>? steamRoots = null, Func<IReadOnlyList<GameProcessWindow>>? windowProvider = null,
        Func<DateTimeOffset>? clock = null, LocalGameProfileStore? profileStore = null)
    {
        _steamRoots = (steamRoots ?? DefaultSteamRoots()).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        _windowProvider = windowProvider ?? ReadVisibleProcessWindows;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _profileStore = profileStore ?? new LocalGameProfileStore();
    }
    public string? ProfileDiagnostic => _storeDiagnostic ?? _pollDiagnostic;
    public long ProfileEpoch { get { lock (_epochSync) return _profileEpoch; } }
    public bool IsCurrent(GameLaunchIdentity identity) => identity.Provenance != GameLaunchProvenance.UserConfirmedExecutable ||
        identity.ProfileEpoch == ProfileEpoch && identity.SourceId is { } id && _readyProfiles.TryGetValue(id, out var ready) &&
        ready.Epoch == identity.ProfileEpoch && ready.Profile.Title == identity.Title && string.Equals(ready.Profile.ExecutablePath, identity.ProcessPath, StringComparison.OrdinalIgnoreCase);
    public void InvalidateConfirmedProfiles()
    {
        lock (_epochSync)
        {
            _profileEpoch++; Interlocked.Exchange(ref _refreshProfiles, 1);
            _readyProfiles.Clear();
            var old = _epochCancellation; _epochCancellation = new(); old.Cancel(); old.Dispose();
        }
    }
    /// <summary>Explicit Start reads only bounded local Steam metadata and this selected executable. Never enumerates windows, starts research or writes profiles.</summary>
    public async Task<GameLaunchIdentity?> ValidateSelectedGameAsync(GameProcessWindow window, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (window.ProcessId == 0 || window.WindowHandle == 0 || string.IsNullOrWhiteSpace(window.ProcessPath)) return null;
        GameLaunchIdentity? steam;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _clock();
            if (now >= _nextIndexRefresh)
            {
                var index = await Task.Run(() => ReadSteamInstallations(_steamRoots, ct), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                _index = index;
                _nextIndexRefresh = now.AddMinutes(2);
                // Reading the index here must not prevent the next automatic poll
                // from loading its separate remembered-profile index.
                Interlocked.Exchange(ref _refreshProfiles, 1);
            }
            ct.ThrowIfCancellationRequested();
            steam = MatchInstallation(window, _index);
        }
        finally { _gate.Release(); }
        if (steam is not null) { ct.ThrowIfCancellationRequested(); return steam; }
        return await ValidateSelectedProfileAsync(window, ct).ConfigureAwait(false);
    }
    /// <summary>Explicit Start checks only this selected executable's remembered pin. No process enumeration or Steam scan.</summary>
    public async Task<GameLaunchIdentity?> ValidateSelectedProfileAsync(GameProcessWindow window, CancellationToken ct = default)
    {
        if (window.ProcessId == 0 || window.WindowHandle == 0 || string.IsNullOrWhiteSpace(window.ProcessPath) ||
            LocalGameProfileStore.IsToolExecutable(window.ProcessName)) return null;
        var path = LocalGameProfileStore.NormalizeExecutablePath(window.ProcessPath);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        CancellationTokenSource? linked = null;
        try
        {
            long epoch;
            lock (_epochSync) { epoch = _profileEpoch; linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _epochCancellation.Token); }
            ct = linked.Token;
            void Current() { ct.ThrowIfCancellationRequested(); if (epoch != ProfileEpoch) throw new OperationCanceledException("Remembered profiles changed during selected-game validation.", ct); }
            IReadOnlyList<UserConfirmedGameProfile> rows;
            try { rows = await _profileStore.ListAsync(ct).ConfigureAwait(false); Current(); _storeDiagnostic = null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            { _storeDiagnostic = error.Message; throw; }
            var profile = rows.SingleOrDefault(p => string.Equals(p.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
            if (profile is null) return null;
            if (_profileValidation.TryGetValue(profile.Id, out var blocked) && blocked.Profile == profile &&
                blocked.Validation.State is GameProfileState.Changed or GameProfileState.Missing)
                throw new InvalidOperationException(blocked.Validation.Message);
            var check = await Task.Run(() => _profileStore.ValidateAsync(profile, ct), ct).ConfigureAwait(false);
            Current();
            _profileValidation[profile.Id] = (profile, check, _clock().AddMinutes(1));
            if (check.State != GameProfileState.Ready)
            {
                _readyProfiles.TryRemove(profile.Id, out _); _pollDiagnostic = check.Message;
                throw new InvalidOperationException(check.Message);
            }
            // A different app instance may forget/reconfirm while the cold hash is pending.
            var after = await _profileStore.ListAsync(ct).ConfigureAwait(false); Current();
            if (after.SingleOrDefault(p => p.Id == profile.Id) != profile)
                throw new InvalidOperationException("The remembered profile changed during Start. Select or reconfirm it again.");
            lock (_epochSync)
            {
                Current(); _readyProfiles[profile.Id] = (epoch, profile); _pollDiagnostic = null;
                return new(profile.Title, window.ProcessId, window.ProcessName, window.WindowHandle, null, path,
                    true, GameLaunchProvenance.UserConfirmedExecutable, profile.Id, epoch);
            }
        }
        finally { linked?.Dispose(); _gate.Release(); }
    }
    public async Task<IReadOnlyList<GameLaunchIdentity>> PollAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        CancellationTokenSource? linked = null;
        try
        {
            long epoch;
            lock (_epochSync) { epoch = _profileEpoch; linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _epochCancellation.Token); }
            ct = linked.Token;
            void Current() { ct.ThrowIfCancellationRequested(); if (epoch != ProfileEpoch) throw new OperationCanceledException("Game profiles changed during detection.", ct); }
            var now = _clock();
            var refreshProfiles = Interlocked.Exchange(ref _refreshProfiles, 0) == 1;
            if (now < _nextPoll && !refreshProfiles && epoch == _lastEpoch) { Current(); return _last; }
            _pollDiagnostic = null;
            if (now >= _nextIndexRefresh)
            {
                _index = await Task.Run(() => ReadSteamInstallations(_steamRoots, ct), ct);
                Current();
                _nextIndexRefresh = now.AddMinutes(2);
                refreshProfiles = true;
            }
            if (refreshProfiles)
            {
                try { var loaded = await _profileStore.ListAsync(ct).ConfigureAwait(false); Current(); _profiles = loaded; _storeDiagnostic = null; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                { _profiles = []; _readyProfiles.Clear(); _storeDiagnostic = error.Message; }
                foreach (var id in _readyProfiles.Keys.Where(id => _profiles.All(profile => profile.Id != id)).ToArray()) _readyProfiles.TryRemove(id, out _);
                foreach (var id in _profileValidation.Keys.Where(id => _profiles.All(profile => profile.Id != id)).ToArray())
                    _profileValidation.Remove(id);
                // An explicit refresh may retry transient IO/timeout failures.
                // It must never unlock an observed changed executable pin.
                foreach (var id in _profileValidation.Where(pair => pair.Value.Validation.State == GameProfileState.Unavailable).Select(pair => pair.Key).ToArray())
                    _profileValidation.Remove(id);
            }
            var windows = await Task.Run(_windowProvider, ct);
            Current();
            var matches = new List<GameLaunchIdentity>();
            var coldHashes = 0;
            foreach (var window in windows.Take(128))
            {
                ct.ThrowIfCancellationRequested();
                var steam = MatchInstallation(window, _index);
                if (steam is not null) { matches.Add(steam); continue; }
                if (window.ProcessId == 0 || window.WindowHandle == 0 || string.IsNullOrWhiteSpace(window.ProcessPath) ||
                    LocalGameProfileStore.IsToolExecutable(window.ProcessName)) continue;
                string path;
                try { path = LocalGameProfileStore.NormalizeExecutablePath(window.ProcessPath); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { continue; }
                var candidates = _profiles.Where(profile => string.Equals(profile.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (candidates.Length != 1) continue;
                var profile = candidates[0];
                var metadata = await Task.Run(() => _profileStore.CheckMetadata(profile), ct).ConfigureAwait(false);
                Current();
                if (metadata.State != GameProfileState.Ready)
                {
                    _readyProfiles.TryRemove(profile.Id, out _);
                    if (metadata.State == GameProfileState.Unavailable && _profileValidation.TryGetValue(profile.Id, out var blocked) &&
                        blocked.Profile == profile && blocked.Validation.State is GameProfileState.Changed or GameProfileState.Missing)
                    { _pollDiagnostic = blocked.Validation.Message; continue; }
                    _profileValidation[profile.Id] = (profile, metadata, now.AddMinutes(1)); _pollDiagnostic = metadata.Message; continue;
                }
                if (!_profileValidation.TryGetValue(profile.Id, out var cached) || cached.Profile != profile ||
                    cached.Validation.State == GameProfileState.Unavailable && now >= cached.RetryAfter)
                {
                    // Only an active exact-path candidate is cold-hashed, at
                    // most once per poll. Unchanged ready pins reuse the cache.
                    if (coldHashes++ >= 1) { _pollDiagnostic = "Another remembered game needs cold verification; it will be checked on the next poll. Manual selection remains available."; continue; }
                    var validated = await Task.Run(() => _profileStore.ValidateAsync(profile, ct), ct).ConfigureAwait(false);
                    Current();
                    cached = (profile, validated, now.AddMinutes(1)); _profileValidation[profile.Id] = cached;
                }
                if (cached.Validation.State != GameProfileState.Ready) { _readyProfiles.TryRemove(profile.Id, out _); _pollDiagnostic = cached.Validation.Message; continue; }
                _readyProfiles[profile.Id] = (epoch, profile);
                matches.Add(new(profile.Title, window.ProcessId, window.ProcessName, window.WindowHandle, null, path,
                    true, GameLaunchProvenance.UserConfirmedExecutable, profile.Id, epoch));
            }
            lock (_epochSync)
            {
                Current(); _last = matches.DistinctBy(game => game.ProcessId).ToArray();
                _lastEpoch = epoch; _nextPoll = now.AddSeconds(2); return _last;
            }
        }
        catch (OperationCanceledException) { Interlocked.Exchange(ref _refreshProfiles, 1); throw; }
        finally { linked?.Dispose(); _gate.Release(); }
    }
    public static GameLaunchIdentity? MatchInstallation(GameProcessWindow window, IEnumerable<SteamGameInstallation> installations)
    {
        if (window.ProcessId == 0 || window.WindowHandle == 0 || IsIgnoredProcess(window.ProcessName) || string.IsNullOrWhiteSpace(window.ProcessPath)) return null;
        try
        {
            var processPath = Path.GetFullPath(window.ProcessPath);
            if (!processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            var match = installations.OrderByDescending(game => game.InstallationPath.Length).FirstOrDefault(game =>
                processPath.StartsWith(Path.GetFullPath(game.InstallationPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            if (match is null || IsInstalledSupportTool(match.AppId, Path.GetRelativePath(match.InstallationPath, processPath))) return null;
            return new(match.Title, window.ProcessId, window.ProcessName, window.WindowHandle, match.AppId, processPath,
                true, GameLaunchProvenance.SteamManifest, match.AppId);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }
    // Exact installed app/path evidence from the 17-game inventory. These are tools, not gameplay children.
    private static readonly HashSet<string> InstalledSupportTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "1205280/lib/py2-windows-i686/zsync.exe", "1205280/lib/py2-windows-i686/zsyncmake.exe",
        "1205280/lib/py2-windows-x86_64/zsync.exe", "1205280/lib/py2-windows-x86_64/zsyncmake.exe",
        "2374160/lib/py2-windows-i686/zsync.exe", "2374160/lib/py2-windows-i686/zsyncmake.exe",
        "2374160/lib/py2-windows-x86_64/zsync.exe", "2374160/lib/py2-windows-x86_64/zsyncmake.exe",
        "637090/BattleTech_Data/StreamingAssets/editors/ContractParser.exe",
        "637090/BattleTech_Data/StreamingAssets/editors/EventEditor.exe",
        "637090/BattleTech_Data/StreamingAssets/editors/FlashpointEditor.exe",
        "470310/Release/bin/LogTransporter.exe", "470310/Release/bin/LogTransporterConsole.exe",
        "470310/Release/bin/TechDemo_CEGUITest.exe", "470310/Release/bin/TechDemo_OgreTest.exe", "470310/Release/bin/TechDemo.exe",
        "640820/Kingmaker_Data/StreamingAssets/AuraSupportService.exe", "329970/curl.exe", "329970/gdb.exe"
    };
    private static bool IsInstalledSupportTool(string appId, string relativePath) =>
        InstalledSupportTools.Contains(appId + "/" + relativePath.Replace('\\', '/'));
    public static GameLaunchIdentity? IdentifySelectedWindow(string title, uint processId, string processName, nint windowHandle, string? processPath = null)
    {
        if (processId == 0 || windowHandle == 0 || IsIgnoredProcess(processName)) return null;
        try { return new(GameKnowledgeText.ValidateTitle(title), processId, processName, windowHandle, null, processPath, false, GameLaunchProvenance.ManualSelection); }
        catch (ArgumentException) { return null; }
    }
    public static IReadOnlyList<SteamGameInstallation> ReadSteamInstallations(IEnumerable<string> roots, CancellationToken ct = default)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var supplied in roots.Take(16))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = Path.GetFullPath(supplied);
                libraries.Add(root);
                var folders = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(folders) || new FileInfo(folders).Length > 256 * 1024) continue;
                foreach (var pair in Pairs(File.ReadAllText(folders)))
                    if (pair.Key == "path" || pair.Key.All(char.IsDigit) && Path.IsPathFullyQualified(pair.Value))
                        if (libraries.Count < 16 && Path.IsPathFullyQualified(pair.Value)) libraries.Add(Path.GetFullPath(pair.Value));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        var games = new List<SteamGameInstallation>();
        foreach (var library in libraries)
        {
            ct.ThrowIfCancellationRequested();
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(apps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly).Take(1024 - games.Count))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if (new FileInfo(file).Length > 256 * 1024) continue;
                        var pairs = Pairs(File.ReadAllText(file)).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.First().Value);
                        if (!pairs.TryGetValue("appid", out var id) || !uint.TryParse(id, out var number) || number == 0 ||
                            Path.GetFileName(file) != "appmanifest_" + id + ".acf" || !pairs.TryGetValue("name", out var name) ||
                            !pairs.TryGetValue("installdir", out var relative) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                            relative.Contains(':') || relative.Split(['/', '\\']).Any(part => part is ".." or ".") || IsToolTitle(name)) continue;
                        var title = GameKnowledgeText.ValidateTitle(name);
                        var common = Path.GetFullPath(Path.Combine(apps, "common"));
                        var installation = Path.GetFullPath(Path.Combine(common, relative));
                        if (!installation.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(installation)) continue;
                        games.Add(new(title, id, installation));
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return games.DistinctBy(game => game.AppId).ToArray();
    }
    private static IEnumerable<KeyValuePair<string, string>> Pairs(string text)
    {
        foreach (Match match in Regex.Matches(text, "\"(?<key>(?:\\\\.|[^\"\\\\])*)\"\\s*\"(?<value>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.None, TimeSpan.FromSeconds(1)))
            yield return new(Unescape(match.Groups["key"].Value), Unescape(match.Groups["value"].Value));
    }
    private static string Unescape(string value) => value.Replace("\\\\", "\\", StringComparison.Ordinal).Replace("\\\"", "\"", StringComparison.Ordinal);
    internal static bool IsToolTitle(string title) => new[] { "Dedicated Server", "Redistributable", "Steamworks", "SDK", "Steam Linux Runtime", "Proton", "Wallpaper Engine", "Blender", "OBS Studio" }
        .Any(term => title.Contains(term, StringComparison.OrdinalIgnoreCase));
    internal static string NormalizeProcessName(string process) { var name = Path.GetFileName(process); return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name; }
    internal static bool IsIgnoredProcess(string process) => IgnoredProcesses.Contains(NormalizeProcessName(process)) ||
        new[] { "launcher", "crashreport", "crashpad", "unitycrash", "updater", "uninstall", "setup" }.Any(term => process.Contains(term, StringComparison.OrdinalIgnoreCase));
    private static IReadOnlyList<GameProcessWindow> ReadVisibleProcessWindows()
    {
        var result = new List<GameProcessWindow>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (result.Count >= 128 || IsIgnoredProcess(process.ProcessName) || process.MainWindowHandle == 0) continue;
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path)) result.Add(new((uint)process.Id, process.ProcessName, process.MainWindowHandle, path));
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return result;
    }
    private static IEnumerable<string> DefaultSteamRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
            foreach (var location in new[] { ("HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath"),
                ("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Valve\\Steam", "InstallPath") })
                try { if (Registry.GetValue(location.Item1, location.Item2, null) is string path && path.Length > 0) roots.Add(path); }
                catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return roots;
    }
}
