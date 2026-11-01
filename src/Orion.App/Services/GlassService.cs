using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Orion.Core.Diagnostics;

namespace Orion.App.Services;

/// <summary>
/// 窗口毛玻璃（Win11 亚克力 / Win10 模糊）。
///
/// WPF 自己没有毛玻璃，只能让 DWM 把窗口背后的内容模糊后合成到窗口里，
/// 再把界面底色做成半透明盖在上面 —— 这就是"玻璃感"的来源。
///
/// · Win11 22H2+：DwmSetWindowAttribute(38 = SYSTEMBACKDROP_TYPE, 3 = 亚克力)
/// · 更老的 Win11/10：SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)
/// · 都不支持时安静降级为纯色界面（不报错，不影响使用）
/// </summary>
public static class GlassService
{
    // ── Win32 交互 ──
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmsbtMainWindow = 2;      // Mica
    private const int DwmsbtTransientWindow = 3; // Acrylic

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;   // AABBGGRR
        public int AnimationId;
    }

    private enum AccentState
    {
        Disabled = 0,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
    }

    /// <summary>当前是否成功启用了系统毛玻璃（供界面显示"已启用/不可用"）。</summary>
    public static bool IsGlassActive { get; private set; }

    /// <summary>
    /// 应用玻璃效果。tintColor 是叠在玻璃上的底色（由主题底色 + 用户不透明度算出来）。
    /// </summary>
    public static void Apply(Window window, bool enabled, Color tintColor, bool darkTheme)
    {
        if (window is null) return;

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                handle = new WindowInteropHelper(window).EnsureHandle();
            }

            if (!enabled)
            {
                Disable(handle, darkTheme);
                IsGlassActive = false;
                return;
            }

            var win11 = Environment.OSVersion.Version.Build >= 22000;

            if (win11 && DwmSetWindowAttribute(handle, DwmwaSystemBackdropType,
                    ref TransientOrMain(), Marshal.SizeOf<int>()) == 0)
            {
                // 圆角 + 深色标题（标题栏是自绘的，但系统阴影/圆角仍走这里）
                var corner = 2; // round
                DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, Marshal.SizeOf<int>());

                IsGlassActive = true;
                AppLog.Info("ui", "glass-acrylic", ("mode", "dwm-backdrop"));
                return;
            }

            // 老系统：走 SetWindowCompositionAttribute 的亚克力模糊
            if (TryApplyAcrylic(handle, tintColor))
            {
                IsGlassActive = true;
                AppLog.Info("ui", "glass-acrylic", ("mode", "composition"));
                return;
            }

            IsGlassActive = false;
            AppLog.Info("ui", "glass-unavailable");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                       or InvalidOperationException or COMException)
        {
            IsGlassActive = false;
            AppLog.Warn("ui", "glass-failed", ("error", ex.Message));
        }
    }

    private static int _backdrop = DwmsbtTransientWindow;

    private static ref int TransientOrMain() => ref _backdrop;

    private static bool TryApplyAcrylic(IntPtr handle, Color tint)
    {
        // GradientColor 的字节序是 AABBGGRR
        var gradient = (tint.A << 24) | (tint.B << 16) | (tint.G << 8) | tint.R;

        var policy = new AccentPolicy
        {
            AccentState = (int)AccentState.EnableAcrylicBlurBehind,
            AccentFlags = 2,
            GradientColor = gradient,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var pointer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(policy, pointer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = 19,         // WCA_ACCENT_POLICY
                Data = pointer,
                SizeOfData = size,
            };

            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static void Disable(IntPtr handle, bool darkTheme)
    {
        try
        {
            var none = 0;
            DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref none, Marshal.SizeOf<int>());

            var dark = darkTheme ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, Marshal.SizeOf<int>());

            var policy = new AccentPolicy { AccentState = (int)AccentState.Disabled };
            var size = Marshal.SizeOf<AccentPolicy>();
            var pointer = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(policy, pointer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = 19,
                    Data = pointer,
                    SizeOfData = size,
                };

                SetWindowCompositionAttribute(handle, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 老系统没有这些 API：忽略
        }
    }
}
