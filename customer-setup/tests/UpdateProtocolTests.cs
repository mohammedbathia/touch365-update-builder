using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Touch365Support;

public static class UpdateProtocolTests
{
    static int failed;
    static readonly DateTime Now = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
    // Public verification values from RFC 7515 Appendix A.2. No private key is stored or generated.
    const string Modulus = "ofgWCuLjybRlzo0tZWJjNiuSfb4p4fAkd_wWJcyQoTbji9k0l8W26mPddxHmfHQp-Vaw-4qPCJrcS2mJPMEzP1Pt0Bm4d4QlL-yRT-SFd2lZS-pCgNMsD1W_YpRPEwOWvG6b32690r2jZ47soMZo9wGzjb_7OMg0LOL-bSf63kpaSHSXndS5z5rexMdbBYUsLA9e-KXBdQOS-UTo7WTBEMa2R2CapHg665xsmtdVMTBQY4uDZlxvb3qCo5ZwKh9kG4LT6_I5IhlJH7aGhyxXFvUK-DWNmoudF8NAco9_h9iaGNj8q2ethFkMLs91kzk2PAcDTW9gb54h4FRWyuXpoQ";
    const string KnownInput = "eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiJqb2UiLA0KICJleHAiOjEzMDA4MTkzODAsDQogImh0dHA6Ly9leGFtcGxlLmNvbS9pc19yb290Ijp0cnVlfQ";
    const string KnownSignature = "cC4hiUPoj9Eetdgtv3hF80EGrhuB__dzERat0XF9g2VtQgr9PJbu3XOiZj5RZmh7AAuHIm4Bh-0Qc_lF5YKt_O8W2Fp5jujGbds9uJdbF9CUAr7t1dnZcAcQjbKBYNX4BAynRFdiuB--f_nZLgrnbyTyWzO75vRK5h6xBArLIARNPvkSjtQBMHlb1L07Qe7K0GarZRmB_eSN9383LcOLn6_dO--xi12jzDwusC-eOkHWEsqtFZESc6BfI7noOPqvhJ1phCnvWh6IeYI2w9QOYEUipUTI8np6LbgGY9Fs98rqVt5AXLIhWkWywlVmtVrBp0igcN_IoypGlUPQGe77Rw";
    static string PublicKey { get { return "<RSAKeyValue><Modulus>" + Convert.ToBase64String(UrlBytes(Modulus)) + "</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>"; } }

