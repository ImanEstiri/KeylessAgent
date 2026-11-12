using System.Reflection;
using ImanSoftware.KeylessAgent.Wpf.Mvvm;

namespace ImanSoftware.KeylessAgent.Wpf.ViewModels;

/// <summary>
/// View model of the about page: name, assembly version, description and the
/// terms-of-service/account-risk notice from the README, plus a button that opens the
/// application folder.
/// </summary>
public sealed class AboutViewModel : ObservableObject
{
    private readonly Func<string, bool> _openFolder;
    private string _note = string.Empty;

    /// <summary>Creates the about page view model.</summary>
    /// <param name="openFolder">Opens a folder with the shell; returns false on failure.</param>
    /// <param name="repositoryFolder">The folder to open; defaults to the application directory.</param>
    public AboutViewModel(Func<string, bool> openFolder, string? repositoryFolder = null)
    {
        _openFolder = openFolder ?? throw new ArgumentNullException(nameof(openFolder));
        RepositoryFolder = repositoryFolder ?? AppContext.BaseDirectory;

        var assembly = typeof(AboutViewModel).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Version = !string.IsNullOrWhiteSpace(informational)
            ? informational.Split('+')[0]   // ← SHA گیت را قطع کن
            : assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        Name = "KeylessAgent";
        Description =
            "KeylessAgent drives chat.deepseek.com to turn plain-English requests into safe, confirmed file and command changes in your workspace — no API key needed.";
        Disclaimer = "This project is not affiliated with DeepSeek — the name is used only to describe compatibility.";
        Warning =
            "KeylessAgent automates a web UI. This may violate DeepSeek's Terms of Use and can get your account limited, throttled or banned. The selectors that drive the page can break with any site update. Use it at your own risk, on an account you accept losing.";
        OpenRepositoryFolderCommand = new RelayCommand(() =>
        {
            if (!_openFolder(RepositoryFolder))
                Note = "Could not open the folder.";
        });
    }

    /// <summary>The application name.</summary>
    public string Name { get; }

    /// <summary>The assembly version (major.minor.build).</summary>
    public string Version { get; }

    /// <summary>A short description of the application.</summary>
    public string Description { get; }

    /// <summary>The affiliation notice.</summary>
    public string Disclaimer { get; }

    /// <summary>The terms-of-service/account-risk warning from the README.</summary>
    public string Warning { get; }

    /// <summary>The folder opened by the button (the application directory).</summary>
    public string RepositoryFolder { get; }

    /// <summary>A note when the folder could not be opened; empty otherwise.</summary>
    public string Note
    {
        get => _note;
        private set => SetProperty(ref _note, value);
    }

    /// <summary>Opens the application folder.</summary>
    public RelayCommand OpenRepositoryFolderCommand { get; }
}
