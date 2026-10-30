using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Touch365Support;

internal static class UpdateEnvironmentTests
{
    private static int passed;
    private static int failed;
    private static readonly DateTime Now = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection was not raised."); }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS updater filesystem: " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL updater filesystem: " + name + ": " + ex.Message); }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root, State, Application, Configuration, Stage, ProfileRoot;
        public const string UserSid = "S-1-5-21-123-456-789-1001";
        public bool Idle = true;
        public bool ServiceExists, ProfileBindingValid = true;
        private readonly bool appOnly;
        public readonly WindowsUpdateEnvironment Environment;
        public Fixture(bool enroll = true, bool appOnlyMode = false)
        {
            appOnly = appOnlyMode;
            Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "env-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            State = Path.Combine(Root, "state"); Application = Path.Combine(Root, "RustDesk");
            ProfileRoot = Path.Combine(Root, "profile");
            Configuration = appOnly ? Path.Combine(UpdateProfile.ConfigRoot(ProfileRoot), "RustDesk2.toml") : Path.Combine(Root, "config", "RustDesk2.toml"); Stage = Path.Combine(Root, "candidate");
            if (Directory.Exists(Root)) throw new IOException("The unique fixture directory already exists.");
            Directory.CreateDirectory(Root); SafeFiles.PrivateDirectory(Application, true);
            Directory.CreateDirectory(Path.GetDirectoryName(Configuration)); Directory.CreateDirectory(Stage);
            File.WriteAllText(Path.Combine(Application, "RustDesk.exe"), "original binary");
            File.WriteAllText(Configuration, "# customer comment\r\n[options]\r\napprove-mode = 'click'\r\nallow-only-conn-window-open = 'Y'\r\naccess-mode = 'custom'\r\nallow-remote-config-modification = 'N'\r\nrelay-server = 'old.example.test'\r\nunrelated-preference = 'preserve me'\r\n[identity]\r\nfixture = 'unchanged'\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Stage, "RustDesk.exe"), "candidate binary");
            if (appOnly)
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(Configuration), "RustDesk.toml"), "fixture-identity-unchanged");
                File.WriteAllText(Configuration, "[options]\r\napprove-mode = 'password-click'\r\nrelay-server = 'customer.example.test'\r\n");
                foreach (string path in UpdatePackage.CompleteFiles)
                {
                    string full = Path.Combine(Stage, path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllText(full, path == "RustDesk.exe" ? new string('x', 512) : "fixture resource " + path);
                }
            }
            Environment = NewEnvironment();
            if (enroll) using (Environment.AcquireLock()) Environment.Initialize(Now);
        }
        public WindowsUpdateEnvironment NewEnvironment(bool adoptProtectedBinding = false)
        {
            if (appOnly)
                return new WindowsUpdateEnvironment(State, Application, Configuration, delegate { return Idle; },
                    delegate { return new Version(1, 5, 0, 0); }, VerifyFixture,
                    UserSid, ProfileRoot, delegate(string sid, string profile) {
                        if (!ProfileBindingValid || sid != UserSid || profile != ProfileRoot) throw new InvalidDataException("Fixture original profile binding changed.");
                        SafeFiles.NoReparse(profile);
                    }, delegate { return UpdateProfile.SnapshotConfigDirectory(Path.GetDirectoryName(Configuration)); }, delegate { return ServiceExists; }, adoptProtectedBinding);
            return new WindowsUpdateEnvironment(State, Application, Configuration, delegate { return Idle; },
                delegate { return new Version(1, 5, 0, 0); }, VerifyFixture);
        }
        public UpdateManifest Manifest(bool binary, bool config)
        {
            var manifest = new UpdateManifest {
                Digest = new string('a', 64), Sequence = 1, IssuedUtc = Now.AddMinutes(-5), ExpiresUtc = Now.AddDays(1),
                BinaryVersion = binary ? new Version(1, 5, 1, 0) : null,
                PackageUrl = binary ? "https://updates.example.test/application.zip" : null,
                PackageSha256 = binary ? new string('b', 64) : null, PackageBytes = binary ? 100 : 0,
                ConfigVersion = config ? 1 : 0, Options = new Dictionary<string, string>(), Files = new List<UpdateFile>()
            };
            if (config) manifest.Options.Add("relay-server", "new.example.test");
            if (binary) manifest.Files.Add(new UpdateFile { Path = "RustDesk.exe", Bytes = new FileInfo(Path.Combine(Stage, "RustDesk.exe")).Length, Sha256 = SafeFiles.Digest(Path.Combine(Stage, "RustDesk.exe")) });
            if (appOnly && binary)
            {
                manifest.PackageLayout = UpdatePackage.CompleteLayout;
                foreach (string path in UpdatePackage.CompleteFiles.Where(path => path != "RustDesk.exe"))
                {
                    string full = Path.Combine(Stage, path.Replace('/', Path.DirectorySeparatorChar));
                    manifest.Files.Add(new UpdateFile { Path = path, Bytes = new FileInfo(full).Length, Sha256 = SafeFiles.Digest(full) });
                }
            }
            return manifest;
        }
        private static void VerifyFixture(string directory, UpdateManifest manifest)
        {
            string[] files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => !String.Equals(path, Path.Combine(directory, UpdatePackage.RetainedShortcut), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length != manifest.Files.Count) throw new InvalidDataException("Fixture file inventory mismatch.");
            foreach (UpdateFile file in manifest.Files)
            {
                string path = Path.Combine(directory, file.Path);
                if (!File.Exists(path) || SafeFiles.Digest(path) != file.Sha256 || new FileInfo(path).Length != file.Bytes)
                    throw new InvalidDataException("Fixture package integrity mismatch.");
            }
        }
        public string Backup(UpdateManifest manifest) { return Environment.PrepareBackup(manifest); }
        public void Dispose()
        {
            string full = Path.GetFullPath(Root);
            string temporary = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("env-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing fixture cleanup outside its temporary directory.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    public static int Run()
    {
        Test("NULL or absent DACL is not mistaken for protected storage", delegate
        {
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var missing = new RawSecurityDescriptor(ControlFlags.None, administrators, administrators, null, null);
            var nullDacl = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, administrators, administrators, null, null);
            byte[] a = new byte[missing.BinaryLength]; missing.GetBinaryForm(a, 0);
            byte[] b = new byte[nullDacl.BinaryLength]; nullDacl.GetBinaryForm(b, 0);
            Assert(!WindowsUpdateEnvironment.HasPresentDacl(a) && !WindowsUpdateEnvironment.HasPresentDacl(b), "An unrestricted DACL was accepted.");
        });
        Test("ordinary and encoded portable RustDesk processes are both busy", delegate
        {
            Assert(WindowsUpdateEnvironment.IsRustDeskProcessName("RustDesk.exe"), "Ordinary client was missed.");
            Assert(WindowsUpdateEnvironment.IsRustDeskProcessName("rustdesk-host=remote.example.test,key=public,.exe"), "Encoded portable client was missed.");
            Assert(WindowsUpdateEnvironment.IsRustDeskProcessName("RuntimeBroker_rustdesk.exe"), "The installed broker helper was missed.");
            Assert(WindowsUpdateEnvironment.IsRustDeskProcessName(null), "Unknown process identity was treated as idle.");
            Assert(!WindowsUpdateEnvironment.IsRustDeskProcessName("another-tool.exe"), "An unrelated named process was misidentified.");
        });
        Test("normalizes installed three-part versions before downgrade comparisons", delegate
        {
            Assert(WindowsUpdateEnvironment.NormalizeVersion(new Version(1, 5, 0)) == new Version(1, 5, 0, 0), "Equivalent installed versions compared differently.");
        });
        Test("explicit enrollment persists and refuses reenrollment", delegate
        {
            using (var fixture = new Fixture())
            {
                UpdateState state = fixture.Environment.LoadState();
                Assert(state.Enrolled && state.HighestBinaryVersion == new Version(1, 5, 0, 0), "The installed-version floor was not enrolled.");
                using (fixture.Environment.AcquireLock()) Throws(delegate { fixture.Environment.Initialize(Now); });
            }
        });
        Test("enrollment requires existing customer-approval policy without changing it", delegate
        {
            string[] names = { "approve-mode", "allow-only-conn-window-open", "access-mode", "allow-remote-config-modification" };
            string[] values = { "password-click", "N", "full", "Y" };
            for (int i = 0; i < names.Length; i++)
                using (var fixture = new Fixture(false))
                using (fixture.Environment.AcquireLock())
                {
                    string changed = ConfigText.SetOption(File.ReadAllText(fixture.Configuration), names[i], values[i]);
                    File.WriteAllText(fixture.Configuration, changed);
                    Throws(delegate { fixture.Environment.Initialize(Now); });
                    Assert(!File.Exists(Path.Combine(fixture.State, "enrolled.marker")), "Unsafe policy was enrolled.");
                    Assert(File.ReadAllText(fixture.Configuration) == changed, "Enrollment silently rewrote customer policy.");
                }
        });
        Test("a partial configuration update cannot bypass later customer-policy drift", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                string changed = ConfigText.SetOption(File.ReadAllText(fixture.Configuration), "approve-mode", "password-click");
                File.WriteAllText(fixture.Configuration, changed);
                Throws(delegate { fixture.Backup(fixture.Manifest(false, true)); });
                Assert(File.ReadAllText(fixture.Configuration) == changed, "Rejected update changed existing customer policy.");
                Assert(fixture.Environment.LoadState().Pending == null, "Rejected policy drift created a transaction.");
            }
        });
        Test("missing enrolled state fails closed instead of resetting its floor", delegate
        {
            using (var fixture = new Fixture())
            {
                File.Delete(Path.Combine(fixture.State, "state.xml"));
                Throws(delegate { fixture.Environment.LoadState(); });
                using (fixture.Environment.AcquireLock()) Throws(delegate { fixture.Environment.Initialize(Now); });
            }
        });
        Test("corrupt state fails closed", delegate
        {
            using (var fixture = new Fixture())
            {
                File.WriteAllText(Path.Combine(fixture.State, "state.xml"), "<!DOCTYPE state [<!ENTITY e SYSTEM 'file:///not-allowed'>]><state>&e;</state>");
                Throws(delegate { fixture.Environment.LoadState(); });
            }
        });
        Test("ordinary setup refuses a pending update and leaves its journal intact", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                fixture.Environment.RequireNoPendingSetup();
                UpdateManifest manifest = fixture.Manifest(true, true);
                string backup = fixture.Backup(manifest);
                UpdateState state = fixture.Environment.LoadState();
                state.SeenSequence = manifest.Sequence; state.SeenDigest = manifest.Digest;
                state.HighestBinaryVersion = manifest.BinaryVersion; state.HighestConfigVersion = manifest.ConfigVersion;
                state.Pending = new UpdatePending { Manifest = manifest, BackupId = backup, Phase = "Prepared" };
                fixture.Environment.SaveState(state);
                string before = File.ReadAllText(Path.Combine(fixture.State, "state.xml"));
                Throws(delegate { fixture.Environment.RequireNoPendingSetup(); });
                Assert(File.ReadAllText(Path.Combine(fixture.State, "state.xml")) == before, "Setup changed the pending recovery journal.");
                Assert(File.ReadAllText(Path.Combine(fixture.Application, "RustDesk.exe")) == "original binary", "Setup guard changed application bytes.");
            }
        });
        Test("ordinary setup cannot bypass missing or corrupt enrolled state", delegate
        {
            using (var fixture = new Fixture())
            {
                string path = Path.Combine(fixture.State, "state.xml");
                File.WriteAllText(path, "<damaged />");
                Throws(delegate { fixture.Environment.RequireNoPendingSetup(); });
                File.Delete(path);
                Throws(delegate { fixture.Environment.RequireNoPendingSetup(); });
            }
        });
        Test("another maintenance owner blocks concurrent mutation", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                WindowsUpdateEnvironment second = fixture.NewEnvironment();
                Throws(delegate { using (second.AcquireLock()) { } });
            }
        });
        Test("state floors cannot move backwards through persistence", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                UpdateState state = fixture.Environment.LoadState(); state.SeenSequence = 7; state.SeenDigest = new string('c', 64);
                state.HighestConfigVersion = 3; state.LastSeenUtc = Now.AddMinutes(1); fixture.Environment.SaveState(state);
                state.SeenSequence = 6;
                Throws(delegate { fixture.Environment.SaveState(state); });
            }
        });
        Test("active or unknown sessions prevent backup and application", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                fixture.Idle = false; UpdateManifest manifest = fixture.Manifest(true, true);
                Throws(delegate { fixture.Backup(manifest); });
                Assert(File.ReadAllText(Path.Combine(fixture.Application, "RustDesk.exe")) == "original binary", "Busy update changed the installation.");
            }
        });
        Test("unavailable process/service enumeration is never proof of idle", delegate
        {
            using (var fixture = new Fixture())
            {
                var unknown = new WindowsUpdateEnvironment(fixture.State, fixture.Application, fixture.Configuration,
                    delegate { throw new IOException("Fixture process enumeration unavailable."); }, delegate { return new Version(1, 5, 0, 0); }, delegate { });
                Assert(!unknown.IsIdle(), "Unknown process state was treated as idle.");
            }
        });
        Test("configuration-only update preserves unrelated fields and can restore exact bytes", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                byte[] before = File.ReadAllBytes(fixture.Configuration); UpdateManifest manifest = fixture.Manifest(false, true);
                string backup = fixture.Backup(manifest); fixture.Environment.Apply(manifest, fixture.Stage, backup);
                string after = File.ReadAllText(fixture.Configuration);
                Assert(after.Contains("customer comment") && after.Contains("unrelated-preference = 'preserve me'") && after.Contains("fixture = 'unchanged'"), "Configuration update lost unrelated customer data.");
                Assert(ConfigText.ReadOption(after, "relay-server") == "new.example.test" && fixture.Environment.IsApplied(manifest), "The requested option did not verify.");
                fixture.Environment.Restore(manifest, backup);
                Assert(before.SequenceEqual(File.ReadAllBytes(fixture.Configuration)) && fixture.Environment.IsRestored(backup), "Recovery did not restore exact original bytes.");
            }
        });
        Test("a changed configuration is never overwritten after staging", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(false, true); string backup = fixture.Backup(manifest);
                File.AppendAllText(fixture.Configuration, "# concurrent customer edit\r\n");
                Throws(delegate { fixture.Environment.Apply(manifest, fixture.Stage, backup); });
                Throws(delegate { fixture.Environment.Restore(manifest, backup); });
                Assert(File.ReadAllText(fixture.Configuration).Contains("concurrent customer edit"), "A concurrent customer edit was overwritten.");
            }
        });
        Test("binary replacement retains original directory and restores it with exact files", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(true, true); string before = UpdatePackage.Fingerprint(fixture.Application);
                string backup = fixture.Backup(manifest); fixture.Environment.Apply(manifest, fixture.Stage, backup);
                Assert(fixture.Environment.IsApplied(manifest), "The full replacement did not verify.");
                Assert(Directory.Exists(fixture.Application + ".touch365-before-" + backup), "The original directory was not retained.");
                fixture.Environment.Restore(manifest, backup);
                Assert(UpdatePackage.Fingerprint(fixture.Application) == before && fixture.Environment.IsRestored(backup), "Directory rollback changed the original files.");
            }
        });
        Test("new package resources inherit the application's read permissions", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                File.WriteAllText(Path.Combine(fixture.Stage, "resource.dat"), "new resource");
                UpdateManifest manifest = fixture.Manifest(true, false);
                manifest.Files.Add(new UpdateFile { Path = "resource.dat", Bytes = new FileInfo(Path.Combine(fixture.Stage, "resource.dat")).Length, Sha256 = SafeFiles.Digest(Path.Combine(fixture.Stage, "resource.dat")) });
                string backup = fixture.Backup(manifest); fixture.Environment.Apply(manifest, fixture.Stage, backup);
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                bool usersCanRead = File.GetAccessControl(Path.Combine(fixture.Application, "resource.dat")).GetAccessRules(true, true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>().Any(rule => rule.IdentityReference.Value == users.Value && rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.ReadData) != 0);
                Assert(usersCanRead, "The replacement resource inherited administrator-only staging access.");
            }
        });
        Test("crash in the directory swap gap can restore the retained original", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(true, false); string backup = fixture.Backup(manifest);
                Directory.Move(fixture.Application, fixture.Application + ".touch365-before-" + backup);
                Assert(fixture.Environment.IsIdle(), "The known swap gap was misclassified as an active session.");
                fixture.Environment.Restore(manifest, backup);
                Assert(fixture.Environment.IsRestored(backup), "The original did not recover from the swap gap.");
            }
        });
        Test("recovery refuses unknown application bytes", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(true, false); string backup = fixture.Backup(manifest);
                fixture.Environment.Apply(manifest, fixture.Stage, backup);
                File.AppendAllText(Path.Combine(fixture.Application, "RustDesk.exe"), "concurrent modification");
                Throws(delegate { fixture.Environment.Restore(manifest, backup); });
                Assert(File.ReadAllText(Path.Combine(fixture.Application, "RustDesk.exe")).Contains("concurrent modification"), "Recovery overwrote unknown bytes.");
            }
        });
        Test("busy state after approval still prevents application", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(true, false); string backup = fixture.Backup(manifest); fixture.Idle = false;
                Throws(delegate { fixture.Environment.Apply(manifest, fixture.Stage, backup); });
                Assert(File.ReadAllText(Path.Combine(fixture.Application, "RustDesk.exe")) == "original binary", "Late active session was interrupted.");
            }
        });
        Test("backup identities cannot escape protected storage", delegate
        {
            using (var fixture = new Fixture())
            using (fixture.Environment.AcquireLock())
                Throws(delegate { fixture.Environment.Restore(fixture.Manifest(true, false), "..\\outside"); });
        });
        Test("new production binaries require the complete application layout in either mode", delegate
        {
            using (var fixture = new Fixture())
                Throws(delegate { WindowsUpdateEnvironment.RequireNewApplicationPackage(fixture.Manifest(true, false)); });
            using (var fixture = new Fixture(true, true))
                WindowsUpdateEnvironment.RequireNewApplicationPackage(fixture.Manifest(true, false));
        });
        Test("app-only enrollment binds the original profile without changing access policy", delegate
        {
            using (var fixture = new Fixture(true, true))
            {
                UpdateState state = fixture.Environment.LoadState();
                Assert(state.TargetMode == UpdateState.AppOnly && state.ProfileSid == Fixture.UserSid && state.ProfileRoot == fixture.ProfileRoot, "The original profile was not bound to enrollment.");
                Assert(ConfigText.ReadOption(File.ReadAllText(fixture.Configuration), "approve-mode") == "password-click", "App-only enrollment rewrote the user's existing policy.");
                Assert(!File.Exists(Path.Combine(fixture.State, "configuration-before")), "Enrollment copied configuration into an application target.");
            }
        });
        Test("app-only rejects every configuration-bearing manifest and incomplete binary", delegate
        {
            using (var fixture = new Fixture(true, true))
            using (fixture.Environment.AcquireLock())
            {
                Throws(delegate { fixture.Backup(fixture.Manifest(false, true)); });
                Throws(delegate { fixture.Backup(fixture.Manifest(true, true)); });
                UpdateManifest legacy = fixture.Manifest(true, false); legacy.PackageLayout = null;
                Throws(delegate { fixture.Backup(legacy); });
                Assert(!Directory.Exists(Path.Combine(fixture.State, "transactions")), "A rejected target created a backup transaction.");
            }
        });
        Test("a fresh worker adopts its app-only target only from protected state", delegate
        {
            using (var fixture = new Fixture(true, true))
            {
                WindowsUpdateEnvironment fresh = fixture.NewEnvironment(true);
                UpdateState loaded = fresh.LoadState();
                Assert(loaded.TargetMode == UpdateState.AppOnly && loaded.ProfileSid == Fixture.UserSid && fresh.IsIdle(), "The fresh worker did not adopt the recorded app-only target.");
                using (fresh.AcquireLock())
                {
                    string backup = fresh.PrepareBackup(fixture.Manifest(true, false));
                    fresh.Restore(fixture.Manifest(true, false), backup);
                    Assert(fresh.IsRestored(backup), "Adopted app-only backup failed to bind original settings.");
                }
            }
        });
        Test("target mode SID and profile root cannot change after enrollment", delegate
        {
            using (var fixture = new Fixture(true, true))
            using (fixture.Environment.AcquireLock())
            {
                UpdateState state = fixture.Environment.LoadState(); state.TargetMode = UpdateState.ServiceManaged; state.ProfileSid = state.ProfileRoot = "";
                Throws(delegate { fixture.Environment.SaveState(state); });
                state = fixture.Environment.LoadState(); state.ProfileSid = "S-1-5-21-123-456-789-1002";
                Throws(delegate { fixture.Environment.SaveState(state); });
                state = fixture.Environment.LoadState(); state.ProfileRoot = fixture.Root;
                Throws(delegate { fixture.Environment.SaveState(state); });
                state = fixture.Environment.LoadState();
                Assert(state.TargetMode == UpdateState.AppOnly && state.ProfileSid == Fixture.UserSid && state.ProfileRoot == fixture.ProfileRoot, "Rejected target changes altered protected state.");
            }
        });
        Test("an appearing service prevents app-only backup application and recovery", delegate
        {
            using (var fixture = new Fixture(true, true))
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(true, false);
                string backup = fixture.Backup(manifest); fixture.ServiceExists = true;
                Assert(!fixture.Environment.IsIdle(), "A newly present service was treated as absent.");
                Throws(delegate { fixture.Environment.Apply(manifest, fixture.Stage, backup); });
                Throws(delegate { fixture.Environment.Restore(manifest, backup); });
                Assert(!fixture.Environment.IsRestored(backup), "Recovery claimed an unchanged target after service mode changed.");
                Assert(File.ReadAllText(Path.Combine(fixture.Application, "RustDesk.exe")) == "original binary", "A changed service mode permitted replacement.");
            }
        });
        Test("app-only binary swap preserves exact identity settings and a retained uninstall shortcut", delegate
        {
            using (var fixture = new Fixture(true, true))
            using (fixture.Environment.AcquireLock())
            {
                string profileBefore = UpdateProfile.SnapshotConfigDirectory(Path.GetDirectoryName(fixture.Configuration));
                File.WriteAllText(Path.Combine(fixture.Application, UpdatePackage.RetainedShortcut), "existing opaque shortcut");
                UpdateManifest manifest = fixture.Manifest(true, false); string before = UpdatePackage.Fingerprint(fixture.Application);
                string backup = fixture.Backup(manifest); fixture.Environment.Apply(manifest, fixture.Stage, backup);
                Assert(fixture.Environment.IsApplied(manifest), "App-only replacement did not verify.");
                Assert(File.ReadAllText(Path.Combine(fixture.Application, UpdatePackage.RetainedShortcut)) == "existing opaque shortcut", "The original uninstall shortcut was lost or replaced.");
                Assert(UpdateProfile.SnapshotConfigDirectory(Path.GetDirectoryName(fixture.Configuration)) == profileBefore, "Application replacement changed user identity/settings.");
                fixture.Environment.Restore(manifest, backup);
                Assert(UpdatePackage.Fingerprint(fixture.Application) == before && fixture.Environment.IsRestored(backup), "App-only rollback failed to restore the exact old tree.");
            }
        });
        Test("app-only backup refuses concurrent profile changes without overwriting them", delegate
        {
            using (var fixture = new Fixture(true, true))
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(true, false); string backup = fixture.Backup(manifest);
                File.AppendAllText(fixture.Configuration, "# concurrent profile edit\r\n");
                string edited = File.ReadAllText(fixture.Configuration);
                Throws(delegate { fixture.Environment.Apply(manifest, fixture.Stage, backup); });
                Throws(delegate { fixture.Environment.Restore(manifest, backup); });
                Assert(File.ReadAllText(fixture.Configuration) == edited, "A concurrent user edit was overwritten.");
                Assert(File.ReadAllText(Path.Combine(fixture.Application, "RustDesk.exe")) == "original binary", "A concurrent user edit permitted application.");
            }
        });
        Test("app-only recovery preserves profile changes after a completed directory swap", delegate
        {
            using (var fixture = new Fixture(true, true))
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(true, false); string backup = fixture.Backup(manifest);
                fixture.Environment.Apply(manifest, fixture.Stage, backup);
                File.AppendAllText(fixture.Configuration, "# later customer change\r\n");
                string installed = UpdatePackage.Fingerprint(fixture.Application);
                Assert(!fixture.Environment.IsApplied(manifest), "Changed user settings were reported unchanged.");
                Throws(delegate { fixture.Environment.Restore(manifest, backup); });
                Assert(UpdatePackage.Fingerprint(fixture.Application) == installed && File.ReadAllText(fixture.Configuration).Contains("later customer change"), "Recovery silently overwrote a changed application/profile target.");
            }
        });
        Test("app-only recovery handles the directory swap gap with no service", delegate
        {
            using (var fixture = new Fixture(true, true))
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(true, false); string backup = fixture.Backup(manifest);
                Directory.Move(fixture.Application, fixture.Application + ".touch365-before-" + backup);
                Assert(fixture.Environment.IsIdle(), "The known app-only swap gap was misclassified as busy.");
                fixture.Environment.Restore(manifest, backup);
                Assert(fixture.Environment.IsRestored(backup), "App-only swap-gap recovery failed.");
            }
        });
        Test("app-only recovery rejects a different original user or profile binding", delegate
        {
            using (var fixture = new Fixture(true, true))
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(true, false); string backup = fixture.Backup(manifest);
                fixture.ProfileBindingValid = false;
                Throws(delegate { fixture.NewEnvironment().LoadState(); });
                Throws(delegate { fixture.Environment.Restore(manifest, backup); });
                Assert(File.ReadAllText(Path.Combine(fixture.Application, "RustDesk.exe")) == "original binary", "Changed user binding allowed recovery mutation.");
            }
        });
        Test("transaction backups bind the app-only original profile and shortcut", delegate
        {
            using (var fixture = new Fixture(true, true))
            using (fixture.Environment.AcquireLock())
            {
                UpdateManifest manifest = fixture.Manifest(true, false); string backup = fixture.Backup(manifest);
                string path = Path.Combine(fixture.State, "transactions", backup, "backup.xml");
                var xml = System.Xml.Linq.XElement.Load(path); xml.SetAttributeValue("profileSid", "S-1-5-21-123-456-789-1002"); xml.Save(path);
                Throws(delegate { fixture.Environment.Apply(manifest, fixture.Stage, backup); });
                Throws(delegate { fixture.Environment.Restore(manifest, backup); });
                Assert(File.ReadAllText(Path.Combine(fixture.Application, "RustDesk.exe")) == "original binary", "A foreign transaction target was applied.");
            }
        });
        Console.WriteLine("RESULT updater filesystem: " + passed + " passed, " + failed + " failed.");
        return failed;
    }
}
