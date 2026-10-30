using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Touch365Support
{
    internal static class UpdateHandoff
    {
        internal static string CanonicalLocalPath(string path)
        {
            // Reject network/device namespaces before GetAttributes/Exists or opening
            // anything; those operations can themselves contact SMB or a device.
            if (String.IsNullOrWhiteSpace(path) || path.Length > 240 || !Regex.IsMatch(path, @"^[A-Za-z]:\\[^:]*$")) throw Invalid();
            string[] parts = path.Substring(3).Split('\\');
            if (parts.Length == 0) throw Invalid();
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." || part != part.Trim() || part.EndsWith(".", StringComparison.Ordinal)
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw Invalid();
                string stem = part.Split('.')[0].TrimEnd(' ', '.');
                if (Regex.IsMatch(stem, @"^(?:CON|PRN|AUX|NUL|CLOCK\$|CONIN\$|CONOUT\$|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])$", RegexOptions.IgnoreCase)) throw Invalid();
            }
            string full;
            try { full = Path.GetFullPath(path); }
            catch (ArgumentException) { throw Invalid(); }
            catch (NotSupportedException) { throw Invalid(); }
            if (!String.Equals(full, path, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            return full;
        }

        internal static string LocalFile(string path)
        {
            string full = CanonicalLocalPath(path);
            if (new DriveInfo(Path.GetPathRoot(full)).DriveType != DriveType.Fixed) throw Invalid();
            SafeFiles.NoReparse(full);
            FileAttributes attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw Invalid();
            return full;
        }

        private static InvalidDataException Invalid()
        { return new InvalidDataException("Update handoff requires a canonical local ordinary-file path."); }
    }
}
