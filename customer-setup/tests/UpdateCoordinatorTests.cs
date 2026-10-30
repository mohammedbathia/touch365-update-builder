using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Touch365Support;

internal static class UpdateCoordinatorTests
{
    private static int passed;
    private static int failed;
    private static readonly DateTime Now = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private sealed class Lease : IDisposable
    {
        private readonly Action release;
        public Lease(Action release) { this.release = release; }
        public void Dispose() { release(); }
    }

    private sealed class Fake : IUpdateEnvironment
    {
        public UpdateState State = new UpdateState { Enrolled = true };
        public bool Locked;
        public bool Idle = true;
        public Version CurrentVersion = new Version(1, 5, 0);
        public long CurrentConfig;
        public Version BackupVersion = new Version(1, 5, 0);
        public long BackupConfig;
        public bool Applied;
        public bool FailApply;
        public bool FailRestore;
        public bool FailCommitSave;
        public bool FailReadBack;
        public int SaveCount;
        public int BackupCount;
        public int ApplyCount;
        public int RestoreCount;
        public Action<UpdateState> AfterSave;
        public Action OnBackup;
        public readonly List<string> Events = new List<string>();

        public IDisposable AcquireLock()
        {
            Events.Add("LOCK");
            if (Locked) throw new IOException("Fixture maintenance lock is busy.");
            Locked = true;
            return new Lease(delegate { Locked = false; Events.Add("UNLOCK"); });
        }
        public UpdateState LoadState()
        {
            Events.Add("LOAD");
            return State == null ? null : UpdateState.FromXml(State.ToXml());
        }
        public void SaveState(UpdateState state)
        {
            if (FailCommitSave && state.AppliedSequence > 0 && state.Pending == null) throw new IOException("Fixture commit write failure.");
            SaveCount++;
            State = UpdateState.FromXml(state.ToXml());
            Events.Add("SAVE " + (State.Pending == null ? "Idle" : State.Pending.Phase));
            if (AfterSave != null) AfterSave(State);
        }
        public bool IsIdle() { Events.Add("IDLE"); return Idle; }
        public Version InstalledVersion() { return CurrentVersion; }
        public string PrepareBackup(UpdateManifest manifest)
        {
            Events.Add("BACKUP"); BackupCount++;
            BackupVersion = CurrentVersion; BackupConfig = CurrentConfig;
            if (OnBackup != null) OnBackup();
            return "fixture-backup";
        }
        public void Apply(UpdateManifest manifest, string stageDirectory, string backupId)
        {
            Events.Add("APPLY"); ApplyCount++;
            if (manifest.BinaryVersion != null) CurrentVersion = manifest.BinaryVersion;
            if (manifest.ConfigVersion > 0) CurrentConfig = manifest.ConfigVersion;
            Applied = !FailReadBack;
            if (FailApply) throw new IOException("Fixture interrupted application.");
        }
        public bool IsApplied(UpdateManifest manifest) { Events.Add("VERIFY"); return Applied && !FailReadBack; }
        public void Restore(UpdateManifest manifest, string backupId)
        {
            Events.Add("RESTORE"); RestoreCount++;
            if (FailRestore) throw new IOException("Fixture concurrent bytes prevent safe restoration.");
            CurrentVersion = BackupVersion; CurrentConfig = BackupConfig; Applied = false;
        }
        public bool IsRestored(string backupId)
        { Events.Add("RESTORED"); return CurrentVersion == BackupVersion && CurrentConfig == BackupConfig; }
    }

