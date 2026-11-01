using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UTubeBulkUploader.Core;

namespace UTubeBulkUploader.Helpers;

/// <summary>Small functions used from x:Bind in XAML, and standard dialogs.</summary>
public static class Ui
{
    public static Visibility Show(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hide(bool v) => v ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility ShowText(string? s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool v) => !v;

    public static async Task<bool> Confirm(XamlRoot root, string title, string body, string primary, string close = "Cancel")
    {
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary, CloseButtonText = close, DefaultButton = ContentDialogButton.Primary,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task Inform(XamlRoot root, string title, string body)
    {
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
        };
        await d.ShowAsync();
    }

    /// <summary>Plain-words list of what went wrong: which folder, which file, what to do.</summary>
    public static string Describe(IEnumerable<FolderFailure> failures) =>
        string.Join("\n\n", failures.Select(f =>
            $"• Folder: {f.Folder}" +
            (string.IsNullOrEmpty(f.File) ? "" : $"\n   File: {f.File}") +
            $"\n   {f.Reason}" +
            (f.RolledBack ? "\n   Nothing in this folder was changed." : "\n   Some files here may still have temporary names (starting with ~ubu_).")));
}

public static class Format
{
    public static string Size(double n)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (n >= 1024 && i < units.Length - 1)
        {
            n /= 1024;
            i++;
        }
        return i == 0 ? $"{n:0} {units[i]}" : $"{n:0.0} {units[i]}";
    }

    public static string S(int n) => n == 1 ? "" : "s";
    public static string Count(int n, string word) => $"{n} {word}{S(n)}";
    public static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
