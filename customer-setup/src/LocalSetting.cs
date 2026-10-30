using System;
using System.IO;
using System.Text;

namespace Touch365Support
{
    internal sealed class LocalSetting
    {
        private readonly string path;
        private readonly string backupRoot;
        private string original;
        private string replacement;
        private bool existed;
        private bool applied;

        public LocalSetting()
            : this(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"RustDesk\config\RustDesk_local.toml"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Touch365\RemoteSupport\Backups"))
        {
        }

        internal LocalSetting(string path, string backupRoot)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A local settings path is required.", "path");
            if (String.IsNullOrWhiteSpace(backupRoot)) throw new ArgumentException("A backup folder is required.", "backupRoot");
            this.path = Path.GetFullPath(path);
            this.backupRoot = Path.GetFullPath(backupRoot);
        }

        public void Apply()
        {
            SafeFiles.NoReparse(path);
            existed = File.Exists(path); original = existed ? File.ReadAllText(path) : "";
            // Keep an already configured profile byte-for-byte unchanged on later runs.
            if (existed && ConfigText.ReadOption(original, "allow-remote-cm-modification") == "N")
            {
                replacement = original;
                return;
            }
            replacement = ConfigText.SetOption(original, "allow-remote-cm-modification", "N");
            if (existed && original == replacement) return;
            string backup = Path.Combine(backupRoot, Guid.NewGuid().ToString("N"));
            SafeFiles.PrivateDirectory(backup, false);
            if (existed) File.Copy(path, Path.Combine(backup, "RustDesk_local.toml"), false);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AtomicWrite(path, replacement); applied = true; Verify();
        }

        public void Verify()
        {
            if (ConfigText.ReadOption(File.ReadAllText(path), "allow-remote-cm-modification") != "N")
                throw new InvalidOperationException("RustDesk could not confirm the customer's local approval controls. Contact Touch365.");
        }

        public void Restore()
        {
            if (!applied) return;
            if (File.ReadAllText(path) != replacement) throw new IOException("Local settings changed during setup; backup retained.");
            if (existed) AtomicWrite(path, original); else File.Delete(path);
            applied = false;
        }

        private static void AtomicWrite(string path, string value)
        {
            string temporary = path + ".touch365-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, value, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
