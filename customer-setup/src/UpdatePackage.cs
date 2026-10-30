using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Touch365Support
{
    // Uses the existing Checker's owned staging/partial-file pattern. A package is
    // a complete application directory, never an executable installer or script.
    public static class UpdatePackage
    {
        private const int MaximumFiles = 256;
        private const long MaximumBytes = 512L * 1024 * 1024;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

        public static void ExtractVerified(string zipPath, string destination, UpdateManifest manifest)
        {
            ExtractVerified(zipPath, destination, manifest,
                delegate(string path) { Signature.RequireOfficial(path, "downloaded application update"); }, ReadVersion);
        }

        internal static void ExtractVerified(string zipPath, string destination, UpdateManifest manifest,
            Action<string> verifyPublisher, Func<string, Version> readVersion)
        {
            var inventory = Inventory(manifest);
            SafeFiles.NoReparse(zipPath);
            string root = Path.GetFullPath(destination);
            bool owned = false;
            try
            {
                using (var input = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (manifest.PackageBytes <= 0 || manifest.PackageBytes > MaximumBytes || input.Length != manifest.PackageBytes)
                        throw new InvalidDataException("The update package size is not the signed size.");
                    if (!HashEquals(Hash(input), manifest.PackageSha256))
                        throw new InvalidDataException("The update package failed its signed SHA-256 check.");
                    input.Position = 0;
                    using (var archive = new ZipArchive(input, ZipArchiveMode.Read, true))
                    {
                        if (archive.Entries.Count > MaximumFiles * 2)
                            throw new InvalidDataException("The update archive has too many entries.");
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var directories = UpdateProtocol.PackageDirectories(manifest.Files);
                        long total = 0;
                        foreach (var entry in archive.Entries)
                        {
                            bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                            string name = RelativeName(directory ? entry.FullName.Substring(0, entry.FullName.Length - 1) : entry.FullName);
                            if (!seen.Add(name)) throw new InvalidDataException("The update archive has duplicate or case-colliding paths.");
                            uint attributes = unchecked((uint)entry.ExternalAttributes);
                            uint unixType = (attributes >> 16) & 0xF000U;
                            if ((attributes & (uint)FileAttributes.ReparsePoint) != 0 || unixType == 0xA000U ||
                                (unixType != 0 && unixType != 0x8000U && unixType != 0x4000U))
                                throw new InvalidDataException("The update archive contains a link or unsupported entry type.");
                            if (directory)
                            {
                                string canonical;
                                if (!directories.TryGetValue(name, out canonical) || !String.Equals(canonical, name, StringComparison.Ordinal) ||
                                    entry.Length != 0 || (unixType != 0 && unixType != 0x4000U))
                                    throw new InvalidDataException("The update archive contains an unsigned directory.");
                                continue;
                            }
                            UpdateFile expected;
                            if (!inventory.TryGetValue(name, out expected) || !String.Equals(expected.Path, name, StringComparison.Ordinal) ||
                                entry.Length != expected.Bytes || unixType == 0x4000U || (attributes & (uint)FileAttributes.Directory) != 0)
                                throw new InvalidDataException("The update archive does not match its signed inventory.");
                            total = checked(total + entry.Length);
                            if (total > MaximumBytes || (entry.Length > 1024 * 1024 &&
                                (entry.CompressedLength == 0 || entry.Length > entry.CompressedLength * 200)))
                                throw new InvalidDataException("The update archive exceeds its extraction limits.");
                        }
                        if (inventory.Keys.Any(name => !seen.Contains(name)))
                            throw new InvalidDataException("The update archive is missing a signed file.");

                        CreatePrivateNew(root); owned = true;
                        foreach (var entry in archive.Entries)
                        {
                            if (entry.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                            string name = RelativeName(entry.FullName);
                            string target = Inside(root, name);
                            string parent = Path.GetDirectoryName(target);
                            SafeFiles.NoReparse(parent); Directory.CreateDirectory(parent); SafeFiles.NoReparse(parent);
                            using (var content = entry.Open())
                            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                CopyBounded(content, output, inventory[name].Bytes);
                                output.Flush(true);
                            }
                        }
                    }
                }
                VerifyDirectory(root, manifest, verifyPublisher, readVersion);
            }
            catch
            {
                if (owned) { try { DeleteOwned(root, root); } catch { /* Retain only this owned staging tree on cleanup failure. */ } }
                throw;
            }
        }

        public static void VerifyDirectory(string directory, UpdateManifest manifest)
        {
            VerifyDirectory(directory, manifest,
                delegate(string path) { Signature.RequireOfficial(path, "staged application update"); }, ReadVersion);
        }

        internal static void VerifyDirectory(string directory, UpdateManifest manifest,
            Action<string> verifyPublisher, Func<string, Version> readVersion)
        {
            if (verifyPublisher == null || readVersion == null) throw new ArgumentNullException("verifier");
            var inventory = Inventory(manifest);
            string root = Path.GetFullPath(directory);
            var actual = Inspect(root);
            if (actual.Files.Count != inventory.Count) throw new InvalidDataException("The application directory has unsigned files.");
            var allowedDirectories = UpdateProtocol.PackageDirectories(manifest.Files);
            foreach (var expected in inventory)
            {
                string path;
                if (!actual.Files.TryGetValue(expected.Key, out path) || new FileInfo(path).Length != expected.Value.Bytes ||
                    !HashEquals(SafeFiles.Digest(path), expected.Value.Sha256))
                    throw new InvalidDataException("An application file failed its signed inventory check.");
            }
            foreach (string name in actual.Directories)
            {
                string canonical;
                if (!allowedDirectories.TryGetValue(name, out canonical) || !String.Equals(canonical, name, StringComparison.Ordinal))
                    throw new InvalidDataException("The application directory has unsigned or ambiguously cased directories.");
            }
            string executable = Inside(root, "RustDesk.exe");
            verifyPublisher(executable);
            Version version = readVersion(executable);
            if (version == null || !SameVersion(version, manifest.BinaryVersion))
                throw new InvalidDataException("The application version does not match the signed version.");
        }

        public static string Fingerprint(string directory)
        {
            var tree = Inspect(Path.GetFullPath(directory));
            var lines = new List<string>();
            lines.AddRange(tree.Directories.Select(name => "D\0" + name));
            lines.AddRange(tree.Files.Select(pair => "F\0" + pair.Key + "\0" + new FileInfo(pair.Value).Length + "\0" + SafeFiles.Digest(pair.Value)));
            lines.Sort(StringComparer.Ordinal);
            using (var input = new MemoryStream(Encoding.UTF8.GetBytes(String.Join("\n", lines)))) return Hash(input);
        }

        public static void CopyVerified(string source, string destination)
        {
            string original = Path.GetFullPath(source), target = Path.GetFullPath(destination);
            if (target.Equals(original, StringComparison.OrdinalIgnoreCase) || target.StartsWith(original.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An application backup cannot be nested inside its source.");
            string before = Fingerprint(original);
            var tree = Inspect(original);
            bool owned = false;
            try
            {
                CreatePrivateNew(target); owned = true;
                foreach (string name in tree.Directories.OrderBy(name => name.Length))
                    Directory.CreateDirectory(Inside(target, name));
                foreach (var pair in tree.Files)
                {
                    SafeFiles.NoReparse(pair.Value);
                    string output = Inside(target, pair.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    using (var input = new FileStream(pair.Value, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var copy = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { CopyBounded(input, copy, input.Length); copy.Flush(true); }
                }
                if (Fingerprint(original) != before || Fingerprint(target) != before)
                    throw new IOException("The application changed while its verified backup was being made.");
            }
            catch
            {
                if (owned) { try { DeleteOwned(target, target); } catch { } }
                throw;
            }
        }

        private sealed class Tree
        {
            public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly List<string> Directories = new List<string>();
            public long Bytes;
        }

        private static Tree Inspect(string root)
        {
            SafeFiles.NoReparse(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The application directory is missing.");
            var tree = new Tree();
            var pending = new Stack<string>(); pending.Push(root);
            var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count != 0)
            {
                string current = pending.Pop(); SafeFiles.NoReparse(current);
                foreach (string child in Directory.GetFileSystemEntries(current))
                {
                    SafeFiles.NoReparse(child);
                    string name = RelativeName(child.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace(Path.DirectorySeparatorChar, '/'));
                    if (!allNames.Add(name)) throw new InvalidDataException("The application directory has case-colliding paths.");
                    if ((File.GetAttributes(child) & FileAttributes.Directory) != 0)
                    {
                        tree.Directories.Add(name); pending.Push(child);
                        if (tree.Directories.Count > MaximumFiles) throw new InvalidDataException("The application directory has too many folders.");
                    }
                    else
                    {
                        tree.Files.Add(name, child); tree.Bytes = checked(tree.Bytes + new FileInfo(child).Length);
                        if (tree.Files.Count > MaximumFiles || tree.Bytes > MaximumBytes)
                            throw new InvalidDataException("The application directory exceeds its backup limits.");
                    }
                }
            }
            return tree;
        }

        private static Dictionary<string, UpdateFile> Inventory(UpdateManifest manifest)
        {
            if (manifest == null || manifest.BinaryVersion == null || manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > MaximumFiles)
                throw new InvalidDataException("A binary update needs a bounded signed file inventory.");
            var result = new Dictionary<string, UpdateFile>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            foreach (var file in manifest.Files)
            {
                if (file == null) throw new InvalidDataException("An update file is missing.");
                string name = UpdateProtocol.SafePackagePath(file.Path);
                if (result.ContainsKey(name) || !IsHash(file.Sha256) || file.Bytes < 0 || file.Bytes > MaximumBytes)
                    throw new InvalidDataException("The update inventory has a duplicate or invalid file.");
                bytes = checked(bytes + file.Bytes);
                if (bytes > MaximumBytes) throw new InvalidDataException("The update inventory exceeds the package limit.");
                result.Add(name, file);
            }
            if (!result.ContainsKey("RustDesk.exe") || result["RustDesk.exe"].Bytes == 0)
                throw new InvalidDataException("The update inventory needs the root RustDesk executable.");
            foreach (string name in result.Keys)
                if (result.Keys.Any(other => other.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("An update file conflicts with a directory.");
            UpdateProtocol.PackageDirectories(result.Values);
            return result;
        }

        private static string RelativeName(string value)
        {
            return UpdateProtocol.SafeRelativePath(value);
        }

        private static string Inside(string root, string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, RelativeName(relative).Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update path escapes its owned directory.");
            SafeFiles.NoReparse(path); return path;
        }

        private static void CreatePrivateNew(string path)
        {
            SafeFiles.NoReparse(path);
            string parent = Path.GetDirectoryName(path);
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("The private staging parent is missing.");
            if (!CreateDirectory(path, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "A new private update directory could not be created.");
            try { SafeFiles.PrivateDirectory(path, false); }
            catch { try { Directory.Delete(path); } catch { } throw; }
        }

        private static void DeleteOwned(string root, string directory)
        {
            SafeFiles.NoReparse(directory);
            if (directory != root && !directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Cleanup would leave the owned staging directory.");
            foreach (string child in Directory.GetFileSystemEntries(directory))
            {
                SafeFiles.NoReparse(child);
                if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) DeleteOwned(root, child);
                else File.Delete(child);
            }
            Directory.Delete(directory);
        }

        private static void CopyBounded(Stream input, Stream output, long expected)
        {
            var buffer = new byte[81920]; long copied = 0;
            while (true)
            {
                int count = input.Read(buffer, 0, buffer.Length); if (count == 0) break;
                copied = checked(copied + count);
                if (copied > expected || copied > MaximumBytes) throw new InvalidDataException("An update stream exceeded its signed size.");
                output.Write(buffer, 0, count);
            }
            if (copied != expected) throw new EndOfStreamException("An update stream ended before its signed size.");
        }

        private static string Hash(Stream stream)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static bool IsHash(string value) { return value != null && Regex.IsMatch(value, "^[a-fA-F0-9]{64}$"); }
        private static bool HashEquals(string actual, string expected) { return IsHash(expected) && String.Equals(actual, expected, StringComparison.OrdinalIgnoreCase); }
        private static bool SameVersion(Version left, Version right)
        { return left.Major == right.Major && left.Minor == right.Minor && Math.Max(0, left.Build) == Math.Max(0, right.Build) && Math.Max(0, left.Revision) == Math.Max(0, right.Revision); }
        private static Version ReadVersion(string path)
        {
            var match = Regex.Match(FileVersionInfo.GetVersionInfo(path).FileVersion ?? "", @"^([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:\+.*)?$");
            if (!match.Success) throw new InvalidDataException("The downloaded RustDesk version could not be read.");
            return new Version(match.Groups[1].Value);
        }
    }
}
