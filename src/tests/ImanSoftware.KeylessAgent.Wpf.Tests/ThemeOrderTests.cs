using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace ImanSoftware.KeylessAgent.Wpf.Tests;

/// <summary>
/// Regression test for the startup XamlParseException "Cannot find resource named
/// 'FadeInStoryboard'": StaticResource only sees keys defined ABOVE it — in the same file, or
/// in a dictionary that App.xaml merges EARLIER (BeginStoryboard.Storyboard cannot use
/// DynamicResource, so animation storyboards must precede the styles that start them).
/// Walks every Themes/*.xaml dictionary in App.xaml merge order and fails when a
/// StaticResource reference points at one of our own keys that is not yet defined.
/// </summary>
public class ThemeOrderTests
{
    private static string ThemesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && directory is not null; i++, directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "UI", "ImanSoftware.KeylessAgent.Wpf", "Themes");
            if (Directory.Exists(path))
                return path;
        }

        throw new DirectoryNotFoundException($"Could not locate Themes above {AppContext.BaseDirectory}.");
    }

    private static List<string> ReadMergeOrder(string appXamlPath)
    {
        var appXaml = File.ReadAllText(appXamlPath);
        return Regex.Matches(appXaml, "Source=\"Themes/(?<File>[A-Za-z.]+)\"")
            .Select(match => match.Groups["File"].Value)
            .ToList();
    }

    [Fact]
    public void StaticResourceReferences_ResolveToKeysDefinedAboveThem()
    {
        var themesDirectory = ThemesDirectory();
        var mergeOrder = ReadMergeOrder(Path.Combine(Directory.GetParent(themesDirectory)!.FullName, "App.xaml"));
        mergeOrder.Should().NotBeEmpty("App.xaml must merge the theme dictionaries in a known order");

        // First definition site of every key: file name + line index.
        var definitionSites = new Dictionary<string, (string File, int Line)>(StringComparer.Ordinal);
        foreach (var file in mergeOrder)
        {
            var lines = File.ReadAllLines(Path.Combine(themesDirectory, file));
            for (var line = 0; line < lines.Length; line++)
            {
                foreach (Match match in Regex.Matches(lines[line], "x:Key=\"([A-Za-z]+)\""))
                {
                    var key = match.Groups[1].Value;
                    if (!definitionSites.ContainsKey(key))
                        definitionSites[key] = (file, line);
                }
            }
        }

        definitionSites.Should().ContainKey("FadeInStoryboard", "the animation storyboards are keys our styles reference");

        var violations = new List<string>();
        foreach (var file in mergeOrder)
        {
            var lines = File.ReadAllLines(Path.Combine(themesDirectory, file));
            for (var line = 0; line < lines.Length; line++)
            {
                foreach (Match match in Regex.Matches(lines[line], "StaticResource ([A-Za-z]+)"))
                {
                    var key = match.Groups[1].Value;
                    if (!definitionSites.TryGetValue(key, out var site))
                        continue; // not one of our keys (framework resource)

                    if (site.File == file)
                    {
                        if (site.Line >= line)
                            violations.Add($"{file}:{line + 1} uses StaticResource {key} but it is defined later in the same file ({file}:{site.Line + 1})");
                    }
                    else if (mergeOrder.IndexOf(site.File) > mergeOrder.IndexOf(file))
                    {
                        violations.Add($"{file}:{line + 1} uses StaticResource {key} but it is defined in {site.File}, which App.xaml merges later");
                    }
                }
            }
        }

        violations.Should().BeEmpty("StaticResource forward references throw XamlParseException at startup");
    }

    [Fact]
    public void ThemeDictionaries_ContainNoDynamicResourceReferences()
    {
        // DynamicResource inside a theme dictionary creates a ResourceReferenceExpression per
        // setter. Sealing a freshly created deferred value then invalidates those expressions,
        // which re-fetches (and re-creates) other deferred values — the re-entrant cascade that
        // overflowed the stack in FindResourceInternal at first window show. Views keep
        // DynamicResource (they resolve against cached entries); the dictionaries themselves
        // must be fully resolvable with StaticResource (order enforced by the test above).
        var themesDirectory = ThemesDirectory();
        var offenders = Directory.GetFiles(themesDirectory, "*.xaml")
            .Select(file => (File: Path.GetFileName(file), Count: Regex.Matches(File.ReadAllText(file), "DynamicResource [A-Za-z]+").Count))
            .Where(entry => entry.Count > 0)
            .Select(entry => $"{entry.File}: {entry.Count} DynamicResource reference(s)")
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void ColorKeys_AndBrushKeys_NeverCollide()
    {
        // A color key shadowed by a same-named brush makes StaticResource hand a Brush to
        // Color-typed properties (observed: GradientStop.Color received '\#FF080B16' because
        // Brushes.xaml's BgDeep brush shadowed Colors.xaml's BgDeep color).
        var themesDirectory = ThemesDirectory();
        var colorKeys = new HashSet<string>(StringComparer.Ordinal);
        var brushKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.GetFiles(themesDirectory, "*.xaml"))
        {
            foreach (var line in File.ReadAllLines(file))
            {
                var keyMatch = Regex.Match(line, "x:Key=\"([A-Za-z]+)\"");
                if (!keyMatch.Success)
                    continue;

                var key = keyMatch.Groups[1].Value;
                if (line.Contains("<Color"))
                    colorKeys.Add(key);
                else if (line.Contains("Brush"))
                    brushKeys.Add(key);
            }
        }

        colorKeys.Should().NotBeEmpty("Colors.xaml defines the color tokens");
        brushKeys.Should().NotBeEmpty("Brushes.xaml defines one brush per color token");
        colorKeys.Overlaps(brushKeys).Should().BeFalse("color keys and brush keys must never share a name");
    }

    [Fact]
    public void GradientStopColors_ResolveToColorResources()
    {
        var themesDirectory = ThemesDirectory();
        var colorsXaml = File.ReadAllText(Path.Combine(themesDirectory, "Colors.xaml"));
        var colorKeys = Regex.Matches(colorsXaml, "<Color x:Key=\"([A-Za-z]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToList();

        colorKeys.Should().NotBeEmpty();

        var violations = new List<string>();
        foreach (var file in Directory.GetFiles(themesDirectory, "*.xaml"))
        {
            var lines = File.ReadAllLines(file);
            for (var line = 0; line < lines.Length; line++)
            {
                foreach (Match match in Regex.Matches(lines[line], "GradientStop Color=\"StaticResource ([A-Za-z]+)\""))
                {
                    var key = match.Groups[1].Value;
                    if (!colorKeys.Contains(key))
                        violations.Add($"{Path.GetFileName(file)}:{line + 1} GradientStop uses StaticResource {key}, which is not a <Color> in Colors.xaml");
                }
            }
        }

        violations.Should().BeEmpty("GradientStop.Color needs a Color, not a brush");
    }
}
