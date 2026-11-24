using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace NPEduConsole.Core;

public sealed record ClassroomUsbVolume(string Root, string VolumePath, string Label, string MediaHash)
{
    public override string ToString() => $"{Label} · {Root}";
}

public sealed class ClassroomUsbEnrollment : IDisposable
{
    public const string FileName = "NPEduTools.classroom-key.json";
    private const int MaxFileBytes = 512;
    private readonly ClassroomUsbVolume _volume;
    private readonly byte[] _secret;
    private readonly bool _create;
    private bool _disposed;
    public ClassroomUsbProof Proof { get; }

    private ClassroomUsbEnrollment(ClassroomUsbVolume volume, string id, byte[] secret, string label, bool create, ClassroomUsbMaterial? replaces = null)
    {
        _volume = volume;
        _secret = secret;
        _create = create;
        Proof = new(id, label, volume.MediaHash, SecretHash(secret), replaces);
    }

    public static ClassroomUsbEnrollment Prepare(ClassroomUsbVolume volume, string label, bool reissue)
    {
        ValidateVolume(volume);
        var key = ReadFile(volume);
        if (reissue)
        {
            if (key is not { } old) throw new IOException("这支 U 盘上没有可供核对的原钥匙文件。");
            try { return new(volume, Guid.NewGuid().ToString("D"), RandomNumberGenerator.GetBytes(32), label,
                create: false, new(old.Id, volume.MediaHash, SecretHash(old.Secret))); }
            finally { CryptographicOperations.ZeroMemory(old.Secret); }
        }
        return key is { } existing ? new(volume, existing.Id, existing.Secret, label, create: false) :
            new(volume, Guid.NewGuid().ToString("D"), RandomNumberGenerator.GetBytes(32), label, create: true);
    }

    public void WriteAndVerify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateVolume(_volume);
        if (Proof.Replaces is { } previous) ReplaceRevokedFile(previous);
        var occupied = _create ? ReadFile(_volume) : null;
        if (_create && occupied is null)
        {
            // This path refers to the original mounted volume even if Windows
            // subsequently assigns its drive letter to a different disk.
            string temp = Path.Combine(_volume.VolumePath, $".npedu-key-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    WriteKey(file, Proof.KeyId, _secret);
                    file.Flush(flushToDisk: true);
                }
                ValidateVolume(_volume);
                File.Move(temp, Path.Combine(_volume.VolumePath, FileName), overwrite: false);
            }
            finally
            {
                try { File.Delete(temp); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        else if (occupied is { } existing) CryptographicOperations.ZeroMemory(existing.Secret);

        ValidateVolume(_volume);
        var readBack = ReadFile(_volume);
        if (readBack is not { } key) throw new IOException("未读到本次登记的 U 盘凭证。");
        try
        {
            if (key.Id != Proof.KeyId || !CryptographicOperations.FixedTimeEquals(key.Secret, _secret))
                throw new IOException("盘上的钥匙已变化，未继续登记。请插回原盘再核对。");
        }
        finally { CryptographicOperations.ZeroMemory(key.Secret); }
    }

    private void ReplaceRevokedFile(ClassroomUsbMaterial previous)
    {
        var path = Path.Combine(_volume.VolumePath, FileName);
        using var file = ClassroomUsbMedia.OpenKeyFile(path, write: true) ?? throw new IOException("原钥匙文件已不在这支盘上。");
        var old = ReadKey(file);
        try
        {
            if (old.Id != previous.KeyId || previous.SecretHash != SecretHash(old.Secret)) throw new IOException("原钥匙文件已改变。请重新核对这一支盘。");
            ValidateVolume(_volume);
            try { WriteKey(file, Proof.KeyId, _secret); file.Flush(flushToDisk: true); }
            catch (IOException)
            {
                // The old file was already revoked. Restore its contents when
                // possible so a failed write can be retried with the same proof.
                try { WriteKey(file, old.Id, old.Secret); file.Flush(flushToDisk: true); }
                catch (IOException) { }
                throw;
            }
        }
        finally { CryptographicOperations.ZeroMemory(old.Secret); }
    }

    private static void WriteKey(FileStream file, string id, byte[] secret)
    {
        file.Position = 0;
        JsonSerializer.Serialize(file, new { schemaVersion = 1, keyId = id, secret = Base64Url(secret) });
        file.SetLength(file.Position);
    }

    private static void ValidateVolume(ClassroomUsbVolume volume)
    {
        var current = ClassroomUsbMedia.Describe(volume.Root);
        if (current?.MediaHash != volume.MediaHash || current.VolumePath != volume.VolumePath)
            throw new IOException("原 U 盘已拔出或改变。请重新插入并核对所选实物。");
    }

    private static (string Id, byte[] Secret)? ReadFile(ClassroomUsbVolume volume)
    {
        string path = Path.Combine(volume.VolumePath, FileName);
        using var file = ClassroomUsbMedia.OpenKeyFile(path, write: false);
        return file is null ? null : ReadKey(file);
    }

    private static (string Id, byte[] Secret) ReadKey(FileStream file)
    {
        file.Position = 0;
        if (file.Length is < 1 or > MaxFileBytes) throw InvalidFile();
        byte[] data = new byte[file.Length];
        try
        {
            file.ReadExactly(data);
            var start = data.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0;
            using var document = JsonDocument.Parse(data.AsMemory(start));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 ||
                !root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schema) || schema != 1 ||
                !root.TryGetProperty("keyId", out var idRow) || idRow.ValueKind != JsonValueKind.String || idRow.GetString() is not { } id ||
                !Guid.TryParseExact(id, "D", out var guid) || guid.ToString("D") != id ||
                !root.TryGetProperty("secret", out var secretRow) || secretRow.ValueKind != JsonValueKind.String || secretRow.GetString() is not { Length: 43 } secret)
                throw InvalidFile();
            byte[] raw = new byte[32];
            if (Convert.TryFromBase64String(secret.Replace('-', '+').Replace('_', '/') + "=", raw, out int count) && count == 32 && Base64Url(raw) == secret)
                return (id, raw);
            CryptographicOperations.ZeroMemory(raw);
            throw InvalidFile();
        }
        catch (JsonException) { throw InvalidFile(); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private static IOException InvalidFile() => new("这支 U 盘上的钥匙文件无法核对。请保留原文件并使用另一支登记盘。");
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string SecretHash(byte[] secret) => Convert.ToHexStringLower(SHA256.HashData(secret));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_secret);
    }
}