    public static int Run()
    {
        failed = 0;
        Test("published RSA/SHA-256 known-answer vector verifies; altered bytes do not", delegate {
            byte[] message = Encoding.ASCII.GetBytes(KnownInput), signature = UrlBytes(KnownSignature);
            Assert(UpdateProtocol.VerifyRsa(message, signature, PublicKey), "Published RSA vector was rejected.");
            message[0] ^= 1;
            Assert(!UpdateProtocol.VerifyRsa(message, signature, PublicKey), "Tampered message was accepted.");
            signature[0] ^= 1;
            Assert(!UpdateProtocol.VerifyRsa(Encoding.ASCII.GetBytes(KnownInput), signature, PublicKey), "Tampered signature was accepted.");
        });
        Test("absent trust disables all network entry points", delegate {
            var trust = new UpdateTrust();
            Assert(!trust.IsConfigured, "Empty trust was configured.");
            Reject(delegate { UpdateProtocol.FetchManifest(trust, Now); });
            Reject(delegate { UpdateProtocol.ParseEnvelope(Envelope(Payload(Configuration())), trust, Now); });
        });
        Test("strict trust profile accepts only public key and explicit HTTPS hosts", delegate {
            string profile = "<updaterTrust><feed>https://updates.example.com/stable.xml</feed><publicKey>" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(PublicKey)) + "</publicKey><downloadHost>downloads.example.com</downloadHost></updaterTrust>";
            Assert(UpdateTrust.Parse(Encoding.UTF8.GetBytes(profile)).IsConfigured, "Valid trust profile was rejected.");
            Reject(delegate { UpdateTrust.Parse(Encoding.UTF8.GetBytes(profile.Replace("https:", "http:"))); });
            Reject(delegate { UpdateTrust.Parse(Encoding.UTF8.GetBytes(profile.Replace("</updaterTrust>", "<feed>https://other.example.com/x</feed></updaterTrust>"))); });
            Reject(delegate { UpdateProtocol.ReadPublicKey(PublicKey.Replace("</RSAKeyValue>", "<D>AQAB</D></RSAKeyValue>")); });
            Reject(delegate { UpdateProtocol.ReadPublicKey("<RSAKeyValue><Modulus>AQAB</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>"); });
        });
        Test("production parser rejects unsigned and incorrectly signed metadata", delegate {
            Reject(delegate { UpdateProtocol.ParseEnvelope(Envelope(Payload(Configuration())), Trust(), Now); });
            Reject(delegate { Parse(Payload(Configuration()), delegate(byte[] p, byte[] s, string k) { return false; }); });
        });
        Test("valid configuration metadata preserves exact digest and identity", delegate {
            string payload = Payload(Configuration());
            UpdateManifest manifest = Parse(payload);
            Assert(manifest.Sequence == 7 && manifest.ConfigVersion == 1 && manifest.BinaryVersion == null, "Update identity was not preserved.");
            Assert(manifest.Options["approve-mode"] == "click" && manifest.Digest == Hash(Encoding.UTF8.GetBytes(payload)), "Payload digest or configuration changed.");
        });
        Test("expired, future and excessive-validity metadata are rejected", delegate {
            Reject(delegate { Parse(Payload(Configuration()).Replace("2026-10-30T11:00:00Z", "2026-10-30T09:00:00Z")); });
            Reject(delegate { Parse(Payload(Configuration()).Replace("2026-10-30T09:59:00Z", "2026-10-30T10:06:00Z")); });
            Reject(delegate { Parse(Payload(Configuration()).Replace("2026-10-30T11:00:00Z", "2026-11-30T11:00:00Z")); });
        });
        Test("wrong product, duplicate sections, unknown fields and DTDs are rejected", delegate {
            Reject(delegate { Parse(Payload(Configuration()).Replace("touch365-remote-support", "other-product")); });
            Reject(delegate { Parse(Payload(Configuration() + Configuration())); });
            Reject(delegate { Parse(Payload("<script>anything</script>")); });
            Reject(delegate { Parse(Payload(Configuration()).Replace("schema=\"1\"", "schema=\"1\" surprise=\"yes\"")); });
            Reject(delegate { Parse("<!DOCTYPE update [<!ENTITY x 'anything'>]>" + Payload(Configuration())); });
            Reject(delegate { Parse(Payload(Configuration()).Replace("sequence=\"7\"", "sequence=\"0\"")); });
        });
        Test("configuration excludes secrets, scripts and consent weakening", delegate {
            foreach (string option in new[] {
                "<option name='password' value='nope'/>", "<option name='script' value='anything'/>", "<option name='stop-service' value=''/>",
                "<option name='approve-mode' value='password'/>", "<option name='allow-only-conn-window-open' value='N'/>",
                "<option name='allow-remote-config-modification' value='Y'/>", "<option name='access-mode' value='full'/>",
                "<option name='key' value='AQAB'/>", "<option name='relay-server' value='https://relay.example.com'/>",
                "<option name='enable-audio' value='run anything'/>", "<option name='approve-mode' value='click'/><option name='approve-mode' value='click'/>"
            }) Reject(delegate { Parse(Payload("<configuration version='1'>" + option + "</configuration>")); });
        });
        Test("allowlisted server, public key and permission values remain explicit data", delegate {
            string config = "<configuration version='2'><option name='relay-server' value='relay.example.com'/><option name='key' value='" +
                Policy.PublicKey + "'/><option name='enable-audio' value='N'/></configuration>";
            Assert(Parse(Payload(config)).Options.Count == 3, "Valid configuration data was rejected.");
        });
        Test("manifest binary hosts and path/resource allowlist reject Windows escapes and extra programs", delegate {
            foreach (string path in new[] { "../RustDesk.exe", "/RustDesk.exe", "C:/RustDesk.exe", "folder\\RustDesk.exe", "NUL.txt", "CON .txt", "CLOCK$.txt", "COM1.json", "folder./x.txt", " assets/x.txt", "other.exe", "config.ps1", "file.txt:stream", "RUSTDESK.EXE" })
                Reject(delegate { UpdateProtocol.SafePackagePath(path); });
            Assert(UpdateProtocol.SafePackagePath("resources/config.json") == "resources/config.json", "Valid resource path was rejected.");
            byte[] package = Package(false, false);
            Reject(delegate { Parse(Payload(Binary(package).Replace("downloads.example.com", "unapproved.example.com"))); });
            Reject(delegate { Parse(Payload(Binary(package).Replace("https://", "http://"))); });
            Reject(delegate { Parse(Payload(Binary(package).Replace("package.zip", "package.zip?token=secret"))); });
        });
        Test("successful download stages and verifies a declared complete ZIP without extraction", delegate {
            WithDirectory(delegate(string root) {
                byte[] package = Package(false, false);
                UpdateManifest manifest = Parse(Payload(Binary(package)));
                string staged = UpdateProtocol.StagePackageForTests(manifest, Trust(), root, delegate(Uri u) { return new MemoryStream(package, false); });
                Assert(File.Exists(staged) && Directory.GetFiles(root).Length == 1 && !Directory.GetDirectories(root).Any(), "Download was extracted or partial files survived.");
                UpdateProtocol.VerifyPackage(staged, manifest);
                byte[] changed = File.ReadAllBytes(staged); changed[0] ^= 1; File.WriteAllBytes(staged, changed);
                Reject(delegate { UpdateProtocol.VerifyPackage(staged, manifest); });
            });
        });
        Test("normal explicit assets directory record is accepted only as a signed file parent", delegate {
            WithDirectory(delegate(string root) {
                byte[] package = Package(false, false, new[] { "assets/" }, true);
                UpdateManifest manifest = Parse(Payload(Binary(package, true)));
                string staged = UpdateProtocol.StagePackageForTests(manifest, Trust(), root, delegate(Uri u) { return new MemoryStream(package, false); });
                UpdateProtocol.VerifyPackage(staged, manifest);
                Assert(File.Exists(staged) && manifest.Files.Count == 2 && Directory.GetFiles(root).Length == 1, "A normal explicit signed-parent directory was rejected or extracted.");
            });
        });
        Test("unknown, duplicate, case-colliding, unsafe and nonempty directory records are rejected", delegate {
            foreach (string[] records in new[] { new[] { "other/" }, new[] { "ASSETS/" }, new[] { "assets/", "assets/" },
                new[] { "assets/", "ASSETS/" }, new[] { "assets//" }, new[] { "../" } })
                WithDirectory(delegate(string root) {
                    byte[] package = Package(false, false, records, true);
                    UpdateManifest manifest = Parse(Payload(Binary(package, true)));
                    Reject(delegate { UpdateProtocol.StagePackageForTests(manifest, Trust(), root, delegate(Uri u) { return new MemoryStream(package, false); }); });
                    Assert(Directory.GetFiles(root).Length == 0, "An unsafe directory record survived staging.");
                });
            WithDirectory(delegate(string root) {
                byte[] package = Package(false, false, new[] { "assets/" }, true, true);
                UpdateManifest manifest = Parse(Payload(Binary(package, true)));
                Reject(delegate { UpdateProtocol.StagePackageForTests(manifest, Trust(), root, delegate(Uri u) { return new MemoryStream(package, false); }); });
            });
        });
        Test("interrupted transport removes only its owned partial and preserves an existing file", delegate {
            WithDirectory(delegate(string root) {
                string existing = Path.Combine(root, "keep.txt"); File.WriteAllText(existing, "keep");
                byte[] package = Package(false, false);
                UpdateManifest manifest = Parse(Payload(Binary(package)));
                Reject(delegate { UpdateProtocol.StagePackageForTests(manifest, Trust(), root, delegate(Uri u) { return new Interrupted(package); }); });
                Assert(Directory.GetFiles(root).Length == 1 && File.ReadAllText(existing) == "keep", "Failed download changed unrelated files or left a partial.");
            });
        });
        Test("corrupt transfer, undeclared ZIP entries and links are rejected before handoff", delegate {
            foreach (int scenario in new[] { 0, 1, 2 }) WithDirectory(delegate(string root) {
                byte[] package = Package(scenario == 1, scenario == 2);
                UpdateManifest manifest = Parse(Payload(Binary(package)));
                if (scenario == 0) package[0] ^= 1;
                Reject(delegate { UpdateProtocol.StagePackageForTests(manifest, Trust(), root, delegate(Uri u) { return new MemoryStream(package, false); }); });
                Assert(Directory.GetFiles(root).Length == 0, "Rejected package survived as an accepted update.");
            });
        });
        Test("mutated metadata, files and trust cannot reuse the authenticated binding", delegate {
            byte[] package = Package(false, false);
            WithDirectory(delegate(string root) {
                UpdateManifest manifest = Parse(Payload(Binary(package)));
                manifest.PackageUrl = "https://downloads.example.com/other.zip";
                Reject(delegate { UpdateProtocol.StagePackageForTests(manifest, Trust(), root, delegate(Uri u) { throw new Exception("Transport must not be reached."); }); });
                manifest = Parse(Payload(Binary(package))); manifest.Files[0].Sha256 = new string('0', 64);
                Reject(delegate { UpdateProtocol.StagePackageForTests(manifest, Trust(), root, delegate(Uri u) { throw new Exception("Transport must not be reached."); }); });
                manifest = Parse(Payload(Binary(package))); UpdateTrust changed = Trust(); changed.ManifestUrl = "https://updates.example.com/other.xml";
                Reject(delegate { UpdateProtocol.StagePackageForTests(manifest, changed, root, delegate(Uri u) { throw new Exception("Transport must not be reached."); }); });
            });
        });
        Console.WriteLine("Update protocol failures: " + failed + ". No network, real installer or signing keys used.");
        return failed;
    }

    static UpdateTrust Trust()
    { return new UpdateTrust { ManifestUrl = "https://updates.example.com/stable.xml", PublicKeyXml = PublicKey, DownloadHosts = new[] { "downloads.example.com" } }; }
    static string Configuration() { return "<configuration version='1'><option name='approve-mode' value='click'/></configuration>"; }
    static string Payload(string children)
    { return "<update schema=\"1\" product=\"touch365-remote-support\" channel=\"stable\" sequence=\"7\" issuedUtc=\"2026-10-30T09:59:00Z\" expiresUtc=\"2026-10-30T11:00:00Z\">" + children + "</update>"; }
    static byte[] Envelope(string payload)
    { return Encoding.UTF8.GetBytes("<signedUpdate format='1'><payload>" + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "</payload><signature algorithm='rsa-sha256'>" + Convert.ToBase64String(new byte[256]) + "</signature></signedUpdate>"); }
    static UpdateManifest Parse(string payload, Func<byte[], byte[], string, bool> verify = null)
    { return UpdateProtocol.ParseEnvelopeForTests(Envelope(payload), Trust(), Now, verify ?? delegate(byte[] p, byte[] s, string k) { return true; }); }
    internal static UpdateManifest ParseFixtureBody(string children)
    { return Parse(Payload(children)); }
    static string Binary(byte[] package, bool assets = false)
    {
        byte[] executable = new byte[512];
        return "<binary version='1.5.1' url='https://downloads.example.com/package.zip' bytes='" + package.Length + "' sha256='" + Hash(package) +
            "'><file path='RustDesk.exe' bytes='512' sha256='" + Hash(executable) + "'/>" +
            (assets ? "<file path='assets/config.json' bytes='2' sha256='" + Hash(Encoding.ASCII.GetBytes("{}")) + "'/>" : "") + "</binary>";
    }
    static byte[] Package(bool extra, bool link, string[] directoryRecords = null, bool assets = false, bool directoryContent = false)
    {
        using (var output = new MemoryStream()) {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) {
                ZipArchiveEntry entry = zip.CreateEntry("RustDesk.exe");
                if (link) entry.ExternalAttributes = unchecked((int)0xA1FF0000U);
                using (Stream content = entry.Open()) content.Write(new byte[512], 0, 512);
                if (extra) using (Stream content = zip.CreateEntry("unapproved.ps1").Open()) content.WriteByte(1);
                if (assets) using (Stream content = zip.CreateEntry("assets/config.json").Open()) { byte[] data = Encoding.ASCII.GetBytes("{}"); content.Write(data, 0, data.Length); }
                if (directoryRecords != null) foreach (string directory in directoryRecords) {
                    ZipArchiveEntry record = zip.CreateEntry(directory);
                    if (directoryContent) using (Stream content = record.Open()) content.WriteByte(1);
                }
            }
            return output.ToArray();
        }
    }
    static byte[] UrlBytes(string value)
    { value = value.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '=')); }
    static string Hash(byte[] value)
    { using (var algorithm = SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } catch (InvalidOperationException) { return; } catch (IOException) { return; } throw new Exception("Unsafe input or failed transport was accepted."); }
    static void Test(string name, Action action)
    { try { action(); Console.WriteLine("PASS updater protocol: " + name); } catch (Exception error) { failed++; Console.WriteLine("FAIL updater protocol: " + name + ": " + error); } }
    static void WithDirectory(Action<string> action)
    {
        string parent = Environment.GetEnvironmentVariable("T365_UPDATER_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(parent)) parent = Path.GetTempPath();
        string root = Path.Combine(parent, "Touch365-UpdateProtocol-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally { foreach (string file in Directory.GetFiles(root)) File.Delete(file); Directory.Delete(root); }
    }
    sealed class Interrupted : Stream
    {
        readonly byte[] bytes; bool first = true;
        internal Interrupted(byte[] bytes) { this.bytes = bytes; }
        public override int Read(byte[] buffer, int offset, int count) {
            if (!first) throw new IOException("Interrupted fixture stream."); first = false;
            int length = Math.Min(8, Math.Min(count, bytes.Length)); Array.Copy(bytes, 0, buffer, offset, length); return length;
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
