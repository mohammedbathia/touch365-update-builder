using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

namespace Touch365Support
{
    // Uses the existing Checker's in-app automatic-check model. No second scheduler,
    // service, startup registration or self-replacing downloaded helper is introduced.
    internal static class UpdateCommands
    {
        internal static bool TryRun(string[] args, out int result)
        {
            result = 2;
            if (args.Length == 1 && args[0] == "--validate-update-trust")
            {
                try { result = UpdateTrust.LoadEmbedded().IsConfigured ? 0 : 2; }
                catch { result = 3; }
                return true;
            }
            if (args.Length == 0 || !args[0].StartsWith("--update", StringComparison.Ordinal)) return false;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length == 1 && args[0] == "--update-controller")
                {
                    bool owner;
                    using (var mutex = new Mutex(true, @"Local\Touch365RemoteSupportUpdateController", out owner))
                    {
                        if (!owner) throw new InvalidOperationException("The update controller is already open in this session.");
                        Application.Run(new UpdateControllerForm(UpdateTrust.LoadEmbedded()));
                        mutex.ReleaseMutex();
                    }
                    result = 0;
                }
                else if (args.Length == 1 && args[0] == "--updater-enroll")
                {
                    RequireAdministrator();
                    if (!UpdateTrust.LoadEmbedded().IsConfigured) throw Disabled();
                    if (MessageBox.Show("Enroll this PC in the pinned Touch365 update channel?\r\n\r\n" +
                        "This creates protected replay/recovery state. It does not install an update, register a task, or change RustDesk permissions. " +
                        "RustDesk and its service must already be stopped. Future changes require review and administrator approval.",
                        "Touch365 update enrollment", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return true;
                    WindowsUpdateEnvironment.Enroll(DateTime.UtcNow);
                    MessageBox.Show("Enrollment recorded. Open the update controller when you want automatic checks. No startup task or service was registered.", "Touch365 updates");
                    result = 0;
                }
                else if (args.Length == 4 && args[0] == "--updater-apply")
                {
                    RequireAdministrator();
                    var trust = UpdateTrust.LoadEmbedded();
                    if (!trust.IsConfigured) throw Disabled();
                    string metadataFile = UpdateHandoff.LocalFile(args[1]);
                    string packageFile = args[3] == "-" ? null : UpdateHandoff.LocalFile(args[3]);
                    byte[] envelope = ReadBounded(metadataFile);
                    UpdateManifest manifest = UpdateProtocol.ParseEnvelope(envelope, trust, DateTime.UtcNow);
                    if (!String.Equals(manifest.Digest, args[2], StringComparison.Ordinal))
                        throw new InvalidDataException("The update changed after review. Check for updates again.");
                    if (MessageBox.Show(Describe(manifest) + "\r\n\r\nApprove this exact signed update?\r\n" +
                        "RustDesk and its service must already be stopped. The updater will not stop a session or restart the service. " +
                        "Configuration changes listed above affect remote access and require your approval.",
                        "Review Touch365 update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return true;

                    var environment = new WindowsUpdateEnvironment();
                    using (environment.AcquireLock())
                    {
                        if (!environment.LoadState().Enrolled) throw new InvalidOperationException("Enroll this PC before staging privileged maintenance.");
                    }
                    string staged = "";
                    if (manifest.BinaryVersion != null)
                    {
                        // Elevation never trusts a user-writable download or a prior check.
                        // Copy into a new protected directory, then reverify the exact bytes.
                        if (packageFile == null) throw new InvalidDataException("The binary update package is missing.");
                        string root = Path.Combine(WindowsUpdateEnvironment.DefaultStateRoot, "Staging");
                        WindowsUpdateEnvironment.CreateProtectedDirectory(root);
                        string job = Path.Combine(root, Guid.NewGuid().ToString("N"));
                        WindowsUpdateEnvironment.CreateProtectedDirectory(job);
                        string zip = Path.Combine(job, "package.zip");
                        using (var input = new FileStream(packageFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            if (input.Length != manifest.PackageBytes) throw new InvalidDataException("The staged update size changed.");
                            input.CopyTo(output); output.Flush(true);
                        }
                        staged = Path.Combine(job, "application");
                        UpdateProtocol.VerifyPackage(zip, manifest);
                        UpdatePackage.ExtractVerified(zip, staged, manifest);
                        WindowsUpdateEnvironment.ProtectTree(staged);
                        UpdatePackage.VerifyDirectory(staged, manifest);
                    }
                    var coordinator = new UpdateCoordinator(environment);
                    string outcome = coordinator.Run(manifest, staged, manifest.Digest, DateTime.UtcNow);
                    MessageBox.Show(Outcome(outcome), "Touch365 updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    result = outcome == "Applied" || outcome == "NoUpdate" ? 0 : 10;
                }
                else if (args.Length == 1 && args[0] == "--updater-recover")
                {
                    RequireAdministrator();
                    if (MessageBox.Show("Verify or restore the recorded interrupted update? RustDesk and its service must already be stopped. " +
                        "Unexpected concurrent changes will be preserved for manual recovery.", "Touch365 update recovery",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return true;
                    string outcome = new UpdateCoordinator(new WindowsUpdateEnvironment()).Recover(DateTime.UtcNow);
                    MessageBox.Show(Outcome(outcome), "Touch365 recovery");
                    result = outcome == "DeferredActiveSession" ? 10 : 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Updates were not applied. " + ex.Message, "Touch365 updates need attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                result = 1;
            }
            return true;
        }

        internal static byte[] ReadBounded(string path)
        {
            path = UpdateHandoff.LocalFile(path);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 1 || input.Length > 98304) throw new InvalidDataException("Update metadata has an invalid size.");
                var data = new byte[(int)input.Length]; int read = 0;
                while (read < data.Length)
                {
                    int count = input.Read(data, read, data.Length - read);
                    if (count == 0) throw new EndOfStreamException("Update metadata was interrupted.");
                    read += count;
                }
                return data;
            }
        }
        private static void RequireAdministrator()
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Use the controller's administrator approval button for maintenance.");
        }
        private static Exception Disabled() { return new InvalidOperationException("No trusted production update channel is provisioned in this build."); }
        internal static string Describe(UpdateManifest manifest)
        {
            var text = new StringBuilder("Signed update #" + manifest.Sequence + "\r\n");
            if (manifest.BinaryVersion != null) text.AppendLine("RustDesk application: " + manifest.BinaryVersion);
            if (manifest.ConfigVersion > 0)
            {
                text.AppendLine("Remote-access configuration version: " + manifest.ConfigVersion);
                foreach (var option in manifest.Options.OrderBy(x => x.Key, StringComparer.Ordinal))
                    text.AppendLine(option.Key + " = " + option.Value);
            }
            text.AppendLine("Expires (UTC): " + manifest.ExpiresUtc.ToString("u"));
            text.Append("Metadata SHA-256: " + manifest.Digest);
            return text.ToString();
        }
        private static string Outcome(string status)
        {
            switch (status)
            {
                case "Applied": return "The update's files and configuration were verified. RustDesk remains stopped; start it only when you are ready to verify the update.";
                case "DeferredActiveSession": return "Nothing was applied. End support sessions, quit RustDesk and have your administrator stop its service before retrying.";
                case "RolledBack": return "The update did not complete. The previous files and settings were restored and verified. RustDesk remains stopped.";
                case "RecoveredApplied": return "The interrupted update was already complete; its files/settings were verified and the recovery journal was finalized.";
                case "NoUpdate": return "This signed update is already recorded as applied, or no interrupted update needs recovery.";
                default: return "Maintenance result: " + status + ". Review the recorded state before continuing.";
            }
        }
    }

    internal sealed class UpdateControllerForm : Form
    {
        private readonly UpdateTrust trust;
        private readonly Label status;
        private readonly TextBox details;
        private readonly Button check, apply, enroll, recover;
        private readonly CheckBox automatic;
        private readonly System.Windows.Forms.Timer timer;
        private bool busy;
        private UpdateManifest pending;
        private string envelopePath, packagePath;
        private string completedDigest;
        private static readonly string CacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Touch365\RemoteSupportUpdates\Cache");
        private static readonly string Preferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Touch365\RemoteSupportUpdates\checks.xml");

        internal UpdateControllerForm(UpdateTrust trust)
        {
            this.trust = trust;
            // Same navy/teal, DPI-aware stacked header/body/footer template as SetupForm.
            Text = "Touch365 Remote Support - Updates";
            Font = new Font("Segoe UI", 10F); ForeColor = Color.FromArgb(36, 50, 67); BackColor = Color.White;
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(650, 530); MinimumSize = new Size(610, 500); StartPosition = FormStartPosition.CenterScreen;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
            var header = new Label { Text = "Touch365\r\nRemote Support Updates", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(28, 17, 28, 14), Font = new Font("Segoe UI", 18F, FontStyle.Bold), ForeColor = Color.White, BackColor = Color.FromArgb(20, 39, 66) };
            root.Controls.Add(header, 0, 0);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(28, 18, 28, 10) };
            body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); body.RowStyles.Add(new RowStyle(SizeType.AutoSize)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.Controls.Add(body, 0, 1);
            status = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = trust.IsConfigured ? "Ready to check the pinned update channel." : "Automatic updates are not activated.", ForeColor = Color.FromArgb(0, 119, 125), Margin = new Padding(0, 0, 0, 12), AccessibleName = "Update status" }; body.Controls.Add(status, 0, 0);
            automatic = new CheckBox { AutoSize = true, Dock = DockStyle.Fill, Text = "Check and stage hourly while this window is open", Enabled = trust.IsConfigured, Checked = trust.IsConfigured && ReadPreference(), Margin = new Padding(0, 0, 0, 12) }; body.Controls.Add(automatic, 0, 1);
            details = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Signed update details", Text = trust.IsConfigured ? "Checks and downloads do not change RustDesk. Applying any update requires review, administrator approval, enrollment and a stopped/idle client." : "A trusted public verification key, approved HTTPS feed and signed update metadata must be provisioned before checks or enrollment are enabled. No production signing key or feed is included." }; body.Controls.Add(details, 0, 2);
            var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(20, 12, 20, 12), BackColor = Color.FromArgb(244, 247, 250) }; root.Controls.Add(footer, 0, 2);
            check = Button("Check now", footer); apply = Button("Review and apply...", footer); enroll = Button("Enroll this PC...", footer); recover = Button("Recover...", footer);
            check.Enabled = enroll.Enabled = trust.IsConfigured; apply.Enabled = false;
            check.Click += async delegate { await CheckAsync(); };
            apply.Click += async delegate { if (pending != null) await ElevatedAsync("--updater-apply", envelopePath, pending.Digest, packagePath ?? "-"); };
            enroll.Click += async delegate { await ElevatedAsync("--updater-enroll"); };
            recover.Click += async delegate { await ElevatedAsync("--updater-recover"); };
            timer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
            timer.Tick += async delegate { if (automatic.Checked) await CheckAsync(); };
            automatic.CheckedChanged += delegate { SavePreference(automatic.Checked); };
            Shown += async delegate { timer.Start(); if (automatic.Checked) await CheckAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "Wait for the current check or maintenance command to finish."; } };
            FormClosed += delegate { timer.Stop(); timer.Dispose(); };
        }
        private static Button Button(string title, Control parent)
        {
            var button = new Button { Text = title, AutoSize = true, MinimumSize = new Size(96, 36), FlatStyle = FlatStyle.Flat, Margin = new Padding(4), Padding = new Padding(7, 2, 7, 2), ForeColor = Color.FromArgb(0, 119, 125) };
            parent.Controls.Add(button); return button;
        }
        private void SetBusy(bool value)
        {
            busy = value; check.Enabled = enroll.Enabled = automatic.Enabled = !value && trust.IsConfigured;
            apply.Enabled = !value && pending != null; recover.Enabled = !value;
        }
        private async Task CheckAsync()
        {
            if (busy || !trust.IsConfigured) return;
            SetBusy(true); status.Text = "Checking signed update metadata...";
            try
            {
                byte[] bytes = await Task.Run(delegate { return UpdateProtocol.FetchEnvelope(trust); });
                UpdateManifest found = UpdateProtocol.ParseEnvelope(bytes, trust, DateTime.UtcNow);
                if (found.Digest == completedDigest)
                {
                    pending = null; envelopePath = packagePath = null;
                    status.Text = "This signed update is already recorded as complete. No new update is staged.";
                    return;
                }
                if (pending == null || pending.Digest != found.Digest)
                {
                    pending = null; packagePath = null; envelopePath = null;
                    // Retain the exact authenticated bytes for elevated re-verification.
                    string cache = Path.Combine(CacheRoot, "check-" + Guid.NewGuid().ToString("N"));
                    SafeFiles.PrivateDirectory(cache, false);
                    string savedEnvelope = Path.Combine(cache, "metadata.xml");
                    File.WriteAllBytes(savedEnvelope, bytes);
                    string zip = found.BinaryVersion == null ? null : await Task.Run(delegate { return UpdateProtocol.StagePackage(found, trust, cache); });
                    pending = found; envelopePath = savedEnvelope; packagePath = zip;
                }
                details.Text = UpdateCommands.Describe(pending);
                status.Text = "Signed update staged. Review and administrator approval are required to apply it.";
            }
            catch (Exception ex) { status.Text = "Check unavailable; RustDesk was not changed."; details.Text = ex.Message; }
            finally { SetBusy(false); }
        }
        private async Task ElevatedAsync(params string[] arguments)
        {
            if (busy) return;
            SetBusy(true); status.Text = "Waiting for administrator review...";
            try
            {
                var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, String.Join(" ", arguments.Select(SafeFiles.Quote))) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var worker = Process.Start(start))
                {
                    if (worker == null) throw new InvalidOperationException("Windows did not start maintenance.");
                    await Task.Run(delegate { worker.WaitForExit(); });
                    if (worker.ExitCode == 0 && arguments[0] == "--updater-apply")
                    {
                        completedDigest = arguments[2]; pending = null; envelopePath = packagePath = null;
                    }
                    status.Text = worker.ExitCode == 0 ? "Maintenance finished. Review its verified result before starting RustDesk." : "Maintenance did not apply an update. Review the maintenance result or retry when idle.";
                }
            }
            catch (Win32Exception ex) { status.Text = ex.NativeErrorCode == 1223 ? "Administrator approval was cancelled. Nothing was applied." : "Windows could not start maintenance."; }
            catch (Exception ex) { status.Text = "Maintenance unavailable."; details.Text = ex.Message; }
            finally { SetBusy(false); }
        }
        private static bool ReadPreference()
        {
            try
            {
                if (!File.Exists(Preferences) || new FileInfo(Preferences).Length > 1024) return false;
                SafeFiles.NoReparse(Preferences);
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 };
                using (var reader = XmlReader.Create(Preferences, settings)) return (string)XElement.Load(reader).Attribute("enabled") == "true";
            }
            catch { return false; }
        }
        private static void SavePreference(bool enabled)
        {
            try
            {
                string directory = Path.GetDirectoryName(Preferences); SafeFiles.PrivateDirectory(directory, false);
                string temporary = Preferences + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(new XElement("automaticChecks", new XAttribute("enabled", enabled ? "true" : "false")).ToString());
                    output.Write(bytes, 0, bytes.Length); output.Flush(true);
                }
                SafeFiles.NoReparse(Preferences);
                if (File.Exists(Preferences)) File.Replace(temporary, Preferences, null); else File.Move(temporary, Preferences);
            }
            catch { /* Preference persistence never grants update authority. */ }
        }
    }
}
