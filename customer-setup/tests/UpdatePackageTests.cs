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
            new XAttribute("sha256", manifest.PackageSha256), manifest.Files.Select(file => new XElement("file",
                new XAttribute("path", file.Path), new XAttribute("bytes", file.Bytes), new XAttribute("sha256", file.Sha256))));
        return UpdateProtocolTests.ParseFixtureBody(binary.ToString(SaveOptions.DisableFormatting));
    }
    private static Entry[] Normal()
    { return new[] { new Entry("RustDesk.exe", "unsigned executable fixture"), new Entry("data/settings.json", "{\"fixture\":true}") }; }

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
        Console.WriteLine("RESULT package " + passed + " passed, " + failed + " failed.");
        return failed;
    }
}
