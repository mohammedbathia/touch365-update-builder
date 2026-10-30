using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace Touch365Support
{
    // Uses the existing Checker's owned staging/partial-file pattern. A package is
    // a complete application directory, never an executable installer or script.
    public static class UpdatePackage
    {
        public const string CompleteLayout = "rustdesk-windows-flutter-v1";
        public const string RetainedShortcut = "Uninstall RustDesk.lnk";
        private const int MaximumFiles = 256;
        private const long MaximumBytes = 512L * 1024 * 1024;
        // A reviewed full Flutter runtime, not an extension-based executable allowlist.
        // A changed upstream layout requires a reviewed profile revision. The one .js
        // file is a fixed upstream Flutter resource; it is never run by this updater.
        internal static readonly string[] CompleteFiles = {
            "data/app.so", "data/icudtl.dat", "data/flutter_assets/AssetManifest.bin",
            "data/flutter_assets/AssetManifest.json", "data/flutter_assets/FontManifest.json",
            "data/flutter_assets/NOTICES.Z", "data/flutter_assets/fonts/MaterialIcons-Regular.otf",
            "data/flutter_assets/shaders/ink_sparkle.frag",
            "data/flutter_assets/assets/actions_mobile.svg", "data/flutter_assets/assets/actions.svg",
            "data/flutter_assets/assets/address_book.ttf", "data/flutter_assets/assets/android.svg",
            "data/flutter_assets/assets/arrow.svg", "data/flutter_assets/assets/auth-apple.svg",
            "data/flutter_assets/assets/auth-auth0.svg",
            "data/flutter_assets/assets/auth-default.svg", "data/flutter_assets/assets/auth-facebook.svg",
            "data/flutter_assets/assets/auth-github.svg", "data/flutter_assets/assets/auth-gitlab.svg",
            "data/flutter_assets/assets/auth-google.svg", "data/flutter_assets/assets/auth-microsoft.svg",
            "data/flutter_assets/assets/auth-okta.svg", "data/flutter_assets/assets/call_end.svg",
            "data/flutter_assets/assets/call_wait.svg", "data/flutter_assets/assets/chat.svg",
            "data/flutter_assets/assets/chat2.svg", "data/flutter_assets/assets/checkbox-outline.svg",
            "data/flutter_assets/assets/chevron_up_chevron_down.svg", "data/flutter_assets/assets/close.svg",
            "data/flutter_assets/assets/device_group.ttf", "data/flutter_assets/assets/display_switcher.svg",
            "data/flutter_assets/assets/display.svg", "data/flutter_assets/assets/dots.svg",
            "data/flutter_assets/assets/file_transfer.svg", "data/flutter_assets/assets/file.svg",
            "data/flutter_assets/assets/folder_new.svg", "data/flutter_assets/assets/folder.svg",
            "data/flutter_assets/assets/fullscreen_exit.svg", "data/flutter_assets/assets/fullscreen.svg",
            "data/flutter_assets/assets/gestures.ttf", "data/flutter_assets/assets/home.svg",
            "data/flutter_assets/assets/icon.svg", "data/flutter_assets/assets/insecure_relay.svg",
            "data/flutter_assets/assets/insecure.svg", "data/flutter_assets/assets/kb_layout_iso.svg",
            "data/flutter_assets/assets/kb_layout_not_iso.svg", "data/flutter_assets/assets/keyboard_mouse.svg",
            "data/flutter_assets/assets/linux.svg",
            "data/flutter_assets/assets/mac.svg", "data/flutter_assets/assets/message_24dp_5F6368.svg",
            "data/flutter_assets/assets/more.ttf", "data/flutter_assets/assets/peer_searchbar.ttf",
            "data/flutter_assets/assets/pinned.svg", "data/flutter_assets/assets/rec.svg",
            "data/flutter_assets/assets/record_screen.svg", "data/flutter_assets/assets/refresh.svg",
            "data/flutter_assets/assets/scam.png", "data/flutter_assets/assets/screen.svg",
            "data/flutter_assets/assets/search.svg", "data/flutter_assets/assets/secure_relay.svg",
            "data/flutter_assets/assets/secure.svg", "data/flutter_assets/assets/tabbar.ttf",
            "data/flutter_assets/assets/transfer.svg", "data/flutter_assets/assets/trash.svg",
            "data/flutter_assets/assets/unpinned.svg", "data/flutter_assets/assets/voice_call_waiting.svg",
            "data/flutter_assets/assets/voice_call.svg", "data/flutter_assets/assets/win.svg",
            "data/flutter_assets/packages/dash_chat_2/assets/placeholder.png",
            "data/flutter_assets/packages/dash_chat_2/assets/profile_placeholder.png",
            "data/flutter_assets/packages/flex_color_picker/assets/opacity.png",
            "data/flutter_assets/packages/wakelock_plus/assets/no_sleep.js",
            "data/flutter_assets/packages/window_manager/images/ic_chrome_close.png",
            "data/flutter_assets/packages/window_manager/images/ic_chrome_maximize.png",
            "data/flutter_assets/packages/window_manager/images/ic_chrome_minimize.png",
            "data/flutter_assets/packages/window_manager/images/ic_chrome_unmaximize.png",
            "desktop_drop_plugin.dll", "desktop_multi_window_plugin.dll", "dylib_virtual_display.dll",
            "file_selector_windows_plugin.dll", "flutter_custom_cursor_plugin.dll",
            "flutter_gpu_texture_renderer_plugin.dll", "flutter_windows.dll", "librustdesk.dll",
            "meta.toml", "printer_driver_adapter.dll", "RuntimeBroker_rustdesk.exe", "RustDesk.exe",
            "screen_retriever_plugin.dll", "texture_rgba_renderer_plugin.dll", "uni_links_desktop_plugin.dll",
            "url_launcher_windows_plugin.dll", "window_manager_plugin.dll", "window_size_plugin.dll", "WindowInjection.dll",
            "drivers/RustDeskPrinterDriver/RustDeskPrinterDriver-manifest.ini",
            "drivers/RustDeskPrinterDriver/rustdeskprinterdriver.cat",
            "drivers/RustDeskPrinterDriver/RustDeskPrinterDriver.gpd",
            "drivers/RustDeskPrinterDriver/RustDeskPrinterDriver.inf",
            "drivers/RustDeskPrinterDriver/RustDeskPrinterDriverRenderFilter-PipelineConfig.xml",
            "drivers/RustDeskPrinterDriver/RustDeskPrinterDriverRenderFilter.dll",
            "usbmmidd_v2/idd_instructions.txt", "usbmmidd_v2/License.txt", "usbmmidd_v2/usbmmidd.cat",
            "usbmmidd_v2/usbmmIdd.inf", "usbmmidd_v2/x64/usbmmIdd.dll"
        };
        private static readonly HashSet<string> CompletePaths = new HashSet<string>(CompleteFiles, StringComparer.Ordinal);

        internal static bool IsCompleteApplicationPath(string path) { return CompletePaths.Contains(path); }

        // The official installer overlays rather than removing old runtime files.
        // These two reviewed 1.4 assets are absent from the pinned 1.5 payload.
        // A source-preparation copy may omit them; metadata/apply may never add them.
        public static bool IsPreparationOnlyExtra(string path)
        { return path == RetainedShortcut || path == "data/flutter_assets/assets/auth-azure.svg" || path == "data/flutter_assets/assets/keyboard.svg"; }

        public static void RequireCompleteApplication(UpdateManifest manifest)
        {
            if (manifest == null || manifest.BinaryVersion == null || manifest.PackageLayout != CompleteLayout || manifest.Files == null)
                throw new InvalidDataException("This update requires a signed complete RustDesk Windows Flutter layout.");
            var paths = new HashSet<string>(StringComparer.Ordinal);
            long bytes = 0;
            foreach (UpdateFile file in manifest.Files)
            {
                if (file == null || !CompletePaths.Contains(file.Path) || !paths.Add(file.Path) || file.Bytes <= 0 || file.Bytes > MaximumBytes || !IsHash(file.Sha256))
                    throw new InvalidDataException("The complete RustDesk inventory is unknown, duplicated or empty.");
                bytes = checked(bytes + file.Bytes);
                if (bytes > MaximumBytes) throw new InvalidDataException("The complete RustDesk inventory exceeds its expansion bound.");
            }
            if (paths.Count != CompletePaths.Count || CompleteFiles.Any(path => !paths.Contains(path)))
                throw new InvalidDataException("The signed complete application package is missing a required runtime file.");
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

        public static void ExtractVerified(string zipPath, string destination, UpdateManifest manifest)
        {
            ExtractVerified(zipPath, destination, manifest,
                delegate(string path) { Signature.RequireOfficial(path, "downloaded application update"); }, ReadVersion);
        }

        internal static void ExtractVerified(string zipPath, string destination, UpdateManifest manifest,
            Action<string> verifyPublisher, Func<string, Version> readVersion)
        { ExtractVerified(zipPath, destination, manifest, verifyPublisher, readVersion, VerifyMicrosoftPublisher, SystemBroker()); }

        internal static void ExtractVerified(string zipPath, string destination, UpdateManifest manifest,
            Action<string> verifyPublisher, Func<string, Version> readVersion, Action<string, string> verifyMicrosoft, string systemBroker)
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
                        var directories = UpdateProtocol.PackageDirectories(manifest.Files, manifest.PackageLayout);
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
                VerifyDirectoryCore(root, manifest, null, verifyPublisher, readVersion, verifyMicrosoft, systemBroker);
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
        { VerifyDirectoryCore(directory, manifest, null, verifyPublisher, readVersion, VerifyMicrosoftPublisher, SystemBroker()); }

        // Only this opaque, locally retained installer shortcut may be outside the
        // signed package inventory. Its hash comes from the protected transaction,
        // never from metadata; it is copied unchanged and never opened/executed.
        public static void VerifyDirectoryWithRetainedShortcut(string directory, UpdateManifest manifest, string expectedShortcutHash)
        {
            VerifyDirectoryCore(directory, manifest, expectedShortcutHash,
                delegate(string path) { Signature.RequireOfficial(path, "staged application update"); }, ReadVersion,
                VerifyMicrosoftPublisher, SystemBroker());
        }

        internal static void VerifyDirectoryCore(string directory, UpdateManifest manifest, string expectedShortcutHash,
            Action<string> verifyPublisher, Func<string, Version> readVersion, Action<string, string> verifyMicrosoft, string systemBroker)
        {
            if (verifyPublisher == null || readVersion == null) throw new ArgumentNullException("verifier");
            var inventory = Inventory(manifest);
            string root = Path.GetFullPath(directory);
            var actual = Inspect(root);
            if (!String.IsNullOrEmpty(expectedShortcutHash))
            {
                string retained;
                if (!IsHash(expectedShortcutHash) || !actual.Files.TryGetValue(RetainedShortcut, out retained) ||
                    !actual.Files.Keys.Contains(RetainedShortcut, StringComparer.Ordinal) ||
                    !HashEquals(SafeFiles.Digest(retained), expectedShortcutHash))
                    throw new InvalidDataException("The retained installer shortcut differs from its protected backup.");
                actual.Files.Remove(RetainedShortcut);
            }
            if (actual.Files.Count != inventory.Count) throw new InvalidDataException("The application directory has unsigned files.");
            var allowedDirectories = UpdateProtocol.PackageDirectories(manifest.Files, manifest.PackageLayout);
            foreach (var expected in inventory)
            {
                string path;
                if (!actual.Files.TryGetValue(expected.Key, out path) || !actual.Files.Keys.Contains(expected.Key, StringComparer.Ordinal) || new FileInfo(path).Length != expected.Value.Bytes ||
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
            if (manifest.PackageLayout == CompleteLayout)
            {
                RequireCompleteApplication(manifest);
                // The DLLs are executable application code, not mere resource data.
                foreach (string name in CompleteFiles.Where(path => path.IndexOf('/') < 0 && path.EndsWith(".dll", StringComparison.Ordinal)))
                    verifyPublisher(Inside(root, name));
                if (verifyMicrosoft == null || String.IsNullOrEmpty(systemBroker)) throw new ArgumentNullException("Microsoft verifier");
                string broker = Inside(root, "RuntimeBroker_rustdesk.exe");
                SafeFiles.NoReparse(systemBroker);
                verifyMicrosoft(systemBroker, "Microsoft Windows");
                verifyMicrosoft(broker, "Microsoft Windows");
                if (!HashEquals(SafeFiles.Digest(broker), SafeFiles.Digest(systemBroker)))
                    throw new InvalidDataException("The broker helper must be this Windows installation's exact trusted RuntimeBroker.");
                // Driver payloads are inert data here; no driver install or command
                // is run. Authenticate their catalogs in addition to signed hashes.
                verifyMicrosoft(Inside(root, "drivers/RustDeskPrinterDriver/rustdeskprinterdriver.cat"), "Microsoft Windows Hardware Compatibility Publisher");
                verifyMicrosoft(Inside(root, "usbmmidd_v2/usbmmidd.cat"), "Microsoft Windows Hardware Compatibility Publisher");
            }
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
                string name = UpdateProtocol.SafePackagePath(file.Path, manifest.PackageLayout);
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
            UpdateProtocol.PackageDirectories(result.Values, manifest.PackageLayout);
            if (!String.IsNullOrEmpty(manifest.PackageLayout)) RequireCompleteApplication(manifest);
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
        private static string SystemBroker()
        { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "RuntimeBroker.exe"); }

        private static void VerifyMicrosoftPublisher(string path, string expectedPublisher)
        {
            SafeFiles.NoReparse(path);
            int status = NativeTrust(path);
            if (unchecked((uint)status) == 0x800B010AU) { System.Threading.Thread.Sleep(1000); SafeFiles.NoReparse(path); status = NativeTrust(path); }
            if (status != 0) throw new InvalidDataException("Windows did not approve the auxiliary publisher signature (" + Path.GetFileName(path) + ", 0x" + unchecked((uint)status).ToString("X8") + ").");
            using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                if (certificate.GetNameInfo(X509NameType.SimpleName, false) != expectedPublisher)
                    throw new InvalidDataException("The auxiliary file has an unexpected publisher: " + Path.GetFileName(path) + ".");
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class PackageFileTrust {
            public uint Size = (uint)Marshal.SizeOf(typeof(PackageFileTrust));
            [MarshalAs(UnmanagedType.LPWStr)] public string Path;
            public IntPtr Handle = IntPtr.Zero, Subject = IntPtr.Zero;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class PackageTrustData {
            public uint Size = (uint)Marshal.SizeOf(typeof(PackageTrustData));
            public IntPtr Policy = IntPtr.Zero, Sip = IntPtr.Zero;
            public uint UiChoice = 2, Revocation = 0, UnionChoice = 1;
            public IntPtr File;
            public uint StateAction = 0;
            public IntPtr State = IntPtr.Zero, Url = IntPtr.Zero;
            public uint Flags = 0, Context = 0; // Same ordinary Windows policy as Signature.RequireOfficial.
        }
        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, PackageTrustData data);
        private static int NativeTrust(string path)
        {
            var file = new PackageFileTrust { Path = path };
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(file));
            try {
                Marshal.StructureToPtr(file, pointer, false);
                return WinVerifyTrust(new IntPtr(-1), new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), new PackageTrustData { File = pointer });
            }
            finally { Marshal.DestroyStructure(pointer, typeof(PackageFileTrust)); Marshal.FreeHGlobal(pointer); }
        }
        private static Version ReadVersion(string path)
        {
            var match = Regex.Match(FileVersionInfo.GetVersionInfo(path).FileVersion ?? "", @"^([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:\+.*)?$");
            if (!match.Success) throw new InvalidDataException("The downloaded RustDesk version could not be read.");
            return new Version(match.Groups[1].Value);
        }
    }
}
