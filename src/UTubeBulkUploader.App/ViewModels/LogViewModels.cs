using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UTubeBulkUploader.Core;
using UTubeBulkUploader.Services;

namespace UTubeBulkUploader.ViewModels;

public sealed class LogItem(LogRow row)
{
    public LogRow Row { get; } = row;
    public bool Undone => Row.Status == LogStatus.Undone;
    public bool Uploaded => Row.Status == LogStatus.Uploaded;
    public bool Failed => Row.Status == LogStatus.Failed;
    public bool Renamed => !Undone && !Uploaded && !Failed;
    public string Title => Undone ? Row.OldName : Row.NewName;
    public string Detail => (Undone ? $"Was renamed to {Row.NewName}" : $"Was {Row.OldName}") + $" · {Row.FolderPath}";
    public string Status => Row.Status;
    public string When => Row.StatusTime == default ? "" : Row.StatusTime.ToString("dd MMM yyyy, HH:mm");
    public string Run => $"Run {Row.RunId}";
    public string Tip => string.IsNullOrEmpty(Row.Note) ? Path.Combine(Row.FolderPath, Title) : Row.Note;
}

public sealed class LogViewModel : ObservableObject
{
    private readonly AppHost _host = AppHost.Current;
    public ObservableCollection<LogItem> Items { get; } = new();

    private string _search = "";
    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value)) Load();
        }
    }

    public string Count { get; private set; } = "";
    public bool IsEmpty => Items.Count == 0;
    public string LogPath => _host.LogFile.LogPath;
    public bool Locked => _host.LogFile.HasUnwrittenChanges;

    public void Load()
    {
        Items.Clear();
        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rows = _host.LogFile.Rows.AsEnumerable().Reverse()
            .Where(r => words.All(w =>
                r.NewName.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                r.OldName.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                r.FolderPath.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                r.Status.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                r.RunId.Contains(w, StringComparison.OrdinalIgnoreCase)));
        foreach (var r in rows) Items.Add(new LogItem(r));
        var total = _host.LogFile.Rows.Count;
        Count = Items.Count == total ? $"{total} row{(total == 1 ? "" : "s")}" : $"{Items.Count} of {total} rows";
        OnPropertyChanged(string.Empty);
    }
}
