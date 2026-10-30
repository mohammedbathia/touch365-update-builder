using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Touch365Support
{
    public static class Policy
    {
        public const string Host = "remote.touch365.co.za";
        public const string PublicKey = "wdHeoSM9P5wVFaaoGh1E5n5TLedKdVlNE5pi+1JnM+8=";
        public const string PayloadHash = "8555777215510d83d2d61c9dc984e4fcc838bd7e79f9d18a42585431f5e8bb47";
        // Consent is set before changing permissions or registering with Touch365.
        public static readonly KeyValuePair<string, string>[] Options = new[] {
            Pair("approve-mode", "click"),
            Pair("allow-only-conn-window-open", "Y"),
            Pair("access-mode", "custom"),
            Pair("enable-keyboard", "Y"),
            Pair("enable-clipboard", "Y"),
            Pair("enable-file-transfer", "Y"),
            Pair("allow-remote-config-modification", "N"),
            Pair("enable-terminal", "N"),
            Pair("enable-tunnel", "N"),
            Pair("enable-camera", "N"),
            Pair("enable-remote-printer", "N"),
            Pair("enable-block-input", "N"),
            Pair("enable-remote-restart", "N"),
            Pair("enable-record-session", "N"),
            Pair("enable-audio", "N"),
            Pair("key", PublicKey),
            Pair("relay-server", Host),
            Pair("api-server", ""),
            Pair("custom-rendezvous-server", Host),
            Pair("stop-service", "")
        };
        private static KeyValuePair<string, string> Pair(string key, string value)
        { return new KeyValuePair<string, string>(key, value); }
    }

    public interface IOptionsClient
    {
        string Get(string key);
        void Set(string key, string value);
        string GetPersisted(string key);
        void SaveBackup(IDictionary<string, string> previous);
        void PauseOnFailure();
    }

    public static class Provisioner
    {
        public static void Apply(IOptionsClient client, Action<string> progress)
        {
            var old = new Dictionary<string, string>();
            foreach (var option in Policy.Options)
            {
                string value = client.Get(option.Key);
                if (client.GetPersisted(option.Key) != value)
                    throw new InvalidOperationException("RustDesk's running and saved settings do not agree. Restart RustDesk and retry setup.");
                old.Add(option.Key, value);
            }
            client.SaveBackup(old);
            bool changed = false;
            try
            {
                foreach (var option in Policy.Options)
                {
                    progress(option.Key == "approve-mode" ? "Setting customer approval..." : "Applying Touch365 settings...");
                    changed = true;
                    client.Set(option.Key, option.Value);
                    if (client.Get(option.Key) != option.Value)
                        throw new InvalidOperationException("RustDesk did not accept setting: " + option.Key);
                }
                progress("Checking the saved settings...");
                foreach (var option in Policy.Options)
                    if (client.Get(option.Key) != option.Value || client.GetPersisted(option.Key) != option.Value)
                        throw new InvalidOperationException("RustDesk could not confirm saved setting: " + option.Key);
            }
            catch (Exception original)
            {
                if (!changed) throw;
                bool restored = true;
                // Restore approval last, once all other original settings are back.
                foreach (var option in Policy.Options.Reverse())
                {
                    try {
                        client.Set(option.Key, old[option.Key]);
                        if (client.Get(option.Key) != old[option.Key] || client.GetPersisted(option.Key) != old[option.Key]) restored = false;
                    } catch { restored = false; }
                }
                if (!restored)
                {
                    try { client.PauseOnFailure(); }
                    catch { throw new InvalidOperationException(original.Message + " Recovery also failed. Close RustDesk and contact Touch365 before using it."); }
                    throw new InvalidOperationException(original.Message + " RustDesk was stopped and disabled because its previous settings could not be fully restored. Contact Touch365.");
                }
                throw new InvalidOperationException(original.Message + " The previous settings were restored. Please retry or contact Touch365.");
            }
        }
    }

    public static class ConfigText
    {
        // Deliberately reads/edits one simple scalar; never parses or logs identity/password fields.
        public static string ReadOption(string text, string key)
        {
            bool inside = false, foundTable = false, found = false;
            string result = "";
            foreach (string line in Split(text))
            {
                if (Regex.IsMatch(line, @"^\s*\["))
                {
                    inside = Regex.IsMatch(line, @"^\s*\[options\]\s*(?:#.*)?$");
                    if (inside && foundTable) throw new InvalidDataException("Duplicate RustDesk options table.");
                    if (inside) foundTable = true;
                }
                if (!inside) continue;
                Match match = Regex.Match(line, @"^\s*(?:" + Regex.Escape(key) + "|'" + Regex.Escape(key) + "'|\"" + Regex.Escape(key) + "\")\\s*=\\s*(.*)$");
                if (!match.Success) continue;
                if (found) throw new InvalidDataException("Duplicate RustDesk setting: " + key);
                found = true;
                string value = match.Groups[1].Value.Trim();
                Match literal = Regex.Match(value, "^'([^']*)'\\s*(?:#.*)?$");
                Match basic = Regex.Match(value, "^\"([^\"\\\\]*)\"\\s*(?:#.*)?$");
                if (literal.Success) result = literal.Groups[1].Value;
                else if (basic.Success) result = basic.Groups[1].Value;
                else throw new InvalidDataException("Unsupported RustDesk setting format: " + key);
            }
            return result;
        }
        public static string SetOption(string text, string key, string value)
        {
            ReadOption(text, key); // Refuse ambiguous existing input before editing.
            if (!Regex.IsMatch(key, "^[a-z0-9-]+$") || value.IndexOfAny(new[] { '\'', '\r', '\n' }) >= 0)
                throw new ArgumentException("Invalid scalar.");
            var lines = new List<string>();
            bool inside = false, foundTable = false, inserted = false;
            foreach (string line in Split(text))
            {
                if (Regex.IsMatch(line, @"^\s*\["))
                {
                    if (inside && !inserted) { lines.Add(key + " = '" + value + "'"); inserted = true; }
                    inside = Regex.IsMatch(line, @"^\s*\[options\]\s*(?:#.*)?$");
                    foundTable |= inside;
                }
                if (inside && Regex.IsMatch(line, @"^\s*(?:" + Regex.Escape(key) + "|'" + Regex.Escape(key) + "'|\"" + Regex.Escape(key) + "\")\\s*=")) continue;
                lines.Add(line);
            }
            if (!foundTable) lines.Add("[options]");
            if (!inserted) lines.Add(key + " = '" + value + "'");
            return String.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n";
        }
        private static string[] Split(string text) { return text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n'); }
    }

    public static class SafeFiles
    {
        public static void NoReparse(string path)
        {
            string p = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(p))
            {
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Setup cannot use a redirected folder: " + p);
                p = Path.GetDirectoryName(p);
            }
        }
        public static void PrivateDirectory(string path, bool readForUsers)
        {
            NoReparse(path);
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (readForUsers) acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(path, acl);
            Directory.SetAccessControl(path, acl);
        }
        public static string Digest(string path)
        {
            using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static string Quote(string argument)
        {
            // CommandLineToArgvW-compatible quoting, including empty values and trailing slashes.
            var b = new StringBuilder("\""); int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { b.Append('\\', slashes * 2 + 1); b.Append(c); slashes = 0; continue; }
                b.Append('\\', slashes); slashes = 0; b.Append(c);
            }
            b.Append('\\', slashes * 2); return b.Append('"').ToString();
        }
    }

    public sealed class CommandResult { public int ExitCode; public string Output; }
    public static class Commands
    {
        public static CommandResult Run(string exe, int timeoutSeconds, params string[] arguments)
        {
            if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException("timeoutSeconds");
            var elapsed = Stopwatch.StartNew();
            int timeoutMilliseconds = checked(timeoutSeconds * 1000);
            var info = new ProcessStartInfo(exe, String.Join(" ", arguments.Select(SafeFiles.Quote))) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)
            };
            using (var process = new Process { StartInfo = info })
            {
                var output = new StringBuilder(); object sync = new object(); int ended = 0;
                DataReceivedEventHandler handler = delegate(object s, DataReceivedEventArgs e) {
                    if (e.Data == null) { Interlocked.Increment(ref ended); return; }
                    lock (sync) { if (output.Length < 16384) output.AppendLine(e.Data); }
                };
                process.OutputDataReceived += handler; process.ErrorDataReceived += handler;
                process.Start();
                try
                {
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    int remaining = Math.Max(0, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds);
                    if (!process.WaitForExit(remaining)) throw CommandTimeout();
                    // Descendants can inherit these pipes after their parent has exited.
                    // Never use the unbounded WaitForExit() overload to drain them.
                    while (Volatile.Read(ref ended) < 2)
                    {
                        remaining = Math.Max(0, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds);
                        if (remaining == 0) throw CommandTimeout();
                        Thread.Sleep(Math.Min(20, remaining));
                    }
                    lock (sync) return new CommandResult { ExitCode = process.ExitCode, Output = output.ToString().Trim() };
                }
                catch
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    throw;
                }
                finally
                {
                    try { process.CancelOutputRead(); } catch { }
                    try { process.CancelErrorRead(); } catch { }
                }
            }
        }

        public static void RunDetachedInstaller(string exe, int timeoutSeconds, Func<bool> installationComplete, params string[] arguments)
        {
            if (timeoutSeconds <= 0) throw new ArgumentOutOfRangeException("timeoutSeconds");
            var elapsed = Stopwatch.StartNew();
            int timeoutMilliseconds = checked(timeoutSeconds * 1000);
            var info = new ProcessStartInfo(exe, String.Join(" ", arguments.Select(SafeFiles.Quote))) {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)
            };
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                int remaining = Math.Max(0, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds);
                if (!process.WaitForExit(remaining))
                {
                    try { process.Kill(); } catch { }
                    throw CommandTimeout();
                }
                if (process.ExitCode != 0) throw new InvalidOperationException("RustDesk could not be installed. Contact Touch365.");
            }
            // The packed EXE only launches the real installer. Its exit code alone
            // is not completion; poll its child command and actual installed state.
            while (elapsed.ElapsedMilliseconds < timeoutMilliseconds)
            {
                if (installationComplete()) return;
                Thread.Sleep(Math.Min(500, Math.Max(1, timeoutMilliseconds - (int)elapsed.ElapsedMilliseconds)));
            }
            throw new System.TimeoutException("Windows did not finish installing RustDesk in time. Close this setup and contact Touch365 before retrying.");
        }

        private static System.TimeoutException CommandTimeout()
        { return new System.TimeoutException("RustDesk took too long to respond. Contact Touch365 if retrying does not help."); }
    }

    public static class Signature
    {
        // Normal Windows trust policy may retrieve missing certificate information.
        // Cache-only verification (0x1000) is unsuitable for a customer's first run.
        internal const uint VerificationFlags = 0;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private class FileInfoData {
            public uint Size = (uint)Marshal.SizeOf(typeof(FileInfoData));
            public string Path; public IntPtr FileHandle = IntPtr.Zero; public IntPtr KnownSubject = IntPtr.Zero;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private class TrustData {
            public uint Size = (uint)Marshal.SizeOf(typeof(TrustData)); public IntPtr Policy = IntPtr.Zero; public IntPtr Sip = IntPtr.Zero;
            public uint UiChoice = 2; public uint Revocation = 0; public uint UnionChoice = 1; public IntPtr File;
            public uint StateAction = 0; public IntPtr State = IntPtr.Zero; public IntPtr Url = IntPtr.Zero;
            public uint Flags = VerificationFlags; public uint Context = 0;
        }
        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, TrustData data);
        public static void RequireOfficial(string path, string stage = "RustDesk application")
        {
            RequireOfficial(path, stage, VerifyNative, GetPublisher);
        }
        internal static void RequireOfficial(string path, string stage, Func<string, uint, int> verify, Func<string, string> publisher)
        {
            SafeFiles.NoReparse(path);
            int status = verify(path, VerificationFlags);
            if (status != 0) throw new InvalidDataException(TrustFailure(path, stage, status));
            if (publisher(path) != "PURSLANE")
                throw new InvalidDataException("RustDesk has an unexpected publisher (" + stage + ", " + Path.GetFileName(path) + "). Contact Touch365.");
        }
        private static string GetPublisher(string path)
        {
            using (var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        private static int VerifyNative(string path, uint flags)
        {
            var file = new FileInfoData { Path = path };
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(file));
            try {
                Marshal.StructureToPtr(file, pointer, false);
                return WinVerifyTrust(new IntPtr(-1), new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), new TrustData { File = pointer, Flags = flags });
            } finally { Marshal.DestroyStructure(pointer, typeof(FileInfoData)); Marshal.FreeHGlobal(pointer); }
        }
        private static string TrustFailure(string path, string stage, int status)
        {
            uint code = unchecked((uint)status);
            string detail;
            switch (code)
            {
                case 0x80096010: detail = "The file's signed contents do not match."; break;
                case 0x800B0100: detail = "Windows could not find a valid signature."; break;
                case 0x800B0101: detail = "A certificate is outside its valid dates. Check this PC's date and time."; break;
                case 0x800B0109: detail = "Windows does not trust the certificate's root."; break;
                case 0x800B010A: detail = "Windows could not build the certificate chain."; break;
                case 0x80092013: detail = "Windows could not reach the certificate revocation service. Check the internet connection."; break;
                case 0x80096005: detail = "Windows could not verify the signature timestamp."; break;
                default: detail = "Windows did not approve the publisher signature."; break;
            }
            return "Signature check failed: " + stage + ", " + Path.GetFileName(path) + ", Windows code 0x" + code.ToString("X8") + ". " + detail + " Send this message to Touch365.";
        }
    }

    public sealed class InstalledClient
    {
        public string Exe; public bool Registered; public bool Msi; public bool ServiceExists;
    }

    public sealed class WindowsHost : IOptionsClient
    {
        private readonly string work; private readonly Action<string> progress;
        public string Exe;
        public bool ServiceTouched { get; private set; }
        private readonly string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config\RustDesk2.toml");
        public WindowsHost(string work, Action<string> progress) { this.work = work; this.progress = progress; }

        public static InstalledClient Inspect()
        {
            var client = new InstalledClient();
            string[] registrationKeys = {
                @"SOFTWARE\RustDesk\InstallState\RustDesk",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{54E86BC2-6C85-41F3-A9EB-1A94AC9B1F93}_is1",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk"
            };
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    foreach (string registrationKey in registrationKeys)
                        using (var key = root.OpenSubKey(registrationKey))
                        {
                            if (key == null) continue;
                            string folder = Convert.ToString(key.GetValue("InstallLocation"));
                            string found = String.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(Path.Combine(folder.Trim(), "RustDesk.exe"));
                            if (client.Exe != null && found != null && !String.Equals(client.Exe, found, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidOperationException("Multiple RustDesk installations were found. Contact Touch365.");
                            client.Exe = found ?? client.Exe; client.Registered = true;
                            client.Msi |= Convert.ToString(key.GetValue("WindowsInstaller")) == "1" || !String.IsNullOrEmpty(Convert.ToString(key.GetValue("MsiProductCode")));
                        }
            using (var query = new ManagementObjectSearcher("SELECT PathName, StartName FROM Win32_Service WHERE Name='RustDesk'"))
                foreach (ManagementObject service in query.Get())
                {
                    client.ServiceExists = true;
                    string command = Convert.ToString(service["PathName"]);
                    Match match = Regex.Match(command, "^(?:\"([^\"]+)\"|(.+?\\.exe))\\s+--service\\s*$", RegexOptions.IgnoreCase);
                    if (!match.Success) throw new InvalidOperationException("The existing RustDesk service has an unsupported configuration. Contact Touch365.");
                    string path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    if (client.Exe != null && !String.Equals(Path.GetFullPath(path), Path.GetFullPath(client.Exe), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The RustDesk service and installation point to different files. Contact Touch365.");
                    client.Exe = path;
                    string account = Convert.ToString(service["StartName"]);
                    if (account != "LocalSystem" && account != @"NT AUTHORITY\LocalService" && account != @"NT AUTHORITY\Local Service")
                        throw new InvalidOperationException("RustDesk uses a custom service account. Contact Touch365 to configure this PC.");
                }
            if (client.Exe == null)
            {
                string conventional = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"RustDesk\RustDesk.exe");
                if (File.Exists(conventional)) client.Exe = conventional;
            }
            return client;
        }

        public static void RequireClosedUserProcesses(bool preliminary = false)
        {
            // A normal service owns --service and --server processes. The unelevated check
            // cannot always inspect them; the elevated worker makes the authoritative check.
            int currentSession = Process.GetCurrentProcess().SessionId;
            string installedExe = null; bool signatureChecked = false;
            if (!preliminary) installedExe = Inspect().Exe;
            using (var query = new ManagementObjectSearcher("SELECT ProcessId, SessionId, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='rustdesk.exe'"))
                foreach (ManagementObject process in query.Get())
                {
                    string command = Convert.ToString(process["CommandLine"]);
                    if (preliminary)
                    {
                        if (Convert.ToInt32(process["SessionId"]) != currentSession || String.IsNullOrWhiteSpace(command)) continue;
                        if (Regex.IsMatch(command, "\\s--(?:service|server)\\s*$", RegexOptions.IgnoreCase)) continue;
                    }
                    else if (!String.IsNullOrWhiteSpace(command) && installedExe != null)
                    {
                        Match match = Regex.Match(command, "^(?:\"([^\"]+)\"|(.+?\\.exe))\\s+--(?:service|server)\\s*$", RegexOptions.IgnoreCase);
                        string processExe = Convert.ToString(process["ExecutablePath"]);
                        string commandExe = match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
                        if (match.Success && !String.IsNullOrEmpty(processExe)
                            && String.Equals(Path.GetFullPath(processExe), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase)
                            && String.Equals(Path.GetFullPath(commandExe), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase))
                        {
                            using (ManagementBaseObject owner = process.InvokeMethod("GetOwnerSid", null, null))
                            {
                                string sid = owner == null ? "" : Convert.ToString(owner["Sid"]);
                                if (owner != null && Convert.ToUInt32(owner["ReturnValue"]) == 0 && (sid == "S-1-5-18" || sid == "S-1-5-19"))
                                {
                                    if (!signatureChecked) { Signature.RequireOfficial(installedExe, "existing service"); signatureChecked = true; }
                                    continue;
                                }
                            }
                        }
                    }
                    throw new InvalidOperationException("RustDesk is already running. Finish any support session, then choose Quit RustDesk from its tray icon and click Retry setup.");
                }
        }

        public string Prepare()
        {
            if (!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("This setup is for 64-bit Windows. Contact Touch365 for another version.");
            RequireClosedUserProcesses();
            var installed = Inspect();
            if (installed.Exe != null)
            {
                Exe = Path.GetFullPath(installed.Exe);
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\') + "\\";
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\') + "\\";
                if (!Exe.StartsWith(pf, StringComparison.OrdinalIgnoreCase) && !Exe.StartsWith(pf86, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("RustDesk is installed in a custom folder. Contact Touch365 to configure this PC.");
                Signature.RequireOfficial(Exe, "existing installation");
                Match version = Regex.Match(FileVersionInfo.GetVersionInfo(Exe).ProductVersion ?? "", "^([0-9]+\\.[0-9]+\\.[0-9]+)");
                if (!version.Success || new Version(version.Groups[1].Value) < new Version(1, 4, 3))
                    throw new InvalidOperationException("This PC has an older RustDesk version. Ask Touch365 to update it, then run this setup again.");
                if (!installed.Registered) throw new InvalidOperationException("An incomplete RustDesk installation was found. Contact Touch365.");
            }
            else
            {
                if (installed.Registered || installed.Msi || installed.ServiceExists) throw new InvalidOperationException("The existing RustDesk installation needs repair by Touch365.");
                progress("Installing RustDesk...");
                string payload = Path.Combine(work, "rustdesk-1.5.0-x86_64.exe");
                using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream("RustDeskPayload"))
                {
                    if (input == null) throw new InvalidDataException("The RustDesk installer is missing.");
                    using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                }
                if (SafeFiles.Digest(payload) != Policy.PayloadHash) throw new InvalidDataException("The bundled RustDesk installer failed its integrity check.");
                Signature.RequireOfficial(payload, "bundled installer");
                ServiceTouched = true;
                Commands.RunDetachedInstaller(payload, 180, delegate {
                    if (HasInstallerProcess("--silent-install")) return false;
                    var current = Inspect();
                    // stop-service=Y can deliberately suppress service creation. The
                    // installed command below creates that service after installation.
                    return current.Registered && current.Exe != null && File.Exists(current.Exe);
                }, "--silent-install");
                installed = Inspect(); Exe = installed.Exe;
                if (Exe == null || !File.Exists(Exe) || !installed.Registered) throw new InvalidOperationException("Windows did not finish installing RustDesk. Retry setup or contact Touch365.");
                Signature.RequireOfficial(Exe, "newly installed application");
            }
            if (!installed.ServiceExists)
            {
                progress("Preparing the RustDesk service...");
                ServiceTouched = true;
                Commands.RunDetachedInstaller(Exe, 180, delegate {
                    return !HasInstallerProcess("--install-service") && Inspect().ServiceExists;
                }, "--install-service");
            }
            using (var serviceInfo = new ManagementObject("Win32_Service.Name='RustDesk'"))
            {
                serviceInfo.Get();
                if (String.Equals(Convert.ToString(serviceInfo["StartMode"]), "Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    ServiceTouched = true;
                    ChangeStartMode(serviceInfo, "Automatic");
                }
            }
            using (var service = new ServiceController("RustDesk"))
            {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.StopPending)
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                if (service.Status == ServiceControllerStatus.Stopped) { ServiceTouched = true; service.Start(); }
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
            EnsureServiceConfig();
            return Exe;
        }

        private static bool HasInstallerProcess(string argument)
        {
            using (var query = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE Name='rustdesk.exe'"))
                foreach (ManagementObject process in query.Get())
                {
                    string command = Convert.ToString(process["CommandLine"]);
                    // An elevated worker should see RustDesk's command line. Do not
                    // declare completion if Windows denies that evidence.
                    if (String.IsNullOrWhiteSpace(command) || Regex.IsMatch(command, "\\s\"?" + Regex.Escape(argument) + "\"?(?:\\s|$)", RegexOptions.IgnoreCase))
                        return true;
                }
            return false;
        }

        private void EnsureServiceConfig()
        {
            if (File.Exists(config)) return;
            // Cold service profiles need not persist a Config2 file until an option
            // changes. A random harmless probe proves the service received our IPC;
            // the invoking user's CLI fallback cannot fake this service-owned file.
            string probe = "touch365-setup-probe-" + Guid.NewGuid().ToString("N");
            string nonce = Guid.NewGuid().ToString("N");
            bool attempted = false;
            try
            {
                attempted = true;
                Set(probe, nonce);
                for (int attempt = 0; attempt < 40; attempt++)
                {
                    if (File.Exists(config) && GetPersisted(probe) == nonce && Get(probe) == nonce) return;
                    Thread.Sleep(250);
                }
                throw new InvalidOperationException("RustDesk did not confirm its service settings. Retry setup or contact Touch365.");
            }
            finally
            {
                if (attempted)
                {
                    Set(probe, "");
                    if (Get(probe) != "" || (File.Exists(config) && GetPersisted(probe) != ""))
                        throw new InvalidOperationException("RustDesk could not remove its temporary setup check. Contact Touch365.");
                }
            }
        }

        public string Get(string key)
        {
            var result = Commands.Run(Exe, 12, "--option", key);
            if (result.ExitCode != 0 || result.Output.Contains("\n") || result.Output.Contains("required!") || result.Output.Contains("disabled!"))
                throw new InvalidOperationException("RustDesk could not read setting: " + key);
            return result.Output;
        }
        public void Set(string key, string value)
        {
            var result = Commands.Run(Exe, 12, "--option", key, value);
            if (result.ExitCode != 0 || result.Output.Length != 0) throw new InvalidOperationException("RustDesk could not apply setting: " + key);
        }
        public string GetPersisted(string key)
        {
            SafeFiles.NoReparse(config);
            using (var stream = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream)) return ConfigText.ReadOption(reader.ReadToEnd(), key);
        }
        public void SaveBackup(IDictionary<string, string> previous)
        {
            string folder = Path.Combine(work, "backup"); SafeFiles.PrivateDirectory(folder, false);
            var doc = new XElement("previousOptions", new XAttribute("executable", Exe));
            foreach (var pair in previous) doc.Add(new XElement("option", new XAttribute("name", pair.Key), new XAttribute("value", pair.Value)));
            doc.Save(Path.Combine(folder, "previous-options.xml"));
        }
        public void PauseOnFailure()
        {
            // A stopped automatic service could resume mixed settings at reboot. Disable it
            // until a deliberate retry successfully provisions the customer policy.
            using (var serviceInfo = new ManagementObject("Win32_Service.Name='RustDesk'"))
            {
                serviceInfo.Get();
                ChangeStartMode(serviceInfo, "Disabled");
            }
            using (var service = new ServiceController("RustDesk")) {
                service.Refresh(); if (service.Status != ServiceControllerStatus.Stopped) service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        private static void ChangeStartMode(ManagementObject serviceInfo, string mode)
        {
            using (ManagementBaseObject input = serviceInfo.GetMethodParameters("ChangeStartMode"))
            {
                input["StartMode"] = mode;
                using (ManagementBaseObject result = serviceInfo.InvokeMethod("ChangeStartMode", input, null))
                    if (result == null || Convert.ToUInt32(result["ReturnValue"]) != 0)
                        throw new InvalidOperationException("Windows could not change the RustDesk service startup setting. Contact Touch365.");
            }
        }
    }
}