    private static UpdateManifest Binary(long sequence = 1, char digest = 'a')
    {
        return new UpdateManifest {
            Digest = new string(digest, 64), Sequence = sequence, IssuedUtc = Now.AddMinutes(-5), ExpiresUtc = Now.AddDays(1),
            BinaryVersion = new Version(1, 6, 0), PackageUrl = "https://updates.example.invalid/rustdesk.zip",
            PackageSha256 = new string('b', 64), PackageBytes = 12, ConfigVersion = 0,
            Options = new Dictionary<string, string>(), Files = new List<UpdateFile> {
                new UpdateFile { Path = "rustdesk.exe", Sha256 = new string('c', 64), Bytes = 10 }
            }
        };
    }
    private static UpdateManifest Config(long sequence = 1, char digest = 'a', long version = 1)
    {
        UpdateManifest manifest = Binary(sequence, digest);
        manifest.BinaryVersion = null; manifest.PackageUrl = ""; manifest.PackageSha256 = ""; manifest.PackageBytes = 0;
        manifest.Files.Clear(); manifest.ConfigVersion = version; manifest.Options.Add("enable-audio", "N");
        return manifest;
    }
    private static UpdateState Interrupted(UpdateManifest manifest, string phase)
    {
        return new UpdateState {
            Enrolled = true, SeenSequence = manifest.Sequence, SeenDigest = manifest.Digest, LastSeenUtc = Now,
            HighestBinaryVersion = manifest.BinaryVersion, HighestConfigVersion = manifest.ConfigVersion,
            Pending = new UpdatePending { Manifest = manifest, BackupId = "fixture-backup", Phase = phase }
        };
    }
    private static UpdateState AppOnlyState()
    {
        return new UpdateState { Enrolled = true, TargetMode = UpdateState.AppOnly,
            ProfileSid = "S-1-5-21-1-2-3-1001", ProfileRoot = @"C:\Users\Fixture\AppData\Roaming\RustDesk\config" };
    }
    private static UpdateManifest AppBinary()
    {
        UpdateManifest manifest = Binary(); manifest.PackageLayout = UpdatePackage.CompleteLayout; return manifest;
    }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static Exception Throws(Action action)
    {
        try { action(); } catch (Exception exception) { return exception; }
        throw new Exception("Expected rejection did not occur.");
    }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS updater coordinator " + name); }
        catch (Exception exception) { failed++; Console.WriteLine("FAIL updater coordinator " + name + ": " + exception.Message); }
    }

    public static int Run()
    {
        passed = 0; failed = 0;
        Test("approval is bound to the exact manifest for binary and configuration", delegate
        {
            foreach (UpdateManifest manifest in new[] { Binary(), Config() })
            {
                var fake = new Fake();
                string result = new UpdateCoordinator(fake).Run(manifest, "staged", new string('f', 64), Now);
                Assert(result == "ApprovalRequired" && fake.SaveCount == 0 && fake.BackupCount == 0 && fake.ApplyCount == 0, "An unapproved update caused mutation.");
            }
        });
        Test("missing or unenrolled state never silently resets", delegate
        {
            var missing = new Fake { State = null };
            Throws(delegate { new UpdateCoordinator(missing).Run(Binary(), "staged", new string('a', 64), Now); });
            var disabled = new Fake { State = new UpdateState() };
            Throws(delegate { new UpdateCoordinator(disabled).Recover(Now); });
            Assert(missing.SaveCount == 0 && disabled.SaveCount == 0, "Enrollment state was automatically recreated.");
        });
        Test("active sessions defer without backup and retain approved replay floors", delegate
        {
            var fake = new Fake { Idle = false };
            UpdateManifest manifest = Binary(); var coordinator = new UpdateCoordinator(fake);
            Assert(coordinator.Run(manifest, "staged", manifest.Digest, Now) == "DeferredActiveSession", "An active session did not defer.");
            Assert(fake.BackupCount == 0 && fake.ApplyCount == 0 && fake.State.SeenSequence == 1 && fake.State.HighestBinaryVersion == manifest.BinaryVersion, "Deferral lost the replay floor or touched the client.");
            fake.Idle = true;
            Assert(coordinator.Run(manifest, "staged", manifest.Digest, Now) == "Applied" && fake.ApplyCount == 1, "The exact deferred digest could not resume.");
        });
        Test("idle is rechecked after backup and the prepared transaction resumes once", delegate
        {
            var fake = new Fake(); UpdateManifest manifest = Binary(); var coordinator = new UpdateCoordinator(fake);
            fake.AfterSave = delegate(UpdateState state) { if (state.Pending != null && state.Pending.Phase == "Prepared") fake.Idle = false; };
            Assert(coordinator.Run(manifest, "staged", manifest.Digest, Now) == "DeferredActiveSession", "A new session after backup was ignored.");
            Assert(fake.State.Pending.Phase == "Prepared" && fake.ApplyCount == 0, "Application ran after the idle guard failed.");
            fake.AfterSave = null; fake.Idle = true;
            Assert(coordinator.Run(manifest, "staged", manifest.Digest, Now) == "Applied" && fake.BackupCount == 1 && fake.ApplyCount == 1, "Prepared recovery repeated backup or installation.");
        });
        Test("durable Prepared and Applying records precede host mutation", delegate
        {
            var fake = new Fake(); UpdateManifest manifest = Config();
            Assert(new UpdateCoordinator(fake).Run(manifest, "", manifest.Digest, Now) == "Applied", "Configuration did not apply.");
            Assert(fake.Events.IndexOf("BACKUP") < fake.Events.IndexOf("SAVE Prepared") && fake.Events.IndexOf("SAVE Prepared") < fake.Events.IndexOf("SAVE Applying")
                && fake.Events.IndexOf("SAVE Applying") < fake.Events.IndexOf("APPLY"), "Journal writes did not precede mutation.");
        });
        Test("a competing maintenance lock prevents all state or client operations", delegate
        {
            var fake = new Fake { Locked = true };
            Throws(delegate { new UpdateCoordinator(fake).Run(Binary(), "staged", new string('a', 64), Now); });
            Assert(!fake.Events.Contains("LOAD") && fake.SaveCount == 0 && fake.ApplyCount == 0, "A competing run reached protected state.");
        });
        Test("expired and future-issued metadata are rejected before writes", delegate
        {
            UpdateManifest expired = Binary(); expired.ExpiresUtc = Now;
            UpdateManifest future = Binary(); future.IssuedUtc = Now.AddMinutes(10);
            foreach (UpdateManifest manifest in new[] { expired, future })
            {
                var fake = new Fake();
                Throws(delegate { new UpdateCoordinator(fake).Run(manifest, "staged", manifest.Digest, Now); });
                Assert(fake.SaveCount == 0 && fake.BackupCount == 0, "Invalid time metadata reached mutation.");
            }
        });
        Test("clock rollback cannot reopen old metadata", delegate
        {
            var fake = new Fake(); fake.State.LastSeenUtc = Now.AddHours(1);
            Throws(delegate { new UpdateCoordinator(fake).Run(Binary(), "staged", new string('a', 64), Now); });
            Assert(fake.SaveCount == 0, "Clock rollback changed protected state.");
        });
        Test("old sequence and same-sequence equivocation are rejected", delegate
        {
            var fake = new Fake(); fake.State.SeenSequence = 2; fake.State.SeenDigest = new string('b', 64);
            foreach (UpdateManifest manifest in new[] { Binary(1), Binary(2, 'c') })
                Throws(delegate { new UpdateCoordinator(fake).Run(manifest, "staged", manifest.Digest, Now); });
            Assert(fake.ApplyCount == 0 && fake.SaveCount == 0, "A replay or conflicting digest was accepted.");
        });
        Test("a completed sequence is not installed twice", delegate
        {
            var fake = new Fake(); UpdateManifest manifest = Binary(); var coordinator = new UpdateCoordinator(fake);
            Assert(coordinator.Run(manifest, "staged", manifest.Digest, Now) == "Applied", "Initial update did not apply.");
            Assert(coordinator.Run(manifest, "staged", manifest.Digest, Now) == "NoUpdate" && fake.ApplyCount == 1, "A completed update was installed again.");
        });
        Test("binary downgrade and same-version replacement are rejected", delegate
        {
            foreach (Version target in new[] { new Version(1, 4, 3), new Version(1, 5, 0) })
            {
                var fake = new Fake(); UpdateManifest manifest = Binary(); manifest.BinaryVersion = target;
                Throws(delegate { new UpdateCoordinator(fake).Run(manifest, "staged", manifest.Digest, Now); });
                Assert(fake.ApplyCount == 0 && fake.SaveCount == 0, "A binary downgrade or same-version replacement was accepted.");
            }
        });
        Test("configuration version rollback or reuse with new metadata is rejected", delegate
        {
            foreach (long version in new long[] { 2, 3 })
            {
                var fake = new Fake(); fake.State.HighestConfigVersion = 3;
                UpdateManifest manifest = Config(1, 'a', version);
                Throws(delegate { new UpdateCoordinator(fake).Run(manifest, "", manifest.Digest, Now); });
                Assert(fake.ApplyCount == 0 && fake.SaveCount == 0, "A non-increasing configuration version was accepted.");
            }
        });
        Test("failed apply restores the baseline but retains all version floors", delegate
        {
            var fake = new Fake { FailApply = true }; UpdateManifest manifest = Binary(); var coordinator = new UpdateCoordinator(fake);
            Assert(coordinator.Run(manifest, "staged", manifest.Digest, Now) == "RolledBack", "Application failure did not recover.");
            Assert(fake.CurrentVersion == new Version(1, 5, 0) && fake.RestoreCount == 1 && fake.State.Pending == null
                && fake.State.SeenSequence == 1 && fake.State.HighestBinaryVersion == new Version(1, 6, 0) && fake.State.AppliedSequence == 0, "Rollback lowered replay floors or retained partial state.");
            UpdateManifest lower = Binary(2, 'b'); lower.BinaryVersion = new Version(1, 5, 1);
            Throws(delegate { coordinator.Run(lower, "staged", lower.Digest, Now); });
            fake.FailApply = false;
            Assert(coordinator.Run(manifest, "staged", manifest.Digest, Now) == "Applied", "The exact rolled-back manifest could not retry.");
        });
        Test("failed final read-back also restores the baseline", delegate
        {
            var fake = new Fake { FailReadBack = true }; UpdateManifest manifest = Config();
            Assert(new UpdateCoordinator(fake).Run(manifest, "", manifest.Digest, Now) == "RolledBack" && fake.CurrentConfig == 0, "Read-back failure was marked applied.");
        });
        Test("failed recovery preserves its journal and never claims success", delegate
        {
            var fake = new Fake { FailApply = true, FailRestore = true }; UpdateManifest manifest = Binary();
            Exception failure = Throws(delegate { new UpdateCoordinator(fake).Run(manifest, "staged", manifest.Digest, Now); });
            Assert(failure.Message.Contains("recovery") && fake.State.Pending.Phase == "RecoveryRequired" && fake.State.SeenSequence == 1 && fake.State.AppliedSequence == 0, "Recovery failure was swallowed or its journal erased.");
        });
        Test("interrupted completed application is committed without another installation", delegate
        {
            UpdateManifest manifest = Binary(); manifest.ExpiresUtc = Now.AddMinutes(-1);
            var fake = new Fake { State = Interrupted(manifest, "Applying"), Applied = true, CurrentVersion = manifest.BinaryVersion };
            Assert(new UpdateCoordinator(fake).Recover(Now) == "RecoveredApplied" && fake.State.AppliedSequence == 1 && fake.State.Pending == null
                && fake.ApplyCount == 0 && fake.RestoreCount == 0, "Recovery reran a completed update or required current feed availability.");
        });
        Test("interrupted partial application restores once and retains replay protection", delegate
        {
            UpdateManifest manifest = Binary();
            var fake = new Fake { State = Interrupted(manifest, "Applying"), CurrentVersion = new Version(1, 5, 9) };
            Assert(new UpdateCoordinator(fake).Recover(Now) == "RolledBack" && fake.CurrentVersion == fake.BackupVersion && fake.State.SeenSequence == 1
                && fake.State.HighestBinaryVersion == manifest.BinaryVersion && fake.RestoreCount == 1 && fake.ApplyCount == 0, "Interrupted recovery lost its version floor or reran installation.");
        });
        Test("concurrent changes after preparation require recovery without overwrite", delegate
        {
            UpdateManifest manifest = Binary();
            var fake = new Fake { State = Interrupted(manifest, "Prepared"), CurrentVersion = new Version(1, 5, 1) };
            Throws(delegate { new UpdateCoordinator(fake).Recover(Now); });
            Assert(fake.CurrentVersion == new Version(1, 5, 1) && fake.RestoreCount == 0 && fake.State.Pending.Phase == "RecoveryRequired", "Prepared recovery overwrote unrelated changes.");
        });
        Test("active sessions also defer recovery", delegate
        {
            var fake = new Fake { State = Interrupted(Binary(), "Applying"), Idle = false };
            Assert(new UpdateCoordinator(fake).Recover(Now) == "DeferredActiveSession" && fake.ApplyCount == 0 && fake.RestoreCount == 0 && fake.SaveCount == 0, "Recovery disturbed an active session.");
        });
        Test("a failed commit write leaves verified bytes for read-back recovery", delegate
        {
            var fake = new Fake { FailCommitSave = true }; UpdateManifest manifest = Binary(); var coordinator = new UpdateCoordinator(fake);
            Throws(delegate { coordinator.Run(manifest, "staged", manifest.Digest, Now); });
            Assert(fake.Applied && fake.State.Pending.Phase == "Applying" && fake.RestoreCount == 0, "Uncertain commit write undid verified bytes or erased intent.");
            fake.FailCommitSave = false;
            Assert(coordinator.Recover(Now) == "RecoveredApplied" && fake.ApplyCount == 1, "Commit recovery reinstalled the candidate.");
        });
        Test("state round-trip preserves all manifest and replay fields", delegate
        {
            UpdateManifest manifest = Binary(); manifest.ConfigVersion = 2; manifest.Options.Add("enable-audio", "N");
            UpdateState state = Interrupted(manifest, "Applying");
            UpdateState copy = UpdateState.FromXml(XElement.Parse(state.ToXml().ToString()));
            Assert(copy.Enrolled && copy.SeenSequence == 1 && copy.LastSeenUtc.Kind == DateTimeKind.Utc && copy.Pending.Manifest.Files[0].Sha256 == new string('c', 64)
                && copy.Pending.Manifest.Options["enable-audio"] == "N" && copy.HighestConfigVersion == 2, "The recovery journal lost protected data.");
            XElement unknown = state.ToXml(); unknown.Add(new XAttribute("ignoreTrust", "true")); Throws(delegate { UpdateState.FromXml(unknown); });
            XElement badPhase = state.ToXml(); badPhase.Element("pending").SetAttributeValue("phase", "CommittedButUnverified"); Throws(delegate { UpdateState.FromXml(badPhase); });
            XElement duplicate = state.ToXml(); XElement option = duplicate.Element("pending").Element("manifest").Element("options").Element("option"); option.AddAfterSelf(new XElement(option));
            Throws(delegate { UpdateState.FromXml(duplicate); });
        });
        Test("the caller cannot mutate the authenticated manifest during backup", delegate
        {
            var fake = new Fake(); UpdateManifest manifest = Binary();
            fake.OnBackup = delegate { manifest.Sequence = 99; manifest.BinaryVersion = new Version(9, 9, 9); };
            Assert(new UpdateCoordinator(fake).Run(manifest, "staged", manifest.Digest, Now) == "Applied" && fake.State.AppliedSequence == 1
                && fake.CurrentVersion == new Version(1, 6, 0), "External mutation changed the approved transaction.");
        });
        Test("legacy state migrates only to the service-managed mode and retains pending fields", delegate
        {
            XElement legacy = Interrupted(Binary(), "Applying").ToXml();
            legacy.SetAttributeValue("schema", "1");
            legacy.Attribute("targetMode").Remove(); legacy.Attribute("profileSid").Remove(); legacy.Attribute("profileRoot").Remove();
            legacy.Element("pending").Element("manifest").Attribute("packageLayout").Remove();
            UpdateState state = UpdateState.FromXml(legacy);
            Assert(state.TargetMode == UpdateState.ServiceManaged && state.ProfileSid == "" && state.ProfileRoot == ""
                && state.Pending.Manifest.PackageLayout == null && state.SeenSequence == 1, "Legacy state lost its original target semantics.");
            Assert((string)state.ToXml().Attribute("schema") == "2", "A migrated state was not written with explicit target binding.");
        });
        Test("app-only target and signed package layout survive durable pending round trips", delegate
        {
            UpdateManifest manifest = AppBinary(); manifest.BoundFields = new string('d', 64); manifest.BoundTrust = new string('e', 64);
            UpdateState state = AppOnlyState(); state.SeenSequence = 1; state.SeenDigest = manifest.Digest;
            state.HighestBinaryVersion = manifest.BinaryVersion; state.Pending = new UpdatePending { Manifest = manifest, BackupId = "fixture-backup", Phase = "Applying" };
            UpdateState copy = UpdateState.FromXml(state.ToXml());
            Assert(copy.TargetMode == UpdateState.AppOnly && copy.ProfileSid == state.ProfileSid && copy.ProfileRoot == state.ProfileRoot
                && copy.Pending.Manifest.PackageLayout == UpdatePackage.CompleteLayout && copy.Pending.Manifest.BoundFields == manifest.BoundFields
                && copy.Pending.Manifest.BoundTrust == manifest.BoundTrust, "App-only recovery lost target or signed layout binding.");
        });
        Test("unknown modes and incomplete or cross-mode profile bindings fail closed", delegate
        {
            UpdateState unknown = new UpdateState { TargetMode = "auto-detect" }; Throws(delegate { unknown.ToXml(); });
            UpdateState missingSid = AppOnlyState(); missingSid.ProfileSid = ""; Throws(delegate { missingSid.ToXml(); });
            UpdateState relative = AppOnlyState(); relative.ProfileRoot = @"C:\Users\Fixture\..\Other"; Throws(delegate { relative.ToXml(); });
            UpdateState network = AppOnlyState(); network.ProfileRoot = @"\\server\profile"; Throws(delegate { network.ToXml(); });
            UpdateState crossed = new UpdateState { ProfileSid = "S-1-5-21-1-2-3-1001" }; Throws(delegate { crossed.ToXml(); });
            UpdateState configFloor = AppOnlyState(); configFloor.HighestConfigVersion = 1; Throws(delegate { configFloor.ToXml(); });
        });
        Test("app-only rejects config-only mixed and legacy packages before any replay-state write", delegate
        {
            UpdateManifest mixed = AppBinary(); mixed.ConfigVersion = 1; mixed.Options.Add("approve-mode", "click");
            foreach (UpdateManifest manifest in new[] { Config(), mixed, Binary() })
            {
                var fake = new Fake { State = AppOnlyState() };
                Throws(delegate { new UpdateCoordinator(fake).Run(manifest, "staged", manifest.Digest, Now); });
                Assert(fake.SaveCount == 0 && fake.BackupCount == 0 && fake.ApplyCount == 0 && fake.State.SeenSequence == 0,
                    "An incompatible signed update advanced an app-only enrollment or touched application data.");
            }
        });
        Test("app-only successful application preserves protected target binding", delegate
        {
            var fake = new Fake { State = AppOnlyState() }; UpdateManifest manifest = AppBinary();
            Assert(new UpdateCoordinator(fake).Run(manifest, "staged", manifest.Digest, Now) == "Applied", "Compatible app-only transaction failed.");
            Assert(fake.State.TargetMode == UpdateState.AppOnly && fake.State.ProfileSid == "S-1-5-21-1-2-3-1001"
                && fake.State.HighestConfigVersion == 0 && fake.State.AppliedSequence == 1 && fake.ApplyCount == 1, "App-only application changed scope.");
        });
        Test("app-only uncertain commit recovers without reapplication or binding loss", delegate
        {
            var fake = new Fake { State = AppOnlyState(), FailCommitSave = true }; UpdateManifest manifest = AppBinary();
            var coordinator = new UpdateCoordinator(fake);
            Throws(delegate { coordinator.Run(manifest, "staged", manifest.Digest, Now); });
            Assert(fake.State.Pending != null && fake.State.Pending.Manifest.PackageLayout == UpdatePackage.CompleteLayout, "Uncertain commit lost app-only intent.");
            fake.FailCommitSave = false;
            Assert(coordinator.Recover(Now) == "RecoveredApplied" && fake.ApplyCount == 1 && fake.State.TargetMode == UpdateState.AppOnly,
                "App-only recovery reinstalled or switched mode.");
        });
        Test("app-only same-version update is still rejected without lowering floors", delegate
        {
            var fake = new Fake { State = AppOnlyState() }; UpdateManifest manifest = AppBinary(); manifest.BinaryVersion = fake.CurrentVersion;
            Throws(delegate { new UpdateCoordinator(fake).Run(manifest, "staged", manifest.Digest, Now); });
            Assert(fake.SaveCount == 0 && fake.ApplyCount == 0, "The service-free mode bypassed version protection.");
        });
        Console.WriteLine("RESULT updater coordinator " + passed + " passed, " + failed + " failed.");
        return failed;
    }
}
