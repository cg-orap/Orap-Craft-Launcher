using System.Windows.Input;
using Orion.App.Mvvm;
using Orion.Core.Instances;

namespace Orion.App.ViewModels;

/// <summary>
/// 版本选择 sub-page: pick which installed version the launch button starts.
///
/// This lives inside the launcher window (the login flow is the only separate window), and returns
/// to the home page once a version is chosen.
/// </summary>
public sealed class VersionPickerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _status = "";

    public VersionPickerViewModel(MainViewModel main)
    {
        _main = main;

        BackCommand = new RelayCommand(() => _main.Navigate("home"));
        SelectCommand = new RelayCommand(Select);
    }

    public ICommand BackCommand { get; }
    public ICommand SelectCommand { get; }

    public ObservableList<Instance> Instances => _main.Instances;

    public Instance? Current => _main.CurrentInstance;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>Called when the page is shown, so the current version is highlighted.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Instances));
        OnPropertyChanged(nameof(Current));

        Status = Instances.Count == 0
            ? "还没有任何版本。请先去「下载中心」安装一个游戏版本，或导入现有整合包。"
            : "";
    }

    private void Select(object? parameter)
    {
        if (parameter is not Instance instance) return;

        _main.CurrentInstance = instance;
        Status = $"已选择 {instance.Name}（{instance.Subtitle}）";

        // Return to the launch page so the choice is immediately usable.
        _main.Navigate("home");
    }
}
