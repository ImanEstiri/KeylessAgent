using System.IO;
using FluentAssertions;

namespace ImanSoftware.KeylessAgent.Wpf.Tests;

/// <summary>
/// Phase 2 theme test: every color token from guide section 5.1 must exist in
/// Themes/Colors.xaml and be mapped to a brush in Themes/Brushes.xaml.
/// The dictionaries are read as text (no Application needed).
/// </summary>
public class ThemeTokenTests
{
    private static readonly string[] RequiredTokens =
    [
        "BgDeep", "BgMid",
        "GlassFill", "GlassFillHover", "GlassFillStrong", "GlassBorder",
        "TextPrimary", "TextSecondary", "TextMuted",
        "Gold", "GoldHover", "GoldPressed",
        "Violet",
        "Success", "Warning", "Danger", "Info",
        "DiffAdd", "DiffRemove",
    ];

    private static string ThemeFilePath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var path = Path.Combine(
                directory!.FullName, "src", "UI", "ImanSoftware.KeylessAgent.Wpf", "Themes", fileName);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException($"Could not locate Themes/{fileName} above {AppContext.BaseDirectory}.");
    }

    [Fact]
    public void ColorsXaml_DefinesEveryTokenFromTheGuide()
    {
        var colors = File.ReadAllText(ThemeFilePath("Colors.xaml"));

        foreach (var token in RequiredTokens)
            colors.Should().Contain($"x:Key=\"{token}\"", "guide 5.1 requires the {0} token", token);
    }

    [Fact]
    public void BrushesXaml_MapsEveryToken_ToABrush()
    {
        var brushes = File.ReadAllText(ThemeFilePath("Brushes.xaml"));

        foreach (var token in RequiredTokens)
            brushes.Should().Contain($"x:Key=\"{token}\"", "every color token needs a matching brush");
    }
}
