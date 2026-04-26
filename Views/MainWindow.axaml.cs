using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using InfraSftp.Models;
using InfraSftp.Services;
using InfraSftp.ViewModels;
using System.IO;
using TabItem = InfraSftp.ViewModels.TabItem;

namespace InfraSftp.Views;

// Payload travelling inside an in-process file drag. Carries the panel of
// origin so the drop handler can reject same-panel drops to *the panel itself*
// (those become #6 intra-panel folder-drop handled separately) and accept
// cross-panel drops as a transfer.
internal sealed record FileDragPayload(string SourcePanel, IReadOnlyList<FileNode> Files);

public partial class MainWindow : Window
{
    // In-process format — TabItem references stay inside this app; no serialization.
    private static readonly DataFormat<TabItem> TabFormat =
        DataFormat.CreateInProcessFormat<TabItem>("InfraSftp.Tab");

    // File drag format. Same in-process trick — the payload IS the live list
    // of FileNode plus panel origin, no serialization.
    private static readonly DataFormat<FileDragPayload> FilesFormat =
        DataFormat.CreateInProcessFormat<FileDragPayload>("InfraSftp.Files");

    private readonly SettingsService _settingsService = new();

    private DragVisualWindow? _dragVisual;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();

        RestoreWindowState();
        Closing += OnWindowClosing;

        // Clicking anywhere in the window triggers a reconnect attempt for
        // any remote tab that has silently dropped (idle timeout). Registered
        // with handledEventsToo so children that consume the event (buttons,
        // text boxes) don't starve us.
        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        DragDrop.AddDragOverHandler(this, OnGlobalDragOver);
        DragDrop.AddDragOverHandler(LeftPanelBorder, OnPanelDragOver);
        DragDrop.AddDropHandler(LeftPanelBorder, OnLeftPanelDrop);
        DragDrop.AddDragOverHandler(RightPanelBorder, OnPanelDragOver);
        DragDrop.AddDropHandler(RightPanelBorder, OnRightPanelDrop);

        // Multi-select + drag. Registered Tunnel + handledEventsToo so the
        // file-row Button doesn't swallow PointerPressed before we read
        // modifiers — a plain Click handler doesn't expose Ctrl/Shift state
        // on Avalonia. PointerMoved/Released cover delayed selection (Explorer
        // semantics) and drag threshold detection.
        foreach (var (items, panel) in new[] { (LeftFilesItems, "Left"), (RightFilesItems, "Right") })
        {
            var p = panel; // capture
            items.AddHandler(InputElement.PointerPressedEvent,
                (s, e) => OnFileRowPressed(p, e),
                RoutingStrategies.Tunnel, handledEventsToo: true);
            items.AddHandler(InputElement.PointerMovedEvent,
                (s, e) => OnFileRowMoved(p, e),
                RoutingStrategies.Bubble, handledEventsToo: true);
            items.AddHandler(InputElement.PointerReleasedEvent,
                (s, e) => OnFileRowReleased(p, e),
                RoutingStrategies.Bubble, handledEventsToo: true);
        }

