using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Touch365Support;

internal static class UpdatePackageTests
{
    private static int failed;
    private static int passed;

    private sealed class Entry
    {
        public string Name;
        public byte[] Bytes;
        public int Attributes;
        public Entry(string name, string value) { Name = name; Bytes = Encoding.UTF8.GetBytes(value); }
    }

    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            if (ex is IOException || ex is InvalidDataException || ex is System.ComponentModel.Win32Exception) return;
            throw;
        }
        throw new Exception("An unsafe package was accepted.");
    }
    private static void Test(string name, Action<string> action)
    {
        string fixtureParent = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "package-fixtures");
        Directory.CreateDirectory(fixtureParent);
        string root = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); passed++; Console.WriteLine("PASS package " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL package " + name + ": " + ex.Message); }
        finally
        {
            try
            {
                string absolute = Path.GetFullPath(root);
                if (!absolute.StartsWith(Path.GetFullPath(fixtureParent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Fixture cleanup would escape its root.");
                SafeFiles.NoReparse(absolute); Directory.Delete(absolute, true);
            }
            catch { /* A failed cleanup remains inside the ignored test output. */ }
        }
    }
    private static string Digest(byte[] bytes)
    { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static UpdateManifest Manifest(IEnumerable<Entry> entries)
    {
        return new UpdateManifest {
            BinaryVersion = new Version(1, 5, 1),
            Files = entries.Where(entry => !entry.Name.EndsWith("/", StringComparison.Ordinal)).Select(entry =>
                new UpdateFile { Path = entry.Name, Bytes = entry.Bytes.Length, Sha256 = Digest(entry.Bytes) }).ToList()
        };
    }
    private static string Zip(string root, IList<Entry> entries, UpdateManifest manifest)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using (var file = new FileStream(path, FileMode.CreateNew))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.Optimal);
                entry.ExternalAttributes = item.Attributes;
                using (var content = entry.Open()) content.Write(item.Bytes, 0, item.Bytes.Length);
            }
        manifest.PackageBytes = new FileInfo(path).Length;
        manifest.PackageSha256 = SafeFiles.Digest(path);
        return path;
    }
    private static void ExtractFixture(string zip, string destination, UpdateManifest manifest)
    { UpdatePackage.ExtractVerified(zip, destination, manifest, delegate { }, delegate { return new Version(1, 5, 1); }); }
    private static UpdateManifest BindFixture(UpdateManifest manifest)
    {
        var binary = new XElement("binary", new XAttribute("version", manifest.BinaryVersion.ToString()),
            new XAttribute("url", "https://downloads.example.com/package.zip"), new XAttribute("bytes", manifest.PackageBytes),
            new XAttribute("sha256", manifest.PackageSha256), String.IsNullOrEmpty(manifest.PackageLayout) ? null : new XAttribute("layout", manifest.PackageLayout), manifest.Files.Select(file => new XElement("file",
                new XAttribute("path", file.Path), new XAttribute("bytes", file.Bytes), new XAttribute("sha256", file.Sha256))));
        return UpdateProtocolTests.ParseFixtureBody(binary.ToString(SaveOptions.DisableFormatting));
    }
    private static Entry[] Normal()
    { return new[] { new Entry("RustDesk.exe", "unsigned executable fixture"), new Entry("data/settings.json", "{\"fixture\":true}") }; }

    private static Entry[] Complete()
    { return UpdatePackage.CompleteFiles.Select(path => new Entry(path, "bounded runtime fixture: " + path) { Bytes = path == "RustDesk.exe" ? new byte[512] : Encoding.UTF8.GetBytes("bounded runtime fixture: " + path) }).ToArray(); }

    private static void ExtractComplete(string zip, string destination, UpdateManifest manifest, string broker,
        Action<string> publisher, Action<string, string> microsoft)
    { UpdatePackage.ExtractVerified(zip, destination, manifest, publisher, delegate { return new Version(1, 5, 1); }, microsoft, broker); }

    public static int Run()
    {
        failed = 0; passed = 0;
        Test("extracts an exact inventory and checks publisher plus version", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            int publisher = 0, version = 0;
            string destination = Path.Combine(root, "stage");
            UpdatePackage.ExtractVerified(zip, destination, manifest, delegate(string path)
            {
                Assert(path == Path.Combine(destination, "RustDesk.exe"), "Publisher check selected a different file."); publisher++;
            }, delegate(string path) { version++; return new Version(1, 5, 1, 0); });
            Assert(publisher == 1 && version == 1, "Required binary checks were not completed.");
            Assert(SafeFiles.Digest(Path.Combine(destination, "data", "settings.json")) == manifest.Files[1].Sha256, "Resource bytes changed.");
        });
        Test("rejects a package digest mismatch before making a directory", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            manifest.PackageSha256 = new string('0', 64); string destination = Path.Combine(root, "stage");
            Reject(delegate { ExtractFixture(zip, destination, manifest); });
            Assert(!Directory.Exists(destination), "Untrusted bytes were extracted.");
        });
        Test("rejects truncated package size", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            manifest.PackageBytes++; Reject(delegate { ExtractFixture(zip, Path.Combine(root, "stage"), manifest); });
        });
        Test("rejects signed file hash or size mismatches and cleans owned staging", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            manifest.Files[0].Sha256 = new string('0', 64); string destination = Path.Combine(root, "hash-stage");
            Reject(delegate { ExtractFixture(zip, destination, manifest); });
            Assert(!Directory.Exists(destination), "Failed hash left unverified staging available.");
            manifest.Files[0].Sha256 = Digest(entries[0].Bytes); manifest.Files[0].Bytes++;
            Reject(delegate { ExtractFixture(zip, Path.Combine(root, "size-stage"), manifest); });
        });
        Test("rejects extra files missing files and case collisions", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries);
            string extra = Zip(root, entries.Concat(new[] { new Entry("extra.txt", "unsigned") }).ToArray(), manifest);
            Reject(delegate { ExtractFixture(extra, Path.Combine(root, "extra"), manifest); });
            string missing = Zip(root, new[] { entries[0] }, manifest);
            Reject(delegate { ExtractFixture(missing, Path.Combine(root, "missing"), manifest); });
            string collision = Zip(root, entries.Concat(new[] { new Entry("RUSTDESK.EXE", "collision") }).ToArray(), manifest);
            Reject(delegate { ExtractFixture(collision, Path.Combine(root, "collision"), manifest); });
        });
        Test("rejects traversal ADS reserved names scripts and a second executable", delegate(string root)
        {
            foreach (string name in new[] { "../outside.txt", "/absolute.txt", "a\\b.txt", "a.txt:stream", "NUL.txt", "NUL .txt", "COM1.dat", " leading.txt", "nested/trailing. /file.txt", "run.ps1", "other.exe" })
            {
                var entries = new[] { Normal()[0], new Entry(name, "unsafe") }; var manifest = Manifest(entries);
                string zip = Zip(root, entries, manifest);
                Reject(delegate { ExtractFixture(zip, Path.Combine(root, Guid.NewGuid().ToString("N")), manifest); });
            }
            Assert(!File.Exists(Path.Combine(root, "outside.txt")), "Traversal escaped staging.");
        });
        Test("rejects archive symbolic links and unsigned empty folders", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries);
            var link = new Entry("data/link.txt", "target") { Attributes = unchecked((int)(0xA1FFU << 16)) };
            var withLink = entries.Concat(new[] { link }).ToArray(); var linkManifest = Manifest(withLink);
            string zip = Zip(root, withLink, linkManifest);
            Reject(delegate { ExtractFixture(zip, Path.Combine(root, "link"), linkManifest); });
            string directoryZip = Zip(root, entries.Concat(new[] { new Entry("unsigned/", "") }).ToArray(), manifest);
            Reject(delegate { ExtractFixture(directoryZip, Path.Combine(root, "directory"), manifest); });
        });
        Test("accepts only inventory parent directory records", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries);
            string zip = Zip(root, new[] { new Entry("data/", ""), entries[0], entries[1] }, manifest);
            ExtractFixture(zip, Path.Combine(root, "stage"), manifest);
        });
        Test("canonical assets directory passes protocol and extraction with the same inventory", delegate(string root)
        {
            var entries = new[] { new Entry("assets/", ""), new Entry("RustDesk.exe", "") { Bytes = new byte[512] },
                new Entry("assets/config.json", "{}") };
            var manifest = Manifest(entries); string zip = Zip(root, entries, manifest); manifest = BindFixture(manifest);
            UpdateProtocol.VerifyPackage(zip, manifest);
            string destination = Path.Combine(root, "stage"); ExtractFixture(zip, destination, manifest);
            Assert(File.ReadAllText(Path.Combine(destination, "assets", "config.json")) == "{}", "Canonical resource extraction changed.");
        });
        Test("case-ambiguous and doubled trailing directory separators fail both parsers", delegate(string root)
        {
            foreach (string directory in new[] { "ASSETS/", "assets//" })
            {
                var files = new[] { new Entry("RustDesk.exe", "") { Bytes = new byte[512] }, new Entry("assets/config.json", "{}") };
                var manifest = Manifest(files);
                string zip = Zip(root, files.Concat(new[] { new Entry(directory, "") }).ToArray(), manifest); manifest = BindFixture(manifest);
                Reject(delegate { UpdateProtocol.VerifyPackage(zip, manifest); });
                Reject(delegate { ExtractFixture(zip, Path.Combine(root, Guid.NewGuid().ToString("N")), manifest); });
            }
        });
        Test("rejects a compressed expansion beyond the ratio limit", delegate(string root)
        {
            var entries = new[] { Normal()[0], new Entry("large.dat", "") { Bytes = new byte[2 * 1024 * 1024] } };
            var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            Reject(delegate { ExtractFixture(zip, Path.Combine(root, "stage"), manifest); });
        });
        Test("bounds implied directories before creating staging", delegate(string root)
        {
            var entries = new List<Entry> { Normal()[0] };
            for (int i = 0; i < 129; i++) entries.Add(new Entry("dir-" + i + "/nested/data.txt", ""));
            var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            string destination = Path.Combine(root, "stage");
            Reject(delegate { ExtractFixture(zip, destination, manifest); });
            Assert(!Directory.Exists(destination), "An oversized directory inventory was partly extracted.");
        });
        Test("never overwrites an existing destination", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            string destination = Path.Combine(root, "existing"); Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep");
            Reject(delegate { ExtractFixture(zip, destination, manifest); });
            Assert(File.ReadAllText(Path.Combine(destination, "keep.txt")) == "keep", "Existing user content changed.");
        });
        Test("production verifier rejects unsigned application and cleans staging", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            string destination = Path.Combine(root, "stage");
            Reject(delegate { UpdatePackage.ExtractVerified(zip, destination, manifest); });
            Assert(!Directory.Exists(destination), "Publisher failure did not clean owned staging.");
        });
        Test("version mismatch is rejected after publisher verification", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            Reject(delegate { UpdatePackage.ExtractVerified(zip, Path.Combine(root, "stage"), manifest,
                delegate { }, delegate { return new Version(1, 5, 0); }); });
        });
        Test("directory verification catches changes extras and unsigned folders", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            string destination = Path.Combine(root, "stage"); ExtractFixture(zip, destination, manifest);
            File.AppendAllText(Path.Combine(destination, "data", "settings.json"), "changed");
            Reject(delegate { UpdatePackage.VerifyDirectory(destination, manifest, delegate { }, delegate { return new Version(1, 5, 1); }); });
            File.WriteAllBytes(Path.Combine(destination, "data", "settings.json"), entries[1].Bytes);
            File.WriteAllText(Path.Combine(destination, "extra.txt"), "extra");
            Reject(delegate { UpdatePackage.VerifyDirectory(destination, manifest, delegate { }, delegate { return new Version(1, 5, 1); }); });
            File.Delete(Path.Combine(destination, "extra.txt")); Directory.CreateDirectory(Path.Combine(destination, "empty"));
            Reject(delegate { UpdatePackage.VerifyDirectory(destination, manifest, delegate { }, delegate { return new Version(1, 5, 1); }); });
        });
        Test("verified copying preserves complete directory identity and detects change", delegate(string root)
        {
            string source = Path.Combine(root, "original"); Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            File.WriteAllText(Path.Combine(source, "RustDesk.exe"), "fixture");
            string before = UpdatePackage.Fingerprint(source);
            string backup = Path.Combine(root, "backup"); UpdatePackage.CopyVerified(source, backup);
            Assert(UpdatePackage.Fingerprint(backup) == before, "Backup identity changed.");
            Assert(Directory.Exists(Path.Combine(backup, "empty")), "Empty folder was lost.");
            File.AppendAllText(Path.Combine(source, "RustDesk.exe"), "changed");
            Assert(UpdatePackage.Fingerprint(source) != before, "Content change was not reflected in fingerprint.");
            Reject(delegate { UpdatePackage.CopyVerified(source, backup); });
            Reject(delegate { UpdatePackage.CopyVerified(source, Path.Combine(source, "nested")); });
        });
        Test("complete profile requires every reviewed runtime file and rejects legacy or unknown layouts", delegate(string root)
        {
            var entries = Complete(); var manifest = Manifest(entries); manifest.PackageLayout = UpdatePackage.CompleteLayout;
            UpdatePackage.RequireCompleteApplication(manifest);
            manifest.Files.RemoveAll(file => file.Path == "data/app.so");
            Reject(delegate { UpdatePackage.RequireCompleteApplication(manifest); });
            manifest = Manifest(entries); manifest.PackageLayout = "unknown-layout";
            Reject(delegate { UpdatePackage.RequireCompleteApplication(manifest); });
            manifest.PackageLayout = null;
            Reject(delegate { UpdatePackage.RequireCompleteApplication(manifest); });
        });
        Test("signed complete profile authenticates expanded assets but never generic scripts or second executables", delegate(string root)
        {
            var entries = Complete(); var manifest = Manifest(entries); manifest.PackageLayout = UpdatePackage.CompleteLayout;
            string zip = Zip(root, entries, manifest); manifest = BindFixture(manifest);
            Assert(manifest.PackageLayout == UpdatePackage.CompleteLayout, "Signed layout was lost.");
            UpdateProtocol.VerifyPackage(zip, manifest);
            foreach (string path in new[] { "run.js", "data/flutter_assets/packages/wakelock_plus/assets/other.js", "data/flutter_assets/assets/run.js", "other.exe", "drivers/new.inf", UpdatePackage.RetainedShortcut })
                Reject(delegate { UpdateProtocol.SafePackagePath(path, UpdatePackage.CompleteLayout); });
            Reject(delegate { UpdateProtocol.SafePackagePath("data/flutter_assets/packages/wakelock_plus/assets/no_sleep.js"); });
            Reject(delegate { UpdateProtocol.SafePackagePath("RuntimeBroker_rustdesk.exe"); });
            manifest.PackageLayout = null;
            Reject(delegate { UpdateProtocol.VerifyPackage(zip, manifest); });
        });
        Test("complete extraction checks every application PE publisher and exact trusted Windows broker", delegate(string root)
        {
            var entries = Complete(); var manifest = Manifest(entries); manifest.PackageLayout = UpdatePackage.CompleteLayout;
            string zip = Zip(root, entries, manifest), broker = Path.Combine(root, "trusted-system-broker.exe");
            File.WriteAllBytes(broker, entries.Single(entry => entry.Name == "RuntimeBroker_rustdesk.exe").Bytes);
            var publisherPaths = new List<string>(); var microsoftPaths = new List<string>();
            string destination = Path.Combine(root, "stage");
            ExtractComplete(zip, destination, manifest, broker, delegate(string path) { publisherPaths.Add(path); }, delegate(string path, string publisher) {
                Assert(publisher == "Microsoft Windows" || publisher == "Microsoft Windows Hardware Compatibility Publisher", "Unexpected auxiliary trust policy.");
                microsoftPaths.Add(path);
            });
            int applicationPeCount = entries.Count(entry => entry.Name == "RustDesk.exe" || (entry.Name.IndexOf('/') < 0 && entry.Name.EndsWith(".dll", StringComparison.Ordinal)));
            Assert(publisherPaths.Count == applicationPeCount && publisherPaths.Distinct().Count() == applicationPeCount, "Application PE publisher coverage is incomplete.");
            Assert(microsoftPaths.Count == 4 && microsoftPaths.Contains(broker), "Broker/catalog trust checks were not all performed.");
            Assert(File.Exists(Path.Combine(destination, "data", "app.so")) && File.Exists(Path.Combine(destination, "data", "flutter_assets", "packages", "wakelock_plus", "assets", "no_sleep.js")), "Reviewed Flutter runtime resources were omitted.");
        });
        Test("complete extraction refuses a differently sourced broker despite accepted publisher", delegate(string root)
        {
            var entries = Complete(); var manifest = Manifest(entries); manifest.PackageLayout = UpdatePackage.CompleteLayout;
            string zip = Zip(root, entries, manifest), broker = Path.Combine(root, "trusted-system-broker.exe");
            File.WriteAllText(broker, "a different Microsoft executable is not the local RuntimeBroker");
            string destination = Path.Combine(root, "stage");
            Reject(delegate { ExtractComplete(zip, destination, manifest, broker, delegate { }, delegate { }); });
            Assert(!Directory.Exists(destination), "Untrusted broker left a usable staged directory.");
        });
        Test("complete extraction fails if an auxiliary catalog publisher is untrusted", delegate(string root)
        {
            var entries = Complete(); var manifest = Manifest(entries); manifest.PackageLayout = UpdatePackage.CompleteLayout;
            string zip = Zip(root, entries, manifest), broker = Path.Combine(root, "trusted-system-broker.exe");
            File.WriteAllBytes(broker, entries.Single(entry => entry.Name == "RuntimeBroker_rustdesk.exe").Bytes);
            Reject(delegate { ExtractComplete(zip, Path.Combine(root, "stage"), manifest, broker, delegate { }, delegate(string path, string publisher) {
                if (path.EndsWith(".cat", StringComparison.Ordinal)) throw new InvalidDataException("Fixture rejected catalog trust.");
            }); });
        });
        Test("retained shortcut must have exact protected hash and cannot authorize another extra file", delegate(string root)
        {
            var entries = Normal(); var manifest = Manifest(entries); string zip = Zip(root, entries, manifest);
            string destination = Path.Combine(root, "stage"); ExtractFixture(zip, destination, manifest);
            string shortcut = Path.Combine(destination, UpdatePackage.RetainedShortcut); File.WriteAllText(shortcut, "opaque pre-existing shortcut fixture");
            string hash = SafeFiles.Digest(shortcut);
            UpdatePackage.VerifyDirectoryCore(destination, manifest, hash, delegate { }, delegate { return new Version(1, 5, 1); }, delegate { }, "unused");
            Reject(delegate { UpdatePackage.VerifyDirectoryCore(destination, manifest, null, delegate { }, delegate { return new Version(1, 5, 1); }, delegate { }, "unused"); });
            File.AppendAllText(shortcut, "changed");
            Reject(delegate { UpdatePackage.VerifyDirectoryCore(destination, manifest, hash, delegate { }, delegate { return new Version(1, 5, 1); }, delegate { }, "unused"); });
            File.WriteAllText(shortcut, "opaque pre-existing shortcut fixture"); File.WriteAllText(Path.Combine(destination, "other.exe"), "unapproved");
            Reject(delegate { UpdatePackage.VerifyDirectoryCore(destination, manifest, hash, delegate { }, delegate { return new Version(1, 5, 1); }, delegate { }, "unused"); });
        });
        Console.WriteLine("RESULT package " + passed + " passed, " + failed + " failed.");
        return failed;
    }
}
