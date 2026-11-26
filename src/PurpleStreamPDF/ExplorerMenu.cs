using Microsoft.Win32;

namespace PurpleStreamPDF;

/// <summary>
/// The "Combine PDFs" entry in Explorer's right-click menu for PDF files. A classic menu entry under the current user:
/// no admin rights, no shell extension. On Windows 11 it shows under "Show more options".
/// </summary>
public static class ExplorerMenu
{
    const string KeyPath = @"Software\Classes\SystemFileAssociations\.pdf\shell\PurpleStreamPDF.Combine";
    /// <summary>The entry as added under the app's first name: found, replaced and removed like the new one.</summary>
    const string OldKeyPath = @"Software\Classes\SystemFileAssociations\.pdf\shell\PocketPDF.Combine";

    /// <summary>The exe the entry starts, or null if there's no entry.</summary>
    public static string? InstalledCommand()
    {
        using var command = Registry.CurrentUser.OpenSubKey(KeyPath + @"\command") ?? Registry.CurrentUser.OpenSubKey(OldKeyPath + @"\command");
        return command?.GetValue(null) as string;
    }

    public static void Install(string exePath)
    {
        Registry.CurrentUser.DeleteSubKeyTree(OldKeyPath, throwOnMissingSubKey: false);
        using var verb = Registry.CurrentUser.CreateSubKey(KeyPath);
        verb.SetValue(null, "Combine PDFs");
        verb.SetValue("Icon", $"\"{exePath}\",0");
        verb.SetValue("MultiSelectModel", "Player"); // any number of files; the default "Document" stops at 15
        using var command = verb.CreateSubKey("command");
        command.SetValue(null, $"\"{exePath}\" --combine \"%1\"");
    }

    public static void Uninstall()
    {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(OldKeyPath, throwOnMissingSubKey: false);
    }
}