        // Tab drag wiring. Must register with handledEventsToo: true because the
        // tab Button handles PointerPressed/Moved internally for its own click
        // tracking — a plain PointerPressed="..." XAML handler never fires on
        // those events, which was why the drag appeared broken.
        foreach (var panel in new[] { LeftTabsPanel, RightTabsPanel })
        {
            panel.AddHandler(InputElement.PointerPressedEvent, OnTabPointerPressed,
                RoutingStrategies.Bubble, handledEventsToo: true);
            panel.AddHandler(InputElement.PointerMovedEvent, OnTabPointerMoved,
                RoutingStrategies.Bubble, handledEventsToo: true);
            panel.AddHandler(InputElement.PointerReleasedEvent, OnTabPointerReleased,
                RoutingStrategies.Bubble, handledEventsToo: true);
        }
    }

    // ── Menu / tab-dock click handlers ─────────────────────────
    private void ToggleMenu(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.IsMenuOpen = !vm.IsMenuOpen;
    }

    private void SwitchToLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.BottomTab = "Log";
    }

    private void SwitchToTransfers(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.BottomTab = "Transfers";
    }

    private void OpenCreateModal(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.OpenCreateModalCommand.Execute(null);
    }

    // ── Panel focus tracking (for Ctrl+W / F5) ─────────────────
    private void OnLeftPanelFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.FocusedPanel = "Left";
    }

    private void OnRightPanelFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.FocusedPanel = "Right";
    }

    // ── Tab drag source ────────────────────────────────────────
    private Point? _dragStartPoint;
    private PointerPressedEventArgs? _dragStartEvent;
    private TabItem? _dragStartTab;

    // Walk the visual tree upwards looking for a Button that matches the predicate.
    private static Button? FindAncestorButton(Visual? v, Func<Button, bool> predicate)
    {
        while (v != null)
        {
            if (v is Button b && predicate(b)) return b;
            v = v.GetVisualParent();
        }
        return null;
    }

    private void OnTabPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual src) return;

        // Clicking the × close button must NOT start a drag.
        if (FindAncestorButton(src, b => b.Classes.Contains("tab-close")) != null) return;

        var tabBtn = FindAncestorButton(src, b => b.Classes.Contains("tab-btn"));
        if (tabBtn?.DataContext is not TabItem tab || !tab.IsRemote) return;

        var properties = e.GetCurrentPoint(tabBtn).Properties;
        if (!properties.IsLeftButtonPressed) return;

        _dragStartPoint = e.GetPosition(null);
        _dragStartEvent = e;
        _dragStartTab = tab;
    }

    private async void OnTabPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStartPoint == null || _dragStartEvent == null || _dragStartTab == null) return;

        var properties = e.GetCurrentPoint(null).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            // The button was released outside our Released handler path.
            _dragStartPoint = null;
            _dragStartEvent = null;
            _dragStartTab = null;
            return;
        }

        var currentPoint = e.GetPosition(null);
        var distance = Math.Abs(currentPoint.X - _dragStartPoint.Value.X)
                     + Math.Abs(currentPoint.Y - _dragStartPoint.Value.Y);

        if (distance > 3)
        {
            var startEvent = _dragStartEvent;
            var tab = _dragStartTab;
            _dragStartPoint = null;
            _dragStartEvent = null;
            _dragStartTab = null;

            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(TabFormat, tab));
            
            _dragVisual = new DragVisualWindow();
            _dragVisual.SetContent("📑", tab.Name);
            var pos = currentPoint;
            var screenPos = this.PointToScreen(pos);
            _dragVisual.Position = new Avalonia.PixelPoint(screenPos.X + 15, screenPos.Y + 15);
            _dragVisual.Show(this);

            this.Cursor = new Cursor(StandardCursorType.Hand);
            try
            {
                await DragDrop.DoDragDropAsync(startEvent, data, DragDropEffects.Move);
            }
            finally
            {
                this.Cursor = Cursor.Default;
                _dragVisual.Close();
                _dragVisual = null;
            }
        }
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStartPoint = null;
        _dragStartEvent = null;
        _dragStartTab = null;
    }

    private void OnGlobalDragOver(object? sender, DragEventArgs e)
    {
        if (_dragVisual != null)
        {
            var pos = e.GetPosition(this);
            var screenPos = this.PointToScreen(pos);
            _dragVisual.Position = new Avalonia.PixelPoint(screenPos.X + 15, screenPos.Y + 15);
        }
    }

    // ── Drop target ────────────────────────────────────────────
    // The same Border accepts three drag scenarios:
    //   - TabFormat: user is rearranging tabs across panels.
    //   - FilesFormat to *empty space* in the other panel: cross-panel transfer.
    //   - FilesFormat to a folder *row* (any panel): same-panel move OR
    //     cross-panel transfer-into-folder. Detected by walking up from
    //     e.Source to a file-row Button whose DataContext is an IsDirectory FileNode.
    //
    // Drag onto a file-row that is a folder takes priority over the panel-level
    // empty-area drop.

    private static FileNode? FindFolderRowAt(object? source)
    {
        if (source is not Visual v) return null;
        var btn = FindAncestorButton(v, b => b.Classes.Contains("file-row"));
        if (btn?.DataContext is FileNode f && (f.IsDirectory || f.Name == ".."))
            return f;
        return null;
    }

    private void OnPanelDragOver(object? sender, DragEventArgs e)
    {
        var thisPanel = ReferenceEquals(sender, LeftPanelBorder) ? "Left" : "Right";
        var files = e.DataTransfer.TryGetValue(FilesFormat);

        if (files != null)
        {
            var folderTarget = FindFolderRowAt(e.Source);
            if (folderTarget != null)
            {
                // Don't allow dropping a folder onto itself.
                if (files.Files.Any(f => f.Name == folderTarget.Name && folderTarget.Name != ".."))
                {
                    e.DragEffects = DragDropEffects.None;
                    return;
                }
                e.DragEffects = files.SourcePanel == thisPanel
                    ? DragDropEffects.Move      // intra-panel: rename = move.
                    : DragDropEffects.Copy;     // cross-panel into folder: copy.
                return;
            }

            // Empty-area drop: only valid across panels.
            e.DragEffects = files.SourcePanel != thisPanel
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            return;
        }

        e.DragEffects = e.DataTransfer.Contains(TabFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private void HandlePanelDrop(string thisPanel, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var files = e.DataTransfer.TryGetValue(FilesFormat);
        if (files != null)
        {
            var folder = FindFolderRowAt(e.Source);
            if (folder != null)
            {
                if (files.SourcePanel == thisPanel)
                {
                    if (folder.Name == "..") { _ = vm.MoveFilesToFolderAsync(thisPanel, files.Files, ".."); return; }
                    if (files.Files.Any(f => f.Name == folder.Name)) return;
                    _ = vm.MoveFilesToFolderAsync(thisPanel, files.Files, folder.Name);
                }
                else
                {
                    var sub = folder.Name == ".." ? ".." : folder.Name;
                    _ = vm.TransferDraggedFilesToFolderAsync(files.Files, files.SourcePanel, thisPanel, sub);
                }
                return;
            }

            if (files.SourcePanel != thisPanel)
            {
                _ = vm.TransferDraggedFilesAsync(files.Files, files.SourcePanel, thisPanel);
            }
            return;
        }

        var tab = e.DataTransfer.TryGetValue(TabFormat);
        if (tab != null)
        {
            if (thisPanel == "Left") vm.MoveTabToLeftCommand.Execute(tab);
            else vm.MoveTabToRightCommand.Execute(tab);
        }
    }

    private void OnLeftPanelDrop(object? sender, DragEventArgs e)  => HandlePanelDrop("Left", e);
    private void OnRightPanelDrop(object? sender, DragEventArgs e) => HandlePanelDrop("Right", e);

    // ── File row pointer pipeline (select + drag) ──────────────
    // Selection + drag share a state machine because Explorer-style "delayed
    // selection" needs to know whether the user pressed-then-dragged (drag
    // wins, selection unchanged) vs pressed-then-released (selection wins).
    //
    // - On press over an already-selected row (no modifier): defer the
    //   selection collapse so a drag can still carry the whole multi-selection.
    // - On press over an unselected row OR with Ctrl/Shift: apply selection
    //   immediately (so the drag carries what the user clicked).
    // - On move past the drag threshold: start a DragDrop with the current
    //   selection and discard any pending selection collapse.
    // - On release without crossing the threshold: apply the deferred
    //   selection (or just clear pending state).
    private Point? _fileDragStart;
    private PointerPressedEventArgs? _fileDragStartEvent;
    private string _fileDragPanel = "";
    private FileNode? _filePendingPlainSelect;          // row to collapse-select on release
    private string _filePendingPlainSelectPanel = "";

    private void OnFileRowPressed(string panel, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var props = e.GetCurrentPoint(null).Properties;
        if (!props.IsLeftButtonPressed) return;

        if (e.Source is not Visual src) return;
        var rowBtn = FindAncestorButton(src, b => b.Classes.Contains("file-row"));
        if (rowBtn?.DataContext is not FileNode file) return;
        if (file.Name == "..") return; // ".." is navigation only.

        var ctrl  = (e.KeyModifiers & KeyModifiers.Control) != 0;
        var shift = (e.KeyModifiers & KeyModifiers.Shift)   != 0;

        // Decide whether to defer selection. A plain press on an already-
        // selected row defers — every other case applies selection now.
        if (file.IsSelected && !ctrl && !shift)
        {
            _filePendingPlainSelect = file;
            _filePendingPlainSelectPanel = panel;
        }
        else
        {
            vm.HandleFileRowClick(panel, file, e.KeyModifiers);
            _filePendingPlainSelect = null;
            _filePendingPlainSelectPanel = "";
        }

        _fileDragStart = e.GetPosition(null);
        _fileDragStartEvent = e;
        _fileDragPanel = panel;
    }

    private async void OnFileRowMoved(string panel, PointerEventArgs e)
    {
        if (_fileDragStart == null || _fileDragStartEvent == null) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (panel != _fileDragPanel) return;

        var props = e.GetCurrentPoint(null).Properties;
        if (!props.IsLeftButtonPressed)
        {
            ResetFileDragState();
            return;
        }

        var p = e.GetPosition(null);
        var manhattan = Math.Abs(p.X - _fileDragStart.Value.X) + Math.Abs(p.Y - _fileDragStart.Value.Y);
        if (manhattan <= 4) return;

        // Build the drag set from the panel selection. If nothing is selected
        // (defensive — pressed handler should have set something), bail.
        var fallback = _filePendingPlainSelect;
        var batch = vm.GetSelection(panel, fallback);
        if (batch.Count == 0) { ResetFileDragState(); return; }

        var startEvent = _fileDragStartEvent;
        ResetFileDragState();
        _filePendingPlainSelect = null; // drag consumed it.
        _filePendingPlainSelectPanel = "";

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(FilesFormat, new FileDragPayload(panel, batch)));
        try
        {
            _dragVisual = new DragVisualWindow();
            var icon = batch.Count == 1 ? batch[0].Icon : "📑";
            var text = batch.Count == 1 ? batch[0].Name : $"{batch.Count} items";
            _dragVisual.SetContent(icon, text);
            var pos = p;
            var screenPos = this.PointToScreen(pos);
            _dragVisual.Position = new Avalonia.PixelPoint(screenPos.X + 15, screenPos.Y + 15);
            _dragVisual.Show(this);

            this.Cursor = new Cursor(StandardCursorType.Hand);
            await DragDrop.DoDragDropAsync(startEvent, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch { }
        finally
        {
            this.Cursor = Cursor.Default;
            if (_dragVisual != null) { _dragVisual.Close(); _dragVisual = null; }
        }
    }

    private void OnFileRowReleased(string panel, PointerReleasedEventArgs e)
    {
        // Pure click on an already-selected row: collapse selection now (the
        // drag never started, so Explorer-style we honour the "single click
        // selects this row" intent).
        if (_filePendingPlainSelect != null
            && _filePendingPlainSelectPanel == panel
            && DataContext is MainWindowViewModel vm)
        {
            vm.HandleFileRowClick(panel, _filePendingPlainSelect, KeyModifiers.None);
        }
        ResetFileDragState();
        _filePendingPlainSelect = null;
        _filePendingPlainSelectPanel = "";
    }

    private void ResetFileDragState()
    {
        _fileDragStart = null;
        _fileDragStartEvent = null;
        _fileDragPanel = "";
    }

    // ── File Row Double Tapped ─────────────────────────────────
    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is Models.FileNode file)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.FileActionCommand.Execute(file);
            }
        }
    }

    // ── Drag Selection ───────────────────────────────────────────
    private Point? _listDragStartPoint;
    private ScrollViewer? _listActiveScrollViewer;
    private Avalonia.Controls.Shapes.Rectangle? _listActiveDragRect;

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        
        var rect = sv.Name == "LeftScrollViewer" ? this.FindControl<Avalonia.Controls.Shapes.Rectangle>("LeftDragRect") : this.FindControl<Avalonia.Controls.Shapes.Rectangle>("RightDragRect");
        if (rect == null) return;
        
        var canvas = rect.Parent as Visual;
        if (canvas == null) return;

        var point = e.GetPosition(canvas);
        if (e.GetCurrentPoint(sv).Properties.IsLeftButtonPressed)
        {
            _listDragStartPoint = point;
            _listActiveScrollViewer = sv;
            _listActiveDragRect = rect;

            if (DataContext is MainWindowViewModel vm)
            {
                var tab = sv.Name == "LeftScrollViewer" ? vm.ActiveLeftTab : vm.ActiveRightTab;
                if (tab != null && tab.Files != null)
                {
                    foreach (var f in tab.Files) f.IsSelected = false;
                }
            }

            Canvas.SetLeft(_listActiveDragRect, point.X);
            Canvas.SetTop(_listActiveDragRect, point.Y);
            _listActiveDragRect.Width = 0;
            _listActiveDragRect.Height = 0;
            _listActiveDragRect.IsVisible = true;
            
            e.Handled = true;
        }
    }

    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_listDragStartPoint == null || _listActiveScrollViewer == null || _listActiveDragRect == null) return;
        
        var canvas = _listActiveDragRect.Parent as Visual;
        if (canvas == null) return;

        var point = e.GetPosition(canvas);
        var start = _listDragStartPoint.Value;

        var x = Math.Min(point.X, start.X);
        var y = Math.Min(point.Y, start.Y);
        var w = Math.Abs(point.X - start.X);
        var h = Math.Abs(point.Y - start.Y);

        Canvas.SetLeft(_listActiveDragRect, x);
        Canvas.SetTop(_listActiveDragRect, y);
        _listActiveDragRect.Width = w;
        _listActiveDragRect.Height = h;
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_listDragStartPoint == null || _listActiveScrollViewer == null || _listActiveDragRect == null) return;

        var canvas = _listActiveDragRect.Parent as Visual;
        if (canvas == null) return;

        var point = e.GetPosition(canvas);
        var start = _listDragStartPoint.Value;

        var rect = new Rect(
            Math.Min(point.X, start.X),
            Math.Min(point.Y, start.Y),
            Math.Abs(point.X - start.X),
            Math.Abs(point.Y - start.Y));

        if (rect.Width > 5 && rect.Height > 5)
        {
            var itemsControl = _listActiveScrollViewer.Name == "LeftScrollViewer" 
                ? this.FindControl<ItemsControl>("LeftFilesItems") 
                : this.FindControl<ItemsControl>("RightFilesItems");

            if (itemsControl != null && DataContext is MainWindowViewModel vm)
            {
                var tab = _listActiveScrollViewer.Name == "LeftScrollViewer" ? vm.ActiveLeftTab : vm.ActiveRightTab;
                if (tab != null && tab.Files != null)
                {
                    var containers = itemsControl.GetRealizedContainers();
                    foreach (var container in containers)
                    {
                        var bounds = container.Bounds;
                        var transform = container.TransformToVisual(canvas);
                        if (transform.HasValue)
                        {
                            var localRect = new Rect(0, 0, bounds.Width, bounds.Height);
                            var transformedBounds = localRect.TransformToAABB(transform.Value);
                            if (rect.Intersects(transformedBounds))
                            {
                                if (container.DataContext is Models.FileNode file && file.Name != "..")
                                {
                                    file.IsSelected = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        if (_listActiveDragRect != null) _listActiveDragRect.IsVisible = false;
        _listDragStartPoint = null;
        _listActiveScrollViewer = null;
        _listActiveDragRect = null;
    }

    // ── Hamburger dismiss layer ────────────────────────────────
    // Using a Border (instead of a stretched Button) avoids the FluentTheme
    // pointerover overlay that darkens the whole window when the menu is open.
    private void OnDismissMenuLayer(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.IsMenuOpen = false;
    }

    // ── Profile Import / Export ────────────────────────────────
    // File pickers need a TopLevel reference, which lives here in the View
    // (the VM must stay ignorant of Avalonia controls). Once we have the
    // chosen file, we hand a Stream back to the VM which owns the real work.
    private async void OnExportProfilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IsMenuOpen = false;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export profiles",
            SuggestedFileName = $"infrasftp-profiles-{DateTime.Now:yyyy-MM-dd}.json",
            DefaultExtension = "json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
            }
        });
        if (file == null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await vm.ExportProfilesAsync(stream);
        }
        catch (Exception ex) { vm.Log($"⚠ Export failed: {ex.Message}"); }
    }

    private async void OnImportProfilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IsMenuOpen = false;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import profiles",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } }
            }
        });
        if (files.Count == 0) return;

        try
        {
            await using var stream = await files[0].OpenReadAsync();
            await vm.ImportProfilesAsync(stream);
        }
        catch (Exception ex) { vm.Log($"⚠ Import failed: {ex.Message}"); }
    }

    // ── Browse for SSH private key ─────────────────────────────
    // Profile editor "Browse…" button. Opens a file picker and writes the
    // chosen path back to the VM so the modal's read-only textbox updates.
    private async void OnBrowseKeyFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        IStorageFolder? startFolder = null;
        if (Directory.Exists(sshDir))
        {
            try { startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(sshDir); }
            catch { /* a missing/locked .ssh folder is non-fatal — picker just opens at default */ }
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select SSH private key",
            AllowMultiple = false,
            SuggestedStartLocation = startFolder,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Private keys")
                {
                    Patterns = new[] { "id_*", "*.pem", "*.key", "*.ppk" }
                },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } }
            }
        });
        if (files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) vm.ModalKeyPath = path;
    }

    // ── Confirm Rename on Enter ────────────────────────────────
    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Enter) vm.ConfirmRenameCommand.Execute(null);
        else if (e.Key == Key.Escape) vm.CancelRenameCommand.Execute(null);
    }

    private void OnCreateFolderKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Enter) vm.ConfirmCreateFolderCommand.Execute(null);
        else if (e.Key == Key.Escape) vm.CancelCreateFolderCommand.Execute(null);
    }

    // ── Path edit boxes: Enter commits, Escape cancels ─────────
    private void OnLeftPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Enter) vm.CommitLeftPathCommand.Execute(null);
        else if (e.Key == Key.Escape) vm.CancelLeftPathCommand.Execute(null);
    }

    private void OnRightPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Enter) vm.CommitRightPathCommand.Execute(null);
        else if (e.Key == Key.Escape) vm.CancelRightPathCommand.Execute(null);
    }

    // ── Context menu: clipboard / Reveal in Explorer ───────────
    // The MenuItem's CommandParameter (the FileNode) arrives as DataContext
    // on the MenuItem because the parent file-row Button's DataContext is the
    // FileNode. Resolving the owning tab here mirrors what TransferFileAsync
    // does — search both panels' Files for the instance.
    private TabItem? FindOwnerTab(FileNode file)
    {
        if (DataContext is not MainWindowViewModel vm) return null;
        if (vm.ActiveLeftTab?.Files?.Contains(file) == true) return vm.ActiveLeftTab;
        if (vm.ActiveRightTab?.Files?.Contains(file) == true) return vm.ActiveRightTab;
        return null;
    }

    private string? BuildFullPath(FileNode file, TabItem tab)
    {
        if (file.Name == "..") return null;
        var sep = tab.IsRemote ? "/" : Path.DirectorySeparatorChar.ToString();
        return tab.Path.TrimEnd('/', '\\') + sep + file.Name;
    }

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not FileNode file) return;
        var tab = FindOwnerTab(file);
        if (tab == null) return;
        var path = BuildFullPath(file, tab);
        if (string.IsNullOrEmpty(path)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        try { await clipboard.SetTextAsync(path); }
        catch { /* clipboard may be unavailable in remote sessions */ }
        if (DataContext is MainWindowViewModel vm) vm.Log($"📋 Copied path: {path}");
    }

    private async void OnCopyNameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not FileNode file) return;
        if (file.Name == "..") return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        try { await clipboard.SetTextAsync(file.Name); }
        catch { }
        if (DataContext is MainWindowViewModel vm) vm.Log($"📋 Copied name: {file.Name}");
    }

    // Local-only. Opens File Explorer with the entry pre-selected. For
    // directories the explorer opens *the directory itself*; for files it
    // opens the parent and highlights the file (the standard /select trick).
    private void OnRevealInExplorerClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not FileNode file) return;
        var tab = FindOwnerTab(file);
        if (tab == null || tab.IsRemote) return;
        var path = BuildFullPath(file, tab);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var psi = file.IsDirectory
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            if (DataContext is MainWindowViewModel vm) vm.Log($"⚠ Could not reveal in Explorer: {ex.Message}");
        }
    }

    // ── Subtree search modal: Enter runs, Escape closes ────────
    private void OnSearchQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            if (!vm.IsSearchRunning) vm.RunSubtreeSearchCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            vm.CloseSubtreeSearchCommand.Execute(null);
        }
    }

    private void OnSearchQueryAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox tb) tb.Focus();
    }

    // ── Filter bar: Escape closes; attach focuses the box ──────
    private void OnLeftFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Escape) vm.CloseLeftFilterCommand.Execute(null);
    }

    private void OnRightFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Key == Key.Escape) vm.CloseRightFilterCommand.Execute(null);
    }

    // AttachedToVisualTree fires every time the Border's IsVisible flips on,
    // so the filter TextBox grabs focus the moment it appears.
    private void OnLeftFilterAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox tb) tb.Focus();
    }

    private void OnRightFilterAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox tb) tb.Focus();
    }

    // ── Window state persistence ───────────────────────────────
    // Applies saved size/position before the window is shown, then snaps to
    // Maximized if the last session was maximized. Out-of-bounds positions
    // (e.g. monitor removed) are ignored and the XAML default takes over.
    private void RestoreWindowState()
    {
        var s = _settingsService.Load();
        if (s.WindowWidth is double w && w >= MinWidth && s.WindowHeight is double h && h >= MinHeight)
        {
            Width = w;
            Height = h;
        }
        if (s.WindowX is int x && s.WindowY is int y && IsPositionVisible(new PixelPoint(x, y)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private bool IsPositionVisible(PixelPoint p)
    {
        foreach (var screen in Screens.All)
        {
            var b = screen.Bounds;
            if (p.X >= b.X && p.X < b.X + b.Width && p.Y >= b.Y && p.Y < b.Y + b.Height)
                return true;
        }
        return false;
    }

    // Any click in the window wakes up the reconnect logic. Cheap when
    // nothing is dropped — the VM returns immediately if every remote tab
    // is already connected.
    private async void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            try { await vm.TryReconnectDisconnectedTabsAsync(); }
            catch { /* swallow — logged inside the VM */ }
        }
    }

    private void OnWindowClosing(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        try
        {
            var s = _settingsService.Load();
            s.WindowMaximized = WindowState == WindowState.Maximized;
            // Only capture size/pos while in Normal state — maximized dimensions
            // match the monitor rather than the user's preferred window size.
            if (WindowState == WindowState.Normal)
            {
                s.WindowWidth = Width;
                s.WindowHeight = Height;
                s.WindowX = Position.X;
                s.WindowY = Position.Y;
            }
            // Snapshot the open tabs so the next launch can replay them.
            // Remote tabs come back disconnected — credentials never get
            // demanded just to redraw the layout.
            if (DataContext is MainWindowViewModel vm) vm.CaptureTabsToSettings(s);
            _settingsService.Save(s);
        }
        catch { /* closing the window should never be blocked by settings I/O */ }
    }
}
