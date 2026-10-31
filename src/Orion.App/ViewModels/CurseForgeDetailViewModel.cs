using System.Windows.Input;
using Orion.App.Mvvm;
using System.Windows.Media;
using Orion.Core.Mods;

namespace Orion.App.ViewModels;

/// <summary>详情页里的一行历史版本。</summary>
public sealed class CurseForgeVersionRow(CurseForgeFile file, CurseForgeDetailViewModel owner)
{
    public CurseForgeFile File { get; } = file;

    public string Name => string.IsNullOrWhiteSpace(File.DisplayName) ? File.FileName : File.DisplayName!;

    public string FileName => File.FileName;

    public string GameVersions => File.GameVersions.Count == 0
        ? "—"
        : string.Join(", ", File.GameVersions.Take(4));

    public ICommand InstallCommand => new AsyncRelayCommand(() => owner.InstallAsync(this), null,
        ex => owner.Status = "安装失败: " + ex.Message);
}

/// <summary>
/// CurseForge 项目详情：头像、描述、分类，以及"历史版本"列表（每个版本都能单独安装）。
/// 与 Modrinth 的详情页对齐：点列表 → 右侧详情 → 选版本 → 安装。
/// </summary>
public sealed class CurseForgeDetailViewModel(
    CurseForgeRow row,
    CatalogViewModel owner,
    Orion.Core.Instances.Instance? instance,
    ImageSource? icon) : ObservableObject
{
    private string _status = "";
    private CurseForgeModDetail? _detail;

    public CurseForgeRow Row { get; } = row;

    /// <summary>宿主（下载中心页）：翻译等操作需要它来拿服务。</summary>
    public CatalogViewModel Owner => owner;

    public CurseForgePack Pack { get; } = row.Pack;

    public CurseForgeKind Kind { get; } = row.Kind;

    public string Title => _detail?.Name ?? Pack.Name;

    public string Summary => _detail?.Summary ?? Pack.Summary;

    private string? _translated;
    private bool _showTranslated;
    private string _translationStatus = "";

    /// <summary>译文缓存；IsTranslated 决定界面显示原文还是译文。</summary>
    public string Description => _showTranslated && _translated is { Length: > 0 }
        ? _translated
        : _detail?.Description ?? "";

    public bool IsTranslated => _showTranslated && _translated is { Length: > 0 };

    public bool HasTranslation => _translated is { Length: > 0 };

    /// <summary>翻译按钮文案：没翻过 → "翻译成中文"，翻过 → "显示原文"/"显示译文"。</summary>
    public string TranslateLabel => _translated is { Length: > 0 }
        ? (IsTranslated ? "显示原文" : "显示译文")
        : "翻译成中文";

    private bool _isTranslating;

    public bool IsTranslating
    {
        get => _isTranslating;
        private set
        {
            if (!SetProperty(ref _isTranslating, value)) return;
            ((AsyncRelayCommand)TranslateCommand).RaiseCanExecuteChanged();
        }
    }

    public string TranslationStatus
    {
        get => _translationStatus;
        private set => SetProperty(ref _translationStatus, value);
    }

    public ICommand TranslateCommand => new AsyncRelayCommand(ToggleTranslationAsync, () => !IsTranslating);

    public ImageSource? Icon { get; } = icon;

    public bool HasIcon => Icon is not null;

    /// <summary>类型标签（整合包 / 模组 / 材质包 / 光影）。</summary>
    public string KindLabel => Row.KindLabel;

    public string Categories => _detail is null || _detail.Categories.Count == 0
        ? owner.KindNameOf(Kind)
        : string.Join(" · ", _detail.Categories.Take(3));

    public string Downloads => Pack.Downloads > 0 ? $"{Pack.Downloads:N0} 次下载" : "";

    public string TargetInfo => Kind == CurseForgeKind.Modpack
        ? "安装后会创建新的版本"
        : instance is null
            ? "先选一个版本才能安装到实例"
            : $"安装到 {instance.Name} → {CurseForgeClient.InstallFolderOf(Kind)}";

    public ObservableList<CurseForgeVersionRow> Versions { get; } = [];

    public string VersionsHeader => $"历史版本（{Versions.Count} 个，新到旧）";

    public bool HasVersions => Versions.Count > 0;

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public ICommand BackCommand => owner.CloseCurseForgeDetailCommand;

    private async Task ToggleTranslationAsync()
    {
        if (_translated is { Length: > 0 })
        {
            _showTranslated = !_showTranslated;
            RaiseTranslationState();
            return;
        }

        var text = _detail?.Description;
        if (string.IsNullOrWhiteSpace(text))
        {
            TranslationStatus = "这个项目没有可翻译的描述。";
            return;
        }

        TranslationStatus = "正在翻译…（MyMemory，非大模型，长文会分段）";
        var result = await owner.Context.Translator.TranslateAsync(text).ConfigureAwait(true);

        if (result is null)
        {
            TranslationStatus = owner.Context.Translator.LastError ?? "翻译失败。";
            return;
        }

        _translated = result;
        _showTranslated = true;
        TranslationStatus = "已翻译（机器翻译，仅供理解，安装前请以原文为准）";
        RaiseTranslationState();
    }

    private void RaiseTranslationState()
    {
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(IsTranslated));
        OnPropertyChanged(nameof(HasTranslation));
        OnPropertyChanged(nameof(TranslateLabel));
    }

    public async Task LoadAsync()
    {
        Status = "正在获取项目详情…";

        try
        {
            _detail = await owner.Context.CurseForge.GetModAsync(Pack.Id).ConfigureAwait(true);
            var files = await owner.Context.CurseForge.GetFilesAsync(Pack.Id).ConfigureAwait(true);

            Versions.Replace(files.Select(f => new CurseForgeVersionRow(f, this)));

            // 详情是异步回来的：这些计算属性必须显式通知，否则界面一直空着
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(Categories));
            OnPropertyChanged(nameof(Downloads));
            OnPropertyChanged(nameof(VersionsHeader));
            OnPropertyChanged(nameof(HasVersions));

            Status = files.Count == 0
                ? "没有可用的文件。"
                : $"共 {files.Count} 个文件（新到旧），点「安装」下载指定版本。";
        }
        catch (Exception ex)
        {
            Status = "获取详情失败: " + ex.Message;
        }
    }

    public async Task InstallAsync(CurseForgeVersionRow version)
    {
        Status = $"正在安装 {version.FileName}…";
        await owner.InstallCurseForgeVersionAsync(Row, version.File).ConfigureAwait(true);
        Status = owner.Status;
    }
}
