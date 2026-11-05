using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ImanSoftware.KeylessAgent.Wpf.Infrastructure;

/// <summary>
/// Applies the Windows 11 system backdrop and dark title bar to a window.
/// Never throws: on unsupported systems it reports false and the caller shows the
/// app-drawn fallback gradient.
/// </summary>
internal static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;   // Windows 11 22H2 (build 22621) or newer
    private const int RoundCorners = 2;

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>kind: 1 = none, 2 = Mica, 3 = Acrylic. Returns false when it could not be applied.</summary>
    public static bool TryApply(Window window, int kind)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;

            // Let the DWM draw behind the WPF content.
            var source = HwndSource.FromHwnd(handle);
            if (source?.CompositionTarget is not null)
                source.CompositionTarget.BackgroundColor = Colors.Transparent;

            var dark = 1;
            _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            var corners = RoundCorners;
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));
            var backdrop = kind;
            return DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
        }
        catch (Exception)
        {
            return false;   // the fallback gradient will be shown
        }
    }
}
