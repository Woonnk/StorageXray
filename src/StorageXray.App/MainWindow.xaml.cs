using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StorageXray.Core;

namespace StorageXray.App;

public partial class MainWindow : Window
{
    private ScanResult? scan;
    private FolderNode? currentFolder;
    private DuplicateResult? duplicateResult;
    private ObservableCollection<CleanupItem> plan = [];
    private CancellationTokenSource? cancellation;
    private bool busy;
    private long fileOffset, folderOffset, duplicateOffset, planOffset, planTotal;
    private int operationId;
    private bool updatingPlan;
    private string? revealPath;
    private readonly HistoryStore history = new(Path.Combine(App.DataDirectory, "history"));
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly string[] titles = ["See where your space goes.", "Find the heavy hitters.", "One copy is enough.", "Your next cleanup, explained.", "Follow the space."];
    private readonly string[] subtitles = ["Choose a drive or folder. Get the full picture.", "Search your scanned files or choose a category. Double-click to reveal a file.", "Verify identical personal files before deciding what to keep.", "Pick a space target, then review every file in your plan.", "Compare complete scans of the same folder, saved only on this PC."];

    public MainWindow()
    {
        InitializeComponent();
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        RootInput.Text = Directory.Exists(downloads) ? downloads : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        DrivePicker.ItemsSource = DriveInfo.GetDrives().Where(d => { try { return d.IsReady; } catch { return false; } }).Select(d => d.Name).ToArray();
        Map.ItemClicked += entry => { if (entry.Folder != null) ShowFolder(entry.Folder); else ShowDetails(entry); };
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); ApplyFilter(); };
        SourceInitialized += (_, _) =>
        {
            try { int enabled = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref enabled, sizeof(int)); } catch { }
        };
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attr, ref int value, int size);

    private void Nav_Click(object sender, RoutedEventArgs e) => Navigate(int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture));
    private void Navigate(int index)
    {
        Pages.SelectedIndex = index; PageTitle.Text = titles[index]; PageSubtitle.Text = subtitles[index];
        Button[] buttons = [MapNav, FilesNav, DuplicateNav, CleanupNav, HistoryNav];
        for (int i = 0; i < buttons.Length; i++) buttons[i].Background = i == index ? Brush("#21394B") : Brushes.Transparent;
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a drive or folder to scan", Multiselect = false };
        if (Directory.Exists(RootInput.Text)) dialog.InitialDirectory = RootInput.Text;
        if (dialog.ShowDialog(this) == true) RootInput.Text = dialog.FolderName;
    }
    private void DrivePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (IsInitialized && DrivePicker.SelectedItem is string drive) RootInput.Text = drive; }
    private async void RootInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter && !busy) await ScanPathAsync(RootInput.Text); }
    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanPathAsync(RootInput.Text);
    private void Cancel_Click(object sender, RoutedEventArgs e) { cancellation?.Cancel(); StatusText.Text = "Stopping after the current item…"; CancelButton.IsEnabled = false; }
    private void SetBusy(bool value)
    {
        busy = value;
        if (value) operationId++;
        Pages.IsEnabled = !value;
        ScanButton.IsEnabled = BrowseButton.IsEnabled = RootInput.IsEnabled = DrivePicker.IsEnabled = !value;
        BusyBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = value && cancellation != null ? Visibility.Visible : Visibility.Collapsed;
        BusyBar.IsIndeterminate = value; CancelButton.IsEnabled = value;
        FindDuplicatesButton.IsEnabled = BuildPlanButton.IsEnabled = !value && scan is { Cancelled: false };
        ExportButton.IsEnabled = !value && scan != null;
        CleanButton.IsEnabled = !value && scan is { Cancelled: false } && plan.Any(i => i.Selected);
    }
    private IProgress<ScanProgress> Progress()
    {
        int generation = operationId, queued = 0;
        ScanProgress? latest = null;
        return new CallbackProgress(p =>
        {
            Interlocked.Exchange(ref latest, p);
            if (Interlocked.Exchange(ref queued, 1) != 0) return;
            Dispatcher.BeginInvoke(() =>
            {
                Interlocked.Exchange(ref queued, 0);
                var current = Interlocked.CompareExchange(ref latest, null, null);
                if (!busy || generation != operationId || current == null) return;
                StatusText.Text = $"{current.Phase} · {current.Files:N0} files · {Sizes.Format(current.Bytes)} · {current.Current}";
            }, DispatcherPriority.Background);
        });
    }
    private sealed class CallbackProgress(Action<ScanProgress> callback) : IProgress<ScanProgress>
    { public void Report(ScanProgress value) => callback(value); }
    private async Task QueryAsync(Func<Task> action)
    {
        if (busy || scan == null) return;
        SetBusy(true);
        try { await action(); }
        catch (Exception ex) { ShowError("Could not load scan results", ex); }
        finally { SetBusy(false); }
    }
    private async Task ScanPathAsync(string path)
    {
        if (busy) return;
        path = path.Trim().Trim('"');
        cancellation = new(); SetBusy(true); StatusText.Text = "Scanning…";
        try
        {
            var progress = Progress();
            var result = await Task.Run(() => new Scanner().Scan(path, progress, cancellation.Token));
            var previous = scan; scan = result; previous?.Dispose(); duplicateResult = null;
            fileOffset = folderOffset = duplicateOffset = planOffset = 0;
            SetPlanPage(new CleanupPage([], 0));
            RootInput.Text = result.Root.Path;
            TotalText.Text = Sizes.Format(result.Root.Bytes);
            CountText.Text = $"{result.FileCount:N0} files · {result.FolderCount:N0} folders" + (result.Cancelled ? " · partial" : "");
            DuplicateText.Text = "—"; DuplicateCountText.Text = "Run the duplicate finder after scanning";
            DuplicateGrid.ItemsSource = null;
            DuplicatePrevious.IsEnabled = DuplicateNext.IsEnabled = false;
            DuplicatePageNote.Text = "Run the duplicate finder to see copies.";
            DuplicatesNote.Text = "Checks supported personal files of 64 KiB or more. Full SHA-256 verification; hard links, cloud folders, and app files are excluded.";
            UpdateDrive(result.Root.Path); await LoadFolderAsync(result.Root); await LoadFilesAsync(); await RefreshPlanAsync();
            if (!result.Cancelled)
            {
                try
                {
                    var before = await Task.Run(() => history.Read(result.Root.Path));
                    var now = await Task.Run(() => history.Capture(result));
                    HistoryGrid.ItemsSource = before == null ? null : HistoryStore.Compare(before, now);
                    HistoryNote.Text = before == null
                        ? "Baseline saved. Scan this same folder again to see changes in the first three folder levels."
                        : $"Compared with {before.WhenUtc.ToLocalTime():MMM d, yyyy h:mm tt}. Parent and child changes overlap; do not add rows together. Skipped or inaccessible files can affect the comparison.";
                    await Task.Run(() => history.Save(result));
                }
                catch (Exception ex) { HistoryNote.Text = "Scan finished, but local history could not be saved: " + ex.Message; HistoryGrid.ItemsSource = null; }
            }
            else { HistoryNote.Text = "This scan was stopped. Partial scans do not replace the saved baseline."; HistoryGrid.ItemsSource = null; }
            StatusText.Text = $"{(result.Cancelled ? "Partial scan — rescan to enable cleanup" : "Scan complete")} · {result.FileCount:N0} files · {result.Skipped:N0} skipped. See scan details.";
        }
        catch (Exception ex) { ShowError("Could not scan that location", ex); }
        finally { SetBusy(false); cancellation?.Dispose(); cancellation = null; }
    }
    private void UpdateDrive(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            FreeText.Text = Sizes.Format(drive.AvailableFreeSpace); DriveText.Text = $"{drive.Name} · {Sizes.Format(drive.TotalSize)} total";
        }
        catch { FreeText.Text = "—"; DriveText.Text = "Capacity not available for this location"; }
    }
    private async void ShowFolder(FolderNode folder) => await QueryAsync(async () => { folderOffset = 0; await LoadFolderAsync(folder); });
    private async Task LoadFolderAsync(FolderNode folder)
    {
        if (scan == null) return;
        var index = scan.Index;
        var views = await Task.Run(() => (Page: index.FolderPage(folder.Id, folderOffset), Map: index.FolderPage(folder.Id, 0, 79)));
        currentFolder = views.Page.Folder; MapPath.Text = folder.Path; MapPath.ToolTip = folder.Path;
        Map.SetFolder(views.Map); MapEmpty.Visibility = folder.Bytes > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (folder.Bytes == 0)
        {
            var texts = MapEmpty.Children.OfType<TextBlock>().ToArray();
            texts[0].Text = "No readable file content here."; texts[1].Text = "Try another folder or check scan details.";
        }
        UpButton.IsEnabled = folder.ParentId != 0; RootButton.IsEnabled = true;
        FolderGrid.ItemsSource = views.Page.Items.Select(n => new MapEntry(n.Name, n.Path, n.Bytes, n.Kind, n.Folder, n.File)).ToArray();
        FolderNote.Text = PageLabel(folderOffset, views.Page.Items.Count, views.Page.TotalCount) + " · Double-click a folder to open it.";
        FolderPrevious.IsEnabled = folderOffset > 0; FolderNext.IsEnabled = folderOffset + views.Page.Items.Count < views.Page.TotalCount;
        ShowDetails(new(folder.Name, folder.Path, folder.Bytes, "Folder", folder, null));
    }
    private static string PageLabel(long offset, int count, long total) => count == 0 ? $"0 of {total:N0}" : $"{offset + 1:N0}–{offset + count:N0} of {total:N0}";
    private async void FolderPage_Click(object sender, RoutedEventArgs e)
    {
        if (currentFolder == null) return;
        await QueryAsync(async () => { folderOffset = Math.Max(0, folderOffset + ((Button)sender == FolderNext ? 1 : -1) * FileIndex.PageSize); await LoadFolderAsync(currentFolder); });
    }
    private void ShowDetails(MapEntry entry)
    {
        DetailName.Text = entry.Name; DetailSize.Text = entry.SizeText + (entry.Folder != null ? $" · {entry.Folder.FileCount:N0} files" : "");
        DetailExplanation.Text = FilePolicy.Explain(entry.Path); revealPath = entry.Path; RevealButton.IsEnabled = true;
    }
    private async void Up_Click(object sender, RoutedEventArgs e) { if (currentFolder is { ParentId: > 0 } folder && scan != null) await QueryAsync(async () => { folderOffset = 0; var parent = await Task.Run(() => scan.Index.GetFolder(folder.ParentId)); await LoadFolderAsync(parent); }); }
    private void Root_Click(object sender, RoutedEventArgs e) { if (scan != null) ShowFolder(scan.Root); }
    private void FolderGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (FolderGrid.SelectedItem is MapEntry entry) ShowDetails(entry); }
    private void FolderGrid_DoubleClick(object sender, MouseButtonEventArgs e) { if (FolderGrid.SelectedItem is MapEntry { Folder: not null } entry) ShowFolder(entry.Folder); }
    private void Reveal_Click(object sender, RoutedEventArgs e) { if (revealPath != null) Reveal(revealPath); }
    private void Reveal(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) { MessageBox.Show(this, "This item has moved or was removed. Rescan to update the view.", "StorageXray"); return; }
            // Arguments are passed to Explorer; no shell command or script is constructed.
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            if (File.Exists(path)) start.Arguments = "/select,\"" + path + "\"";
            else start.ArgumentList.Add(path);
            Process.Start(start);
        }
        catch (Exception ex) { ShowError("Could not open Explorer", ex); }
    }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (!IsInitialized) return; filterTimer.Stop(); filterTimer.Start(); }
    private void Category_Changed(object sender, SelectionChangedEventArgs e) { if (IsInitialized) ApplyFilter(); }
    private async void ApplyFilter() => await QueryAsync(async () => { fileOffset = 0; await LoadFilesAsync(); });
    private async Task LoadFilesAsync()
    {
        if (scan == null) return;
        string search = SearchInput.Text.Trim();
        string? category = (CategoryFilter.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (category == "All file types") category = null;
        var sort = (FileSort)Math.Max(0, FileSortPicker.SelectedIndex);
        var index = scan.Index;
        var matches = await Task.Run(() => index.FilesPage(search, category, sort, fileOffset));
        FilesGrid.ItemsSource = matches.Items;
        FilesNote.Text = PageLabel(fileOffset, matches.Items.Count, matches.TotalCount) + $" files · {Sizes.Format(matches.TotalBytes)} matched";
        FilesPrevious.IsEnabled = fileOffset > 0; FilesNext.IsEnabled = fileOffset + matches.Items.Count < matches.TotalCount;
    }
    private async void FilesPage_Click(object sender, RoutedEventArgs e) => await QueryAsync(async () =>
    {
        fileOffset = Math.Max(0, fileOffset + ((Button)sender == FilesNext ? 1 : -1) * FileIndex.PageSize);
        await LoadFilesAsync();
    });
    private void File_DoubleClick(object sender, MouseButtonEventArgs e) { if (FilesGrid.SelectedItem is FileRecord file) Reveal(file.Path); }
    private async void Duplicates_Click(object sender, RoutedEventArgs e)
    {
        if (busy || scan == null || scan.Cancelled) return;
        cancellation = new(); SetBusy(true);
        try
        {
            var progress = Progress();
            duplicateResult = null; DuplicateGrid.ItemsSource = null;
            DuplicateText.Text = "—"; DuplicateCountText.Text = "Checking duplicates…";
            DuplicatePrevious.IsEnabled = DuplicateNext.IsEnabled = false;
            SetPlanPage(new CleanupPage([], 0));
            duplicateResult = await Task.Run(() => new DuplicateFinder().Find(scan.Index, progress, cancellation.Token));
            duplicateOffset = 0; await LoadDuplicatesAsync();
            DuplicateText.Text = Sizes.Format(duplicateResult.ExtraBytes);
            DuplicateCountText.Text = $"{duplicateResult.GroupCount:N0} verified groups" + (duplicateResult.Cancelled ? " · partial search" : "");
            DuplicatesNote.Text = $"{duplicateResult.GroupCount:N0} exact-match groups found. KEEP THIS COPY identifies the retained file, including across pages. {duplicateResult.Skipped:N0} changed, locked, or hard-linked files skipped. " + (duplicateResult.Cancelled ? "Search stopped; only fully hashed matches are shown." : "Open Cleanup planner to review extra copies.");
            await RefreshPlanAsync(); StatusText.Text = duplicateResult.Cancelled ? "Duplicate search stopped. Verified matches remain available." : "Duplicate search complete. No files changed.";
        }
        catch (Exception ex) { ShowError("Duplicate search could not finish", ex); }
        finally { SetBusy(false); cancellation?.Dispose(); cancellation = null; }
    }
    private void Duplicate_DoubleClick(object sender, MouseButtonEventArgs e)
    { if (DuplicateGrid.SelectedItem is DuplicateRow row) Reveal(row.File.Path); }
    private async Task LoadDuplicatesAsync()
    {
        if (scan == null) return;
        var rows = await Task.Run(() => scan.Index.DuplicatePage(duplicateOffset));
        DuplicateGrid.ItemsSource = rows;
        DuplicatePageNote.Text = PageLabel(duplicateOffset, rows.Count, duplicateResult?.CopyCount ?? 0) + " copies";
        DuplicatePrevious.IsEnabled = duplicateOffset > 0;
        DuplicateNext.IsEnabled = duplicateOffset + rows.Count < (duplicateResult?.CopyCount ?? 0);
    }
    private async void DuplicatePage_Click(object sender, RoutedEventArgs e) => await QueryAsync(async () =>
    {
        duplicateOffset = Math.Max(0, duplicateOffset + ((Button)sender == DuplicateNext ? 1 : -1) * FileIndex.PageSize);
        await LoadDuplicatesAsync();
    });
    private async Task RefreshPlanAsync()
    {
        planOffset = 0;
        var page = scan is { Cancelled: false } ? await Task.Run(() => CleanupPlanner.Candidates(scan, DateTime.UtcNow)) : new CleanupPage([], 0);
        SetPlanPage(page);
    }
    private void SetPlanPage(CleanupPage page)
    {
        foreach (var item in plan) item.PropertyChanged -= PlanChanged;
        plan = new(page.Items); planTotal = page.TotalCount;
        foreach (var item in plan) item.PropertyChanged += PlanChanged;
        PlanGrid.ItemsSource = plan; UpdatePlanSummary();
        PlanPageNote.Text = PageLabel(planOffset, plan.Count, planTotal) + " candidates · Changing pages clears the selection. Goals apply to this page.";
        PlanPrevious.IsEnabled = planOffset > 0; PlanNext.IsEnabled = planOffset + plan.Count < planTotal;
    }
    private async void PlanPage_Click(object sender, RoutedEventArgs e) => await QueryAsync(async () =>
    {
        if (scan == null) return;
        planOffset = Math.Max(0, planOffset + ((Button)sender == PlanNext ? 1 : -1) * FileIndex.CleanupPageSize);
        SetPlanPage(await Task.Run(() => scan.Index.CleanupPage(planOffset)));
    });
    private void PlanChanged(object? sender, PropertyChangedEventArgs e) { if (!updatingPlan) UpdatePlanSummary(); }
    private void UpdatePlanSummary()
    {
        long total = plan.Where(i => i.Selected).Sum(i => i.File.Size); int count = plan.Count(i => i.Selected);
        PlanSummary.Text = count > 0 ? $"{count:N0} files selected on this page · {Sizes.Format(total)} of file content" : $"{planTotal:N0} candidates · nothing selected";
        if (duplicateResult == null && scan != null) PlanSummary.Text += " · run Duplicate finder for more candidates";
        CleanButton.IsEnabled = !busy && count > 0 && scan is { Cancelled: false };
    }
    private void BuildPlan_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(GoalInput.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double gib) || double.IsNaN(gib) || double.IsInfinity(gib) || gib <= 0 || gib > 100000)
        { MessageBox.Show(this, "Enter a target greater than 0 and no more than 100,000 GiB.", "Choose a target"); return; }
        long goal = (long)(gib * 1024 * 1024 * 1024);
        if (goal < 1) { MessageBox.Show(this, "That target is too small.", "Choose a target"); return; }
        updatingPlan = true;
        long chosen;
        try { chosen = CleanupPlanner.SelectGoal(plan, goal); }
        finally { updatingPlan = false; }
        UpdatePlanSummary();
        if (chosen < goal) PlanSummary.Text += $" · {Sizes.Format(goal - chosen)} short of target";
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { updatingPlan = true; try { foreach (var item in plan) item.Selected = false; } finally { updatingPlan = false; } UpdatePlanSummary(); }
    private void Plan_DoubleClick(object sender, MouseButtonEventArgs e) { if (PlanGrid.SelectedItem is CleanupItem item && e.OriginalSource is not CheckBox) Reveal(item.Path); }
    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (busy || scan == null) return;
        var selected = plan.Where(i => i.Selected).ToArray(); if (selected.Length == 0) return;
        var dialog = new ReviewWindow(selected) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        cancellation = new(); SetBusy(true);
        int recycled = 0; long recycledBytes = 0; var errors = new List<string>();
        string root = scan.Root.Path;
        try
        {
            await Task.Run(() => CleanupPlanner.ValidateSelection(selected));
            foreach (var item in selected)
            {
                if (cancellation.IsCancellationRequested) break;
                StatusText.Text = $"Verifying and recycling {recycled + 1} of {selected.Length} · {item.Name}";
                try
                {
                    using var lease = await Task.Run(() => CleanupPlanner.AcquireCleanupLease(item, cancellation.Token));
                    // Shell COM requires an STA thread. Use the WPF dispatcher after background verification.
                    FilePolicy.ValidateUnchanged(item.File);
                    RecycleService.Send(item.Path);
                    recycled++; recycledBytes += item.File.Size;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { errors.Add(item.Path + " — " + ex.Message); }
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            try
            {
                string logs = Path.Combine(App.DataDirectory, "cleanup"); Directory.CreateDirectory(logs);
                File.WriteAllText(Path.Combine(logs, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json"), JsonSerializer.Serialize(new { WhenUtc = DateTime.UtcNow, RecycledCount = recycled, RecycledBytes = recycledBytes, RequestedPaths = selected.Select(i => i.Path).ToArray(), Errors = errors }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { errors.Add("The local cleanup receipt could not be saved."); }
            MessageBox.Show(this, $"Recycled {recycled:N0} files ({Sizes.Format(recycledBytes)} of content).\n\nRestore them from the Windows Recycle Bin if needed. Space is recovered only when you empty the bin. StorageXray never empties it."
                + (errors.Count > 0 ? $"\n\n{errors.Count:N0} items need attention:\n" + string.Join("\n", errors.Take(8)) : ""), "Cleanup result", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError("Cleanup stopped before starting", ex); }
        finally { SetBusy(false); cancellation?.Dispose(); cancellation = null; }
        await ScanPathAsync(root);
    }
    private void History_DoubleClick(object sender, MouseButtonEventArgs e) { if (HistoryGrid.SelectedItem is FolderChange change) Reveal(change.Path); }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (busy || scan == null) return;
        var dialog = new SaveFileDialog { Title = "Export scanned file inventory", Filter = "CSV file|*.csv", FileName = "StorageXray-files.csv" };
        if (dialog.ShowDialog(this) != true) return;
        cancellation = new(); SetBusy(true); StatusText.Text = "Exporting all scanned files…";
        try { await Task.Run(() => CsvExport.Write(dialog.FileName, scan.Index.EnumerateFiles(cancellation.Token))); StatusText.Text = "Exported the full scanned inventory."; }
        catch (OperationCanceledException) { StatusText.Text = "Export stopped. The CSV contains only the rows written so far."; }
        catch (Exception ex) { ShowError("Could not export the report", ex); }
        finally { SetBusy(false); cancellation.Dispose(); cancellation = null; }
    }
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        string content = scan == null ? "No scan yet. Choose a folder or drive, then click Scan folder." : $"Root: {scan.Root.Path}\nCompleted: {scan.CompletedUtc.ToLocalTime():g}\nFiles: {scan.FileCount:N0}\nFolders: {scan.FolderCount:N0}\nSkipped: {scan.Skipped:N0}\nStopped early: {scan.Cancelled}\n\nSizes are logical file lengths. They can differ from disk usage because of permissions, hard links, compression, sparse files, cloud placeholders, and Windows-managed storage.\n\nFirst 100 skipped items:\n" + string.Join("\n", scan.SkipDetails);
        ShowText("Scan details", content);
    }
    private void RecycleBin_Click(object sender, RoutedEventArgs e)
    { try { Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = false }); } catch (Exception ex) { ShowError("Could not open Recycle Bin", ex); } }
    private void Help_Click(object sender, RoutedEventArgs e) => ShowText("About StorageXray", "STORAGEXRAY 0.2.1\n\n1. Start with Downloads, or choose a drive. Scanning is read-only.\n2. Click map blocks to explore folders. Find the biggest files in Largest files.\n3. Run Duplicate finder, then open Cleanup planner.\n4. Enter your space goal, review the selected paths, and confirm recycling.\n5. Scan the same root again to see what changed.\n\nDuplicate matching covers supported personal files of at least 64 KiB. Application, cloud, system, read-only, linked, and hard-linked files are excluded from cleanup. Older-file suggestions use modification dates, not an assumption that you no longer need them.\n\nCleanup is limited to fixed local drives. Windows must accept a recycle operation. No permanent-delete fallback or automatic bin emptying is provided. Recycle Bin files still occupy disk space. Use your game launcher to uninstall games.\n\nResults are paged from a local disk index. Cleanup goals apply to the current page of at most 1,000 candidates; changing pages clears selections. Temporary indexes are removed on normal close or replacement.\n\nHistory and cleanup receipts are stored in:\n" + App.DataDirectory + "\n\nHistory contains folder names and sizes, not file contents. CSV exports contain local file paths; share them only if you intend to.\n\nThis early build is unsigned. No network access, background startup task, or administrator account is required.");
    private void ShowText(string title, string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20) };
        new Window { Owner = this, Title = title, Width = 760, Height = 570, MinWidth = 500, MinHeight = 350, Background = Brush("#101927"), Content = box, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }
    private void ShowError(string title, Exception ex) { StatusText.Text = title + "."; MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (busy) { e.Cancel = true; cancellation?.Cancel(); StatusText.Text = "Stopping… Close the window after the current operation finishes."; }
        else { filterTimer.Stop(); scan?.Dispose(); }
    }
}
