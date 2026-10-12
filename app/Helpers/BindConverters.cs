using Microsoft.UI.Xaml;

namespace BoxeZone.Helpers
{
    /// <summary>Functions for x:Bind (it converts bool to Visibility, but not the inverse).</summary>
    public static class BindConverters
    {
        public static Visibility CollapsedWhen(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility VisibleWhenText(string? value) =>
            string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
    }
}
