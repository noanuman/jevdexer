using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Jevdexer.Core.Files;
using Jevdexer.Core.History;
using Jevdexer.Core.Search;

namespace Jevdexer.App;

/// <summary>A section heading ("Applications", "Folders", "Recent searches"), with its row count when it scrolls.</summary>
public sealed record HeaderItem(string Title, bool Clearable = false, int Count = 0)
{
    public Visibility ClearVisibility => Clearable ? Visibility.Visible : Visibility.Collapsed;
    public string CountText => Count > 0 ? Count.ToString() : "";
}

/// <summary>
/// One section of results with its own scroll area: a few rows show, the rest scroll inside it
/// (the next row peeks out, so it reads as scrollable). Rows are updated in place, so a list
/// refreshed while typing does not flicker.
/// </summary>
public sealed class ResultSection(string key) : INotifyPropertyChanged
{
    public const double RowHeight = 50;

    public string Key { get; } = key;
    public ObservableCollection<object> Rows { get; } = [];

    public HeaderItem Header
    {
        get;
        set { field = value; Changed(); }
    } = new("");

    /// <summary>Rows shown before scrolling; the section's height follows.</summary>
    public int VisibleRows
    {
        get;
        set { field = value; Changed(); Changed(nameof(MaxHeight)); }
    } = 3;

    public double MaxHeight => Rows.Count > VisibleRows ? (VisibleRows + 0.45) * RowHeight : double.PositiveInfinity;

    /// <summary>The list showing it, once loaded (selection is driven from the window).</summary>
    public ListBox? View { get; set; }

    /// <summary>Replaces the rows with <paramref name="rows"/>, touching only what changed.</summary>
    public void Sync(IReadOnlyList<object> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < Rows.Count)
            {
                if (!Equals(Rows[i], rows[i])) Rows[i] = rows[i];
            }
            else Rows.Add(rows[i]);
        }
        while (Rows.Count > rows.Count) Rows.RemoveAt(Rows.Count - 1);
        Changed(nameof(MaxHeight));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>An app.</summary>
public sealed record ResultItem(SearchResult Result, string Name, string Subtitle, ImageSource? Icon)
{
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public Visibility InitialVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// A file or folder: its shell icon, name, and where it is and when it changed. Found by Ultra
/// Search, <paramref name="Inside"/> says why (the matching passage, words, or what photos show).
/// </summary>
public sealed record FileItem(FileEntry Entry, ImageSource? Icon, string? Inside = null)
{
    public string Name => Entry.Name;
    public string Subtitle => Inside is { } why ? $"{why}  ·  {ShortPath(Entry.Parent)}" : $"{ShortPath(Entry.Parent)}  ·  {Ago(Entry.Modified)}";
    public Geometry FallbackIcon => (Geometry)Application.Current.FindResource(Entry.IsFolder ? "Icon.Folder" : "Icon.File");
    public Visibility FallbackVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>"C:\Users\n\OneDrive\Work" as "~\OneDrive\Work".</summary>
    private static string ShortPath(string path) =>
        path.StartsWith(Profile, StringComparison.OrdinalIgnoreCase) ? "~" + path[Profile.Length..] : path;

    internal static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalMinutes < 1 ? "just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : d.TotalDays < 2 ? "yesterday"
            : d.TotalDays < 30 ? $"{(int)d.TotalDays} days ago"
            : utc.ToLocalTime().ToString("d MMM yyyy");
    }
}

/// <summary>
/// A recent search, with the icon of what it led to: the app, the file or the folder (the history
/// icon if that is gone).
/// </summary>
public sealed record RecentItem(SearchHistory.Entry Entry, ImageSource? Icon)
{
    public string Query => Entry.Query;
    public string Subtitle => Entry.AppName is { } opened ? $"Opened {opened}" : "Searched";
    public Visibility FallbackVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>History ids for files and folders; apps use their entity id.</summary>
    public const string PathPrefix = "path:";
}
