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
    /// 本地整合包（拖入 zip / mrpack）：只有"安装"和"取消"，没有"只下载到…"。
    /// 实例名默认用压缩包文件名。
    /// </summary>
    public static string? AskLocalPackInstall(string zipPath, string suggestedName, string detail)
    {
        var nameBox = new TextBox { Text = suggestedName, Height = 30, FontSize = 12 };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "实例名称",
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedTextBrush"),
        });
        panel.Children.Add(nameBox);

        var dialog = Create(
            "安装整合包",
            $"要把「{System.IO.Path.GetFileName(zipPath)}」安装成一个新的版本吗？",
            detail,
            danger: false,
            "安装",
            "",
            "取消");

        dialog.ExtraContent = panel;
        dialog.GlyphText = "↓";
        FocusInput(dialog, nameBox);

        Orion.Core.Diagnostics.AppLog.Info("dialog", "local-pack-install-shown",
            ("zip", System.IO.Path.GetFileName(zipPath)));
        dialog.ShowDialog();

        if (!dialog.Accepted)
        {
            Orion.Core.Diagnostics.AppLog.Info("dialog", "local-pack-install-cancelled",
                ("zip", System.IO.Path.GetFileName(zipPath)));
            return null;
        }

        return string.IsNullOrWhiteSpace(nameBox.Text) ? suggestedName : nameBox.Text.Trim();
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
            Visibility = versions.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
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
        FocusInput(dialog, nameBox);

        Orion.Core.Diagnostics.AppLog.Info("dialog", "pack-install-shown", ("pack", packTitle));
        dialog.ShowDialog();

        if (!dialog.Accepted)
        {
            Orion.Core.Diagnostics.AppLog.Info("dialog", "pack-install-cancelled", ("pack", packTitle));
            return null;
        }

        var index = Math.Clamp(versionBox.SelectedIndex, 0, versions.Count - 1);
        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? suggestedName : nameBox.Text.Trim();

        return new PackInstallRequest(versions[index].Id, name);
    }


    /// <summary>
    /// 透明无边框对话框有时拿不到键盘焦点（点了输入框却打不出字）。
    /// 载入后强制激活窗口并把焦点交给输入框，并全选默认名字方便直接覆盖。
    /// </summary>
    private static void FocusInput(ModalDialog dialog, System.Windows.Controls.TextBox box)
    {
        dialog.Loaded += (_, _) =>
        {
            dialog.Activate();
            box.Focus();
            System.Windows.Input.Keyboard.Focus(box);
            box.SelectAll();
        };
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
