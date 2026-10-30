using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Touch365Support
{
    // App-only maintenance observes the original user's profile; it never edits,
    // restores or imports it. The elevated account must be that same user.
    internal static class UpdateProfile
    {
        private const int MaximumFiles = 256, MaximumDirectories = 256;
        private const long MaximumFileBytes = 1024 * 1024, MaximumTotalBytes = 16 * 1024 * 1024;
        private const uint ReadAttributes = 0x80, GenericRead = 0x80000000;
        private const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
        private const uint OpenReparsePoint = 0x00200000, BackupSemantics = 0x02000000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static string Resolve(string expectedUserSid)
        {
            string current = WindowsIdentity.GetCurrent().User.Value;
            if (String.IsNullOrEmpty(expectedUserSid) || current != expectedUserSid)
                throw new InvalidOperationException("App-only updates require administrator approval using the same Windows account that enrolled this profile.");
            string registered;
            using (RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + current))
            {
                if (key == null || key.GetValueKind("ProfileImagePath") != RegistryValueKind.ExpandString)
                    throw new InvalidOperationException("The enrolled Windows profile cannot be verified.");
                registered = Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
            }
            string profile = CanonicalDirectory(registered);
            string reported = CanonicalDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            string roaming = CanonicalDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            if (!SamePath(profile, reported) || !SamePath(roaming, Path.Combine(profile, @"AppData\Roaming")))
                throw new InvalidOperationException("Redirected or mismatched Windows profiles require separate maintenance.");
            using (var held = new HeldPaths())
            {
                held.HoldDirectoryChain(ConfigRoot(profile));
                held.RequirePathsUnchanged();
            }
            return profile;
        }

        internal static void RequireBinding(string sid, string profileRoot)
        {
            string resolved = Resolve(sid);
            if (!SamePath(resolved, CanonicalDirectory(profileRoot)))
                throw new InvalidOperationException("The app-only update belongs to a different Windows profile. Its binding must not be reset.");
        }

        internal static string ConfigRoot(string profileRoot)
        { return CanonicalDirectory(Path.Combine(CanonicalDirectory(profileRoot), @"AppData\Roaming\RustDesk\config")); }

        internal static string Snapshot(string profileRoot)
        { return SnapshotConfigDirectory(ConfigRoot(profileRoot)); }

        // Tests pass only isolated directories. No command-line path, manifest or
        // test delegate can replace Resolve/RequireBinding in the Windows adapter.
        internal static string SnapshotConfigDirectory(string directory)
        { return SnapshotConfigDirectory(directory, null); }

        internal static string SnapshotConfigDirectory(string directory, Action beforeReadBack)
        {
            string root = CanonicalDirectory(directory);
            using (var held = new HeldPaths())
            {
                held.HoldDirectoryChain(root);
                List<Entry> entries = Inspect(root, held);
                if (!entries.Any(e => !e.Directory && e.Relative == "RustDesk.toml")
                    || !entries.Any(e => !e.Directory && e.Relative == "RustDesk2.toml"))
                    throw new InvalidDataException("The original user's RustDesk identity and settings files are required.");
                long total = 0;
                using (var bytes = new MemoryStream())
                using (var writer = new BinaryWriter(bytes, Utf8))
                {
                    writer.Write("Touch365-RustDesk-profile-snapshot-v1");
                    foreach (Entry entry in entries.OrderBy(e => e.Relative, StringComparer.Ordinal))
                    {
                        writer.Write(entry.Directory);
                        writer.Write(entry.Relative);
                        if (entry.Directory) continue;
                        SafeFileHandle handle = Open(entry.FullPath, false);
                        held.AddFile(handle, entry.FullPath);
                        FileInformation before = Information(handle, entry.FullPath, false);
                        long length = ((long)before.SizeHigh << 32) | before.SizeLow;
                        total = checked(total + length);
                        if (length > MaximumFileBytes || total > MaximumTotalBytes)
                            throw new InvalidDataException("The RustDesk profile exceeds safe snapshot bounds.");
                        var stream = new FileStream(handle, FileAccess.Read, 4096, false);
                        held.AddStream(stream);
                        byte[] digest;
                        using (SHA256 hash = SHA256.Create()) digest = hash.ComputeHash(stream);
                        FileInformation after = Information(handle, entry.FullPath, false);
                        if (!SameInformation(before, after) || stream.Position != length || stream.Length != length)
                            throw new IOException("The RustDesk profile changed while its snapshot was read.");
                        writer.Write(length);
                        writer.Write(digest);
                    }
                    if (beforeReadBack != null) beforeReadBack();
                    held.RequirePathsUnchanged();
                    List<Entry> readBack = Inspect(root, held);
                    if (entries.Count != readBack.Count || !entries.OrderBy(e => e.Relative, StringComparer.Ordinal)
                        .Zip(readBack.OrderBy(e => e.Relative, StringComparer.Ordinal), (a, b) => a.Relative == b.Relative && a.Directory == b.Directory).All(v => v))
                        throw new IOException("The RustDesk profile inventory changed while its snapshot was read.");
                    writer.Flush();
                    using (SHA256 hash = SHA256.Create())
                        return BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        private sealed class Entry
        {
            internal string Relative, FullPath;
            internal bool Directory;
        }

        private static List<Entry> Inspect(string root, HeldPaths held)
        {
            var result = new List<Entry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>(); pending.Push(root);
            int files = 0, directories = 0;
            while (pending.Count != 0)
            {
                string current = pending.Pop();
                held.HoldDirectory(current);
                foreach (string child in Directory.GetFileSystemEntries(current))
                {
                    string full = CanonicalDirectory(child);
                    string relative = full.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
                    if (!names.Add(relative)) throw new InvalidDataException("The RustDesk profile has ambiguous file names.");
                    FileAttributes attributes = File.GetAttributes(full);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                        throw new InvalidDataException("The RustDesk profile contains a redirected or special file.");
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    if (directory) { if (++directories > MaximumDirectories) throw new InvalidDataException("The RustDesk profile has too many directories."); pending.Push(full); }
                    else if (++files > MaximumFiles) throw new InvalidDataException("The RustDesk profile has too many files.");
                    result.Add(new Entry { Relative = relative, FullPath = full, Directory = directory });
                }
            }
            return result;
        }

        private sealed class HeldPaths : IDisposable
        {
            private sealed class Held { internal SafeFileHandle Handle; internal string Path; internal bool Directory; internal FileInformation Before; }
            private readonly Dictionary<string, Held> paths = new Dictionary<string, Held>(StringComparer.OrdinalIgnoreCase);
            private readonly List<FileStream> streams = new List<FileStream>();
            internal void HoldDirectoryChain(string path)
            {
                var ancestors = new Stack<string>();
                for (string next = path; !String.IsNullOrEmpty(next); next = Path.GetDirectoryName(next)) ancestors.Push(next);
                while (ancestors.Count != 0) HoldDirectory(ancestors.Pop());
            }
            internal void HoldDirectory(string path)
            {
                if (paths.ContainsKey(path)) return;
                SafeFileHandle handle = Open(path, true);
                try { paths.Add(path, new Held { Handle = handle, Path = path, Directory = true, Before = Information(handle, path, true) }); }
                catch { handle.Dispose(); throw; }
            }
            internal void AddFile(SafeFileHandle handle, string path)
            {
                try { paths.Add(path, new Held { Handle = handle, Path = path, Directory = false, Before = Information(handle, path, false) }); }
                catch { handle.Dispose(); throw; }
            }
            internal void AddStream(FileStream stream) { streams.Add(stream); }
            internal void RequirePathsUnchanged()
            {
                foreach (Held held in paths.Values)
                {
                    FileInformation after = Information(held.Handle, held.Path, held.Directory);
                    if (held.Directory ? !SameIdentity(held.Before, after) : !SameInformation(held.Before, after))
                        throw new IOException("The RustDesk profile changed during verification.");
                }
            }
            public void Dispose()
            {
                foreach (FileStream stream in streams) stream.Dispose();
                foreach (Held held in paths.Values) held.Handle.Dispose();
            }
        }

        private static string CanonicalDirectory(string path)
        {
            string full = UpdateHandoff.CanonicalLocalPath(path);
            if (new DriveInfo(Path.GetPathRoot(full)).DriveType != DriveType.Fixed)
                throw new InvalidDataException("App-only update profiles require a local fixed drive.");
            SafeFiles.NoReparse(full);
            return full;
        }

        private static SafeFileHandle Open(string path, bool directory)
        {
            SafeFileHandle handle = CreateFile(path, directory ? ReadAttributes : GenericRead,
                directory ? ShareRead | ShareWrite : ShareRead, IntPtr.Zero, OpenExisting,
                OpenReparsePoint | (directory ? BackupSemantics : 0), IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("The RustDesk profile could not be held safely for verification.", new Win32Exception(error)); }
            return handle;
        }

        private static FileInformation Information(SafeFileHandle handle, string expected, bool directory)
        {
            FileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
                throw new IOException("The RustDesk profile file identity could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
            const uint invalid = (uint)(FileAttributes.ReparsePoint | FileAttributes.Device);
            if ((information.Attributes & invalid) != 0 || ((information.Attributes & (uint)FileAttributes.Directory) != 0) != directory
                || (!directory && information.Links != 1))
                throw new InvalidDataException("The RustDesk profile contains a redirected, linked or unexpected file.");
            var path = new StringBuilder(1024);
            uint length = GetFinalPathNameByHandle(handle, path, (uint)path.Capacity, 0);
            if (length == 0 || length >= path.Capacity || !path.ToString().StartsWith(@"\\?\", StringComparison.Ordinal)
                || !SamePath(path.ToString().Substring(4), expected))
                throw new InvalidDataException("The RustDesk profile path changed during verification.");
            return information;
        }

        private static bool SamePath(string a, string b)
        { return String.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); }

        private static bool SameInformation(FileInformation a, FileInformation b)
        {
            return SameIdentity(a, b) && a.SizeHigh == b.SizeHigh && a.SizeLow == b.SizeLow && a.Links == b.Links
                && a.WriteHigh == b.WriteHigh && a.WriteLow == b.WriteLow;
        }

        private static bool SameIdentity(FileInformation a, FileInformation b)
        { return a.VolumeSerial == b.VolumeSerial && a.IndexHigh == b.IndexHigh && a.IndexLow == b.IndexLow && a.Attributes == b.Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            internal uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint capacity, uint flags);
    }
}
