using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UTubeBulkUploader.Core;
using UTubeBulkUploader.Helpers;
using UTubeBulkUploader.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace UTubeBulkUploader.Views;

public sealed class BatchItem
{
    public required Batch Batch { get; init; }
    public string Title => $"Batch {Batch.Number} · {Format.Count(Batch.Rows.Count, "clip")} · {Format.Size(Batch.TotalSize)}";
    public string Range => Batch.Rows.Count == 1 ? Batch.FirstName : $"{Batch.FirstName}  …  {Batch.LastName}";
    public string Names => string.Join("\n", Batch.Rows.Select(r => r.NewName));
}

public sealed partial class BatchesPage : Page, IActivatable
{
    private readonly AppHost _host = AppHost.Current;

    public BatchesPage()
    {
        InitializeComponent();
        _host.LogChanged += Reload;
        Reload();
    }

    public void OnActivated() => Reload();

    private void Reload()
    {
        var batches = BatchPlanner.Plan(_host.LogFile, _host.Settings.BatchSize, out var missing);
        List.ItemsSource = batches.Select(b => new BatchItem { Batch = b }).ToList();
        var clips = batches.Sum(b => b.Rows.Count);
        Summary.Text = clips == 0 ? "" : $"{Format.Count(clips, "clip")} waiting in {Format.Count(batches.Count, "batch", "batches")}";
        ListArea.Visibility = Ui.Show(batches.Count > 0);
        Empty.Visibility = Ui.Show(batches.Count == 0);
        MissingBar.IsOpen = missing.Count > 0;
        MissingBar.Message = missing.Count == 0 ? "" :
            $"{Format.Count(missing.Count, "renamed clip")} can't be found on disk (moved, deleted or the drive isn't connected), so " +
            $"{(missing.Count == 1 ? "it isn't" : "they aren't")} in any batch: " +
            string.Join(", ", missing.Take(5).Select(m => m.NewName)) + (missing.Count > 5 ? " …" : "");
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();
    private void OnNightMode(object sender, RoutedEventArgs e) => App.Window?.GoTo("night");

    private async void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is not BatchItem item) return;
        var deferral = args.GetDeferral();
        try
        {
            var files = new List<IStorageItem>();
            foreach (var p in item.Batch.Paths) files.Add(await StorageFile.GetFileFromPathAsync(p));
            args.Data.SetStorageItems(files);
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.DragUI.SetContentFromDataPackage();
        }
        catch (Exception ex)
        {
            Log.Warn($"Drag of batch {item.Batch.Number} failed: {ex.Message}");
            args.Cancel = true;
        }
        finally { deferral.Complete(); }
    }

    private async void OnMarkUploaded(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BatchItem item) return;
        if (!await Ui.Confirm(XamlRoot, $"Mark batch {item.Batch.Number} as uploaded?",
                $"Only do this once YouTube Studio has finished uploading all {item.Batch.Rows.Count} clips. They'll be recorded as uploaded in the log and won't be offered again.",
                "Mark uploaded")) return;
        var saved = BatchPlanner.MarkUploaded(_host.LogFile, item.Batch, "Marked uploaded by hand.");
        if (!saved) await Ui.Inform(XamlRoot, "Saved, log open in Excel", "The log file is open in Excel. The change is kept and will be written when you close it.");
        _host.NotifyLogChanged();
    }
}
