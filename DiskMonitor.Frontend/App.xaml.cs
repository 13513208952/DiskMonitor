using System.IO;
using System.Windows;

namespace DiskMonitor.Frontend;

public partial class App : Application
{
    public static readonly string[] ThemeNames =
        ["Darkly", "Superhero", "Cosmo", "Flatly", "Journal", "Litera"];

    private static readonly string _settingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiskMonitor");

    public static string LoadSavedTheme()
    {
        try
        {
            var file = Path.Combine(_settingsDir, "theme.txt");
            if (File.Exists(file))
            {
                var name = File.ReadAllText(file).Trim();
                if (ThemeNames.Contains(name)) return name;
            }
        }
        catch { }
        return "Darkly";
    }

    public static void ApplyTheme(string themeName)
    {
        var dicts = Current.Resources.MergedDictionaries;

        var old = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("/Themes/Theme.") == true);
        if (old != null) dicts.Remove(old);

        dicts.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/Theme.{themeName}.xaml",
                             UriKind.Absolute)
        });

        try
        {
            Directory.CreateDirectory(_settingsDir);
            File.WriteAllText(Path.Combine(_settingsDir, "theme.txt"), themeName);
        }
        catch { }
    }
}
