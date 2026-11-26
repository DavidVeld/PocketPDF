using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Data;

namespace PurpleStreamPDF;

/// <summary>What the app remembers between runs, in %AppData%\PurpleStreamPDF\settings.json.</summary>
public sealed class AppSettings
{
    const int MaxRecentFiles = 10;

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PurpleStreamPDF", "settings.json");
    /// <summary>Where they were under the app's first name, read until the first save under the new one.</summary>
    static readonly string OldFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PocketPDF", "settings.json");

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }
    public bool PagesPanelOpen { get; set; } = true;
    public bool Snap { get; set; } = true;
    /// <summary>The snaps switched off next to the Snap box, by name, so snaps added later start on.</summary>
    public List<string> SnapsOff { get; set; } = [];
    /// <summary>False shows the drawing's lines one pixel wide.</summary>
    public bool LineWeights { get; set; } = true;
    /// <summary>Shape and outline of new callouts and clouds, as the pickers' tags.</summary>
    public string AreaShape { get; set; } = "Box";
    public string AreaOutline { get; set; } = "Cloud";
    /// <summary>Pattern of new CAD hatches, as the hatch picker's tag.</summary>
    public string HatchPattern { get; set; } = "Lines";
    /// <summary>Dark or light window, as switched in View; null follows Windows.</summary>
    public bool? DarkMode { get; set; }
    public string? LastFolder { get; set; }
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>The saved settings, or defaults if there are none or the file is unreadable.</summary>
    public static AppSettings Load()
    {
        try
        {
            string path = File.Exists(FilePath) ? FilePath : OldFilePath;
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    /// <summary>Saves the settings. Failing to save only loses preferences, so errors are ignored.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Puts a file at the top of the recent list.</summary>
    public void AddRecentFile(string filePath)
    {
        RecentFiles.RemoveAll(path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, filePath);
        if (RecentFiles.Count > MaxRecentFiles) RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        LastFolder = Path.GetDirectoryName(filePath);
    }
}

/// <summary>Shows a path's file name or its folder, for the recent files list.</summary>
public sealed class PathParts(Func<string, string?> part) : IValueConverter
{
    public static PathParts FileName { get; } = new(Path.GetFileName);
    public static PathParts Folder { get; } = new(Path.GetDirectoryName);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string path ? part(path) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
