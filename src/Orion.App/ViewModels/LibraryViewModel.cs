using System.Windows.Input;
using Orion.App.Mvvm;

namespace Orion.App.ViewModels;

/// <summary>
/// 「内容管理」页：把资源包 / 存档 / 空间三块并成一个侧栏入口，用顶部分段控件切换。
/// 三个子页各自保留自己的视图模型和工具条，这里只负责切换与"切过去就刷新"。
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int _tabIndex;

    public LibraryViewModel(MainViewModel main)
    {
        _main = main;

        SelectTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is string key && int.TryParse(key, out var index)) TabIndex = index;
        });
    }

    public ICommand SelectTabCommand { get; }

    public IReadOnlyList<string> Tabs { get; } = ["资源", "存档", "空间"];

    /// <summary>0 = 资源（资源包/光影/数据包），1 = 存档与截图，2 = 空间占用。</summary>
    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (!SetProperty(ref _tabIndex, Math.Clamp(value, 0, 2))) return;

            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(CurrentTitle));
            OnPropertyChanged(nameof(Subtitle));
            _ = RefreshCurrentAsync();
        }
    }

    public object CurrentPage => TabIndex switch
    {
        1 => _main.SavesPage,
        2 => _main.SpacePage,
        _ => _main.ContentPage,
    };

    public string CurrentTitle => TabIndex switch
    {
        1 => "存档与截图",
        2 => "空间占用",
        _ => "资源管理",
    };

    public string Subtitle => TabIndex switch
    {
        1 => "世界列表、备份恢复、截图缩略图",
        2 => "分类占用统计与白名单清理",
        _ => "资源包 / 光影 / 数据包",
    };

    /// <summary>切到当前页签时刷新它（三个子页都跟随当前版本）。</summary>
    public Task RefreshCurrentAsync() => TabIndex switch
    {
        1 => RefreshSavesAsync(),
        2 => _main.SpacePage.RefreshAsync(),
        _ => RefreshContentAsync(),
    };

    private Task RefreshContentAsync()
    {
        _main.ContentPage.SyncInstance();
        return _main.ContentPage.RefreshAsync();
    }

    private Task RefreshSavesAsync()
    {
        _main.SavesPage.SyncInstance();
        return _main.SavesPage.RefreshAsync();
    }

    /// <summary>侧栏点进来时按当前页签刷新一次。</summary>
    public Task RefreshAsync() => RefreshCurrentAsync();
}
