using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Touch365Support
{
    public sealed class UpdateFile
    {
        public string Path, Sha256;
        public long Bytes;
    }

    public sealed class UpdateManifest
    {
        public string Digest;
        public long Sequence;
        public DateTime IssuedUtc, ExpiresUtc;
        public Version BinaryVersion;
        public string PackageUrl, PackageSha256, PackageLayout;
        public long PackageBytes, ConfigVersion;
        public Dictionary<string, string> Options = new Dictionary<string, string>(StringComparer.Ordinal);
        public List<UpdateFile> Files = new List<UpdateFile>();
        internal string BoundFields, BoundTrust;
    }

    public sealed class UpdateTrust
    {
        public string ManifestUrl, PublicKeyXml;
        public string[] DownloadHosts = new string[0];

        public bool IsConfigured
        {
            get
            {
                if (String.IsNullOrEmpty(ManifestUrl) && String.IsNullOrEmpty(PublicKeyXml) &&
                    (DownloadHosts == null || DownloadHosts.Length == 0)) return false;
                Validate();
                return true;
            }
        }

        public static UpdateTrust LoadEmbedded()
        {
            using (Stream input = typeof(UpdateTrust).Assembly.GetManifestResourceStream("UpdaterTrust"))
            {
                if (input == null) return new UpdateTrust();
                return Parse(UpdateProtocol.ReadBounded(input, 16384, TimeSpan.FromSeconds(30)));
            }
        }

        public static UpdateTrust Parse(byte[] xml)
        {
            XElement root = UpdateProtocol.ReadXml(xml, 16384).Root;
            UpdateProtocol.Container(root, "updaterTrust");
            string feed = null, key = null;
            var hosts = new List<string>();
            foreach (XElement child in root.Elements())
            {
                if (child.Name == "feed" && feed == null) feed = UpdateProtocol.Leaf(child);
                else if (child.Name == "publicKey" && key == null)
                    key = UpdateProtocol.DecodeUtf8(UpdateProtocol.Base64(UpdateProtocol.Leaf(child), 8192));
                else if (child.Name == "downloadHost") hosts.Add(UpdateProtocol.Leaf(child));
                else throw new InvalidDataException("The updater trust profile has an unknown or duplicate field.");
            }
            var result = new UpdateTrust { ManifestUrl = feed, PublicKeyXml = key, DownloadHosts = hosts.ToArray() };
            result.Validate();
            return result;
        }

        internal void Validate()
        {
            UpdateProtocol.Https(ManifestUrl);
            UpdateProtocol.ReadPublicKey(PublicKeyXml);
            if (DownloadHosts == null || DownloadHosts.Length < 1 || DownloadHosts.Length > 16)
                throw new InvalidDataException("The updater trust profile needs a bounded download-host allowlist.");
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string host in DownloadHosts)
                if (!UpdateProtocol.DnsHost(host) || host.IndexOf('.') < 0 || !distinct.Add(host))
                    throw new InvalidDataException("The updater trust profile has an invalid or duplicate download host.");
        }
    }

    // Uses the established Checker pattern: bounded HTTPS, owned partial files,
    // immutable verified release binding, then revalidation before handoff.
    // Signed metadata adds authentication; it never substitutes for Authenticode.
    public static class UpdateProtocol
    {
        public const string Product = "touch365-remote-support", Channel = "stable";
        public const int MaximumEnvelopeBytes = 96 * 1024;
        public const long MaximumPackageBytes = 256L * 1024 * 1024;
        const int MaximumPayloadBytes = 64 * 1024, MaximumFiles = 256;
        const long MaximumExpandedBytes = 512L * 1024 * 1024;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static readonly HashSet<string> ResourceExtensions = new HashSet<string>(new[] {
            ".dll", ".pak", ".dat", ".bin", ".json", ".txt", ".png", ".ico", ".ttf"
        }, StringComparer.OrdinalIgnoreCase);

        public static UpdateManifest ParseEnvelope(byte[] envelope, UpdateTrust trust, DateTime nowUtc)
        { return ParseEnvelopeCore(envelope, trust, nowUtc, VerifyRsa); }

        internal static UpdateManifest ParseEnvelopeForTests(byte[] envelope, UpdateTrust trust, DateTime nowUtc,
            Func<byte[], byte[], string, bool> verifier)
        { return ParseEnvelopeCore(envelope, trust, nowUtc, verifier); }

        static UpdateManifest ParseEnvelopeCore(byte[] envelope, UpdateTrust trust, DateTime nowUtc,
            Func<byte[], byte[], string, bool> verifier)
        {
            RequireTrust(trust);
            if (nowUtc.Kind == DateTimeKind.Unspecified) throw new ArgumentException("An explicit UTC clock is required.");
            nowUtc = nowUtc.ToUniversalTime();
            XElement root = ReadXml(envelope, MaximumEnvelopeBytes).Root;
            Container(root, "signedUpdate", "format");
            if (Attr(root, "format") != "1") throw new InvalidDataException("Unsupported signed update format.");
            XElement[] children = root.Elements().ToArray();
            if (children.Length != 2 || children[0].Name != "payload" || children[1].Name != "signature")
                throw new InvalidDataException("The signed update envelope must contain one payload and one signature.");
            byte[] payload = Base64(Leaf(children[0]), MaximumPayloadBytes);
            byte[] signature = Base64(Leaf(children[1], "algorithm"), 1024);
            if (Attr(children[1], "algorithm") != "rsa-sha256" || !verifier(payload, signature, trust.PublicKeyXml))
                throw new InvalidDataException("The update metadata signature is not trusted.");
            UpdateManifest result = ParsePayload(payload, trust, nowUtc);
            result.Digest = Hash(payload);
            result.BoundFields = Fingerprint(result);
            result.BoundTrust = TrustFingerprint(trust);
            return result;
        }

        static UpdateManifest ParsePayload(byte[] payload, UpdateTrust trust, DateTime nowUtc)
        {
            XElement root = ReadXml(payload, MaximumPayloadBytes).Root;
            Container(root, "update", "schema", "product", "channel", "sequence", "issuedUtc", "expiresUtc");
            if (Attr(root, "schema") != "1" || Attr(root, "product") != Product || Attr(root, "channel") != Channel)
                throw new InvalidDataException("The update metadata belongs to an unsupported schema, product or channel.");
            var result = new UpdateManifest {
                Sequence = Positive(Attr(root, "sequence")), IssuedUtc = Timestamp(Attr(root, "issuedUtc")),
                ExpiresUtc = Timestamp(Attr(root, "expiresUtc"))
            };
            if (result.IssuedUtc > nowUtc.AddMinutes(5) || result.ExpiresUtc <= nowUtc ||
                result.ExpiresUtc <= result.IssuedUtc || result.ExpiresUtc - result.IssuedUtc > TimeSpan.FromDays(7))
                throw new InvalidDataException("The update metadata is expired or has an invalid validity interval.");
            foreach (XElement child in root.Elements())
            {
                if (child.Name == "binary" && result.BinaryVersion == null) ReadBinary(child, trust, result);
                else if (child.Name == "configuration" && result.ConfigVersion == 0) ReadConfiguration(child, result);
                else throw new InvalidDataException("The update metadata has an unknown or duplicate update section.");
            }
            if (result.BinaryVersion == null && result.ConfigVersion == 0)
                throw new InvalidDataException("The update metadata has no binary or configuration update.");
            return result;
        }

        static void ReadBinary(XElement binary, UpdateTrust trust, UpdateManifest result)
        {
            bool complete = binary.Attribute("layout") != null;
            if (complete) Container(binary, "binary", "version", "url", "bytes", "sha256", "layout");
            else Container(binary, "binary", "version", "url", "bytes", "sha256");
            result.PackageLayout = complete ? Attr(binary, "layout") : null;
            if (complete && result.PackageLayout != UpdatePackage.CompleteLayout)
                throw new InvalidDataException("The application package layout is not supported.");
            result.BinaryVersion = ParseVersion(Attr(binary, "version"));
            result.PackageUrl = DownloadUri(Attr(binary, "url"), trust).AbsoluteUri;
            if (!new Uri(result.PackageUrl).AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A RustDesk application update must be a ZIP package.");
            result.PackageBytes = Positive(Attr(binary, "bytes"));
            if (result.PackageBytes > MaximumPackageBytes) throw new InvalidDataException("The update package exceeds its size limit.");
            result.PackageSha256 = Digest(Attr(binary, "sha256"));
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (XElement file in binary.Elements())
            {
                Container(file, "file", "path", "bytes", "sha256");
                if (file.Elements().Any()) throw new InvalidDataException("Update file records cannot contain nested elements.");
                string path = SafePackagePath(Attr(file, "path"), result.PackageLayout);
                if (!paths.Add(path) || paths.Any(p => !String.Equals(p, path, StringComparison.OrdinalIgnoreCase) &&
                    (p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidDataException("The update package has duplicate or conflicting Windows file paths.");
                long bytes = Nonnegative(Attr(file, "bytes"));
                if (bytes > MaximumPackageBytes || (path == "RustDesk.exe" && bytes < 512))
                    throw new InvalidDataException("An update file has an invalid size.");
                expanded = checked(expanded + bytes);
                if (expanded > MaximumExpandedBytes || paths.Count > MaximumFiles)
                    throw new InvalidDataException("The expanded update package exceeds its bounds.");
                result.Files.Add(new UpdateFile { Path = path, Bytes = bytes, Sha256 = Digest(Attr(file, "sha256")) });
            }
            if (!paths.Contains("RustDesk.exe") || !result.Files.Any(f => f.Path == "RustDesk.exe"))
                throw new InvalidDataException("The application package must contain root RustDesk.exe.");
            PackageDirectories(result.Files, result.PackageLayout);
            if (complete) UpdatePackage.RequireCompleteApplication(result);
        }

        static void ReadConfiguration(XElement configuration, UpdateManifest result)
        {
            Container(configuration, "configuration", "version");
            result.ConfigVersion = Positive(Attr(configuration, "version"));
            var allowed = new HashSet<string>(Policy.Options.Select(p => p.Key).Where(p => p != "stop-service"), StringComparer.Ordinal);
            foreach (XElement option in configuration.Elements())
            {
                Container(option, "option", "name", "value");
                if (option.Elements().Any()) throw new InvalidDataException("Configuration options cannot contain commands or nested elements.");
                string name = Attr(option, "name"), value = Attr(option, "value");
                if (!allowed.Contains(name) || result.Options.ContainsKey(name) || !ValidOption(name, value))
                    throw new InvalidDataException("The configuration contains an unknown, duplicate or unsafe option.");
                result.Options.Add(name, value);
            }
            if (result.Options.Count == 0) throw new InvalidDataException("A configuration update must contain allowlisted options.");
        }

        static bool ValidOption(string name, string value)
        {
            if (value == null || value.Length > 253) return false;
            if (name == "approve-mode") return value == "click";
            if (name == "allow-only-conn-window-open") return value == "Y";
            if (name == "allow-remote-config-modification") return value == "N";
            if (name == "access-mode") return value == "custom";
            if (name == "key") {
                try { return Base64(value, 32).Length == 32; } catch (InvalidDataException) { return false; }
            }
            if (name == "relay-server" || name == "custom-rendezvous-server") return DnsHost(value);
            if (name == "api-server") return value.Length == 0 || DnsHost(value);
            return value == "Y" || value == "N";
        }

        public static UpdateManifest FetchManifest(UpdateTrust trust, DateTime nowUtc)
        { return ParseEnvelope(FetchEnvelope(trust), trust, nowUtc); }

        public static byte[] FetchEnvelope(UpdateTrust trust)
        {
            RequireTrust(trust);
            using (ResponseStream response = Open(Https(trust.ManifestUrl)))
            {
                if (response.Length > MaximumEnvelopeBytes) throw new InvalidDataException("The update metadata is too large.");
                return ReadBounded(response.Stream, MaximumEnvelopeBytes, TimeSpan.FromSeconds(30));
            }
        }

        public static string StagePackage(UpdateManifest manifest, UpdateTrust trust, string stagingDirectory)
        { return StageCore(manifest, trust, stagingDirectory, Open); }

        internal static string StagePackageForTests(UpdateManifest manifest, UpdateTrust trust, string directory, Func<Uri, Stream> transport)
        {
            return StageCore(manifest, trust, directory, delegate(Uri uri) {
                Stream stream = transport(uri);
                return new ResponseStream(stream, stream.CanSeek ? stream.Length : -1, null);
            });
        }

        static string StageCore(UpdateManifest manifest, UpdateTrust trust, string stagingDirectory, Func<Uri, ResponseStream> transport)
        {
            RequireBound(manifest, trust);
            if (manifest.BinaryVersion == null) throw new InvalidDataException("This update has no application package.");
            Uri uri = DownloadUri(manifest.PackageUrl, trust);
            string root = Path.GetFullPath(stagingDirectory);
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("Use a local updater staging directory.");
            SafeFiles.NoReparse(root);
            Directory.CreateDirectory(root);
            SafeFiles.NoReparse(root);
            string name = "update-" + Guid.NewGuid().ToString("N");
            string partial = Path.Combine(root, name + ".part"), complete = Path.Combine(root, name + ".zip");
            bool ownsPartial = false, ownsComplete = false, success = false;
            try
            {
                using (ResponseStream response = transport(uri))
                {
                    if (response.Length >= 0 && response.Length != manifest.PackageBytes)
                        throw new InvalidDataException("The update response size does not match its signed metadata.");
                    using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        ownsPartial = true;
                        byte[] buffer = new byte[65536];
                        long received = 0;
                        var timer = Stopwatch.StartNew();
                        int count;
                        while ((count = response.Stream.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            received += count;
                            if (received > manifest.PackageBytes || timer.Elapsed > TimeSpan.FromMinutes(15))
                                throw new InvalidDataException("The update download exceeded its signed size or time limit.");
                            output.Write(buffer, 0, count);
                        }
                        if (received != manifest.PackageBytes) throw new InvalidDataException("The update download was interrupted or incomplete.");
                        output.Flush(true);
                    }
                }
                RequireBound(manifest, trust);
                VerifyPackage(partial, manifest);
                SafeFiles.NoReparse(root);
                File.Move(partial, complete);
                ownsPartial = false;
                ownsComplete = true;
                VerifyPackage(complete, manifest);
                success = true;
                return complete;
            }
            finally
            {
                if (ownsPartial) DeleteOwned(partial);
                if (ownsComplete && !success) DeleteOwned(complete);
            }
        }

        // Extraction/apply must repeat these checks immediately before destination writes.
        public static void VerifyPackage(string path, UpdateManifest manifest)
        {
            if (manifest == null || manifest.BoundFields == null || Fingerprint(manifest) != manifest.BoundFields)
                throw new InvalidDataException("The verified update metadata was changed.");
            SafeFiles.NoReparse(path);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != manifest.PackageBytes || Hash(input) != manifest.PackageSha256)
                    throw new InvalidDataException("The application package failed its signed size or SHA-256 check.");
                input.Position = 0;
                using (var zip = new ZipArchive(input, ZipArchiveMode.Read, true))
                {
                    if (zip.Entries.Count > MaximumFiles * 2) throw new InvalidDataException("The ZIP contains too many file or directory records.");
                    Dictionary<string, string> directories = PackageDirectories(manifest.Files, manifest.PackageLayout);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int fileCount = 0;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                        string entryPath = SafeRelativePath(directory ? entry.FullName.Substring(0, entry.FullName.Length - 1) : entry.FullName);
                        uint attributes = unchecked((uint)entry.ExternalAttributes);
                        uint unixType = (attributes >> 16) & 0xF000U;
                        if (!seen.Add(entryPath) || (attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                            (unixType != 0 && unixType != 0x8000U && unixType != 0x4000U))
                            throw new InvalidDataException("The ZIP contains a duplicate, link or special file.");
                        if (directory)
                        {
                            string canonical;
                            if (!directories.TryGetValue(entryPath, out canonical) || canonical != entryPath || entry.Length != 0 ||
                                (unixType != 0 && unixType != 0x4000U))
                                throw new InvalidDataException("The ZIP contains an unsigned or ambiguous directory record.");
                            continue;
                        }
                        SafePackagePath(entryPath, manifest.PackageLayout);
                        UpdateFile expected = manifest.Files.SingleOrDefault(f => f.Path == entryPath);
                        if (expected == null || entry.Length != expected.Bytes || (attributes & (uint)FileAttributes.Directory) != 0 ||
                            unixType == 0x4000U || (entry.Length > 1024 * 1024 &&
                            (entry.CompressedLength == 0 || entry.Length > entry.CompressedLength * 200)))
                            throw new InvalidDataException("A ZIP entry differs from the signed file manifest.");
                        fileCount++;
                        using (Stream content = entry.Open())
                            if (HashBounded(content, expected.Bytes) != expected.Sha256)
                                throw new InvalidDataException("A ZIP entry failed its signed SHA-256 check.");
                    }
                    if (fileCount != manifest.Files.Count) throw new InvalidDataException("The ZIP is missing a signed file.");
                }
            }
        }

        internal static string SafePackagePath(string path)
        { return SafePackagePath(path, null); }

        internal static string SafePackagePath(string path, string layout)
        {
            SafeRelativePath(path);
            if (!String.IsNullOrEmpty(layout))
            {
                if (layout != UpdatePackage.CompleteLayout || !UpdatePackage.IsCompleteApplicationPath(path))
                    throw new InvalidDataException("The complete application package has an unapproved path.");
                return path;
            }
            if (path != "RustDesk.exe" && !ResourceExtensions.Contains(Path.GetExtension(path)))
                throw new InvalidDataException("The update package includes an unapproved executable, script or resource type.");
            return path;
        }

        // Shared ZIP/extraction interpretation. Directories are explicit ZIP records
        // only when they are implied by the authenticated inventory's file paths.
        internal static string SafeRelativePath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 180 || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 ||
                path.StartsWith("/", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("The update package has an unsafe Windows path.");
            string[] parts = path.Split('/');
            if (parts.Length > 16) throw new InvalidDataException("The update package path is too deep.");
            foreach (string part in parts)
            {
                string stem = part.Split('.')[0].TrimEnd(' ');
                if (part.Length < 1 || part.Length > 80 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) ||
                    part.StartsWith(" ", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.Any(c => c < 32 || c > 126 || "\\:<>\"|?*".IndexOf(c) >= 0) ||
                    Regex.IsMatch(stem, @"\A(?:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new InvalidDataException("The update package has an unsafe Windows path segment.");
            }
            return path;
        }

        internal static Dictionary<string, string> PackageDirectories(IEnumerable<UpdateFile> files)
        { return PackageDirectories(files, null); }

        internal static Dictionary<string, string> PackageDirectories(IEnumerable<UpdateFile> files, string layout)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (files == null) throw new InvalidDataException("The signed update file inventory is missing.");
            foreach (UpdateFile file in files)
            {
                if (file == null) throw new InvalidDataException("The signed update file inventory is incomplete.");
                string path = SafePackagePath(file.Path, layout);
                int slash = path.LastIndexOf('/');
                while (slash >= 0)
                {
                    string parent = path.Substring(0, slash), existing;
                    if (result.TryGetValue(parent, out existing) && existing != parent)
                        throw new InvalidDataException("The signed inventory has case-colliding Windows directories.");
                    result[parent] = parent;
                    if (result.Count > MaximumFiles) throw new InvalidDataException("The update archive needs too many folders.");
                    slash = path.LastIndexOf('/', slash - 1);
                }
            }
            return result;
        }

        static void RequireBound(UpdateManifest manifest, UpdateTrust trust)
        {
            RequireTrust(trust);
            if (manifest == null || manifest.BoundFields == null || manifest.BoundTrust != TrustFingerprint(trust) ||
                manifest.BoundFields != Fingerprint(manifest))
                throw new InvalidDataException("The update details changed or have no verified metadata binding.");
        }

        static void RequireTrust(UpdateTrust trust)
        { if (trust == null || !trust.IsConfigured) throw new InvalidOperationException("Automatic updates are disabled until a trusted feed and public verification key are provisioned."); }

        static Uri DownloadUri(string url, UpdateTrust trust)
        {
            Uri uri = Https(url);
            if (!trust.DownloadHosts.Any(host => String.Equals(host, uri.DnsSafeHost, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The update download is outside its configured HTTPS host allowlist.");
            return uri;
        }

        internal static Uri Https(string text)
        {
            Uri uri;
            if (String.IsNullOrWhiteSpace(text) || text != text.Trim() || text.Length > 2048 || text.IndexOf('\\') >= 0 ||
                !Uri.TryCreate(text, UriKind.Absolute, out uri) || uri.Scheme != "https" || uri.Port != 443 ||
                !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment) ||
                uri.IsLoopback || !DnsHost(uri.DnsSafeHost) || uri.DnsSafeHost.IndexOf('.') < 0)
                throw new InvalidDataException("Updater URLs require an approved public HTTPS host, without credentials, query parameters or fragments.");
            return uri;
        }

        internal static bool DnsHost(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 253 || value.EndsWith(".", StringComparison.Ordinal) ||
                value.Equals("localhost", StringComparison.OrdinalIgnoreCase) || Uri.CheckHostName(value) != UriHostNameType.Dns) return false;
            return value.Split('.').All(label => label.Length > 0 && label.Length <= 63 &&
                Regex.IsMatch(label, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\z"));
        }

        static ResponseStream Open(Uri uri)
        {
            // Process-local TLS selection mirrors the existing Checker downloader.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.AllowAutoRedirect = false;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.UseDefaultCredentials = false;
            request.Credentials = null;
            request.PreAuthenticate = false;
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            request.UserAgent = "Touch365-Remote-Support-Updater/1";
            HttpWebResponse response = null;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
                if (response.StatusCode != HttpStatusCode.OK || response.ResponseUri != uri)
                    throw new InvalidDataException("The updater rejects redirects and non-success responses.");
                return new ResponseStream(response.GetResponseStream(), response.ContentLength, response);
            }
            catch (WebException error)
            {
                if (error.Response != null) error.Response.Dispose();
                if (response != null) response.Dispose();
                throw new IOException("The trusted update source is unavailable. Existing software and configuration were left unchanged.");
            }
            catch { if (response != null) response.Dispose(); throw; }
        }

        sealed class ResponseStream : IDisposable
        {
            internal readonly Stream Stream;
            internal readonly long Length;
            readonly IDisposable owner;
            internal ResponseStream(Stream stream, long length, IDisposable owner)
            { if (stream == null) throw new IOException("The updater response has no content."); Stream = stream; Length = length; this.owner = owner; }
            public void Dispose() { Stream.Dispose(); if (owner != null) owner.Dispose(); }
        }

        internal static byte[] ReadBounded(Stream stream, int maximum, TimeSpan timeout)
        {
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                var timer = Stopwatch.StartNew();
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (output.Length + count > maximum || timer.Elapsed > timeout) throw new InvalidDataException("The updater response exceeded its bounds.");
                    output.Write(buffer, 0, count);
                }
                return output.ToArray();
            }
        }

        internal static bool VerifyRsa(byte[] payload, byte[] signature, string publicKeyXml)
        {
            RSAParameters parameters = ReadPublicKey(publicKeyXml);
            if (signature == null || signature.Length != parameters.Modulus.Length) return false;
            // Import a PUBLIC-ONLY CNG blob. No key-generation call, key container,
            // private parameter or persisted credential is involved in verification.
            byte[] blob = new byte[24 + parameters.Exponent.Length + parameters.Modulus.Length];
            BitConverter.GetBytes(0x31415352U).CopyTo(blob, 0); // BCRYPT_RSAPUBLIC_MAGIC (RSA1).
            BitConverter.GetBytes((uint)(parameters.Modulus.Length * 8)).CopyTo(blob, 4);
            BitConverter.GetBytes((uint)parameters.Exponent.Length).CopyTo(blob, 8);
            BitConverter.GetBytes((uint)parameters.Modulus.Length).CopyTo(blob, 12);
            parameters.Exponent.CopyTo(blob, 24);
            parameters.Modulus.CopyTo(blob, 24 + parameters.Exponent.Length);
            IntPtr algorithm = IntPtr.Zero, key = IntPtr.Zero;
            try
            {
                if (BCryptOpenAlgorithmProvider(out algorithm, "RSA", null, 0) != 0 ||
                    BCryptImportKeyPair(algorithm, IntPtr.Zero, "RSAPUBLICBLOB", out key, blob, blob.Length, 0) != 0) return false;
                byte[] digest;
                using (var hash = SHA256.Create()) digest = hash.ComputeHash(payload);
                var padding = new Pkcs1PaddingInfo { Algorithm = "SHA256" };
                return BCryptVerifySignature(key, ref padding, digest, digest.Length, signature, signature.Length, 2) == 0;
            }
            finally
            {
                if (key != IntPtr.Zero) BCryptDestroyKey(key);
                if (algorithm != IntPtr.Zero) BCryptCloseAlgorithmProvider(algorithm, 0);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Pkcs1PaddingInfo { [MarshalAs(UnmanagedType.LPWStr)] public string Algorithm; }
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string name, string implementation, uint flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptImportKeyPair(IntPtr algorithm, IntPtr importKey, string type, out IntPtr key, byte[] blob, int length, uint flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptVerifySignature(IntPtr key, ref Pkcs1PaddingInfo padding, byte[] hash, int hashLength, byte[] signature, int signatureLength, uint flags);
        [DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);

        internal static RSAParameters ReadPublicKey(string text)
        {
            if (text == null) throw new InvalidDataException("The updater public key is missing.");
            XElement root = ReadXml(Utf8.GetBytes(text), 8192).Root;
            Container(root, "RSAKeyValue");
            XElement[] fields = root.Elements().ToArray();
            if (fields.Length != 2 || fields[0].Name != "Modulus" || fields[1].Name != "Exponent")
                throw new InvalidDataException("Only RSA public modulus and exponent may appear in updater trust.");
            byte[] modulus = Base64(Leaf(fields[0]), 1024), exponent = Base64(Leaf(fields[1]), 4);
            int bits = (modulus.Length - 1) * 8;
            for (int first = modulus[0]; first != 0; first >>= 1) bits++;
            ulong e = 0;
            foreach (byte b in exponent) e = (e << 8) | b;
            if (bits < 2048 || bits > 8192 || modulus[0] == 0 || exponent[0] == 0 || e < 3 || (e & 1) == 0)
                throw new InvalidDataException("The updater requires an RSA public key of at least 2048 bits.");
            return new RSAParameters { Modulus = modulus, Exponent = exponent };
        }

        internal static XDocument ReadXml(byte[] bytes, int maximum)
        {
            if (bytes == null || bytes.Length < 1 || bytes.Length > maximum) throw new InvalidDataException("Updater XML is empty or too large.");
            try
            {
                string text = DecodeUtf8(bytes);
                using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximum,
                    IgnoreComments = false, IgnoreProcessingInstructions = false
                }))
                {
                    XDocument doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                    if (doc.Root == null || doc.Nodes().Any(n => n != doc.Root && !(n is XText && String.IsNullOrWhiteSpace(((XText)n).Value))) ||
                        (doc.Declaration != null && !String.IsNullOrEmpty(doc.Declaration.Encoding) && !doc.Declaration.Encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Updater XML has unsupported document content.");
                    return doc;
                }
            }
            catch (XmlException) { throw new InvalidDataException("Updater XML is malformed or contains prohibited declarations."); }
        }

        internal static string DecodeUtf8(byte[] bytes)
        { try { return Utf8.GetString(bytes); } catch (DecoderFallbackException) { throw new InvalidDataException("Updater content must be valid UTF-8."); } }

        internal static void Container(XElement element, string name, params string[] attributes)
        {
            if (element == null || element.Name != name || element.Attributes().Count() != attributes.Length ||
                element.Attributes().Any(a => a.Name.Namespace != XNamespace.None || !attributes.Contains(a.Name.LocalName)) ||
                element.Nodes().Any(n => !(n is XElement) && !(n is XText && !(n is XCData) && String.IsNullOrWhiteSpace(((XText)n).Value))))
                throw new InvalidDataException("Updater XML has unknown fields, attributes or node types.");
        }

        internal static string Leaf(XElement element, params string[] attributes)
        {
            if (element.Name.Namespace != XNamespace.None || element.Attributes().Count() != attributes.Length ||
                element.Attributes().Any(a => a.Name.Namespace != XNamespace.None || !attributes.Contains(a.Name.LocalName)) ||
                element.Nodes().Any(n => !(n is XText) || n is XCData))
                throw new InvalidDataException("Updater XML requires a plain text leaf field.");
            return element.Value;
        }

        static string Attr(XElement element, string name)
        { XAttribute attribute = element.Attribute(name); if (attribute == null) throw new InvalidDataException("Updater XML is missing a required attribute."); return attribute.Value; }

        internal static byte[] Base64(string value, int maximum)
        {
            try
            {
                if (String.IsNullOrEmpty(value) || value.Length > ((maximum + 2) / 3) * 4) throw new FormatException();
                byte[] bytes = Convert.FromBase64String(value);
                if (bytes.Length < 1 || bytes.Length > maximum || Convert.ToBase64String(bytes) != value) throw new FormatException();
                return bytes;
            }
            catch (FormatException) { throw new InvalidDataException("Updater base64 is empty, noncanonical or too large."); }
        }

        static string Digest(string value)
        { if (value == null || !Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z")) throw new InvalidDataException("The update SHA-256 value is invalid."); return value.ToLowerInvariant(); }
        static long Positive(string value)
        { long result = Nonnegative(value); if (result < 1) throw new InvalidDataException("An updater version, sequence or size must be positive."); return result; }
        static long Nonnegative(string value)
        { long result; if (value == null || !Regex.IsMatch(value, @"\A(?:0|[1-9][0-9]{0,18})\z") || !Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result)) throw new InvalidDataException("Updater numeric metadata is invalid."); return result; }
        static DateTime Timestamp(string value)
        {
            DateTime result;
            if (!DateTime.TryParseExact(value, new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'" },
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result))
                throw new InvalidDataException("Updater timestamps must be explicit UTC values.");
            return result;
        }
        static Version ParseVersion(string value)
        {
            Version result;
            if (value == null || !Regex.IsMatch(value, @"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?\z") ||
                !Version.TryParse(value, out result) || result.Major == 0)
                throw new InvalidDataException("The application update version is invalid.");
            return new Version(result.Major, result.Minor, result.Build, Math.Max(0, result.Revision));
        }

        static string TrustFingerprint(UpdateTrust trust)
        { return Hash(Utf8.GetBytes(trust.ManifestUrl + "\n" + trust.PublicKeyXml + "\n" + String.Join("\n", trust.DownloadHosts))); }
        static string Fingerprint(UpdateManifest manifest)
        {
            if (manifest.Options == null || manifest.Files == null) throw new InvalidDataException("The update metadata was changed.");
            var text = new StringBuilder();
            foreach (string value in new[] { manifest.Digest, manifest.Sequence.ToString(CultureInfo.InvariantCulture),
                manifest.IssuedUtc.ToString("o", CultureInfo.InvariantCulture), manifest.ExpiresUtc.ToString("o", CultureInfo.InvariantCulture),
                manifest.BinaryVersion == null ? "" : manifest.BinaryVersion.ToString(), manifest.PackageUrl, manifest.PackageSha256,
                manifest.PackageBytes.ToString(CultureInfo.InvariantCulture), manifest.ConfigVersion.ToString(CultureInfo.InvariantCulture) }) Append(text, value);
            Append(text, manifest.Options.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var option in manifest.Options.OrderBy(p => p.Key, StringComparer.Ordinal)) { Append(text, option.Key); Append(text, option.Value); }
            Append(text, manifest.Files.Count.ToString(CultureInfo.InvariantCulture));
            foreach (UpdateFile file in manifest.Files) { if (file == null) throw new InvalidDataException("The update file list was changed."); Append(text, file.Path); Append(text, file.Sha256); Append(text, file.Bytes.ToString(CultureInfo.InvariantCulture)); }
            // Preserve legacy bindings when no layout was signed, including old journals.
            if (!String.IsNullOrEmpty(manifest.PackageLayout)) { Append(text, "package-layout"); Append(text, manifest.PackageLayout); }
            return Hash(Utf8.GetBytes(text.ToString()));
        }
        static void Append(StringBuilder target, string value)
        { value = value ?? ""; target.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value); }
        static string Hash(byte[] bytes)
        { using (var algorithm = SHA256.Create()) return Hex(algorithm.ComputeHash(bytes)); }
        static string Hash(Stream stream)
        { using (var algorithm = SHA256.Create()) return Hex(algorithm.ComputeHash(stream)); }
        static string HashBounded(Stream stream, long expected)
        {
            using (var algorithm = SHA256.Create())
            {
                byte[] buffer = new byte[65536]; long count = 0; int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) != 0) {
                    count += read; if (count > expected) throw new InvalidDataException("An expanded ZIP file exceeds its signed length.");
                    algorithm.TransformBlock(buffer, 0, read, buffer, 0);
                }
                if (count != expected) throw new InvalidDataException("An expanded ZIP file is incomplete.");
                algorithm.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(algorithm.Hash);
            }
        }
        static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        static void DeleteOwned(string path)
        { try { SafeFiles.NoReparse(path); if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
