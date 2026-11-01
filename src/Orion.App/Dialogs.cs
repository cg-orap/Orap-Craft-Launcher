using System.Windows;
using System.Windows.Controls;
using Orion.App.Views;

namespace Orion.App;

/// <summary>对话框的三种结果。</summary>
public enum DialogOutcome
{
    Cancelled,
    Yes,
    No,
}

/// <summary>整合包安装时用户在对话框里选的东西。</summary>
public sealed record PackInstallRequest(string VersionId, string InstanceName);

/// <summary>
/// 启动器里所有"提示/确认/选择"都从这里走，统一用 <see cref="ModalDialog"/> 的样式，
/// 不再出现 Windows 原生 MessageBox 的观感。
/// </summary>
public static class Dialogs
{
    private static Window? Owner => Application.Current?.MainWindow;

    /// <summary>是 / 否 / 取消 三选一。</summary>
    public static DialogOutcome Ask(string header, string message, string yesText = "是", string noText = "否",
        string cancelText = "取消", string detail = "", bool danger = false)
    {
        var dialog = Create(header, message, detail, danger, yesText, noText, cancelText);
        dialog.ShowDialog();

        return dialog.Declined ? DialogOutcome.No
            : dialog.Accepted ? DialogOutcome.Yes
            : DialogOutcome.Cancelled;
    }

    /// <summary>只有「知道了」的提示。</summary>
    public static void Info(string header, string message, string detail = "") =>
        Create(header, message, detail, danger: false, "知道了", "", "").ShowDialog();

    /// <summary>错误提示（红色图标）。</summary>
    public static void Error(string header, string message, string detail = "") =>
        Create(header, message, detail, danger: true, "知道了", "", "").ShowDialog();

    /// <summary>
    /// 下载位置三选一：
    /// 「下载并安装」= 装进当前版本；「只下载到…」= 选一个目录，只把文件存过去；「取消」。
    /// 返回值：null = 取消，("", false) = 安装到版本，("path", true) = 只下载到 path。
    /// </summary>
    public static (string Directory, bool DownloadOnly)? AskDownloadLocation(
        string title, string fileName, string defaultDirectory, string currentDirectory)
    {
        var message =
            $"要把「{title}」下载到哪里？\n\n" +
            "「下载并安装」：按启动器默认流程装进当前版本\n" +
            "「只下载到…」：只把文件存到你选的目录（不会装进版本）";

        var detail = $"文件：{fileName}\n默认下载目录：{defaultDirectory}\n当前下载目录：{currentDirectory}";

        var outcome = Ask("选择下载位置", message, "下载并安装", "只下载到…", "取消", detail);

        if (outcome != DialogOutcome.No) return outcome == DialogOutcome.Yes ? ("", false) : null;

        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载目录",
            InitialDirectory = System.IO.Directory.Exists(currentDirectory) ? currentDirectory : defaultDirectory,
            Multiselect = false,
        };

        return picker.ShowDialog() == true ? (picker.FolderName, true) : null;
    }

    /// <summary>
    /// 安装整合包：版本选择 + 实例名，和"下载位置"合并成一个对话框。
    /// 返回 null 表示用户取消。
    /// </summary>
    public static PackInstallRequest? AskPackInstall(
        string packTitle,
        IReadOnlyList<(string Id, string Label)> versions,
        string suggestedName,
        string targetFolder)
    {
        var versionBox = new ComboBox
        {
            ItemsSource = versions.Select(v => v.Label).ToList(),
            SelectedIndex = 0,
            Height = 30,
            FontSize = 12,
        };

        var nameBox = new TextBox { Text = suggestedName, Height = 30, FontSize = 12 };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "版本",
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedTextBrush"),
        });
        panel.Children.Add(versionBox);
        panel.Children.Add(new TextBlock
        {
            Text = "实例名称",
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 4),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedTextBrush"),
        });
        panel.Children.Add(nameBox);

        var dialog = Create(
            "安装整合包",
            $"准备安装「{packTitle}」。\n选好版本后点「安装」，会按整合包清单逐个下载并校验。",
            $"实例文件夹：{targetFolder}",
            danger: false,
            "安装",
            "",
            "取消");

        dialog.ExtraContent = panel;
        dialog.GlyphText = "↓";
        dialog.ShowDialog();

        if (!dialog.Accepted) return null;

        var index = Math.Clamp(versionBox.SelectedIndex, 0, versions.Count - 1);
        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? suggestedName : nameBox.Text.Trim();

        return new PackInstallRequest(versions[index].Id, name);
    }

    private static ModalDialog Create(string header, string message, string detail, bool danger,
        string yesText, string noText, string cancelText)
    {
        var dialog = new ModalDialog
        {
            Owner = Owner,
            Header = header,
            Message = message,
            Detail = detail,
            YesText = yesText,
            NoText = noText,
            CancelText = cancelText,
            IsDanger = danger,
            GlyphText = danger ? "!" : "?",
        };

        return dialog;
    }
}
