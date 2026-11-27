using System.Globalization;
using System.Runtime.InteropServices;

namespace PurpleStreamPDF;

/// <summary>
/// The spell checker built into Windows, with the dictionaries of the languages installed (Settings ▸ Time &amp; language).
/// Words added through other apps' "Add to dictionary" count as spelt right too.
/// </summary>
sealed class WindowsSpellChecker
{
    readonly ISpellChecker _checker;

    WindowsSpellChecker(ISpellChecker checker) => _checker = checker;

    /// <summary>A checker for the Windows display language, else English, or null when Windows has neither.</summary>
    public static WindowsSpellChecker? Create()
    {
        var factory = (ISpellCheckerFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"))!)!;
        foreach (string language in new[] { CultureInfo.CurrentUICulture.Name, CultureInfo.CurrentCulture.Name, "en-GB", "en-US" })
        {
            factory.IsSupported(language, out int supported);
            if (supported != 0) return new WindowsSpellChecker(factory.CreateSpellChecker(language));
        }
        return null;
    }

    public bool IsSpeltRight(string word) => _checker.Check(word).Next(out _) != 0; // S_FALSE: no errors

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellCheckerFactory
    {
        IntPtr SupportedLanguages(); // unused, keeps the method order
        void IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag, out int supported);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISpellChecker
    {
        IntPtr LanguageTag(); // unused, keeps the method order
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumSpellingError
    {
        [PreserveSig] int Next([MarshalAs(UnmanagedType.IUnknown)] out object? error);
    }
}
