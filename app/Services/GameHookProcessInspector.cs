using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AzerothCompanion.Services;

public sealed class GameHookProcessInspector : IGameHookProcessInspector
{
    private readonly IGameProfileFileSystem files;
    public GameHookProcessInspector(IGameProfileFileSystem? files = null) => this.files = files ?? new LocalExecutableFileSystem();
    public async Task<GameHookProcessIdentity> InspectAsync(uint processId, CancellationToken ct)
    {
        if (processId == 0 || processId > int.MaxValue) throw new ArgumentException("A direct-text process PID is required.");
        using var process = Process.GetProcessById((int)processId);
        string path = GameHookFiles.LocalPath(process.MainModule?.FileName ?? throw new IOException("Process executable cannot be inspected."));
        long start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
        if (!IsWow64Process2(process.Handle, out ushort machine, out ushort native)) throw new IOException("Process architecture cannot be inspected.");
        string architecture = (machine == 0 ? native : machine) switch { 0x8664 => "x64", 0x014c => "x86", _ => throw new IOException("Direct-text helper does not support this architecture.") };
        var fingerprint = await files.FingerprintAsync(path, LocalGameProfileStore.MaximumExecutableBytes, LocalGameProfileStore.HashTimeout, ct).ConfigureAwait(false);
        var identity = new GameHookProcessIdentity(processId, start, path, fingerprint.Sha256, fingerprint.Identity, architecture);
        ct.ThrowIfCancellationRequested(); if (!IsCurrent(identity)) throw new IOException("Process identity changed during the direct-text check.");
        return identity;
    }
    public bool IsCurrent(GameHookProcessIdentity expected)
    {
        try
        {
            using var process = Process.GetProcessById((int)expected.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().ToFileTimeUtc() == expected.StartedFileTime
                && string.Equals(process.MainModule?.FileName, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                && files.ReadIdentity(expected.ExecutablePath) == expected.FileIdentity;
        }
        catch { return false; }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(nint process, out ushort machine, out ushort nativeMachine);
}
