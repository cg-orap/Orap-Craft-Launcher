using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using Orion.App.Mvvm;
using Orion.Core.Mods;

namespace Orion.App.ViewModels;

/// <summary>One installable file of a project, shown in the detail window.</summary>
public sealed class CatalogVersionRow(
    ModrinthProject project,
    CatalogKindOption kind,
    ModrinthVersion version,
    CatalogDetailViewModel owner)
{
    public ModrinthVersion Version { get; } = version;

    public string Number => string.IsNullOrWhiteSpace(Version.VersionNumber) ? Version.Name : Version.VersionNumber;

    public string GameVersions => Version.GameVersions.Count == 0
        ? "—"
        : string.Join(", ", Version.GameVersions.Take(6)) + (Version.GameVersions.Count > 6 ? " …" : "");

    public string Loaders => Version.Loaders.Count == 0 ? "—" : string.Join(", ", Version.Loaders);

    public string FileName => Version.PrimaryFile?.Filename ?? "";

    public string Published => Version.DatePublished?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";

    public string ChannelIcon => Version.VersionType switch
    {
        "release" => "✓",
        "beta" => "β",
        _ => "α",
    };

    public ICommand InstallCommand => new AsyncRelayCommand(() => owner.InstallAsync(this));

    public ModrinthProject Project => project;
    public CatalogKindOption Kind => kind;
}

/// <summary>Detail window view model: description plus the full version history.</summary>
public sealed class CatalogDetailViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly CatalogViewModel _catalog;
    private string _status = "";
    private bool _busy;

    public CatalogDetailViewModel(
        MainViewModel main,
        CatalogViewModel catalog,
        ModrinthProject project,
        CatalogKindOption kind,
        IReadOnlyList<ModrinthVersion> versions,
        ImageSource? icon)
    {
        _main = main;
        _catalog = catalog;

        Project = project;
        Kind = kind;
        Icon = icon;

        foreach (var version in versions
                     .OrderByDescending(v => v.DatePublished ?? DateTimeOffset.MinValue)
                     .Where(v => v.PrimaryFile is not null))
        {
            Versions.Add(new CatalogVersionRow(project, kind, version, this));
        }

        // Modrinth returns markdown; headings and images are stripped to keep it readable.
        Description = ToPlainText(project.Body);
    }

    public ModrinthProject Project { get; }
    public CatalogKindOption Kind { get; }
    public ImageSource? Icon { get; }

    public bool HasIcon => Icon is not null;

    public string Title => string.IsNullOrWhiteSpace(Project.Title) ? Project.Slug : Project.Title;

    public string KindLabel => Kind.Name;

    public string Author => string.IsNullOrWhiteSpace(Project.Author) ? "未知作者" : Project.Author;

    public string DownloadsText => Project.Downloads > 0 ? $"{Project.Downloads:N0} 次下载" : "";

    public string FollowsText => Project.Follows > 0 ? $"{Project.Follows:N0} 关注" : "";

    public string CategoryText => Project.Categories.Count == 0 ? "" : string.Join(" · ", Project.Categories);

    public string Description { get; }

    public ObservableList<CatalogVersionRow> Versions { get; } = [];

    public string VersionsHeader => $"可用版本（{Versions.Count} 个，按发布时间倒序）";

    public bool HasVersions => Versions.Count > 0;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    /// <summary>Installs the chosen file; the shared catalogue does the real work.</summary>
    public async Task InstallAsync(CatalogVersionRow row)
    {
        if (_busy) return;

        Busy = true;
        Status = "正在安装…";

        try
        {
            await _catalog.InstallVersionAsync(row.Project, row.Kind, row.Version, Title).ConfigureAwait(true);
            Status = _catalog.Status;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                                       or TaskCanceledException)
        {
            Status = "安装失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Strips the most common markdown constructs so the description reads as plain text.</summary>
    private static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "（作者没有提供详细说明。）";

        var text = markdown.Replace("\r\n", "\n");

        // Drop images entirely and keep link labels.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"!\[[^\]]*\]\([^)]*\)", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^#{1,6}\s*", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^\s*[-*+]\s+", "• ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*|\*|`", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");

        return text.Trim();
    }
}
