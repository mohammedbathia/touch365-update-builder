using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Touch365Support;

internal static class UpdateProfileTests
{
    private static int passed, failed;

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        internal Fixture()
        {
            Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "RustDesk.toml"), "fixture identity\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(Root, "RustDesk2.toml"), "fixture settings\r\n", new UTF8Encoding(false));
        }
        internal string Snapshot() { return UpdateProfile.SnapshotConfigDirectory(Root); }
        public void Dispose()
        {
            string root = Path.GetFullPath(Root);
            string parent = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("profile-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing profile fixture cleanup outside its exact temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS update profile: " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL update profile: " + name + ": " + ex.Message); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action)
    { try { action(); } catch { return; } throw new Exception("Expected fail-closed rejection."); }

    internal static int Run()
    {
        Test("another Windows account is rejected before profile resolution", delegate
        {
            Throws(delegate { UpdateProfile.Resolve("S-1-5-21-1-2-3-987654321"); });
            Throws(delegate { UpdateProfile.RequireBinding("", @"C:\not-an-enrolled-profile"); });
        });
        Test("network and device profile roots are rejected before filesystem access", delegate
        {
            foreach (string root in new[] { @"\\server\share\profile", @"\\?\C:\Users\fixture", @"C:relative", @"C:\Users\fixture\..\different" })
                Throws(delegate { UpdateProfile.ConfigRoot(root); });
        });
        Test("snapshot is deterministic and contains no identity or settings values", delegate
        {
            using (var fixture = new Fixture())
            {
                Directory.CreateDirectory(Path.Combine(fixture.Root, "peers"));
                File.WriteAllText(Path.Combine(fixture.Root, "peers", "fixture.toml"), "private fixture value");
                string first = fixture.Snapshot(), second = fixture.Snapshot();
                Assert(first == second && System.Text.RegularExpressions.Regex.IsMatch(first, "^[0-9a-f]{64}$"), "Snapshot is not a stable bounded digest.");
            }
        });
        Test("identity and settings files are both required", delegate
        {
            foreach (string name in new[] { "RustDesk.toml", "RustDesk2.toml" })
                using (var fixture = new Fixture())
                {
                    File.Delete(Path.Combine(fixture.Root, name));
                    Throws(delegate { fixture.Snapshot(); });
                }
        });
        Test("identity settings peers and empty directory edits change the snapshot", delegate
        {
            using (var fixture = new Fixture())
            {
                string previous = fixture.Snapshot();
                foreach (string path in new[] { "RustDesk.toml", "RustDesk2.toml", "peers.toml" })
                {
                    File.AppendAllText(Path.Combine(fixture.Root, path), "changed fixture");
                    string changed = fixture.Snapshot();
                    Assert(changed != previous, "A profile file edit was missed."); previous = changed;
                }
                Directory.CreateDirectory(Path.Combine(fixture.Root, "empty-peer-folder"));
                Assert(fixture.Snapshot() != previous, "An empty directory was omitted from the inventory.");
            }
        });
        Test("oversized files are refused without reading unbounded data", delegate
        {
            using (var fixture = new Fixture())
            {
                using (var stream = File.Create(Path.Combine(fixture.Root, "large.toml"))) stream.SetLength(1024 * 1024 + 1);
                Throws(delegate { fixture.Snapshot(); });
            }
        });
        Test("file count and total size bounds fail closed", delegate
        {
            using (var fixture = new Fixture())
            {
                for (int i = 0; i < 255; i++) File.WriteAllText(Path.Combine(fixture.Root, "peer-" + i + ".toml"), "fixture");
                Throws(delegate { fixture.Snapshot(); });
            }
            using (var fixture = new Fixture())
            {
                for (int i = 0; i < 17; i++) using (var stream = File.Create(Path.Combine(fixture.Root, "large-" + i + ".toml"))) stream.SetLength(1024 * 1024);
                Throws(delegate { fixture.Snapshot(); });
            }
        });
        Test("directory count bound fails closed", delegate
        {
            using (var fixture = new Fixture())
            {
                for (int i = 0; i < 257; i++) Directory.CreateDirectory(Path.Combine(fixture.Root, "empty-" + i));
                Throws(delegate { fixture.Snapshot(); });
            }
        });
        Test("hard-linked profile files are rejected", delegate
        {
            using (var fixture = new Fixture())
            {
                string alias = Path.Combine(fixture.Root, "linked.toml");
                if (!CreateHardLink(alias, Path.Combine(fixture.Root, "RustDesk.toml"), IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                Throws(delegate { fixture.Snapshot(); });
            }
        });
        Test("redirected profile directories are rejected without following them", delegate
        {
            using (var fixture = new Fixture())
            {
                string target = Path.Combine(fixture.Root, "ordinary-target");
                string junction = Path.Combine(fixture.Root, "redirected-peer-folder");
                Directory.CreateDirectory(target);
                var command = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    "/d /c mklink /J \"" + junction + "\" \"" + target + "\"") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                using (Process process = Process.Start(command))
                {
                    string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new IOException("Could not create isolated junction fixture: " + output);
                }
                try { Throws(delegate { fixture.Snapshot(); }); }
                finally { Directory.Delete(junction); }
                Assert(Directory.Exists(target), "Junction verification modified its ordinary target.");
            }
        });
        Test("inventory changes during a held snapshot are detected", delegate
        {
            using (var fixture = new Fixture())
            {
                Throws(delegate { UpdateProfile.SnapshotConfigDirectory(fixture.Root, delegate { File.WriteAllText(Path.Combine(fixture.Root, "late-peer.toml"), "concurrent fixture"); }); });
                Assert(File.ReadAllText(Path.Combine(fixture.Root, "RustDesk.toml")) == "fixture identity\r\n", "Snapshot modified profile bytes.");
            }
        });
        Test("held profile files cannot be overwritten during snapshot verification", delegate
        {
            using (var fixture = new Fixture())
            {
                bool refused = false;
                string hash = UpdateProfile.SnapshotConfigDirectory(fixture.Root, delegate {
                    try { File.WriteAllText(Path.Combine(fixture.Root, "RustDesk.toml"), "concurrent replacement"); }
                    catch (IOException) { refused = true; }
                });
                Assert(refused && hash == fixture.Snapshot(), "A held identity file could be overwritten.");
            }
        });
        Test("held profile directory cannot be renamed into a path-swap gap", delegate
        {
            using (var fixture = new Fixture())
            {
                bool refused = false;
                string sibling = fixture.Root + "-unexpected-move";
                string hash = UpdateProfile.SnapshotConfigDirectory(fixture.Root, delegate {
                    try { Directory.Move(fixture.Root, sibling); }
                    catch (IOException) { refused = true; }
                });
                Assert(refused && Directory.Exists(fixture.Root) && !Directory.Exists(sibling) && hash == fixture.Snapshot(), "A held directory could be swapped during verification.");
            }
        });
        Test("snapshot releases all held paths after rejection", delegate
        {
            using (var fixture = new Fixture())
            {
                string path = Path.Combine(fixture.Root, "RustDesk.toml");
                Throws(delegate { UpdateProfile.SnapshotConfigDirectory(fixture.Root, delegate { throw new IOException("fixture interruption"); }); });
                File.WriteAllText(path, "after interrupted fixture");
                Assert(fixture.Snapshot().Length == 64, "An interrupted snapshot retained a filesystem lock.");
            }
        });
        Console.WriteLine("RESULT update profile: " + passed + " passed, " + failed + " failed.");
        return failed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
