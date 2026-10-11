using System;
using System.Linq;
using System.Numerics;
using BoxeZone.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace BoxeZone.Views.Controls
{
    /// <summary>One provider: icon, name, status, switch and its drop zone.</summary>
    public sealed partial class ProviderRow : UserControl
    {
        public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
            nameof(ViewModel),
            typeof(ProviderViewModel),
            typeof(ProviderRow),
            new PropertyMetadata(null, (d, _) => ((ProviderRow)d).Bindings.Update()));

        private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(180);

        public ProviderRow()
        {
            InitializeComponent();
            SetDropZoneShowAnimation();
        }

        public ProviderViewModel? ViewModel
        {
            get => (ProviderViewModel?)GetValue(ViewModelProperty);
            set => SetValue(ViewModelProperty, value);
        }

        /// <summary>Fade in and slide down 4 px when the zone appears (prototype "dzIn", 180 ms ease-out).</summary>
        private void SetDropZoneShowAnimation()
        {
            var compositor = ElementCompositionPreview.GetElementVisual(DropZone).Compositor;
            var easeOut = compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0.58f, 1));

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.Target = "Opacity";
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, easeOut);
            fade.Duration = ShowDuration;

            var slide = compositor.CreateVector3KeyFrameAnimation();
            slide.Target = "Translation";
            slide.InsertKeyFrame(0, new Vector3(0, -4, 0));
            slide.InsertKeyFrame(1, Vector3.Zero, easeOut);
            slide.Duration = ShowDuration;

            var show = compositor.CreateAnimationGroup();
            show.Add(fade);
            show.Add(slide);

            ElementCompositionPreview.SetIsTranslationEnabled(DropZone, true);
            ElementCompositionPreview.SetImplicitShowAnimation(DropZone, show);
        }

        private void DropZone_PointerEntered(object sender, PointerRoutedEventArgs e) =>
            VisualStateManager.GoToState(this, "PointerOver", true);

        private void DropZone_PointerExited(object sender, PointerRoutedEventArgs e) =>
            VisualStateManager.GoToState(this, "Normal", true);

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems) || ViewModel is null)
            {
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = string.Format(new ResourceLoader().GetString("DropCaptionFormat"), ViewModel.Name);
            VisualStateManager.GoToState(this, "DragTarget", true);
        }

        private void DropZone_DragLeave(object sender, DragEventArgs e) =>
            VisualStateManager.GoToState(this, "Normal", true);

        private async void DropZone_Drop(object sender, DragEventArgs e)
        {
            VisualStateManager.GoToState(this, "Normal", true);
            if (ViewModel is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            // The drag source must stay alive until the items are read.
            var deferral = e.GetDeferral();
            var items = await e.DataView.GetStorageItemsAsync();
            deferral.Complete();

            // Folders are ignored for now; only files are uploaded.
            var files = items.OfType<StorageFile>().ToList();
            await ViewModel.SimulateUploadAsync(files);
        }
    }
}
