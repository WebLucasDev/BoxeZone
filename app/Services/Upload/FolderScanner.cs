using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace BoxeZone.Services.Upload
{
    /// <summary>A file found in a drop. RelativeFolder: its subfolder inside a dropped folder ("" otherwise).</summary>
    public sealed record ScannedFile(string LocalPath, string RelativeFolder, long Size, DateTimeOffset LastWriteTime);

    public sealed record ScannedFolder(string LocalPath, string Name, IReadOnlyList<ScannedFile> Files);

    /// <summary>What a drop turned into, in drop order: loose files and folders, each folder with its files.</summary>
    public sealed record ScanResult(IReadOnlyList<object> Items)
    {
        public bool IsEmpty => Items.Count == 0;
    }

    /// <summary>
    /// Walks dropped paths off the UI thread. Skips hidden and system entries (desktop.ini,
    /// Thumbs.db) and reparse points (links can loop); empty folders produce nothing.
    /// </summary>
    public static class FolderScanner
    {
        private const FileAttributes Skipped = FileAttributes.Hidden | FileAttributes.System;

        public static Task<ScanResult> ScanAsync(IReadOnlyList<string> paths) => Task.Run(() =>
        {
            var items = new List<object>();
            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & Skipped) == 0)
                    {
                        items.Add(new ScannedFile(info.FullName, string.Empty, info.Length, info.LastWriteTimeUtc));
                    }
                }
                else if (Directory.Exists(path))
                {
                    var root = new DirectoryInfo(path);
                    var files = new List<ScannedFile>();
                    Walk(root, string.Empty, files);
                    if (files.Count > 0)
                    {
                        items.Add(new ScannedFolder(root.FullName, root.Name, files));
                    }
                }
            }

            return new ScanResult(items);
        });

        private static void Walk(DirectoryInfo folder, string relative, List<ScannedFile> files)
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = folder.EnumerateFileSystemInfos()
                    .OrderBy(e => e is DirectoryInfo)
                    .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // A protected or vanished subfolder: send what can be read.
                return;
            }

            foreach (FileSystemInfo entry in entries)
            {
                if ((entry.Attributes & (Skipped | FileAttributes.ReparsePoint)) != 0)
                {
                    continue;
                }

                if (entry is FileInfo file)
                {
                    files.Add(new ScannedFile(file.FullName, relative, file.Length, file.LastWriteTimeUtc));
                }
                else if (entry is DirectoryInfo sub)
                {
                    Walk(sub, relative.Length == 0 ? sub.Name : $"{relative}/{sub.Name}", files);
                }
            }
        }
    }
}
