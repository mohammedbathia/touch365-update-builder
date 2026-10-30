using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace Touch365Support
{
    // The setup worker and updater share this on-disk, machine-wide lock.
    // It is deliberately acquired only after administrator approval.
    public static class MaintenanceLock
    {
        public static IDisposable Acquire()
        {
            RequireAdministrator();
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var security = new MutexSecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new MutexAccessRule(administrators, MutexRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new MutexAccessRule(system, MutexRights.FullControl, AccessControlType.Allow));
            bool created;
            var mutex = new Mutex(false, @"Global\Touch365RemoteSupportMaintenance", out created, security);
            try
            {
                MutexSecurity actual = mutex.GetAccessControl();
                if (!WindowsUpdateEnvironment.HasPresentDacl(actual.GetSecurityDescriptorBinaryForm()))
                    throw new InvalidOperationException("The maintenance lock has an unrestricted permission descriptor. Administrator recovery is required.");
                string owner = actual.GetOwner(typeof(SecurityIdentifier)).Value;
                if (owner != administrators.Value && owner != system.Value)
                    throw new InvalidOperationException("The maintenance lock has an untrusted owner. Administrator recovery is required.");
                foreach (MutexAccessRule rule in actual.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                    if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Value != administrators.Value && rule.IdentityReference.Value != system.Value)
                        throw new InvalidOperationException("The maintenance lock has an untrusted permission. Administrator recovery is required.");
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; } // Durable journals determine recovery, never the mutex alone.
                if (!acquired) throw new InvalidOperationException("Another Touch365 maintenance operation is running. Try again after it finishes.");
                return new MutexOwner(mutex);
            }
            catch { mutex.Dispose(); throw; }
        }

        internal static IDisposable AcquireAt(string path)
        {
            SafeFiles.NoReparse(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new InvalidOperationException("Another Touch365 maintenance operation is running. Try again after it finishes."); }
        }

        internal static void RequireAdministrator()
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Administrator approval is required for Touch365 updates.");
        }

        private sealed class MutexOwner : IDisposable
        {
            private Mutex mutex;
            public MutexOwner(Mutex value) { mutex = value; }
            public void Dispose() { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); mutex = null; } }
        }
    }

    /// <summary>
    /// File transactions for the existing official EXE installation. No installer,
    /// service-control command, scheduled task or arbitrary script is run here.
    /// </summary>
    public sealed class WindowsUpdateEnvironment : IUpdateEnvironment
    {
        public static readonly string DefaultStateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Touch365-RemoteSupportUpdates");
        private readonly string root;
        private readonly string application;
        private readonly string configuration;
        private readonly Func<bool> idle;
        private readonly Func<Version> version;
        private readonly Action<string, UpdateManifest> verify;
        private readonly Action requireTarget;
        private readonly bool fixture;
        private bool locked;
        private string preparedId;

        public WindowsUpdateEnvironment()
        {
            root = Path.GetFullPath(DefaultStateRoot);
            application = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk");
            configuration = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config\RustDesk2.toml");
            idle = WindowsIdle;
            requireTarget = ValidateWindowsTarget;
            version = delegate {
                ValidateWindowsTarget();
                string executable = Path.Combine(application, "RustDesk.exe");
                Signature.RequireOfficial(executable, "existing installation before update");
                return ReadVersion(executable);
            };
            verify = UpdatePackage.VerifyDirectory;
        }

        // This seam is used only by isolated filesystem tests. No command-line or
        // downloaded metadata can select it or supply its delegates.
        internal WindowsUpdateEnvironment(string stateRoot, string applicationDirectory, string configPath,
            Func<bool> isIdle, Func<Version> installedVersion, Action<string, UpdateManifest> verifyDirectory)
        {
            root = Path.GetFullPath(stateRoot);
            application = Path.GetFullPath(applicationDirectory);
            configuration = Path.GetFullPath(configPath);
            idle = isIdle;
            version = installedVersion;
            verify = verifyDirectory;
            requireTarget = delegate { SafeFiles.NoReparse(application); SafeFiles.NoReparse(configuration); };
            fixture = true;
        }

        public static void Enroll(DateTime now)
        {
            var environment = new WindowsUpdateEnvironment();
            using (environment.AcquireLock()) environment.Initialize(now);
        }

        // The setup worker calls this while holding the shared maintenance lock.
        // Ordinary setup must not overwrite an updater's interrupted transaction.
        public static void RequireSetupReady()
        { new WindowsUpdateEnvironment().RequireNoPendingSetup(); }

        internal void RequireNoPendingSetup()
        {
            SafeFiles.NoReparse(root);
            if (File.Exists(root)) throw new InvalidOperationException("Updater storage needs administrator recovery before setup can continue.");
            if (!Directory.Exists(root)) return; // No updater storage is present.
            UpdateState state = LoadState(); // Missing/damaged protected state must also block setup.
            if (state.Pending != null)
                throw new InvalidOperationException("An interrupted Touch365 update needs recovery. Open the update controller and choose Recover before running setup again.");
        }

        internal void Initialize(DateTime now)
        {
            RequireLock();
            RequireIdle();
            requireTarget();
            Version installed = InstalledVersion();
            RequireConsentPolicy(ReadBytes(configuration, 1048576));
            MakePrivateDirectory(root, true); // Read-only status is not secret; backups are private below.
            string marker = Path.Combine(root, "enrolled.marker");
            if (File.Exists(marker) || File.Exists(StatePath))
                throw new InvalidOperationException("Updater enrollment already exists. Missing or damaged state requires administrator recovery; it must not be reset.");
            WriteNew(marker, Encoding.UTF8.GetBytes("Touch365 updater enrollment version 1\r\n"));
            SaveState(new UpdateState {
                Enrolled = true, SeenSequence = 0, SeenDigest = "", LastSeenUtc = now.ToUniversalTime(),
                HighestBinaryVersion = installed, HighestConfigVersion = 0, AppliedSequence = 0
            });
        }

        public IDisposable AcquireLock()
        {
            if (locked) throw new InvalidOperationException("This maintenance operation already owns its lock.");
            IDisposable handle;
            if (fixture)
            {
                SafeFiles.PrivateDirectory(root, true);
                handle = MaintenanceLock.AcquireAt(Path.Combine(root, "maintenance.lock"));
            }
            else handle = MaintenanceLock.Acquire();
            locked = true;
            return new OwnedLock(handle, delegate { locked = false; });
        }

        public UpdateState LoadState()
        {
            SafeFiles.NoReparse(StatePath);
            if (!fixture)
            {
                VerifyAdministratorProtection(root, true);
                if (File.Exists(Path.Combine(root, "enrolled.marker"))) VerifyAdministratorProtection(Path.Combine(root, "enrolled.marker"), false);
                if (File.Exists(StatePath)) VerifyAdministratorProtection(StatePath, false);
            }
            if (!File.Exists(Path.Combine(root, "enrolled.marker")) || !File.Exists(StatePath))
                throw new InvalidOperationException("Updater is not enrolled or its protected state is missing. Administrator recovery is required; no replay floor was reset.");
            try
            {
                var document = ReadXml(StatePath, 262144);
                var state = UpdateState.FromXml(document);
                if (!state.Enrolled) throw new InvalidDataException("Enrollment is not valid.");
                return state;
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is XmlException || ex is InvalidDataException || ex is FormatException || ex is ArgumentException)
                    throw new InvalidOperationException("Updater state is unreadable or damaged. Administrator recovery is required; no replay floor was reset.", ex);
                throw;
            }
        }

        public void SaveState(UpdateState state)
        {
            RequireLock();
            if (state == null || !state.Enrolled) throw new InvalidDataException("Updater state must remain enrolled.");
            SafeFiles.NoReparse(StatePath);
            if (!File.Exists(Path.Combine(root, "enrolled.marker"))) throw new InvalidOperationException("Updater is not enrolled.");
            if (File.Exists(StatePath))
            {
                UpdateState previous = LoadState();
                if (state.SeenSequence < previous.SeenSequence || state.HighestConfigVersion < previous.HighestConfigVersion
                    || (previous.HighestBinaryVersion != null && (state.HighestBinaryVersion == null || state.HighestBinaryVersion < previous.HighestBinaryVersion))
                    || state.LastSeenUtc < previous.LastSeenUtc)
                    throw new InvalidDataException("Updater replay protection cannot move backwards.");
            }
            AtomicWrite(StatePath, Encoding.UTF8.GetBytes(state.ToXml().ToString(SaveOptions.DisableFormatting)), null);
        }

        public bool IsIdle()
        {
            try { requireTarget(); return idle(); }
            catch { return false; } // Unknown service/process state is busy, never proof of safety.
        }

        public Version InstalledVersion() { requireTarget(); return version(); }

        public string PrepareBackup(UpdateManifest manifest)
        {
            RequireLock(); RequireIdle(); requireTarget();
            ValidateManifestConfiguration(manifest);
            InstalledVersion(); // Configuration also requires a currently trusted installed client.
            RequireConsentPolicy(ReadBytes(configuration, 1048576));
            if (!Directory.Exists(application)) throw new InvalidOperationException("A managed RustDesk installation is required before updating.");
            string id = Guid.NewGuid().ToString("N");
            string directory = Transaction(id);
            MakePrivateDirectory(Path.Combine(root, "transactions"), false);
            MakePrivateDirectory(directory, false);
            string originalApplicationHash = UpdatePackage.Fingerprint(application);
            UpdatePackage.CopyVerified(application, Path.Combine(directory, "application-before"));
            ProtectOwnedTree(Path.Combine(directory, "application-before"));
            FlushTree(Path.Combine(directory, "application-before"));
            if (UpdatePackage.Fingerprint(application) != originalApplicationHash
                || UpdatePackage.Fingerprint(Path.Combine(directory, "application-before")) != originalApplicationHash)
                throw new IOException("RustDesk changed while its update backup was prepared. No update was applied.");
            bool existed = File.Exists(configuration);
            byte[] before = existed ? ReadBytes(configuration, 1048576) : null;
            RequireConsentPolicy(before);
            byte[] after = before;
            if (manifest.ConfigVersion > 0)
            {
                if (!existed) throw new InvalidOperationException("The existing RustDesk service configuration is missing. Repair it before applying a policy update.");
                string text = new UTF8Encoding(false, true).GetString(before);
                foreach (var option in manifest.Options.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    text = ConfigText.SetOption(text, option.Key, option.Value);
                after = new UTF8Encoding(false).GetBytes(text);
            }
            RequireConsentPolicy(after);
            if (before != null) WriteNew(Path.Combine(directory, "configuration-before"), before);
            if (after != null) WriteNew(Path.Combine(directory, "configuration-after"), after);
            var backup = new Backup {
                Id = id, Digest = manifest.Digest, AppBefore = originalApplicationHash,
                AppAfter = manifest.BinaryVersion == null ? originalApplicationHash : "",
                ConfigBefore = before == null ? "absent" : Digest(before),
                ConfigAfter = after == null ? "absent" : Digest(after)
            };
            SaveBackup(backup);
            preparedId = id;
            return id;
        }

        public void Apply(UpdateManifest manifest, string stagedDirectory, string backupId)
        {
            RequireLock(); RequireIdle(); requireTarget(); ValidateManifestConfiguration(manifest);
            Backup backup = LoadBackup(backupId, manifest.Digest);
            RequireOriginal(backup);
            if (manifest.BinaryVersion != null)
            {
                SafeFiles.NoReparse(stagedDirectory);
                verify(stagedDirectory, manifest);
                string replacement = Sibling("stage", backup.Id);
                string retained = Sibling("before", backup.Id);
                if (Directory.Exists(replacement) || Directory.Exists(retained))
                    throw new IOException("An earlier update directory needs recovery before another update can be applied.");
                UpdatePackage.CopyVerified(stagedDirectory, replacement);
                ProtectOwnedTree(replacement);
                PreserveApplicationAccess(application, replacement);
                if (!fixture) VerifyProtectedTree(replacement);
                verify(replacement, manifest);
                FlushTree(replacement);
                backup.AppAfter = UpdatePackage.Fingerprint(replacement);
                SaveBackup(backup); // Durable expected bytes before either directory moves.
                RequireOriginal(backup);
                RequireIdle();
                Directory.Move(application, retained); // Retain the exact old tree and its original ACLs.
                RequireIdle();
                Directory.Move(replacement, application);
            }
            if (manifest.ConfigVersion > 0)
            {
                RequireConfiguration(backup.ConfigBefore);
                RequireIdle();
                byte[] after = ReadBytes(Path.Combine(Transaction(backup.Id), "configuration-after"), 1048576);
                if (Digest(after) != backup.ConfigAfter) throw new InvalidDataException("The prepared configuration does not match its verified backup record.");
                RequireConsentPolicy(after);
                AtomicWrite(configuration, after, backup.ConfigBefore);
            }
            if (!IsApplied(manifest)) throw new InvalidOperationException("The applied update could not be verified. Recovery is required.");
        }

        public bool IsApplied(UpdateManifest manifest)
        {
            try
            {
                Backup backup = CurrentBackup(manifest);
                if (String.IsNullOrEmpty(backup.AppAfter) || !Directory.Exists(application)
                    || UpdatePackage.Fingerprint(application) != backup.AppAfter || CurrentConfig() != backup.ConfigAfter) return false;
                RequireConsentPolicy(ReadBytes(configuration, 1048576));
                if (manifest.BinaryVersion != null) verify(application, manifest);
                return true;
            }
            catch { return false; }
        }

        public void Restore(UpdateManifest manifest, string backupId)
        {
            RequireLock(); RequireIdle(); requireTarget();
            Backup backup = LoadBackup(backupId, manifest.Digest);
            string currentConfig = CurrentConfig();
            if (currentConfig != backup.ConfigBefore && currentConfig != backup.ConfigAfter)
                throw new InvalidOperationException("RustDesk configuration changed outside this update. Recovery will not overwrite it.");
            string currentApplication = Directory.Exists(application) ? UpdatePackage.Fingerprint(application) : "absent";
            string retained = Sibling("before", backup.Id);
            bool retainedExists = Directory.Exists(retained);
            bool applicationBefore = currentApplication == backup.AppBefore;
            bool applicationAfter = !String.IsNullOrEmpty(backup.AppAfter) && currentApplication == backup.AppAfter;
            if (!applicationBefore)
            {
                if ((!applicationAfter && currentApplication != "absent") || !retainedExists || UpdatePackage.Fingerprint(retained) != backup.AppBefore)
                    throw new InvalidOperationException("RustDesk application files changed outside this update or its retained backup is unavailable. Recovery will not overwrite them.");
            }
            else if (retainedExists)
                throw new InvalidOperationException("Ambiguous duplicate installation directories require administrator recovery.");
            if (currentConfig != backup.ConfigBefore)
            {
                RequireIdle();
                if (backup.ConfigBefore == "absent")
                    throw new InvalidOperationException("Unexpected new configuration requires administrator recovery.");
                byte[] before = ReadBytes(Path.Combine(Transaction(backup.Id), "configuration-before"), 1048576);
                if (Digest(before) != backup.ConfigBefore) throw new InvalidDataException("The configuration recovery backup is damaged.");
                AtomicWrite(configuration, before, backup.ConfigAfter);
            }
            if (!applicationBefore)
            {
                if (Directory.Exists(application))
                {
                    string rejected = Sibling("rejected", backup.Id);
                    if (Directory.Exists(rejected)) throw new IOException("A retained failed update requires administrator recovery.");
                    RequireIdle();
                    Directory.Move(application, rejected);
                }
                RequireIdle();
                Directory.Move(retained, application);
            }
            if (!IsRestored(backup.Id)) throw new InvalidOperationException("The previous RustDesk installation could not be fully restored.");
        }

        public bool IsRestored(string backupId)
        {
            try
            {
                Backup backup = LoadBackup(backupId, null);
                return Directory.Exists(application) && UpdatePackage.Fingerprint(application) == backup.AppBefore && CurrentConfig() == backup.ConfigBefore;
            }
            catch { return false; }
        }

        private void ValidateWindowsTarget()
        {
            MaintenanceLock.RequireAdministrator();
            SafeFiles.NoReparse(application); SafeFiles.NoReparse(configuration); SafeFiles.NoReparse(root);
            InstalledClient client = WindowsHost.Inspect();
            if (!client.Registered || client.Msi || !client.ServiceExists || client.Exe == null
                || !String.Equals(Path.GetFullPath(client.Exe), Path.Combine(application, "RustDesk.exe"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Automatic file updates require the existing standard Program Files RustDesk EXE installation. MSI, custom, incomplete or conflicting installations require separate maintenance.");
        }

        private bool WindowsIdle()
        {
            using (var service = new ServiceController("RustDesk"))
            {
                service.Refresh();
                if (service.Status != ServiceControllerStatus.Stopped) return false;
            }
            // Touch365's distributed portable client encodes its server in its
            // filename (rustdesk-host=...,.exe); it can own a session too.
            using (var query = new ManagementObjectSearcher("SELECT Name, ProcessId FROM Win32_Process WHERE Name LIKE 'rustdesk%.exe'"))
                foreach (ManagementObject process in query.Get())
                {
                    using (process) if (IsRustDeskProcessName(Convert.ToString(process["Name"]))) return false;
                }
            return true;
        }
        internal static bool IsRustDeskProcessName(string name)
        { return String.IsNullOrWhiteSpace(name) || (name.StartsWith("rustdesk", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)); }

        private void RequireIdle()
        {
            if (!IsIdle()) throw new InvalidOperationException("Update deferred. Quit RustDesk and have an administrator stop its service after all support sessions finish. Touch365 updates never stop sessions or services automatically.");
        }

        private void RequireLock() { if (!locked) throw new InvalidOperationException("The machine-wide maintenance lock is required."); }
        public static void CreateProtectedDirectory(string path)
        {
            MaintenanceLock.RequireAdministrator();
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            string approved = Path.GetFullPath(DefaultStateRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!String.Equals(full, approved, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(approved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Updater staging must stay inside its protected state directory.");
            CreateAdministratorDirectory(approved, true);
            if (full.Length == approved.Length) return;
            string current = approved;
            foreach (string part in full.Substring(approved.Length + 1).Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                CreateAdministratorDirectory(current, false);
            }
        }

        // Copies/extraction initially create files using the existing helper,
        // whose same-user ACL is appropriate for ordinary setup backups but not
        // privileged update staging. Seal every descendant before final checks.
        public static void ProtectTree(string path)
        {
            MaintenanceLock.RequireAdministrator();
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            string approved = Path.GetFullPath(DefaultStateRoot).TrimEnd(Path.DirectorySeparatorChar);
            string siblingPrefix = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk.touch365-");
            bool statePath = String.Equals(full, approved, StringComparison.OrdinalIgnoreCase) || full.StartsWith(approved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            bool siblingPath = full.StartsWith(siblingPrefix, StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(full.Substring(siblingPrefix.Length), "^(?:stage|before|rejected)-[a-f0-9]{32}$");
            if (!statePath && !siblingPath) throw new InvalidOperationException("Only owned updater staging can be protected.");
            ProtectDirectoryAndChildren(full);
            VerifyProtectedTree(full);
        }

        private void ProtectOwnedTree(string path) { if (!fixture) ProtectTree(path); }

        private static void ProtectDirectoryAndChildren(string directory)
        {
            SafeFiles.NoReparse(directory);
            Directory.SetAccessControl(directory, AdministratorDirectorySecurity(false));
            foreach (string child in Directory.GetDirectories(directory)) ProtectDirectoryAndChildren(child);
            foreach (string file in Directory.GetFiles(directory)) { SafeFiles.NoReparse(file); File.SetAccessControl(file, AdministratorFileSecurity()); }
        }

        private static void VerifyProtectedTree(string directory)
        {
            VerifyAdministratorProtection(directory, true);
            foreach (string child in Directory.GetDirectories(directory)) VerifyProtectedTree(child);
            foreach (string file in Directory.GetFiles(directory)) VerifyAdministratorProtection(file, false);
        }

        private void MakePrivateDirectory(string path, bool readForUsers)
        {
            if (fixture) SafeFiles.PrivateDirectory(path, readForUsers);
            else CreateProtectedDirectory(path);
        }

        private static void CreateAdministratorDirectory(string path, bool readForUsers)
        {
            SafeFiles.NoReparse(path);
            if (Directory.Exists(path)) { VerifyAdministratorProtection(path, true); return; }
            Directory.CreateDirectory(path, AdministratorDirectorySecurity(readForUsers));
            VerifyAdministratorProtection(path, true);
        }

        private static DirectorySecurity AdministratorDirectorySecurity(bool readForUsers)
        {
            var acl = new DirectorySecurity();
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            acl.SetAccessRuleProtection(true, false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (readForUsers) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            return acl;
        }

        private static void VerifyAdministratorProtection(string path, bool directory)
        {
            SafeFiles.NoReparse(path);
            FileSystemSecurity acl = directory ? (FileSystemSecurity)Directory.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner)
                : File.GetAccessControl(path, AccessControlSections.Access | AccessControlSections.Owner);
            if (!HasPresentDacl(acl.GetSecurityDescriptorBinaryForm()))
                throw new InvalidOperationException("Updater protected storage has an unrestricted permission descriptor. Recovery is required.");
            string administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
            string system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value;
            string owner = acl.GetOwner(typeof(SecurityIdentifier)).Value;
            if (owner != administrators && owner != system)
                throw new InvalidOperationException("Updater protected storage has an untrusted owner. Administrator recovery is required.");
            const FileSystemRights mutation = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
                | FileSystemRights.WriteExtendedAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete
                | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & mutation) != 0
                    && rule.IdentityReference.Value != administrators && rule.IdentityReference.Value != system)
                    throw new InvalidOperationException("Updater protected storage is writable outside administrator maintenance. Recovery is required.");
        }
        internal static bool HasPresentDacl(byte[] descriptor)
        {
            var raw = new RawSecurityDescriptor(descriptor, 0);
            return (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0 && raw.DiscretionaryAcl != null;
        }

        private static void ProtectNewFile(string path)
        {
            string approved = Path.GetFullPath(DefaultStateRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(approved, StringComparison.OrdinalIgnoreCase)) return;
            File.SetAccessControl(path, AdministratorFileSecurity());
            VerifyAdministratorProtection(path, false);
        }

        private static FileSecurity AdministratorFileSecurity()
        {
            var acl = new FileSecurity();
            acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            return acl;
        }
        private string StatePath { get { return Path.Combine(root, "state.xml"); } }
        private string Transaction(string id)
        {
            if (!Regex.IsMatch(id ?? "", "^[a-f0-9]{32}$")) throw new InvalidDataException("Invalid update transaction identity.");
            string path = Path.Combine(root, "transactions", id); SafeFiles.NoReparse(path); return path;
        }
        private string Sibling(string purpose, string id)
        { Transaction(id); string path = application + ".touch365-" + purpose + "-" + id; SafeFiles.NoReparse(path); return path; }

        private static Version ReadVersion(string executable)
        {
            Match match = Regex.Match(FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "", @"^\d+\.\d+\.\d+(?:\.\d+)?");
            if (!match.Success) throw new InvalidDataException("RustDesk has an unsupported file version.");
            return NormalizeVersion(new Version(match.Value));
        }
        internal static Version NormalizeVersion(Version parsed)
        { return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision)); }

        private void RequireOriginal(Backup backup)
        {
            if (!Directory.Exists(application) || UpdatePackage.Fingerprint(application) != backup.AppBefore)
                throw new IOException("RustDesk application files changed while preparing the update. No replacement is permitted.");
            RequireConfiguration(backup.ConfigBefore);
        }
        private void RequireConfiguration(string expected)
        { if (CurrentConfig() != expected) throw new IOException("RustDesk configuration changed outside this update. It will not be overwritten."); }
        private string CurrentConfig() { return File.Exists(configuration) ? Digest(ReadBytes(configuration, 1048576)) : "absent"; }

        private Backup CurrentBackup(UpdateManifest manifest)
        {
            string id = preparedId;
            if (String.IsNullOrEmpty(id))
            {
                UpdateState state = LoadState();
                if (state.Pending == null || state.Pending.Manifest.Digest != manifest.Digest) throw new InvalidDataException("No matching recovery journal exists.");
                id = state.Pending.BackupId;
            }
            return LoadBackup(id, manifest.Digest);
        }

        private sealed class Backup
        {
            public string Id, Digest, AppBefore, AppAfter, ConfigBefore, ConfigAfter;
        }
        private void SaveBackup(Backup backup)
        {
            var xml = new XElement("backup", new XAttribute("schema", "1"), new XAttribute("id", backup.Id),
                new XAttribute("digest", backup.Digest), new XAttribute("applicationBefore", backup.AppBefore),
                new XAttribute("applicationAfter", backup.AppAfter), new XAttribute("configurationBefore", backup.ConfigBefore),
                new XAttribute("configurationAfter", backup.ConfigAfter));
            AtomicWrite(Path.Combine(Transaction(backup.Id), "backup.xml"), Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting)), null);
        }
        private Backup LoadBackup(string id, string digest)
        {
            if (!fixture)
            {
                VerifyAdministratorProtection(root, true);
                VerifyAdministratorProtection(Path.Combine(root, "transactions"), true);
                VerifyAdministratorProtection(Transaction(id), true);
            }
            XElement xml = ReadXml(Path.Combine(Transaction(id), "backup.xml"), 16384);
            string[] attributes = { "schema", "id", "digest", "applicationBefore", "applicationAfter", "configurationBefore", "configurationAfter" };
            if (xml.Name != "backup" || xml.Elements().Any() || xml.Attributes().Count() != attributes.Length
                || xml.Attributes().Any(a => !attributes.Contains(a.Name.LocalName) || a.Name.Namespace != XNamespace.None)
                || (string)xml.Attribute("schema") != "1" || (string)xml.Attribute("id") != id)
                throw new InvalidDataException("The update backup record is invalid.");
            var backup = new Backup { Id = id, Digest = (string)xml.Attribute("digest"), AppBefore = (string)xml.Attribute("applicationBefore"),
                AppAfter = (string)xml.Attribute("applicationAfter"), ConfigBefore = (string)xml.Attribute("configurationBefore"), ConfigAfter = (string)xml.Attribute("configurationAfter") };
            if (!IsHash(backup.Digest) || (digest != null && backup.Digest != digest) || !IsHash(backup.AppBefore)
                || (!String.IsNullOrEmpty(backup.AppAfter) && !IsHash(backup.AppAfter))
                || (backup.ConfigBefore != "absent" && !IsHash(backup.ConfigBefore)) || (backup.ConfigAfter != "absent" && !IsHash(backup.ConfigAfter)))
                throw new InvalidDataException("The update backup record does not match the authenticated update.");
            return backup;
        }

        private static bool IsHash(string text) { return Regex.IsMatch(text ?? "", "^[a-f0-9]{64}$"); }
        private static string Digest(byte[] bytes)
        { using (var hash = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static byte[] ReadBytes(string path, int maximum)
        {
            SafeFiles.NoReparse(path);
            string approved = Path.GetFullPath(DefaultStateRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (Path.GetFullPath(path).StartsWith(approved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                VerifyAdministratorProtection(path, false);
                string parent = Path.GetDirectoryName(Path.GetFullPath(path));
                while (parent != null && (String.Equals(parent, approved, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(approved + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                { VerifyAdministratorProtection(parent, true); if (String.Equals(parent, approved, StringComparison.OrdinalIgnoreCase)) break; parent = Path.GetDirectoryName(parent); }
            }
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > maximum) throw new InvalidDataException("An update state or configuration file exceeds its supported size.");
                byte[] bytes = new byte[(int)input.Length]; int offset = 0;
                while (offset < bytes.Length) { int count = input.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
                return bytes;
            }
        }
        private static XElement ReadXml(string path, int maximum)
        {
            byte[] bytes = ReadBytes(path, maximum);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = maximum };
            using (var input = new MemoryStream(bytes)) using (var reader = XmlReader.Create(input, settings)) return XElement.Load(reader);
        }
        private static void WriteNew(string path, byte[] bytes)
        {
            SafeFiles.NoReparse(path);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
            ProtectNewFile(path);
        }
        private void AtomicWrite(string path, byte[] bytes, string expected)
        {
            SafeFiles.NoReparse(path);
            string temporary = path + ".touch365-" + Guid.NewGuid().ToString("N") + ".tmp";
            WriteNew(temporary, bytes);
            try
            {
                if (expected != null)
                {
                    if ((File.Exists(path) ? Digest(ReadBytes(path, 1048576)) : "absent") != expected)
                        throw new IOException("A file changed before its atomic update. It will not be overwritten.");
                    RequireIdle();
                }
                SafeFiles.NoReparse(path);
                if (File.Exists(path)) File.Replace(temporary, path, null); // Preserve the original destination ACL.
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary) && SafeFiles.Digest(temporary) == Digest(bytes)) File.Delete(temporary); }
        }
        private static void FlushTree(string directory)
        {
            SafeFiles.NoReparse(directory);
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                SafeFiles.NoReparse(file);
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) stream.Flush(true);
            }
        }
        private static void PreserveApplicationAccess(string previous, string replacement)
        {
            SafeFiles.NoReparse(previous); SafeFiles.NoReparse(replacement);
            CopyDirectoryAccess(previous, replacement);
            foreach (string directory in Directory.GetDirectories(replacement, "*", SearchOption.AllDirectories).OrderBy(p => p.Length))
            {
                SafeFiles.NoReparse(directory);
                string old = previous + directory.Substring(replacement.Length);
                if (Directory.Exists(old)) CopyDirectoryAccess(old, directory);
                else
                {
                    var inherited = Directory.GetAccessControl(directory, AccessControlSections.Access);
                    inherited.SetAccessRuleProtection(true, false);
                    inherited.SetAccessRuleProtection(false, false);
                    Directory.SetAccessControl(directory, inherited);
                }
            }
            foreach (string file in Directory.GetFiles(replacement, "*", SearchOption.AllDirectories))
            {
                SafeFiles.NoReparse(file);
                string old = previous + file.Substring(replacement.Length);
                if (File.Exists(old))
                {
                    var copied = new FileSecurity();
                    copied.SetSecurityDescriptorBinaryForm(File.GetAccessControl(old, AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    File.SetAccessControl(file, copied);
                }
                else
                {
                    var inherited = File.GetAccessControl(file, AccessControlSections.Access);
                    inherited.SetAccessRuleProtection(true, false);
                    inherited.SetAccessRuleProtection(false, false);
                    File.SetAccessControl(file, inherited);
                }
            }
        }
        private static void CopyDirectoryAccess(string previous, string replacement)
        {
            var copied = new DirectorySecurity();
            copied.SetSecurityDescriptorBinaryForm(Directory.GetAccessControl(previous, AccessControlSections.Access).GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            Directory.SetAccessControl(replacement, copied);
        }
        private static void RequireConsentPolicy(byte[] bytes)
        {
            if (bytes == null) throw new InvalidDataException("The customer-approved RustDesk configuration is missing. Run approved setup before enrolling or updating.");
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (ConfigText.ReadOption(text, "approve-mode") != "click"
                || ConfigText.ReadOption(text, "allow-only-conn-window-open") != "Y"
                || ConfigText.ReadOption(text, "access-mode") != "custom"
                || ConfigText.ReadOption(text, "allow-remote-config-modification") != "N")
                throw new InvalidDataException("RustDesk's customer-approval policy has changed or is incomplete. Review it through approved setup before enrolling or applying updates.");
        }
        private static void ValidateManifestConfiguration(UpdateManifest manifest)
        {
            if (manifest == null || !IsHash(manifest.Digest)) throw new InvalidDataException("An authenticated update identity is required.");
            var allowed = new HashSet<string>(Policy.Options.Where(p => p.Key != "stop-service").Select(p => p.Key), StringComparer.Ordinal);
            foreach (var option in manifest.Options)
            {
                if (!allowed.Contains(option.Key) || option.Value == null || option.Value.Length > 512
                    || option.Value.IndexOfAny(new[] { '\r', '\n', '\0', '\'' }) >= 0)
                    throw new InvalidDataException("The update contains an unsupported configuration option.");
                if ((option.Key == "approve-mode" && option.Value != "click") || (option.Key == "allow-only-conn-window-open" && option.Value != "Y")
                    || (option.Key == "allow-remote-config-modification" && option.Value != "N") || (option.Key == "access-mode" && option.Value != "custom"))
                    throw new InvalidDataException("An update cannot weaken customer approval requirements.");
            }
        }

        private sealed class OwnedLock : IDisposable
        {
            private IDisposable handle; private Action release;
            public OwnedLock(IDisposable value, Action action) { handle = value; release = action; }
            public void Dispose() { if (handle != null) { handle.Dispose(); handle = null; release(); release = null; } }
        }
    }
}
