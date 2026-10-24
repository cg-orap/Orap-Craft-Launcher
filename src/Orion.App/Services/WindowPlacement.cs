using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Orion.App.Services;

/// <summary>
/// Fits a window into the work area of the monitor it is shown on.
///
/// WPF sizes windows in device independent pixels while the monitor reports physical pixels, so
/// on a 125%/150% scaled small screen a 1180 DIP window can be larger than the desktop. The
/// monitor rectangle is queried directly and converted with the window's own DPI so the result is
/// correct on per-monitor DPI setups.
/// </summary>
public static class WindowPlacement
{
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static void FitToMonitor(Window window)
    {
        void Fit()
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return;

                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info)) return;

                var dpi = VisualTreeHelper.GetDpi(window);
                var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
                var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

                var workWidth = (info.Work.Right - info.Work.Left) / scaleX;
                var workHeight = (info.Work.Bottom - info.Work.Top) / scaleY;
                var workLeft = info.Work.Left / scaleX;
                var workTop = info.Work.Top / scaleY;

                var maxWidth = Math.Max(640, workWidth - 16);
                var maxHeight = Math.Max(480, workHeight - 16);

                if (window.Width > maxWidth) window.Width = maxWidth;
                if (window.Height > maxHeight) window.Height = maxHeight;

                window.MinWidth = Math.Min(window.MinWidth, maxWidth);
                window.MinHeight = Math.Min(window.MinHeight, maxHeight);

                // Keep the window fully inside the work area.
                if (window.Left < workLeft) window.Left = workLeft;
                if (window.Top < workTop) window.Top = workTop;
                if (window.Left + window.Width > workLeft + workWidth)
                    window.Left = workLeft + workWidth - window.Width;
                if (window.Top + window.Height > workTop + workHeight)
                    window.Top = workTop + workHeight - window.Height;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Non Windows or restricted host: keep the default placement.
            }
        }

        Fit();
        window.Loaded += (_, _) => Fit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
