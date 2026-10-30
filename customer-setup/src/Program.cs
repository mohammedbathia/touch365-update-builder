using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

[assembly: AssemblyTitle("Touch365 Remote Support Setup")]
[assembly: AssemblyDescription("Customer-approved RustDesk setup for Touch365")]
[assembly: AssemblyCompany("Touch365")]
[assembly: AssemblyProduct("Touch365 Remote Support")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

namespace Touch365Support
{
    internal static class Program
    {
        private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Touch365\RemoteSupportSetup");
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--worker") return Worker(args[1]);
            bool preview = args.Length == 1 && args[0] == "--preview";
            if (args.Length != 0 && !preview) return 2;
            bool acquired;
            using (var mutex = new Mutex(true, @"Local\Touch365RemoteSupportSetup", out acquired))
            {
                if (!acquired) { MessageBox.Show("Touch365 setup is already open.", "Touch365 Remote Support"); return 1; }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SetupForm(preview, RunSetup));
                mutex.ReleaseMutex(); return 0;
            }
        }

        private static Task<SetupResult> RunSetup(Action<string> progress)
        {
            return Task.Run(delegate {
                LocalSetting local = null;
                bool serviceConfigured = false;
                try
                {
                    if (!Environment.Is64BitOperatingSystem) throw new InvalidOperationException("This setup needs 64-bit Windows. Contact Touch365 for another version.");
                    WindowsHost.RequireClosedUserProcesses(true);
                    local = new LocalSetting();
                    local.Apply();
                    string id = Guid.NewGuid().ToString("N");
                    string folder = Path.Combine(Root, id);
                    progress("Approve the Windows setup prompt...");
                    var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--worker " + id) {
                        UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
                    };
                    using (var worker = Process.Start(start))
                    {
                        if (worker == null) throw new InvalidOperationException("Windows could not start setup.");
                        string previous = "";
                        while (!worker.WaitForExit(350))
                        {
                            try {
                                string path = Path.Combine(folder, "status.txt");
                                if (File.Exists(path)) {
                                    string status = File.ReadAllText(path);
                                    if (status != previous && status.Length < 200) { progress(status); previous = status; }
                                }
                            } catch (IOException) { }
                        }
                        string resultFile = Path.Combine(folder, "result.xml");
                        if (!File.Exists(resultFile)) throw new InvalidOperationException("Windows setup did not finish. Retry setup or contact Touch365.");
                        XElement result;
                        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8192 };
                        using (var reader = XmlReader.Create(resultFile, settings)) result = XElement.Load(reader);
                        if (worker.ExitCode != 0 || (string)result.Attribute("success") != "true")
                            throw new InvalidOperationException((string)result.Element("message") ?? "Setup did not finish. Contact Touch365.");
                        serviceConfigured = true;
                        string exe = (string)result.Element("executable");
                        Signature.RequireOfficial(exe, "configured application in customer account");
                        local.Verify();
                        string shortcutNote = "A Touch365 Remote Support shortcut is available in your Start menu.";
                        try { CreateShortcut(exe); }
                        catch { shortcutNote = "For future support, open RustDesk from your Start menu."; }
                        return new SetupResult { Success = true, ExecutablePath = exe, Message = shortcutNote };
                    }
                }
                catch (Win32Exception ex)
                {
                    string message = ex.NativeErrorCode == 1223
                        ? "Windows approval was cancelled. Click Retry setup when you are ready to approve it."
                        : "Windows could not complete setup. Contact Touch365 if retrying does not help.";
                    return Failure(local, serviceConfigured, message);
                }
                catch (Exception ex)
                {
                    string message = ex is InvalidOperationException || ex is InvalidDataException || ex is TimeoutException
                        ? ex.Message : "Setup could not finish. Contact Touch365 for help.";
                    return Failure(local, serviceConfigured, message);
                }
            });
        }

        private static SetupResult Failure(LocalSetting local, bool serviceConfigured, string message)
        {
            if (local != null && !serviceConfigured) {
                try { local.Restore(); }
                catch { message += " Your local settings backup is retained for Touch365 to review."; }
            }
            return new SetupResult { Success = false, Message = message };
        }

        private static int Worker(string id)
        {
            Guid parsed;
            if (!Guid.TryParseExact(id, "N", out parsed)) return 2;
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
            string folder = Path.Combine(Root, id);
            WindowsHost host = null;
            try
            {
                SafeFiles.PrivateDirectory(Root, true);
                if (Directory.Exists(folder)) return 4;
                SafeFiles.PrivateDirectory(folder, true);
                ExtractNotice(folder, "RustDeskLicense", "RustDesk-LICENSE.txt");
                ExtractNotice(folder, "ThirdPartyNotices", "THIRD-PARTY-NOTICES.txt");
                Action<string> progress = delegate(string value) { File.WriteAllText(Path.Combine(folder, "status.txt"), value, new UTF8Encoding(false)); };
                progress("Checking this PC...");
                host = new WindowsHost(folder, progress);
                string exe = host.Prepare();
                Provisioner.Apply(host, progress);
                WriteResult(folder, true, "", exe);
                return 0;
            }
            catch (Exception ex)
            {
                string message = ex is InvalidOperationException || ex is InvalidDataException || ex is TimeoutException
                    ? ex.Message : "Windows could not complete the RustDesk setup. Contact Touch365.";
                if (host != null && host.ServiceTouched) {
                    try { host.PauseOnFailure(); message += " The newly started RustDesk service has been paused until setup succeeds."; }
                    catch { message += " Close RustDesk and contact Touch365 before using it."; }
                }
                if (Directory.Exists(folder)) { try { WriteResult(folder, false, message, ""); } catch { } }
                return 1;
            }
        }

        private static void WriteResult(string folder, bool success, string message, string exe)
        {
            var result = new XElement("result", new XAttribute("success", success ? "true" : "false"), new XElement("message", message), new XElement("executable", exe));
            string temp = Path.Combine(folder, "result.tmp"); result.Save(temp);
            File.Move(temp, Path.Combine(folder, "result.xml"));
        }

        private static void ExtractNotice(string folder, string resource, string filename)
        {
            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (var output = new FileStream(Path.Combine(folder, filename), FileMode.CreateNew, FileAccess.Write))
            {
                if (input == null) throw new InvalidDataException("Required third-party notices are missing.");
                input.CopyTo(output);
            }
        }

        private static void CreateShortcut(string exe)
        {
            // Runs in the original customer's context, including after alternate-admin UAC.
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Touch365 Remote Support.lnk");
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try {
                dynamic shortcut = shell.CreateShortcut(path);
                try { shortcut.TargetPath = exe; shortcut.WorkingDirectory = Path.GetDirectoryName(exe); shortcut.Description = "Open Touch365 customer-approved remote support"; shortcut.IconLocation = exe + ",0"; shortcut.Save(); }
                finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
            } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
    }

}
