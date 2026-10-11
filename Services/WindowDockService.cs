using System;
using System.ComponentModel;
using System.Linq;
using BoxeZone.Helpers;
using BoxeZone.ViewModels;
using BoxeZone.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace BoxeZone.Services
{
    public enum DockState
    {
        Hidden,
        Normal,

        /// <summary>Thin strip on the right edge of the screen.</summary>
        Docked,

        /// <summary>Docked, opened into the compact drop panel because a file was dragged over the strip.</summary>
        DockedExpanded,
    }

    /// <summary>
    /// Owns the widget's window state (Normal / Docked / Hidden), where the window sits,
    /// and the side tab that drags it. All positions are physical pixels of the visible frame.
    /// </summary>
    public sealed class WindowDockService
    {
        private const int ScreenMargin = 16;

        // Releasing the tab with the widget's right edge this close to the screen edge docks it.
        private const int SnapDistance = 24;

        private static readonly TimeSpan UploadDoneHold = TimeSpan.FromSeconds(1.5);
        private static readonly TimeSpan CursorLeaveGrace = TimeSpan.FromMilliseconds(300);

        private readonly MainWindow _main;
        private readonly TabWindow _tab;
        private readonly MainViewModel _viewModel;
        private readonly SettingsService _settingsService;
        private readonly DispatcherQueueTimer _collapseTimer;
        private readonly DispatcherQueueTimer _collapseAfterUploadTimer;

        private RectInt32? _normalBounds;
        private RectInt32 _dragStartBounds;
        private RectInt32 _dockWorkArea;
        private int _dockCenterY;
        private DateTime _holdUntil;
        private DateTime _cursorLastInside;
        private bool _tabShownOnce;
        private bool _isDragging;
        private bool _isExiting;

        public WindowDockService(MainWindow main, TabWindow tab, MainViewModel viewModel, SettingsService settingsService)
        {
            _main = main;
            _tab = tab;
            _viewModel = viewModel;
            _settingsService = settingsService;

            // The widget is owned by the tab so it always stays above it and covers the tab's tucked part.
            Win32.SetOwner(_main.GetHwnd(), _tab.GetHwnd());

            _main.ContentSizeChanged += (_, _) => UpdatePlacement();
            _main.StripClicked += (_, _) => ShowNormal();
            _main.StripDragEntered += (_, _) => Expand();
            _main.AppWindow.Closing += AppWindow_Closing;
            _tab.AppWindow.Closing += AppWindow_Closing;

            _tab.TabHandle.DragStarted += (_, _) =>
            {
                _isDragging = true;
                _dragStartBounds = _main.GetVisibleBounds();
            };
            _tab.TabHandle.DragMoved += TabHandle_DragMoved;
            _tab.TabHandle.DragCompleted += (_, _) => OnDragCompleted();

            foreach (ProviderViewModel provider in _viewModel.Providers)
            {
                provider.PropertyChanged += Provider_PropertyChanged;
            }

            _viewModel.Settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsViewModel.AlwaysOnTop))
                {
                    ApplyTopmost();
                }
            };

            _collapseTimer = _main.DispatcherQueue.CreateTimer();
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(150);
            _collapseTimer.Tick += (_, _) => CollapseIfIdle();

            _collapseAfterUploadTimer = _main.DispatcherQueue.CreateTimer();
            _collapseAfterUploadTimer.Interval = UploadDoneHold;
            _collapseAfterUploadTimer.IsRepeating = false;
            _collapseAfterUploadTimer.Tick += (_, _) =>
            {
                if (State == DockState.Normal && !_viewModel.Providers.Any(p => p.IsUploading))
                {
                    Dock();
                }
            };
        }

        public DockState State { get; private set; } = DockState.Hidden;

        /// <summary>
        /// First show: back where the widget was last time (docked or not), or the default corner.
        /// <paramref name="startHidden"/> keeps it in the tray; the next show uses the restored place.
        /// </summary>
        public void Start(bool startHidden = false)
        {
            WindowSettings? saved = _settingsService.Settings.Window;
            if (saved is not null && saved.Width > 0 && saved.Height > 0)
            {
                var bounds = new RectInt32(saved.X, saved.Y, saved.Width, saved.Height);

                // A monitor may have been unplugged since: only reuse a place that is still on a screen.
                if (DisplayArea.GetFromRect(bounds, DisplayAreaFallback.None) is not null)
                {
                    _normalBounds = bounds;
                    if (saved.WasDocked && !startHidden)
                    {
                        _dockCenterY = saved.DockCenterY;
                        _dockWorkArea = GetWorkArea(bounds);
                        SetState(DockState.Docked);
                        return;
                    }
                }
            }

            if (!startHidden)
            {
                SetState(DockState.Normal);
            }
        }

        public void ShowNormal() => SetState(DockState.Normal);

        public void Dock()
        {
            if (State != DockState.Normal)
            {
                return;
            }

            RectInt32 bounds = _main.GetVisibleBounds();
            _dockCenterY = bounds.Y + (bounds.Height / 2);
            _dockWorkArea = GetWorkArea(bounds);
            SetState(DockState.Docked);
        }

        public void Hide() => SetState(DockState.Hidden);

        /// <summary>The global shortcut: hides the widget if it is open, otherwise brings it back.</summary>
        public void ToggleNormalHidden()
        {
            if (State == DockState.Normal)
            {
                Hide();
            }
            else
            {
                ShowNormal();
            }
        }

        /// <summary>Closes both windows for real; any other close request only hides them.</summary>
        public void Shutdown()
        {
            _isExiting = true;
            _collapseTimer.Stop();
            _collapseAfterUploadTimer.Stop();
            _main.Close();
            _tab.Close();
        }

        private void Expand()
        {
            if (State == DockState.Docked)
            {
                SetState(DockState.DockedExpanded);
            }
        }

        private void SetState(DockState state)
        {
            DockState previous = State;
            State = state;
            _collapseTimer.Stop();
            _collapseAfterUploadTimer.Stop();

            if (state != DockState.Normal)
            {
                // Settings is only reachable from the open widget; come back to the widget next time.
                _main.ShowWidgetPage();
            }

            if (state == DockState.Hidden)
            {
                _tab.AppWindow.Hide();
                _main.AppWindow.Hide();
                return;
            }

            _main.SetMode(state switch
            {
                DockState.Normal => MainWindowMode.Widget,
                DockState.Docked => MainWindowMode.Strip,
                _ => MainWindowMode.Compact,
            });
            UpdatePlacement();

            if (state == DockState.Normal)
            {
                if (!_tabShownOnce)
                {
                    // Activate once so the tab's XAML content loads; it never takes focus (WS_EX_NOACTIVATE).
                    _tab.Activate();
                    _tabShownOnce = true;
                }

                _tab.AppWindow.Show(false);
                _main.AppWindow.Show(true);
                _main.Activate();
            }
            else
            {
                _tab.AppWindow.Hide();
                _main.AppWindow.Show(false);
            }

            ApplyTopmost();

            // Frame insets are only measurable once the window is shown.
            UpdatePlacement();

            if (state == DockState.DockedExpanded)
            {
                _cursorLastInside = DateTime.UtcNow;
                _collapseTimer.Start();
            }

            if (state != previous && state is DockState.Normal or DockState.Docked)
            {
                SavePlacement();
            }
        }

        /// <summary>
        /// "Sempre no topo" applies to the open widget. The docked strip and the compact panel
        /// always stay on top: covered by other windows they could not be reached at all.
        /// Being owned by the tab window makes Windows drop the topmost style, so it is set on every show.
        /// </summary>
        private void ApplyTopmost()
        {
            bool topmost = State != DockState.Normal || _viewModel.Settings.AlwaysOnTop;
            foreach (Microsoft.UI.Xaml.Window window in new Microsoft.UI.Xaml.Window[] { _main, _tab })
            {
                // Keep the presenter in sync, or WinUI puts the window back on top when it activates.
                if (window.AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsAlwaysOnTop = topmost;
                }

                Win32.SetTopmost(window.GetHwnd(), topmost);
            }
        }

        /// <summary>Sizes the window to its content and places it for the current state.</summary>
        private void UpdatePlacement()
        {
            // While dragging the window follows the cursor; crossing onto a monitor with another
            // DPI resizes it mid-drag, and placement is settled when the drag ends.
            if (State == DockState.Hidden || _isDragging)
            {
                return;
            }

            SizeInt32 size = _main.GetSizeForContent();
            int width = size.Width;
            int height = size.Height;

            if (State == DockState.Normal)
            {
                RectInt32 workArea;
                int x;
                int y;
                if (_normalBounds is RectInt32 previous)
                {
                    // Grow away from the nearer screen edges: a widget in the lower half keeps its
                    // bottom, one in the right half keeps its right side (Settings is wider).
                    workArea = GetWorkArea(previous);
                    bool keepBottom = previous.Y + (previous.Height / 2) > workArea.Y + (workArea.Height / 2);
                    bool keepRight = previous.X + (previous.Width / 2) > workArea.X + (workArea.Width / 2);
                    x = keepRight ? previous.X + previous.Width - width : previous.X;
                    y = keepBottom ? previous.Y + previous.Height - height : previous.Y;
                }
                else
                {
                    workArea = DisplayArea.GetFromWindowId(_main.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                    int margin = Scale(ScreenMargin);
                    x = workArea.X + workArea.Width - margin - width;
                    y = workArea.Y + workArea.Height - margin - height;
                }

                RectInt32 bounds = ClampToWorkArea(new RectInt32(x, y, width, height), workArea);
                _main.SetVisibleBounds(bounds);
                bool moved = _normalBounds is not RectInt32 old || old.X != bounds.X || old.Y != bounds.Y || old.Width != bounds.Width || old.Height != bounds.Height;
                _normalBounds = bounds;
                PlaceTab(bounds);
                if (moved)
                {
                    SavePlacement();
                }
            }
            else
            {
                int y = Math.Clamp(
                    _dockCenterY - (height / 2),
                    _dockWorkArea.Y,
                    _dockWorkArea.Y + _dockWorkArea.Height - height);
                _main.SetVisibleBounds(new RectInt32(_dockWorkArea.X + _dockWorkArea.Width - width, y, width, height));
            }
        }

        private void SavePlacement()
        {
            if (_normalBounds is not RectInt32 bounds)
            {
                return;
            }

            _settingsService.Settings.Window = new WindowSettings
            {
                WasDocked = State is DockState.Docked or DockState.DockedExpanded,
                X = bounds.X,
                Y = bounds.Y,
                Width = bounds.Width,
                Height = bounds.Height,
                DockCenterY = _dockCenterY,
            };
            _settingsService.Save();
        }

        private void PlaceTab(RectInt32 widget)
        {
            int tabHeight = Scale(TabWindow.TabHeight);
            _tab.MoveVisible(
                widget.X - Scale(TabWindow.VisibleWidth),
                widget.Y + ((widget.Height - tabHeight) / 2));
        }

        private void TabHandle_DragMoved(object? sender, PointInt32 offset)
        {
            var bounds = new RectInt32(
                _dragStartBounds.X + offset.X,
                _dragStartBounds.Y + offset.Y,
                _dragStartBounds.Width,
                _dragStartBounds.Height);
            _main.MoveVisible(bounds.X, bounds.Y);
            PlaceTab(bounds);
        }

        private void OnDragCompleted()
        {
            _isDragging = false;
            RectInt32 bounds = _main.GetVisibleBounds();
            var center = new PointInt32(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
            DisplayArea? display = DisplayArea.GetFromPoint(center, DisplayAreaFallback.None);

            if (display is null)
            {
                // More than half off every screen: thrown away. It comes back where it was before the drag.
                Hide();
                return;
            }

            // Only a real screen edge docks; the edge between two monitors does not.
            RectInt32 screen = display.OuterBounds;
            bool isScreenEdge = DisplayArea.GetFromPoint(
                new PointInt32(screen.X + screen.Width, center.Y), DisplayAreaFallback.None) is null;
            RectInt32 workArea = display.WorkArea;

            if (isScreenEdge && bounds.X + bounds.Width > workArea.X + workArea.Width - Scale(SnapDistance))
            {
                Dock();
            }
            else
            {
                _normalBounds = ClampToWorkArea(bounds, workArea);
                UpdatePlacement();
                SavePlacement();
            }
        }

        private void CollapseIfIdle()
        {
            if (State != DockState.DockedExpanded)
            {
                _collapseTimer.Stop();
                return;
            }

            DateTime now = DateTime.UtcNow;
            RectInt32 bounds = _main.GetVisibleBounds();
            Win32.GetCursorPos(out Win32.POINT cursor);
            bool isCursorInside =
                cursor.X >= bounds.X && cursor.X < bounds.X + bounds.Width &&
                cursor.Y >= bounds.Y && cursor.Y < bounds.Y + bounds.Height;

            if (isCursorInside)
            {
                _cursorLastInside = now;
            }

            if (_viewModel.Providers.Any(p => p.IsUploading) || now < _holdUntil || now - _cursorLastInside < CursorLeaveGrace)
            {
                return;
            }

            SetState(DockState.Docked);
        }

        private void Provider_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ProviderViewModel.State) || sender is not ProviderViewModel { IsDone: true })
            {
                return;
            }

            // Keep the compact panel open for a moment so "Enviado" can be seen.
            _holdUntil = DateTime.UtcNow + UploadDoneHold;

            // "Recolher após o envio": back to the edge once "Enviado" has been on screen for a moment.
            if (State == DockState.Normal && _viewModel.Settings.CollapseAfterUpload)
            {
                _collapseAfterUploadTimer.Stop();
                _collapseAfterUploadTimer.Start();
            }
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_isExiting)
            {
                return;
            }

            // Alt+F4 hides, like the "X"; only "Sair" in the tray menu exits.
            args.Cancel = true;
            Hide();
        }

        private RectInt32 ClampToWorkArea(RectInt32 bounds, RectInt32 workArea)
        {
            // Leave room on the left for the tab.
            int minX = workArea.X + Scale(TabWindow.VisibleWidth);
            int maxX = Math.Max(minX, workArea.X + workArea.Width - bounds.Width);
            int maxY = Math.Max(workArea.Y, workArea.Y + workArea.Height - bounds.Height);
            return new RectInt32(
                Math.Clamp(bounds.X, minX, maxX),
                Math.Clamp(bounds.Y, workArea.Y, maxY),
                bounds.Width,
                bounds.Height);
        }

        private static RectInt32 GetWorkArea(RectInt32 bounds) =>
            DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;

        private int Scale(double value) => (int)Math.Round(value * _main.GetScale());
    }
}
