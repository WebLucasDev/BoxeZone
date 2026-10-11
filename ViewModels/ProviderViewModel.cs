using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.Storage;

namespace BoxeZone.ViewModels
{
    public enum UploadState
    {
        Idle,
        Uploading,
        Done,
    }

    /// <summary>State of one provider: switched on/off and the upload shown in its drop zone.</summary>
    public partial class ProviderViewModel : ObservableObject
    {
        private static readonly ResourceLoader Strings = new();

        public ProviderViewModel(string id, string name, string initial)
        {
            Id = id;
            Name = name;
            Initial = initial;
            FileName = string.Empty;
        }

        public string Id { get; }

        public string Name { get; }

        /// <summary>Letter shown in the 28 px provider icon.</summary>
        public string Initial { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Status))]
        public partial bool IsEnabled { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Status), nameof(IsIdle), nameof(IsUploading), nameof(IsDone))]
        public partial UploadState State { get; set; }

        /// <summary>Upload progress, 0 to 100.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ProgressLabel))]
        public partial double Progress { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DoneLabel))]
        public partial string FileName { get; set; }

        public bool IsIdle => State == UploadState.Idle;

        public bool IsUploading => State == UploadState.Uploading;

        public bool IsDone => State == UploadState.Done;

        public string ProgressLabel => $"{Progress:0}%";

        public string DoneLabel => string.Format(Strings.GetString("UploadDoneFormat"), FileName);

        public string Status =>
            !IsEnabled ? Strings.GetString("StatusOff")
            : IsUploading ? Strings.GetString("StatusUploading")
            : Strings.GetString("StatusReady");

        // TEMP (step 2): fakes the upload so the drop zone states can be seen.
        // Replaced by IStorageProvider.UploadAsync in step 5.
        public async Task SimulateUploadAsync(IReadOnlyList<StorageFile> files)
        {
            if (IsUploading || files.Count == 0)
            {
                return;
            }

            foreach (StorageFile file in files)
            {
                FileName = file.Name;
                Progress = 0;
                State = UploadState.Uploading;

                while (Progress < 100)
                {
                    await Task.Delay(90);
                    Progress = Math.Min(100, Progress + 4 + Random.Shared.Next(7));
                }
            }

            State = UploadState.Done;
        }
    }
}
