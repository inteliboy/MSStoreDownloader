using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MSStoreDownloader
{
    internal sealed class WimPackage
    {
        private static readonly Regex FullName = new Regex(
            @"^(?<name>[a-z0-9.-]+)_(?<version>\d+\.\d+\.\d+\.\d+)_(?<arch>neutral|x86|x64|arm|arm64)_(?<resource>[a-z0-9.~+-]*)_(?<publisher>[a-z0-9]+)\.(?<format>e?appx(?:bundle)?|e?msix(?:bundle)?)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public string FileName;
        public string Name;
        public string PublisherId;
        public string Architecture;
        public string Resource;
        public bool Bundle;
        public bool Encrypted;
        public Version Version;

        public string Family { get { return Name + "_" + PublisherId; } }

        public static WimPackage Parse(string fileName)
        {
            Match match = FullName.Match(fileName);
            Version version;
            if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out version))
                return null;

            string format = match.Groups["format"].Value.ToLowerInvariant();
            return new WimPackage
            {
                FileName = fileName,
                Name = match.Groups["name"].Value,
                PublisherId = match.Groups["publisher"].Value,
                Architecture = match.Groups["arch"].Value.ToLowerInvariant(),
                Resource = match.Groups["resource"].Value,
                Bundle = format.EndsWith("bundle", StringComparison.Ordinal),
                Encrypted = format.StartsWith("e", StringComparison.Ordinal),
                Version = version
            };
        }
    }

    internal sealed class WimUpdatePlan
    {
        public readonly List<string> Add = new List<string>();
        public readonly List<string> Remove = new List<string>();
        public int SkippedApps;
        public bool PruningSkipped;
        public string BackupPath;
    }

    internal static class PackageWim
    {
        public static WimUpdatePlan CreateUpdated(string input,
            Func<IList<WimPackage>, bool, string, string, WimUpdatePlan> planUpdate, Action<string> log)
        {
            if (!string.Equals(Path.GetFileName(input), "packages.wim", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Select the active packages.wim.");

            string folder = Path.GetDirectoryName(input);
            string lockFile = Path.Combine(folder, ".packages-update.lock");
            using (var updateLock = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose))
                return CreateUpdatedLocked(input, folder, planUpdate, log);
        }

        private static WimUpdatePlan CreateUpdatedLocked(string input, string folder,
            Func<IList<WimPackage>, bool, string, string, WimUpdatePlan> planUpdate, Action<string> log)
        {
            string sevenZip = FindSevenZip();
            string[] entries = ReadEntries(sevenZip, input);
            var packages = entries.Select(WimPackage.Parse).Where(package => package != null).ToList();
            if (packages.Count == 0)
                throw new InvalidDataException("The WIM has no recognizable Store packages at its root.");

            bool unsupportedPackages = entries.Any(name => WimPackage.Parse(name) == null &&
                Regex.IsMatch(name, @"\.(?:e?appx|e?msix)(?:bundle)?$", RegexOptions.IgnoreCase));

            string staging = Path.Combine(folder, ".packages-" + Guid.NewGuid().ToString("N"));
            string sourceFiles = Path.Combine(staging, "source");
            string downloads = Path.Combine(staging, "downloads");
            try
            {
                Directory.CreateDirectory(sourceFiles);
                Directory.CreateDirectory(downloads);
                string listFile = Path.Combine(staging, "files.txt");
                log("Extracting packages to inspect their manifests...");
                WriteList(listFile, packages.Where(package => !package.Encrypted).Select(package => package.FileName));
                if (packages.Any(package => !package.Encrypted))
                    Run(sevenZip, sourceFiles, "e -y -spd -scsUTF-8 " + Quote(input) + " @" + Quote(listFile));

                WimUpdatePlan plan = planUpdate(packages, unsupportedPackages, sourceFiles, downloads);
                if (plan.Add.Count == 0 && plan.Remove.Count == 0)
                    return plan;

                string candidate = Path.Combine(staging, "packages.wim");
                File.Copy(input, candidate);
                File.SetAttributes(candidate, File.GetAttributes(candidate) & ~FileAttributes.ReadOnly);
                if (plan.Add.Count > 0)
                {
                    WriteList(listFile, plan.Add);
                    Run(sevenZip, downloads, "a -twim -spd -scsUTF-8 " + Quote(candidate) + " @" + Quote(listFile));
                }

                if (plan.Remove.Count > 0)
                {
                    WriteList(listFile, plan.Remove);
                    Run(sevenZip, downloads, "d -spd -scsUTF-8 " + Quote(candidate) + " @" + Quote(listFile));
                }

                log("Verifying the updated WIM...");
                Run(sevenZip, downloads, "t " + Quote(candidate));
                var actual = new HashSet<string>(ReadEntries(sevenZip, candidate), StringComparer.OrdinalIgnoreCase);
                var expected = new HashSet<string>(entries.Except(plan.Remove, StringComparer.OrdinalIgnoreCase)
                    .Concat(plan.Add), StringComparer.OrdinalIgnoreCase);
                if (!actual.SetEquals(expected))
                    throw new InvalidDataException("The updated WIM did not contain the expected files.");
                log("Generating MD5 and SHA-256 checksums and archiving the previous WIM...");
                plan.BackupPath = Publish(input, candidate, staging);
                return plan;
            }
            catch (AggregateException)
            {
                // Recovery files must stay on disk if the rollback could not finish.
                staging = null;
                throw;
            }
            finally
            {
                try { if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch (IOException ex) { log("Could not remove temporary files: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { log("Could not remove temporary files: " + ex.Message); }
            }
        }

        internal static string Publish(string input, string candidate, string staging)
        {
            string folder = Path.GetDirectoryName(input);
            string backup = Path.Combine(folder, "packages.backup-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wim");
            string activeName = Path.GetFileName(input);
            string backupName = Path.GetFileName(backup);
            string[] extensions = { ".md5", ".sha256" };
            WriteChecksum(candidate, Path.Combine(staging, "new.md5"), activeName, MD5.Create());
            WriteChecksum(candidate, Path.Combine(staging, "new.sha256"), activeName, SHA256.Create());

            foreach (string extension in extensions.Where(extension => File.Exists(input + extension)))
            {
                // Standard checksum entries must point to the renamed backup WIM.
                string original = File.ReadAllText(input + extension);
                string renamed = Regex.Replace(original, @"(?im)^([0-9a-f]+[ \t]+\*?)" +
                    Regex.Escape(activeName) + @"(?=\r?$)", match => match.Groups[1].Value + backupName);
                File.WriteAllText(Path.Combine(staging, "backup" + extension), renamed, new UTF8Encoding(false));
            }

            var undo = new Stack<Action>();
            Action<string, string> move = (from, to) =>
            {
                File.Move(from, to);
                undo.Push(() => File.Move(to, from));
            };
            try
            {
                move(input, backup);
                foreach (string extension in extensions)
                {
                    string old = Path.Combine(staging, "backup" + extension);
                    if (File.Exists(old))
                    {
                        move(input + extension, Path.Combine(staging, "original" + extension));
                        move(old, backup + extension);
                    }
                }
                move(candidate, input);
                foreach (string extension in extensions)
                    move(Path.Combine(staging, "new" + extension), input + extension);
                return backup;
            }
            catch (Exception publishError)
            {
                var errors = new List<Exception> { publishError };
                while (undo.Count > 0)
                {
                    try { undo.Pop()(); }
                    catch (Exception error) { errors.Add(error); }
                }
                if (errors.Count > 1)
                    throw new AggregateException("Publishing and rollback failed. Keep recovery files in " +
                        staging + " and check " + backup, errors);
                throw;
            }
        }

        private static void WriteChecksum(string input, string destination, string name, HashAlgorithm algorithm)
        {
            using (algorithm)
            using (FileStream stream = File.OpenRead(input))
            {
                string hash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                File.WriteAllText(destination, hash + " *" + name + "\n", new UTF8Encoding(false));
            }
        }

        private static string[] ReadEntries(string sevenZip, string path)
        {
            string[] lines = Run(sevenZip, null, "l -slt " + Quote(path)).Replace("\r", "").Split('\n');
            int entries = Array.IndexOf(lines, "----------");
            if (entries < 0 || !lines.Take(entries).Contains("Type = wim") ||
                !lines.Take(entries).Contains("Images = 1") || !lines.Take(entries).Contains("Volumes = 1"))
                throw new InvalidDataException("Choose a single-image, standalone WIM.");

            return Enumerable.Range(entries + 1, Math.Max(0, lines.Length - entries - 2))
                .Where(index => lines[index].StartsWith("Path = ", StringComparison.Ordinal)
                    && lines[index + 1] == "Folder = -")
                .Select(index => lines[index].Substring("Path = ".Length)).ToArray();
        }

        private static void WriteList(string path, IEnumerable<string> names)
        {
            File.WriteAllLines(path, names, new UTF8Encoding(false));
        }

        private static string FindSevenZip()
        {
            string[] directories = { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            foreach (string directory in directories.Where(path => !string.IsNullOrEmpty(path)))
            {
                string executable = Path.Combine(directory, "7-Zip", "7z.exe");
                if (File.Exists(executable))
                    return executable;
            }
            throw new FileNotFoundException("Install 7-Zip with WIM update support in its standard Program Files location.");
        }

        private static string Run(string executable, string directory, string arguments)
        {
            var start = new ProcessStartInfo(executable, arguments + " -sccUTF-8")
            {
                WorkingDirectory = directory ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (Process process = Process.Start(start))
            {
                var errors = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new IOException("7-Zip exited with code " + process.ExitCode + ". " + errors.Result.Trim()
                        + Environment.NewLine + output.Trim());
                return output;
            }
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }
    }
}