public static class ClassroomUsbMedia
{
    private const uint QueryStorageProperty = 0x002D1400;
    private const int UsbBus = 7;
    private const int DescriptorLimit = 4096;

    internal static FileStream? OpenKeyFile(string path, bool write)
    {
        const uint genericRead = 0x80000000, genericWrite = 0x40000000;
        const uint openExisting = 3, openReparsePoint = 0x00200000, writeThrough = 0x80000000;
        var handle = OpenVolume(path, genericRead | (write ? genericWrite : 0), write ? 0u : 1u, IntPtr.Zero, openExisting,
            openReparsePoint | (write ? writeThrough : 0), IntPtr.Zero);
        try
        {
            if (handle.IsInvalid)
            {
                if (Marshal.GetLastPInvokeError() == 2) return null;
                throw new IOException("无法打开原 U 盘的钥匙文件。");
            }
            if (GetFileType(handle) != 1 || !GetFileInformationByHandleEx(handle, 9, out var info, 8) ||
                (info.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new IOException("U 盘凭证须位于本盘根目录的普通文件中。");
            var file = new FileStream(handle, write ? FileAccess.ReadWrite : FileAccess.Read);
            handle = null!; // FileStream now owns the original file handle.
            return file;
        }
        finally { handle?.Dispose(); }
    }

    public static ClassroomUsbVolume[] List()
    {
        if (!OperatingSystem.IsWindows()) return [];
        return DriveInfo.GetDrives().Select(drive => Describe(drive.Name)).OfType<ClassroomUsbVolume>().ToArray();
    }

    internal static ClassroomUsbVolume? Describe(string root)
    {
        if (!OperatingSystem.IsWindows() || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\') return null;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed) || !drive.IsReady) return null;
            using var handle = OpenVolume(@"\\.\" + root[..2], 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            var descriptor = new byte[DescriptorLimit];
            if (handle.IsInvalid || !DeviceIoControl(handle, QueryStorageProperty, new byte[12], 12, descriptor, descriptor.Length, out int count, IntPtr.Zero)) return null;
            string? mediaHash = count is >= 36 and <= DescriptorLimit ? MediaHash(descriptor.AsSpan(0, count)) : null;
            if (mediaHash is null) return null;
            var volumePath = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPoint(root, volumePath, volumePath.Capacity)) return null;
            var label = drive.VolumeLabel.Trim();
            return new(root, volumePath.ToString(), label.Length > 0 ? label : "USB 存储设备", mediaHash);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? MediaHash(ReadOnlySpan<byte> descriptor)
    {
        int size = BinaryPrimitives.ReadInt32LittleEndian(descriptor[4..]);
        if (size is < 36 or > DescriptorLimit || size > descriptor.Length || BinaryPrimitives.ReadInt32LittleEndian(descriptor[28..]) != UsbBus) return null;
        descriptor = descriptor[..size];
        var vendor = ReadField(descriptor, 12);
        var product = ReadField(descriptor, 16);
        var serial = ReadField(descriptor, 24);
        if (vendor is null || product is null || string.IsNullOrEmpty(serial)) return null;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes($"NPEDUTOOLS-USB-MEDIA-V1\n{vendor}\n{product}\n{serial}")));
    }

    private static string? ReadField(ReadOnlySpan<byte> descriptor, int fieldOffset)
    {
        int position = BinaryPrimitives.ReadInt32LittleEndian(descriptor[fieldOffset..]);
        if (position == 0) return "";
        if (position < 36 || position >= descriptor.Length) return null;
        var part = descriptor[position..];
        int end = part.IndexOf((byte)0);
        if (end is < 0 or > 256) return null;
        foreach (byte ch in part[..end]) if (ch is < 0x20 or > 0x7E) return null;
        return Encoding.ASCII.GetString(part[..end]).Trim(' ').ToUpperInvariant();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle OpenVolume(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        [Out] byte[] output, int outputSize, out int bytes, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder name, int length);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public FileAttributes FileAttributes;
        public uint ReparseTag;
    }
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);
}
