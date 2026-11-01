using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Orion.App.Views;

/// <summary>
/// 启动器自绘的通用对话框（替代 Windows 原生 MessageBox）。
///
/// 为什么自己画：原生 MessageBox 用的是系统主题，和启动器配色/圆角/按钮风格完全不搭；
/// 而且它只能显示纯文本，没法把"版本选择 + 实例名"这类输入一起放进来。
/// </summary>
public partial class ModalDialog : Window
{
    public ModalDialog()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>自定义内容（例如安装整合包时的版本下拉 + 实例名）。</summary>
    public object? ExtraContent
    {
        get => ExtraHost.Content;
        set
        {
            ExtraHost.Content = value;
            ExtraHost.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public string Header { set => HeaderText.Text = value; }

    public string Message { set => MessageText.Text = value; }

    /// <summary>等宽字体的补充说明（路径、文件列表等）。空字符串会隐藏整块。</summary>
    public string Detail
    {
        set
        {
            DetailText.Text = value;
            DetailCard.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public string YesText
    {
        set => YesButton.Content = value;
    }

    public string NoText
    {
        set
        {
            NoButton.Content = value;
            NoButton.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public string CancelText
    {
        set
        {
            CancelButton.Content = value;
            CancelButton.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>标题左边的图标字（? ! i ×）。</summary>
    public string GlyphText
    {
        set => Glyph.Text = value;
    }

    /// <summary>图标底色：信息用强调色，警告/错误用危险色。</summary>
    public bool IsDanger
    {
        set
        {
            if (!value) return;
            GlyphHost.Background = (Brush)FindResource("DangerBrush");
        }
    }

    /// <summary>用户点了「是」（或唯一的主按钮）。</summary>
    public bool Accepted { get; private set; }

    /// <summary>用户点了「否」。</summary>
    public bool Declined { get; private set; }

    private void OnYes(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        DialogResult = true;
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        Declined = true;
        DialogResult = false;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Declined = false;
        DialogResult = false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            OnCancel(sender, e);
            e.Handled = true;
        }
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开：忽略
        }
    }
}
