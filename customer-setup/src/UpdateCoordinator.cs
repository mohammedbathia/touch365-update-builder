using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Touch365Support
{
    public interface IUpdateEnvironment
    {
        IDisposable AcquireLock();
        UpdateState LoadState();
        void SaveState(UpdateState state);
        bool IsIdle();
        Version InstalledVersion();
        string PrepareBackup(UpdateManifest manifest);
        void Apply(UpdateManifest manifest, string stageDirectory, string backupId);
        bool IsApplied(UpdateManifest manifest);
        void Restore(UpdateManifest manifest, string backupId);
        bool IsRestored(string backupId);
    }

    public sealed class UpdatePending
    {
        public UpdateManifest Manifest;
        public string BackupId;
        public string Phase;
    }

    public sealed class UpdateState
    {
        public bool Enrolled;
        public long SeenSequence;
        public string SeenDigest = "";
        public DateTime LastSeenUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        public Version HighestBinaryVersion;
        public long HighestConfigVersion;
        public long AppliedSequence;
        public UpdatePending Pending;

        public XElement ToXml()
        {
            Validate();
            var root = new XElement("updaterState",
                new XAttribute("schema", "1"), new XAttribute("enrolled", Enrolled ? "true" : "false"),
                new XAttribute("seenSequence", Number(SeenSequence)), new XAttribute("seenDigest", SeenDigest ?? ""),
                new XAttribute("lastSeenUtc", Time(LastSeenUtc)),
                new XAttribute("highestBinaryVersion", HighestBinaryVersion == null ? "" : HighestBinaryVersion.ToString()),
                new XAttribute("highestConfigVersion", Number(HighestConfigVersion)), new XAttribute("appliedSequence", Number(AppliedSequence)));
            if (Pending != null)
                root.Add(new XElement("pending", new XAttribute("backupId", Pending.BackupId),
                    new XAttribute("phase", Pending.Phase), ManifestXml(Pending.Manifest)));
            return root;
        }

        public static UpdateState FromXml(XElement root)
        {
            Attributes(root, "updaterState", "schema", "enrolled", "seenSequence", "seenDigest", "lastSeenUtc", "highestBinaryVersion", "highestConfigVersion", "appliedSequence");
            if (Value(root, "schema") != "1") throw Corrupt();
            string enrolled = Value(root, "enrolled");
            if (enrolled != "true" && enrolled != "false") throw Corrupt();
            var state = new UpdateState {
                Enrolled = enrolled == "true", SeenSequence = ReadNumber(root, "seenSequence"), SeenDigest = Value(root, "seenDigest"),
                LastSeenUtc = ReadTime(root, "lastSeenUtc"), HighestBinaryVersion = ReadVersion(root, "highestBinaryVersion"),
                HighestConfigVersion = ReadNumber(root, "highestConfigVersion"), AppliedSequence = ReadNumber(root, "appliedSequence")
            };
            if (root.Elements().Count() > 1 || root.Elements().Any(e => e.Name != "pending")) throw Corrupt();
            XElement pending = root.Element("pending");
            if (pending != null)
            {
                Attributes(pending, "pending", "backupId", "phase");
                if (pending.Elements().Count() != 1 || pending.Elements().Single().Name != "manifest") throw Corrupt();
                state.Pending = new UpdatePending { BackupId = Value(pending, "backupId"), Phase = Value(pending, "phase"), Manifest = ReadManifest(pending.Element("manifest")) };
            }
            state.Validate();
            return state;
        }

        internal UpdateState Copy() { return FromXml(ToXml()); }

        internal static UpdateManifest CopyManifest(UpdateManifest manifest)
        {
            ValidateManifest(manifest);
            return ReadManifest(ManifestXml(manifest));
        }

        internal static void ValidateManifest(UpdateManifest manifest)
        {
            if (manifest == null || !Digest(manifest.Digest) || manifest.Sequence <= 0 || manifest.ConfigVersion < 0
                || manifest.IssuedUtc.Kind != DateTimeKind.Utc || manifest.ExpiresUtc.Kind != DateTimeKind.Utc
                || manifest.ExpiresUtc <= manifest.IssuedUtc || (manifest.BinaryVersion == null && manifest.ConfigVersion == 0)
                || (manifest.ConfigVersion == 0 && manifest.Options != null && manifest.Options.Count != 0)
                || (manifest.ConfigVersion > 0 && (manifest.Options == null || manifest.Options.Count == 0)))
                throw new InvalidDataException("The authenticated update manifest is incomplete.");
        }

        private void Validate()
        {
            if (SeenSequence < 0 || AppliedSequence < 0 || AppliedSequence > SeenSequence || HighestConfigVersion < 0
                || LastSeenUtc.Kind != DateTimeKind.Utc || (SeenSequence == 0 ? !String.IsNullOrEmpty(SeenDigest) : !Digest(SeenDigest)))
                throw Corrupt();
            if (Pending == null) return;
            ValidateManifest(Pending.Manifest);
            if (!Regex.IsMatch(Pending.BackupId ?? "", "^[A-Za-z0-9._-]{1,128}$") || Pending.BackupId == "." || Pending.BackupId == ".."
                || (Pending.Phase != "Prepared" && Pending.Phase != "Applying" && Pending.Phase != "RecoveryRequired")
                || Pending.Manifest.Sequence != SeenSequence || !SameDigest(Pending.Manifest.Digest, SeenDigest)
                || Pending.Manifest.Sequence <= AppliedSequence || Pending.Manifest.ConfigVersion > HighestConfigVersion
                || (Pending.Manifest.BinaryVersion != null && (HighestBinaryVersion == null || Pending.Manifest.BinaryVersion > HighestBinaryVersion)))
                throw Corrupt();
        }

        private static XElement ManifestXml(UpdateManifest manifest)
        {
            var root = new XElement("manifest", new XAttribute("digest", manifest.Digest),
                new XAttribute("sequence", Number(manifest.Sequence)), new XAttribute("issuedUtc", Time(manifest.IssuedUtc)),
                new XAttribute("expiresUtc", Time(manifest.ExpiresUtc)),
                new XAttribute("binaryVersion", manifest.BinaryVersion == null ? "" : manifest.BinaryVersion.ToString()),
                new XAttribute("packageUrl", manifest.PackageUrl ?? ""), new XAttribute("packageSha256", manifest.PackageSha256 ?? ""),
                new XAttribute("packageBytes", Number(manifest.PackageBytes)), new XAttribute("configVersion", Number(manifest.ConfigVersion)),
                new XAttribute("boundFields", manifest.BoundFields ?? ""), new XAttribute("boundTrust", manifest.BoundTrust ?? ""));
            root.Add(new XElement("options", (manifest.Options ?? new Dictionary<string, string>()).OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new XElement("option", new XAttribute("name", p.Key), new XAttribute("value", p.Value)))));
            root.Add(new XElement("files", (manifest.Files ?? new List<UpdateFile>()).Select(f =>
                new XElement("file", new XAttribute("path", f.Path), new XAttribute("sha256", f.Sha256), new XAttribute("bytes", Number(f.Bytes))))));
            return root;
        }

        private static UpdateManifest ReadManifest(XElement root)
        {
            Attributes(root, "manifest", "digest", "sequence", "issuedUtc", "expiresUtc", "binaryVersion", "packageUrl", "packageSha256", "packageBytes", "configVersion", "boundFields", "boundTrust");
            if (root.Elements().Count() != 2 || root.Elements("options").Count() != 1 || root.Elements("files").Count() != 1) throw Corrupt();
            XElement options = root.Element("options"), files = root.Element("files");
            Attributes(options, "options"); Attributes(files, "files");
            var manifest = new UpdateManifest {
                Digest = Value(root, "digest"), Sequence = ReadNumber(root, "sequence"), IssuedUtc = ReadTime(root, "issuedUtc"),
                ExpiresUtc = ReadTime(root, "expiresUtc"), BinaryVersion = ReadVersion(root, "binaryVersion"),
                PackageUrl = Value(root, "packageUrl"), PackageSha256 = Value(root, "packageSha256"), PackageBytes = ReadNumber(root, "packageBytes"),
                ConfigVersion = ReadNumber(root, "configVersion"), Options = new Dictionary<string, string>(StringComparer.Ordinal), Files = new List<UpdateFile>(),
                BoundFields = NullIfEmpty(Value(root, "boundFields")), BoundTrust = NullIfEmpty(Value(root, "boundTrust"))
            };
            foreach (XElement option in options.Elements())
            {
                Attributes(option, "option", "name", "value");
                if (option.HasElements || manifest.Options.ContainsKey(Value(option, "name"))) throw Corrupt();
                manifest.Options.Add(Value(option, "name"), Value(option, "value"));
            }
            foreach (XElement file in files.Elements())
            {
                Attributes(file, "file", "path", "sha256", "bytes");
                if (file.HasElements) throw Corrupt();
                manifest.Files.Add(new UpdateFile { Path = Value(file, "path"), Sha256 = Value(file, "sha256"), Bytes = ReadNumber(file, "bytes") });
            }
            ValidateManifest(manifest);
            return manifest;
        }

        private static void Attributes(XElement element, string name, params string[] attributes)
        {
            if (element == null || element.Name != name || element.Attributes().Count() != attributes.Length
                || element.Attributes().Any(a => !attributes.Contains(a.Name.ToString(), StringComparer.Ordinal))
                || element.Nodes().OfType<XText>().Any(t => !String.IsNullOrWhiteSpace(t.Value))) throw Corrupt();
        }
        private static string Value(XElement element, string name)
        { XAttribute value = element.Attribute(name); if (value == null) throw Corrupt(); return value.Value; }
        private static long ReadNumber(XElement element, string name)
        { long value; if (!Int64.TryParse(Value(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 0) throw Corrupt(); return value; }
        private static DateTime ReadTime(XElement element, string name)
        { DateTime value; if (!DateTime.TryParseExact(Value(element, name), "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value) || value.Kind != DateTimeKind.Utc) throw Corrupt(); return value; }
        private static Version ReadVersion(XElement element, string name)
        { string text = Value(element, name); Version value; if (text == "") return null; if (!Version.TryParse(text, out value)) throw Corrupt(); return value; }
        private static string Number(long value) { return value.ToString(CultureInfo.InvariantCulture); }
        private static string NullIfEmpty(string value) { return value == "" ? null : value; }
        private static string Time(DateTime value) { return value.ToString("o", CultureInfo.InvariantCulture); }
        private static bool Digest(string value) { return Regex.IsMatch(value ?? "", "^[A-Fa-f0-9]{64}$"); }
        internal static bool SameDigest(string left, string right) { return String.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
        private static InvalidDataException Corrupt() { return new InvalidDataException("Updater state is invalid. Administrator recovery is required; state must not be reset."); }
    }

    public sealed class UpdateCoordinator
    {
        private readonly IUpdateEnvironment environment;
        private static readonly long ClockToleranceTicks = TimeSpan.FromMinutes(5).Ticks;

        public UpdateCoordinator(IUpdateEnvironment environment)
        { if (environment == null) throw new ArgumentNullException("environment"); this.environment = environment; }

        public string Run(UpdateManifest manifest, string stageDirectory, string approvedDigest, DateTime now)
        {
            UpdateManifest candidate = UpdateState.CopyManifest(manifest);
            RequireUtc(now);
            using (IDisposable maintenance = environment.AcquireLock())
            {
                if (maintenance == null) throw new InvalidOperationException("Another maintenance operation is running.");
                UpdateState state = Load(now);
                if (candidate.IssuedUtc.Ticks > now.Ticks + ClockToleranceTicks || candidate.ExpiresUtc <= now)
                    throw new InvalidOperationException("Update metadata is expired or not yet valid.");
                if (candidate.Sequence < state.SeenSequence || (candidate.Sequence == state.SeenSequence && !UpdateState.SameDigest(candidate.Digest, state.SeenDigest)))
                    throw new InvalidOperationException("Update metadata is a replay or conflicts with the recorded sequence.");
                if (candidate.Sequence == state.AppliedSequence && UpdateState.SameDigest(candidate.Digest, state.SeenDigest)) return "NoUpdate";
                if (!UpdateState.SameDigest(candidate.Digest, approvedDigest)) return "ApprovalRequired";
                if (state.Pending != null)
                {
                    if (!UpdateState.SameDigest(state.Pending.Manifest.Digest, candidate.Digest))
                        throw new InvalidOperationException("A previous update must be recovered before another update can run.");
                    if (state.Pending.Phase != "Prepared") return RecoverLocked(state, now);
                    if (!environment.IsIdle()) return "DeferredActiveSession";
                    if (!environment.IsRestored(state.Pending.BackupId))
                        return RecoveryFailed(state, new InvalidOperationException("The prepared installation changed before application."));
                    return ApplyPrepared(state, stageDirectory, now);
                }
                ValidateVersions(candidate, state);
                state.SeenSequence = candidate.Sequence; state.SeenDigest = candidate.Digest;
                state.LastSeenUtc = Later(state.LastSeenUtc, now);
                if (candidate.BinaryVersion != null && (state.HighestBinaryVersion == null || candidate.BinaryVersion > state.HighestBinaryVersion)) state.HighestBinaryVersion = candidate.BinaryVersion;
                if (candidate.ConfigVersion > state.HighestConfigVersion) state.HighestConfigVersion = candidate.ConfigVersion;
                environment.SaveState(state.Copy());
                if (!environment.IsIdle()) return "DeferredActiveSession";
                string backupId = environment.PrepareBackup(candidate);
                state.Pending = new UpdatePending { Manifest = candidate, BackupId = backupId, Phase = "Prepared" };
                environment.SaveState(state.Copy());
                return ApplyPrepared(state, stageDirectory, now);
            }
        }

        public string Recover(DateTime now)
        {
            RequireUtc(now);
            using (IDisposable maintenance = environment.AcquireLock())
            {
                if (maintenance == null) throw new InvalidOperationException("Another maintenance operation is running.");
                UpdateState state = Load(now);
                return state.Pending == null ? "NoUpdate" : RecoverLocked(state, now);
            }
        }

        private UpdateState Load(DateTime now)
        {
            UpdateState loaded = environment.LoadState();
            if (loaded == null) throw new InvalidDataException("Updater enrollment state is missing. Administrator recovery is required.");
            UpdateState state = loaded.Copy();
            if (!state.Enrolled) throw new InvalidOperationException("Automatic updates have not been enrolled by an administrator.");
            if (now.Ticks + ClockToleranceTicks < state.LastSeenUtc.Ticks)
                throw new InvalidOperationException("The clock moved backwards. Administrator recovery is required.");
            return state;
        }

        private void ValidateVersions(UpdateManifest candidate, UpdateState state)
        {
            bool sameSeen = candidate.Sequence == state.SeenSequence && UpdateState.SameDigest(candidate.Digest, state.SeenDigest);
            if (candidate.BinaryVersion != null)
            {
                Version installed = environment.InstalledVersion();
                if (installed == null) throw new InvalidOperationException("The installed RustDesk version could not be confirmed.");
                if (candidate.BinaryVersion <= installed || (state.HighestBinaryVersion != null && candidate.BinaryVersion < state.HighestBinaryVersion))
                    throw new InvalidOperationException("A binary update must be newer than the installed version and cannot lower the recorded version floor.");
            }
            if (candidate.ConfigVersion > 0 && (candidate.ConfigVersion < state.HighestConfigVersion
                || (candidate.ConfigVersion == state.HighestConfigVersion && !sameSeen)))
                throw new InvalidOperationException("Configuration versions cannot be replayed or downgraded.");
        }

        private string ApplyPrepared(UpdateState state, string stageDirectory, DateTime now)
        {
            if (state.Pending.Manifest.BinaryVersion != null && String.IsNullOrWhiteSpace(stageDirectory))
                throw new InvalidOperationException("The verified staged update is required.");
            if (!environment.IsIdle()) return "DeferredActiveSession";
            state.Pending.Phase = "Applying";
            state.LastSeenUtc = Later(state.LastSeenUtc, now);
            environment.SaveState(state.Copy());
            if (!environment.IsIdle()) { state.Pending.Phase = "Prepared"; environment.SaveState(state.Copy()); return "DeferredActiveSession"; }
            try
            {
                environment.Apply(state.Pending.Manifest, stageDirectory, state.Pending.BackupId);
                if (!environment.IsApplied(state.Pending.Manifest)) throw new InvalidOperationException("The installed update did not pass read-back verification.");
            }
            catch (Exception failure)
            {
                return RollBack(state, failure);
            }
            // A failed commit write must leave the Applying journal available for
            // read-back recovery; never undo verified bytes on an uncertain write.
            Complete(state, now);
            return "Applied";
        }

        private string RecoverLocked(UpdateState state, DateTime now)
        {
            if (!environment.IsIdle()) return "DeferredActiveSession";
            if (state.Pending.Phase != "Prepared" && environment.IsApplied(state.Pending.Manifest))
            {
                Complete(state, now);
                return "RecoveredApplied";
            }
            if (state.Pending.Phase == "Prepared")
            {
                if (!environment.IsRestored(state.Pending.BackupId))
                    return RecoveryFailed(state, new InvalidOperationException("The prepared installation changed before recovery."));
                state.Pending = null; state.LastSeenUtc = Later(state.LastSeenUtc, now);
                environment.SaveState(state.Copy());
                return "RolledBack";
            }
            return RollBack(state, null);
        }

        private string RollBack(UpdateState state, Exception failure)
        {
            try
            {
                if (!environment.IsIdle()) throw new InvalidOperationException("A remote session prevents safe recovery.");
                environment.Restore(state.Pending.Manifest, state.Pending.BackupId);
                if (!environment.IsRestored(state.Pending.BackupId)) throw new InvalidOperationException("The previous installation could not be confirmed after recovery.");
            }
            catch (Exception recoveryFailure)
            {
                return RecoveryFailed(state, failure == null ? recoveryFailure : new AggregateException(failure, recoveryFailure));
            }
            state.Pending = null;
            environment.SaveState(state.Copy());
            return "RolledBack";
        }

        private string RecoveryFailed(UpdateState state, Exception failure)
        {
            state.Pending.Phase = "RecoveryRequired";
            environment.SaveState(state.Copy());
            throw new InvalidOperationException("Update recovery could not be verified. The recovery journal and version floors were retained; administrator attention is required.", failure);
        }

        private void Complete(UpdateState state, DateTime now)
        {
            state.AppliedSequence = state.Pending.Manifest.Sequence;
            state.Pending = null; state.LastSeenUtc = Later(state.LastSeenUtc, now);
            environment.SaveState(state.Copy());
        }
        private static DateTime Later(DateTime left, DateTime right) { return left > right ? left : right; }
        private static void RequireUtc(DateTime value) { if (value.Kind != DateTimeKind.Utc) throw new ArgumentException("An explicit UTC time is required."); }
    }
}
