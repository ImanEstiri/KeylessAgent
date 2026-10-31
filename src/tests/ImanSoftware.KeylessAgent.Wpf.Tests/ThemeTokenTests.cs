using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests;

/// <summary>
/// Phase 2 theme test: every color token from guide section 5.1 must exist in
/// Themes/Colors.xaml and be mapped to a brush in Themes/Brushes.xaml. Phase 12 adds the
/// documentation contract: every token documented in docs/wpf-ui.md must exist too.
/// The files are read as text (no Application needed).
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
            var path = Path.Combine(directory.FullName, "src", "UI", "ImanSoftware.KeylessAgent.Wpf", "Themes", fileName);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException($"Could not locate Themes/{fileName} above {AppContext.BaseDirectory}.");
    }

    private static string RepositoryFilePath(params string[] relativeParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. relativeParts]);
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException($"Could not locate {string.Join('/', relativeParts)} above {AppContext.BaseDirectory}.");
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

    [Fact]
    public void DocumentedThemeTokens_ExistInColorsXaml()
    {
        var documentation = File.ReadAllText(RepositoryFilePath("docs", "wpf-ui.md"));
        var section = documentation[documentation.IndexOf("## Theme tokens", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n## ", StringComparison.Ordinal)];
        var documented = Regex.Matches(section, @"^-\s+`([A-Za-z]+)`", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToList();

        documented.Should().NotBeEmpty("the docs/wpf-ui.md 'Theme tokens' section lists one token per bullet");
        documented.Should().BeEquivalentTo(RequiredTokens, "the documented tokens and guide 5.1 must stay in sync");

        var colors = File.ReadAllText(ThemeFilePath("Colors.xaml"));
        foreach (var token in documented)
            colors.Should().Contain($"x:Key=\"{token}\"", "docs/wpf-ui.md documents the {0} token", token);
    }
}
