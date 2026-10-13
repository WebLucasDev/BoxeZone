using System;
using System.Collections.Generic;
using BoxeZone.Services.Storage;

namespace BoxeZone.Services.Upload
{
    public enum QueuedFileStatus
    {
        Pending,
        Done,

        /// <summary>The local file disappeared; "Tentar de novo" moves on without it.</summary>
        Skipped,
    }

    /// <summary>A folder dropped on the zone: recreated at the destination before its files.</summary>
    public sealed class QueuedFolder
    {
        public string LocalPath { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>Drop order, shared with loose files: "Copiar link" lists items in this order.</summary>
        public int Order { get; set; }

        /// <summary>Set once the folder exists at the destination (its real name may end in " (1)").</summary>
        public UploadedItem? Created { get; set; }
    }

    public sealed class QueuedFile
    {
        public string LocalPath { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        /// <summary>Index in <see cref="UploadQueueState.Folders"/>, or -1 for a loose file.</summary>
        public int FolderIndex { get; set; } = -1;

        /// <summary>Subfolder inside the dropped folder, with "/" separators ("" at its root).</summary>
        public string RelativeFolder { get; set; } = string.Empty;

        public int Order { get; set; }

        public long Size { get; set; }

        public DateTimeOffset LastWriteTime { get; set; }

        public string? ResumeState { get; set; }

        public QueuedFileStatus Status { get; set; }

        public UploadedItem? Uploaded { get; set; }
    }

    /// <summary>One provider's batch in progress. Saved encrypted (UploadQueueStore) so it survives a restart.</summary>
    public sealed class UploadQueueState
    {
        public string TargetFolder { get; set; } = "/";

        public List<QueuedFolder> Folders { get; set; } = [];

        public List<QueuedFile> Files { get; set; } = [];

        public int NextOrder { get; set; }
    }
}
