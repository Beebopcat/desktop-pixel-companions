using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AzerothCompanion.Services;

internal sealed record GamePathNode(bool Directory, bool Reparse, string? Target = null);

/// <summary>Only the selected path is inspected. Links are never created, changed or removed.</summary>
internal static class LocalGamePathResolver
{
    internal static string Resolve(string path, Func<string, GamePathNode> read, Func<string, bool> localVolume)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int hops = 0; hops < 24; hops++)
        {
            path = NormalizeTarget(path);
            var root = Path.GetPathRoot(path)!;
            if (!localVolume(root)) throw new IOException("The executable target is not on a local volume. Use manual selection.");
            if (!seen.Add(path)) throw new IOException("Executable directory links form a loop. Choose the actual local installation.");
            var pieces = path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (pieces.Length is 0 or > 256) throw new IOException("Executable path is empty or too complex.");
            string current = root; bool redirected = false;
            for (int i = 0; i < pieces.Length; i++)
            {
                current = Path.Combine(current, pieces[i]);
                var node = read(current);
                if (!node.Reparse)
                {
                    if (i < pieces.Length - 1 && !node.Directory) throw new IOException("Executable parent is not a directory.");
                    if (i == pieces.Length - 1 && node.Directory) throw new IOException("Select an executable file, not a directory.");
                    continue;
                }
                if (i == pieces.Length - 1 || !node.Directory || string.IsNullOrWhiteSpace(node.Target))
                    throw new IOException("Unsupported executable link or cloud placeholder. Choose the actual local executable.");
                string target = node.Target;
                if (!Path.IsPathRooted(target)) target = Path.Combine(Path.GetDirectoryName(current)!, target);
                target = NormalizeTarget(target); // Reject UNC/device destinations BEFORE accessing their content.
                path = Path.Combine([target, .. pieces.Skip(i + 1)]);
                redirected = true; break;
            }
            if (!redirected) return path;
        }
        throw new IOException("Too many executable directory links. Use the actual local installation.");
    }
    private static string NormalizeTarget(string path)
    {
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase)) path = @"\\?\" + path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) path = path[4..];
        bool volume = path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase);
        if (!volume && (path.StartsWith("\\", StringComparison.Ordinal) || path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("UNC\\", StringComparison.OrdinalIgnoreCase) || path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')))
            throw new IOException("Network or device-backed executable targets are not accepted.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || full[root.Length..].Contains(':')) throw new IOException("Invalid executable target path.");
        return full;
    }
}

internal sealed class LocalExecutableFileSystem : IGameProfileFileSystem
{
    public GameExecutableIdentity ReadIdentity(string path)
    {
        using var handle = OpenValidated(path, false);
        return Identity(handle);
    }
    public async Task<string> HashAsync(string path, long maximumBytes, TimeSpan timeout, CancellationToken ct) =>
        (await FingerprintAsync(path, maximumBytes, timeout, ct).ConfigureAwait(false)).Sha256;

    public async Task<GameExecutableFingerprint> FingerprintAsync(string path, long maximumBytes, TimeSpan timeout, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct); bounded.CancelAfter(timeout);
        bounded.Token.ThrowIfCancellationRequested();
        using var handle = OpenValidated(path, true);
        var before = Identity(handle);
        if (before.Bytes is <= 0 || before.Bytes > maximumBytes) throw new InvalidOperationException("Executable is empty or exceeds the 512 MiB fingerprint cap. Use manual selection.");
        await using var stream = new FileStream(handle, FileAccess.Read, 65536, isAsync: true);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, bounded.Token).ConfigureAwait(false)).ToLowerInvariant();
        bounded.Token.ThrowIfCancellationRequested();
        if (Identity(handle) != before || ReadIdentity(path) != before) throw new IOException("Executable or its directory link changed during fingerprinting.");
        return new(before, hash);
    }

    private static SafeFileHandle OpenValidated(string alias, bool asynchronous)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Executable pins require Windows file identity.");
        var path = LocalGamePathResolver.Resolve(alias, ReadNode, IsLocalVolume);
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            asynchronous ? FileOptions.Asynchronous | FileOptions.SequentialScan : FileOptions.None);
        try
        {
            // The opened file is authoritative even if an alias changed after preflight.
            var final = FinalPath(handle);
            var root = Path.GetPathRoot(final)!;
            if (!final.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) || !IsLocalVolume(root))
                throw new IOException("The resolved executable is not on a local volume.");
            _ = Identity(handle); return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    private static bool IsLocalVolume(string root) => GetDriveTypeW(root) is 2 or 3 or 5 or 6;
    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        uint count = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 1 /* VOLUME_NAME_GUID | FILE_NAME_NORMALIZED */);
        if (count == 0 || count >= buffer.Capacity) throw new IOException("Final local executable path could not be checked.", new Win32Exception(Marshal.GetLastWin32Error()));
        return buffer.ToString();
    }
    private static GameExecutableIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("File identity could not be read.");
        if ((info.Attributes & 0x10) != 0) throw new IOException("Expected an executable file.");
        long size = ((long)info.SizeHigh << 32) | info.SizeLow;
        long time = ((long)info.WriteHigh << 32) | info.WriteLow;
        return new(size, DateTimeOffset.FromFileTime(time), $"windows:{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}");
    }
    private static GamePathNode ReadNode(string path)
    {
        var attrs = File.GetAttributes(path);
        bool directory = (attrs & FileAttributes.Directory) != 0;
        if ((attrs & FileAttributes.ReparsePoint) == 0) return new(directory, false);
        // Open this link itself: never follow its target to discover whether it is remote.
        using var handle = CreateFileW(path, 0, 7, nint.Zero, 3, 0x00200000 | 0x02000000, nint.Zero);
        if (handle.IsInvalid) throw new IOException("Directory link metadata is unavailable.");
        byte[] bytes = new byte[16384];
        if (!DeviceIoControl(handle, 0x900a8, nint.Zero, 0, bytes, bytes.Length, out int count, nint.Zero))
            throw new IOException("Directory link target is unavailable.");
        uint tag = BitConverter.ToUInt32(bytes, 0);
        int start = tag switch { 0xA0000003 => 16, 0xA000000C => 20, _ => -1 };
        if (start < 0 || count < start) return new(directory, true);
        int offset = BitConverter.ToUInt16(bytes, 8), length = BitConverter.ToUInt16(bytes, 10);
        if (length == 0 || length % 2 != 0 || start + offset + length > count) throw new IOException("Invalid directory link metadata.");
        return new(directory, true, Encoding.Unicode.GetString(bytes, start + offset, length));
    }
    [StructLayout(LayoutKind.Sequential)] private struct HandleInfo
    { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out HandleInfo info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle file, uint code, nint input, int inputSize, byte[] output, int outputSize, out int returned, nint overlapped);
}
