using System;
using System.IO;
using Touch365Support;

internal static class UpdateHandoffTests
{
    internal static int Run()
    {
        int failed = 0, passed = 0;
        Action<string, Action> test = delegate(string name, Action check) {
            try { check(); passed++; Console.WriteLine("PASS update handoff: " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL update handoff: " + name + ": " + ex.Message); }
        };
        test("network device relative and alternate-stream paths are rejected before filesystem access", delegate {
            foreach (string path in new[] { @"\\server\share\metadata.xml", @"\\?\C:\metadata.xml", @"\\.\pipe\capture", @"C:relative.xml", "metadata.xml", @"C:\temp\file.xml:stream", @"C:\temp\..\metadata.xml", @"C:\temp\CON", @"C:\temp\CONIN$", @"C:\temp\CONOUT$", @"C:\temp\NUL .txt", @"C:\temp\file.", "C:\\temp\\COM\u00b9.txt", "C:\\temp\\LPT\u00b2.txt", @"C:\temp\\metadata.xml" })
            {
                try { UpdateHandoff.CanonicalLocalPath(path); }
                catch (InvalidDataException) { continue; }
                throw new Exception("An unsafe path was accepted.");
            }
        });
        test("canonical local paths preserve their exact authority", delegate {
            string path = @"C:\Touch365 Updates\metadata.xml";
            if (UpdateHandoff.CanonicalLocalPath(path) != path) throw new Exception("Local path changed.");
        });
        test("ordinary fixture file is accepted but directories are rejected", delegate {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(root, "handoff-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(path, "fixture");
            try {
                if (UpdateHandoff.LocalFile(path) != path) throw new Exception("The fixture file changed.");
                try { UpdateHandoff.LocalFile(root.TrimEnd(Path.DirectorySeparatorChar)); }
                catch (InvalidDataException) { return; }
                throw new Exception("A directory was accepted as a file.");
            } finally { File.Delete(path); }
        });
        test("app-only handoff rejects configuration before privileged staging", delegate {
            var state = new UpdateState { Enrolled = true, TargetMode = UpdateState.AppOnly };
            var config = new UpdateManifest { ConfigVersion = 1 };
            config.Options.Add("approve-mode", "click");
            try { UpdateCommands.RequireCompatibleBeforeStaging(state, config); }
            catch (InvalidOperationException) { return; }
            throw new Exception("A config-only manifest reached app-only staging.");
        });
        test("new service binary handoff also requires a complete signed layout", delegate {
            var state = new UpdateState { Enrolled = true };
            var legacy = new UpdateManifest { BinaryVersion = new Version(1, 6, 0) };
            try { UpdateCommands.RequireCompatibleBeforeStaging(state, legacy); }
            catch (InvalidDataException) { return; }
            throw new Exception("A legacy incomplete binary package reached privileged staging.");
        });
        test("service-managed config handoff remains allowed without a binary package", delegate {
            var state = new UpdateState { Enrolled = true };
            var config = new UpdateManifest { ConfigVersion = 1 };
            config.Options.Add("approve-mode", "click");
            UpdateCommands.RequireCompatibleBeforeStaging(state, config);
        });
        test("unenrolled handoff is rejected before package validation", delegate {
            try { UpdateCommands.RequireCompatibleBeforeStaging(new UpdateState(), new UpdateManifest()); }
            catch (InvalidOperationException) { return; }
            throw new Exception("An unenrolled handoff reached privileged staging.");
        });
        Console.WriteLine("RESULT update handoff: " + passed + " passed, " + failed + " failed.");
        return failed;
    }
}
