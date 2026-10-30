using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Touch365Support;

internal static class EngineTests
{
    private static int passed;
    private static int failed;

    private sealed class Fake : IOptionsClient
    {
        public readonly Dictionary<string, string> Live = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Disk = new Dictionary<string, string>();
        public readonly List<string> Events = new List<string>();
        public Dictionary<string, string> Backup;
        public Action<string, string> AfterSet;
        public Func<string, string, string> ReadOverride;
        public bool FailBackup;
        public bool Paused;

        public Fake()
        {
            foreach (var pair in Policy.Options) Live[pair.Key] = "";
            Live["approve-mode"] = "password-click";
            Live["access-mode"] = "view";
            Live["custom-rendezvous-server"] = "previous.example.invalid";
            Live["unrelated-preference"] = "keep-this";
            Live["device-identity-fixture"] = "fixture-device-123";
            foreach (var pair in Live) Disk.Add(pair.Key, pair.Value);
        }

        public string Get(string key)
        {
            Events.Add("GET " + key);
            string value;
            if (!Live.TryGetValue(key, out value)) value = "";
            return ReadOverride == null ? value : ReadOverride(key, value);
        }
        public void Set(string key, string value)
        {
            Events.Add("SET " + key + "=" + value);
            Live[key] = value;
            Disk[key] = value;
            if (AfterSet != null) AfterSet(key, value);
        }
        public string GetPersisted(string key)
        {
            Events.Add("DISK " + key);
            string value;
            return Disk.TryGetValue(key, out value) ? value : "";
        }
        public void SaveBackup(IDictionary<string, string> previous)
        {
            Events.Add("BACKUP");
            if (FailBackup) throw new IOException("Fixture backup failure.");
            Backup = new Dictionary<string, string>(previous);
        }
        public void PauseOnFailure() { Events.Add("PAUSE"); Paused = true; }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Equal(string expected, string actual, string message)
    {
        if (expected != actual) throw new Exception(message);
    }
    private static Exception Throws(Action action)
    {
        try { action(); }
        catch (Exception exception) { return exception; }
        throw new Exception("Expected failure was not raised.");
    }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception.Message); }
    }
    private static void Apply(Fake fake) { Provisioner.Apply(fake, delegate { }); }
    private static Dictionary<string, string> Snapshot(Fake fake) { return new Dictionary<string, string>(fake.Live); }
    private static void AssertRestored(Fake fake, IDictionary<string, string> expected)
    {
        Assert(fake.Live.Count == expected.Count, "Live option count changed.");
        Assert(fake.Disk.Count == expected.Count, "Persisted option count changed.");
        foreach (var pair in expected)
        {
            Equal(pair.Value, fake.Live[pair.Key], "Live option was not restored: " + pair.Key);
            Equal(pair.Value, fake.Disk[pair.Key], "Persisted option was not restored: " + pair.Key);
        }
    }

    private static void ProvisioningTests()
    {
        Test("policy uses click approval first and contains no password assignment", delegate
        {
            Assert(Policy.Options.Length > 0, "Policy is empty.");
            Equal("approve-mode", Policy.Options[0].Key, "Approval must be the first setting.");
            Equal("click", Policy.Options[0].Value, "Approval must require a customer click.");
            Assert(Policy.Options.Select(p => p.Key).Distinct().Count() == Policy.Options.Length, "Duplicate policy key.");
            Assert(!Policy.Options.Any(p => p.Key == "password" || p.Key == "permanent-password" || p.Key == "temporary-password"), "Policy assigns a password.");
        });
        Test("successful provisioning verifies policy and preserves unrelated identity", delegate
        {
            var fake = new Fake();
            var previous = Snapshot(fake);
            Apply(fake);
            foreach (var pair in Policy.Options)
            {
                Equal(pair.Value, fake.Live[pair.Key], "Live policy mismatch: " + pair.Key);
                Equal(pair.Value, fake.Disk[pair.Key], "Persisted policy mismatch: " + pair.Key);
                Assert(fake.Events.Contains("DISK " + pair.Key), "Persisted setting was not verified: " + pair.Key);
                Equal(previous[pair.Key], fake.Backup[pair.Key], "Backup mismatch: " + pair.Key);
            }
            Equal("keep-this", fake.Live["unrelated-preference"], "Unrelated option changed.");
            Equal("fixture-device-123", fake.Live["device-identity-fixture"], "Device identity changed.");
            Equal("SET approve-mode=click", fake.Events.First(e => e.StartsWith("SET ")), "Approval was not applied first.");
            Assert(fake.Events.IndexOf("BACKUP") < fake.Events.FindIndex(e => e.StartsWith("SET ")), "Mutation preceded backup.");
            Assert(!fake.Paused, "Successful setup paused the service.");
        });
        Test("running setup twice has the same resulting settings", delegate
        {
            var fake = new Fake();
            Apply(fake);
            var afterFirst = Snapshot(fake);
            Apply(fake);
            AssertRestored(fake, afterFirst);
            foreach (var pair in Policy.Options) Equal(pair.Value, fake.Backup[pair.Key], "Second run backup did not match the first result.");
        });
        Test("setter failure after a write restores all previous settings", delegate
        {
            var fake = new Fake();
            var before = Snapshot(fake);
            bool fail = true;
            fake.AfterSet = delegate(string key, string value)
            {
                if (fail && key == "enable-clipboard") { fail = false; throw new IOException("Fixture setter failure."); }
            };
            Exception exception = Throws(delegate { Apply(fake); });
            Assert(exception.Message.Contains("previous settings were restored"), "Restoration was not reported.");
            AssertRestored(fake, before);
            Equal("SET approve-mode=password-click", fake.Events.Last(e => e.StartsWith("SET ")), "Approval must be restored last.");
            Assert(!fake.Paused, "Successfully restored setup paused service.");
        });
        Test("immediate CLI readback mismatch rolls back", delegate
        {
            var fake = new Fake();
            var before = Snapshot(fake);
            bool armed = false;
            bool used = false;
            fake.AfterSet = delegate(string key, string value) { if (key == "enable-keyboard" && value == "Y") armed = true; };
            fake.ReadOverride = delegate(string key, string value)
            {
                if (armed && !used && key == "enable-keyboard") { used = true; return "N"; }
                return value;
            };
            Throws(delegate { Apply(fake); });
            Assert(used, "Fixture did not exercise CLI mismatch.");
            AssertRestored(fake, before);
        });
        Test("persisted mismatch fails even when CLI reports correct settings", delegate
        {
            var fake = new Fake();
            var before = Snapshot(fake);
            bool injected = false;
            fake.AfterSet = delegate(string key, string value)
            {
                if (!injected && key == "enable-file-transfer" && value == "Y")
                {
                    injected = true;
                    fake.Disk[key] = "N";
                }
            };
            Exception exception = Throws(delegate { Apply(fake); });
            Assert(exception.Message.Contains("confirm saved setting"), "Persisted mismatch was not identified.");
            AssertRestored(fake, before);
            Assert(!fake.Paused, "Successful rollback should not pause service.");
        });
        Test("rollback failure pauses RustDesk and reports incomplete recovery", delegate
        {
            var fake = new Fake();
            bool failedFirst = false;
            fake.AfterSet = delegate(string key, string value)
            {
                if (!failedFirst && key == "enable-clipboard" && value == "Y")
                { failedFirst = true; throw new IOException("Fixture apply failure."); }
                if (failedFirst && key == "access-mode" && value == "view")
                    throw new IOException("Fixture rollback failure.");
            };
            Exception exception = Throws(delegate { Apply(fake); });
            Assert(fake.Paused, "Incomplete recovery must pause RustDesk.");
            Assert(exception.Message.Contains("could not be fully restored"), "Incomplete recovery was not reported.");
        });
        Test("backup failure produces no settings mutations", delegate
        {
            var fake = new Fake { FailBackup = true };
            var before = Snapshot(fake);
            Throws(delegate { Apply(fake); });
            Assert(!fake.Events.Any(e => e.StartsWith("SET ")), "Mutation occurred despite backup failure.");
            AssertRestored(fake, before);
            Assert(!fake.Paused, "Backup failure should not stop an unchanged service.");
        });
        Test("initial read failure produces no settings mutations", delegate
        {
            var fake = new Fake();
            fake.ReadOverride = delegate(string key, string value) { if (key == "access-mode") throw new IOException("Fixture read failure."); return value; };
            Throws(delegate { Apply(fake); });
            Assert(!fake.Events.Contains("BACKUP"), "Incomplete snapshot was backed up.");
            Assert(!fake.Events.Any(e => e.StartsWith("SET ")), "Mutation occurred before a complete snapshot.");
        });
        Test("initial CLI and persisted disagreement fails before backup or writes", delegate
        {
            var fake = new Fake();
            fake.Disk["approve-mode"] = "click";
            Exception exception = Throws(delegate { Apply(fake); });
            Assert(exception.Message.Contains("do not agree"), "Initial mismatch was not identified.");
            Assert(!fake.Events.Contains("BACKUP"), "An inconsistent snapshot was backed up.");
            Assert(!fake.Events.Any(e => e.StartsWith("SET ")), "Mismatch caused settings mutation.");
            Assert(!fake.Paused, "Unchanged service was paused.");
        });
    }

    private static void ConfigTests()
    {
        Test("TOML reads literal/basic strings and quoted option names", delegate
        {
            string text = "[options] # settings\n'approve-mode' = 'click' # comment\n\"enable-keyboard\" = \"Y\"\napi-server = ''\n";
            Equal("click", ConfigText.ReadOption(text, "approve-mode"), "Literal value not read.");
            Equal("Y", ConfigText.ReadOption(text, "enable-keyboard"), "Basic quoted key not read.");
            Equal("", ConfigText.ReadOption(text, "api-server"), "Empty literal not read.");
            Equal("", ConfigText.ReadOption(text, "absent-key"), "Absent value should be empty.");
        });
        Test("TOML updates one setting and preserves unrelated Unicode/comments/sections", delegate
        {
            string text = "\uFEFFid = 'fixture-keep'\r\n# Keep this comment\r\n[options]\r\n'approve-mode' = 'password-click' # replace this value\r\nlabel = 'Caf\u00e9 \u6771\u4eac'\r\n[other]\r\napprove-mode = 'unrelated'\r\n";
            string updated = ConfigText.SetOption(text, "approve-mode", "click");
            Equal("click", ConfigText.ReadOption(updated, "approve-mode"), "Updated value not readable.");
            Assert(updated.Contains("id = 'fixture-keep'"), "Identity fixture changed.");
            Assert(updated.Contains("# Keep this comment"), "Unrelated comment changed.");
            Assert(updated.Contains("label = 'Caf\u00e9 \u6771\u4eac'"), "Unicode preference changed.");
            Assert(updated.Contains("[other]\r\napprove-mode = 'unrelated'"), "Another table changed.");
            Assert(!updated.Contains("password-click"), "Original targeted value remained.");
        });
        Test("TOML adds a missing option before the next section", delegate
        {
            string updated = ConfigText.SetOption("[options]\nexisting = 'value'\n[other]\nkeep = 'yes'\n", "approve-mode", "click");
            Equal("click", ConfigText.ReadOption(updated, "approve-mode"), "Inserted option is outside options table.");
            Assert(updated.IndexOf("approve-mode", StringComparison.Ordinal) < updated.IndexOf("[other]", StringComparison.Ordinal), "Option inserted into wrong table.");
        });
        Test("TOML empty file and absent options table can be initialized", delegate
        {
            string empty = ConfigText.SetOption("", "approve-mode", "click");
            Equal("click", ConfigText.ReadOption(empty, "approve-mode"), "Empty file was not initialized.");
            string existing = ConfigText.SetOption("id = 'fixture'\n[other]\nvalue = 'keep'\n", "enable-keyboard", "Y");
            Equal("Y", ConfigText.ReadOption(existing, "enable-keyboard"), "Missing table was not added.");
            Assert(existing.Contains("id = 'fixture'") && existing.Contains("value = 'keep'"), "Unrelated content changed.");
        });
        Test("TOML duplicate keys and duplicate options tables fail closed", delegate
        {
            string duplicateKey = "[options]\napprove-mode = 'click'\n\"approve-mode\" = 'password'\n";
            Assert(Throws(delegate { ConfigText.ReadOption(duplicateKey, "approve-mode"); }) is InvalidDataException, "Duplicate key did not fail closed.");
            Assert(Throws(delegate { ConfigText.SetOption(duplicateKey, "approve-mode", "click"); }) is InvalidDataException, "Duplicate key was edited.");
            string duplicateTable = "[options]\n[other]\nx = 'x'\n[options]\n";
            Assert(Throws(delegate { ConfigText.ReadOption(duplicateTable, "approve-mode"); }) is InvalidDataException, "Duplicate table did not fail closed.");
        });
        Test("TOML unsupported scalars and injected values fail closed", delegate
        {
            Throws(delegate { ConfigText.ReadOption("[options]\napprove-mode = 1", "approve-mode"); });
            Throws(delegate { ConfigText.ReadOption("[options]\napprove-mode = \"a\\nb\"", "approve-mode"); });
            Throws(delegate { ConfigText.SetOption("", "bad.key", "value"); });
            Throws(delegate { ConfigText.SetOption("", "approve-mode", "click'\nnext='value"); });
        });
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static string[] DecodeArguments(string commandLine)
    {
        int count;
        IntPtr pointer = CommandLineToArgvW(commandLine, out count);
        if (pointer == IntPtr.Zero) throw new Exception("Windows could not parse fixture arguments.");
        try
        {
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size));
            return result;
        }
        finally { LocalFree(pointer); }
    }
    private static void ArgumentTests()
    {
        Test("Windows argument quoting round trips empty spaces quotes slashes and Unicode", delegate
        {
            string[] arguments = { "", "plain", "two words", @"C:\folder with spaces\", "a\"b", "slash\\\"quote", "ends\\\\", "\u6771\u4eac Caf\u00e9", "&|<>^%$()", "\"", "\\" };
            string commandLine = "fixture.exe " + String.Join(" ", arguments.Select(SafeFiles.Quote));
            string[] decoded = DecodeArguments(commandLine);
            Assert(decoded.Length == arguments.Length + 1, "Argument boundaries changed.");
            for (int i = 0; i < arguments.Length; i++) Equal(arguments[i], decoded[i + 1], "Windows argument round trip failed at " + i);
        });
    }

    private static string FixtureFolder(string name)
    {
        string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void LocalSettingTests()
    {
        Test("local patch writes atomically and restores the original fixture", delegate
        {
            string folder = FixtureFolder("existing");
            string path = Path.Combine(folder, "RustDesk_local.toml");
            string backups = Path.Combine(folder, "backups");
            string original = "# Keep this fixture\r\n[options]\r\nallow-remote-cm-modification = 'Y'\r\nlabel = 'Caf\u00e9'\r\n[other]\r\nvalue = 'keep'\r\n";
            File.WriteAllText(path, original, new UTF8Encoding(false));
            var setting = new LocalSetting(path, backups);
            setting.Apply();
            setting.Verify();
            Equal("N", ConfigText.ReadOption(File.ReadAllText(path), "allow-remote-cm-modification"), "Local permission was not applied.");
            Assert(File.ReadAllText(path).Contains("label = 'Caf\u00e9'"), "Unrelated local preference changed.");
            string[] backupFiles = Directory.GetFiles(backups, "RustDesk_local.toml", SearchOption.AllDirectories);
            Assert(backupFiles.Length == 1, "Exactly one local backup was expected.");
            Equal(original, File.ReadAllText(backupFiles[0]), "Local backup does not contain the original fixture.");
            Assert(Directory.GetFiles(folder, "*.tmp", SearchOption.AllDirectories).Length == 0, "An atomic-write temporary file remained.");
            setting.Restore();
            Equal(original, File.ReadAllText(path), "Original local fixture was not restored.");
            setting.Restore();
            Equal(original, File.ReadAllText(path), "Repeated restore changed the fixture.");
        });
        Test("new local config is removed on rollback without touching neighbours", delegate
        {
            string folder = FixtureFolder("new");
            string path = Path.Combine(folder, "profile", "RustDesk_local.toml");
            string neighbour = Path.Combine(folder, "keep.txt");
            File.WriteAllText(neighbour, "fixture-neighbour");
            var setting = new LocalSetting(path, Path.Combine(folder, "backups"));
            setting.Apply();
            setting.Verify();
            Assert(File.Exists(path), "New local config was not created.");
            setting.Restore();
            Assert(!File.Exists(path), "New local config was not removed on rollback.");
            Equal("fixture-neighbour", File.ReadAllText(neighbour), "Neighbour file changed.");
        });
        Test("reapplying local policy leaves an already configured file unchanged", delegate
        {
            string folder = FixtureFolder("repeat");
            string path = Path.Combine(folder, "RustDesk_local.toml");
            string backups = Path.Combine(folder, "backups");
            File.WriteAllText(path, "[options]\r\nallow-remote-cm-modification = 'Y'\r\n");
            new LocalSetting(path, backups).Apply();
            string once = File.ReadAllText(path);
            int before = Directory.GetDirectories(backups).Length;
            var second = new LocalSetting(path, backups);
            second.Apply();
            second.Verify();
            Equal(once, File.ReadAllText(path), "Repeat setup rewrote an already configured file.");
            Assert(Directory.GetDirectories(backups).Length == before, "Repeat setup made an unnecessary backup.");
            second.Restore();
            Equal(once, File.ReadAllText(path), "No-op rollback changed an already configured file.");
        });
        Test("local rollback refuses to overwrite concurrent customer changes", delegate
        {
            string folder = FixtureFolder("concurrent");
            string path = Path.Combine(folder, "RustDesk_local.toml");
            string backups = Path.Combine(folder, "backups");
            File.WriteAllText(path, "[options]\r\nallow-remote-cm-modification = 'Y'\r\n");
            var setting = new LocalSetting(path, backups);
            setting.Apply();
            string concurrent = File.ReadAllText(path) + "# Customer changed this fixture\r\n";
            File.WriteAllText(path, concurrent);
            Assert(Throws(delegate { setting.Restore(); }) is IOException, "Concurrent-change guard did not stop rollback.");
            Equal(concurrent, File.ReadAllText(path), "Concurrent change was overwritten.");
            Assert(Directory.GetFiles(backups, "RustDesk_local.toml", SearchOption.AllDirectories).Length == 1, "Recovery backup was not retained.");
        });
        Test("local backup ACL excludes inherited broad user access", delegate
        {
            string folder = FixtureFolder("acl");
            string path = Path.Combine(folder, "RustDesk_local.toml");
            string backups = Path.Combine(folder, "backups");
            File.WriteAllText(path, "[options]\r\nallow-remote-cm-modification = 'Y'\r\n");
            new LocalSetting(path, backups).Apply();
            string backupFolder = Directory.GetDirectories(backups).Single();
            DirectorySecurity acl = Directory.GetAccessControl(backupFolder);
            Assert(acl.AreAccessRulesProtected, "Local backup inherits broad permissions.");
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var sid = (SecurityIdentifier)rule.IdentityReference;
                bool broad = sid.IsWellKnown(WellKnownSidType.WorldSid) || sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) || sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid);
                Assert(!(broad && rule.AccessControlType == AccessControlType.Allow), "Local backup permits a broad user group.");
            }
        });
        Test("test constructor never falls back to live profile folders", delegate
        {
            Throws(delegate { new LocalSetting(null, "fixture"); });
            Throws(delegate { new LocalSetting("fixture", ""); });
        });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string Reserved;
        public string Desktop;
        public string Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr ReservedPointer, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int number);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static string FixtureExe { get { return System.Reflection.Assembly.GetExecutingAssembly().Location; } }

    private static void SpawnFixtureChild(params string[] arguments)
    {
        var startup = new StartupInfo();
        startup.Size = Marshal.SizeOf(typeof(StartupInfo));
        startup.Flags = 0x100; // Explicitly inherit the parent pipe handles, reproducing packed-EXE descendants.
        startup.StdInput = GetStdHandle(-10);
        startup.StdOutput = GetStdHandle(-11);
        startup.StdError = GetStdHandle(-12);
        ProcessInformation child;
        string command = SafeFiles.Quote(FixtureExe) + " " + String.Join(" ", arguments.Select(SafeFiles.Quote));
        if (!CreateProcessW(FixtureExe, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true, 0x08000000,
            IntPtr.Zero, AppDomain.CurrentDomain.BaseDirectory, ref startup, out child))
            throw new Exception("Could not start bounded fixture child, Windows error " + Marshal.GetLastWin32Error() + ".");
        CloseHandle(child.Thread);
        CloseHandle(child.Process);
    }

    private static void RequireFixtureMarker(string path)
    {
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fixtures")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Fixture marker is outside the test output folder.");
    }

    private static int FixtureMode(string[] args)
    {
        if (args[0] == "--fixture-emit")
        {
            Console.Out.WriteLine("fixture stdout");
            Console.Error.WriteLine("fixture stderr");
            return 0;
        }
        if (args[0] == "--fixture-zero") return 0;
        if (args[0] == "--fixture-nonzero") return 17;
        if (args.Length != 2) return 18;
        string marker = args[1];
        RequireFixtureMarker(marker);
        if (args[0] == "--fixture-parent-pipes")
        {
            SpawnFixtureChild("--fixture-hold-pipes", marker);
            Console.WriteLine("fixture parent exiting");
            return 0;
        }
        if (args[0] == "--fixture-hold-pipes")
        {
            File.WriteAllText(marker + ".started", "started");
            Thread.Sleep(2800);
            File.WriteAllText(marker, "finished");
            return 0;
        }
        if (args[0] == "--fixture-parent-marker")
        {
            SpawnFixtureChild("--fixture-delayed-marker", marker);
            return 0;
        }
        if (args[0] == "--fixture-delayed-marker")
        {
            Thread.Sleep(800);
            File.WriteAllText(marker, "complete");
            return 0;
        }
        return 19;
    }

    private static void CommandTests()
    {
        Test("real command helper captures stdout and stderr before returning", delegate
        {
            CommandResult result = Commands.Run(FixtureExe, 3, "--fixture-emit");
            Assert(result.ExitCode == 0, "Helper exit code changed.");
            Assert(result.Output.Contains("fixture stdout") && result.Output.Contains("fixture stderr"), "Helper output was not drained.");
        });
        Test("inherited descendant pipes time out within the command deadline", delegate
        {
            string folder = FixtureFolder("inherited-pipes");
            string marker = Path.Combine(folder, "child-finished.txt");
            var watch = Stopwatch.StartNew();
            Exception exception = Throws(delegate { Commands.Run(FixtureExe, 1, "--fixture-parent-pipes", marker); });
            long elapsed = watch.ElapsedMilliseconds;
            Assert(exception is System.TimeoutException, "Inherited pipes did not cause a bounded timeout.");
            Assert(elapsed < 2200, "Inherited-pipe wait exceeded its bounded deadline.");
            Assert(File.Exists(marker + ".started"), "The descendant fixture never started.");
            Assert(!File.Exists(marker), "The descendant ended before timeout; fixture did not exercise held pipes.");
            while (!File.Exists(marker) && watch.ElapsedMilliseconds < 5000) Thread.Sleep(25);
            Assert(File.Exists(marker), "The bounded descendant did not exit by itself.");
        });
        Test("detached installer waits for actual state after launcher exits", delegate
        {
            string marker = Path.Combine(FixtureFolder("detached-state"), "installed.txt");
            int falsePolls = 0;
            var watch = Stopwatch.StartNew();
            Commands.RunDetachedInstaller(FixtureExe, 4, delegate
            {
                bool ready = File.Exists(marker);
                if (!ready) falsePolls++;
                return ready;
            }, "--fixture-parent-marker", marker);
            Assert(falsePolls > 0, "Installer did not poll incomplete state after the launcher exited.");
            Assert(File.Exists(marker), "Launcher exit code was accepted before actual state existed.");
            Assert(watch.ElapsedMilliseconds >= 650 && watch.ElapsedMilliseconds < 3500, "State polling did not wait for the bounded child.");
        });
        Test("detached installer rejects successful launcher without completion state", delegate
        {
            int polls = 0;
            var watch = Stopwatch.StartNew();
            Exception exception = Throws(delegate
            {
                Commands.RunDetachedInstaller(FixtureExe, 1, delegate { polls++; return false; }, "--fixture-zero");
            });
            Assert(exception is System.TimeoutException, "Missing installed state should time out.");
            Assert(polls > 0, "Actual state was never checked.");
            Assert(watch.ElapsedMilliseconds < 2200, "Detached state timeout was not bounded.");
        });
        Test("detached installer rejects a failing launcher before state polling", delegate
        {
            bool polled = false;
            Exception exception = Throws(delegate
            {
                Commands.RunDetachedInstaller(FixtureExe, 2, delegate { polled = true; return true; }, "--fixture-nonzero");
            });
            Assert(exception is InvalidOperationException, "Nonzero launcher exit was not rejected.");
            Assert(!polled, "Completion callback bypassed launcher failure.");
        });
    }

    private static void SignatureTests()
    {
        Test("publisher verification can obtain a missing certificate chain instead of forcing cache-only", delegate
        {
            int trustCalls = 0;
            int publisherCalls = 0;
            uint observedFlags = UInt32.MaxValue;
            Signature.RequireOfficial(FixtureExe, "cold-cache fixture", delegate(string path, uint flags)
            {
                trustCalls++;
                observedFlags = flags;
                return (flags & 0x1000U) != 0 ? unchecked((int)0x800B010AU) : 0;
            }, delegate(string path)
            {
                publisherCalls++;
                return "PURSLANE";
            });
            Assert(trustCalls == 1 && publisherCalls == 1, "Verification did not complete both trust and publisher checks.");
            Assert((observedFlags & 0x1000U) == 0, "Verification still forces cache-only chain building.");
        });

        foreach (uint status in new uint[] { 0x80096010U, 0x800B0109U, 1U })
        {
            uint capturedStatus = status;
            Test("nonzero trust status 0x" + capturedStatus.ToString("X8") + " fails before publisher lookup with safe diagnostics", delegate
            {
                bool publisherCalled = false;
                string stage = "bundled installer fixture";
                Exception exception = Throws(delegate
                {
                    Signature.RequireOfficial(FixtureExe, stage, delegate(string path, uint flags)
                    {
                        return unchecked((int)capturedStatus);
                    }, delegate(string path)
                    {
                        publisherCalled = true;
                        return "PURSLANE";
                    });
                });
                Assert(exception is InvalidDataException, "Nonzero trust status was not rejected.");
                Assert(!publisherCalled, "Publisher lookup ran before Windows trust succeeded.");
                Assert(exception.Message.Contains(stage), "Failure did not identify the verification stage.");
                Assert(exception.Message.Contains(Path.GetFileName(FixtureExe)), "Failure did not identify the filename.");
                Assert(exception.Message.Contains("Windows code 0x" + capturedStatus.ToString("X8") + "."), "Failure did not preserve the exact eight-digit Windows status.");
                Assert(!exception.Message.Contains(FixtureExe) && !exception.Message.Contains(Path.GetDirectoryName(FixtureExe)), "Failure exposed a full profile or workspace path.");
            });
        }

        Test("publisher is checked only after successful Windows trust", delegate
        {
            var calls = new List<string>();
            Signature.RequireOfficial(FixtureExe, "ordering fixture", delegate(string path, uint flags)
            {
                calls.Add("trust");
                return 0;
            }, delegate(string path)
            {
                calls.Add("publisher");
                return "PURSLANE";
            });
            Assert(calls.SequenceEqual(new[] { "trust", "publisher" }), "Publisher verification order changed.");
        });

        Test("trusted signature from a different publisher is still rejected", delegate
        {
            Exception exception = Throws(delegate
            {
                Signature.RequireOfficial(FixtureExe, "publisher fixture", delegate(string path, uint flags) { return 0; },
                    delegate(string path) { return "UNEXPECTED FIXTURE PUBLISHER"; });
            });
            Assert(exception is InvalidDataException, "Unexpected publisher was accepted.");
            Assert(exception.Message.Contains("unexpected publisher") && exception.Message.Contains("publisher fixture"), "Publisher failure diagnostics are missing.");
            Assert(!exception.Message.Contains(FixtureExe), "Publisher failure exposed the full path.");
        });

        Test("actual unsigned test executable is rejected with a Windows trust code", delegate
        {
            Exception exception = Throws(delegate { Signature.RequireOfficial(FixtureExe, "unsigned fixture"); });
            Assert(exception is InvalidDataException, "Unsigned native fixture was not rejected by Windows trust.");
            Assert(exception.Message.Contains("unsigned fixture") && exception.Message.Contains(Path.GetFileName(FixtureExe)), "Native trust failure is missing stage or filename.");
            Assert(System.Text.RegularExpressions.Regex.IsMatch(exception.Message, @"Windows code 0x[0-9A-F]{8}\."), "Native trust failure is missing its exact Windows code.");
            Assert(!exception.Message.Contains(FixtureExe) && !exception.Message.Contains(Path.GetDirectoryName(FixtureExe)), "Native trust failure exposed the full fixture path.");
        });
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--fixture-", StringComparison.Ordinal)) return FixtureMode(args);
        ProvisioningTests();
        ConfigTests();
        ArgumentTests();
        LocalSettingTests();
        CommandTests();
        SignatureTests();
        if (args.Length != 2)
        {
            Console.WriteLine("FAIL verification arguments: supply bundled payload and installed RustDesk paths.");
            failed++;
        }
        else
        {
            Test("bundled payload has the pinned official SHA-256", delegate { Equal(Policy.PayloadHash, SafeFiles.Digest(args[0]), "Bundled payload digest mismatch."); });
            Test("bundled payload passes the actual publisher trust check", delegate { Signature.RequireOfficial(args[0]); });
            Test("installed RustDesk passes the actual publisher trust check", delegate { Signature.RequireOfficial(args[1]); });
        }
        Console.WriteLine("RESULT " + passed + " passed, " + failed + " failed. No live settings or installation changed.");
        return failed == 0 ? 0 : 1;
    }
}
