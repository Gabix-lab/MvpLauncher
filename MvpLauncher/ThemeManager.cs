using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace MvpLauncher
{
    public static class ThemeManager
    {
        private static string? _currentTheme;

        public static void ApplyTheme(string theme)
        {
            string effective = theme;
            if (theme == "System")
            {
                effective = IsWindowsLightTheme() ? "Light" : "Dark";
            }

            if (_currentTheme == effective) return;
            _currentTheme = effective;

            var dict = effective == "Light" ? BuildLight() : BuildDark();
            var app = Application.Current;
            if (app == null) return;

            var keysToRemove = new List<object>();
            foreach (var key in app.Resources.Keys)
            {
                if (key is string s && s.StartsWith("Clr"))
                {
                    keysToRemove.Add(key);
                }
            }
            foreach (var key in keysToRemove)
            {
                app.Resources.Remove(key);
            }

            foreach (var key in dict.Keys)
            {
                app.Resources[key] = dict[key];
            }
        }

        public static bool IsWindowsLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                var value = key?.GetValue("AppsUseLightTheme");
                if (value is int i) return i == 1;
            }
            catch { }
            return false;
        }

        private static ResourceDictionary BuildDark()
        {
            var d = new ResourceDictionary();
            d["ClrWindowBg"] = Brush(0x0F, 0x0F, 0x18);
            d["ClrWindowBgDeep"] = Brush(0x08, 0x08, 0x0C);
            d["ClrSidebarBg"] = Brush(0x0C, 0x0C, 0x12);
            d["ClrHeaderBg"] = Brush(0x0E, 0x0E, 0x16);
            d["ClrCardBg"] = Brush(0x10, 0x10, 0x1A);
            d["ClrCardBgDeep"] = Brush(0x0A, 0x0A, 0x12);
            d["ClrRowBg"] = Brush(0x13, 0x13, 0x1A);
            d["ClrSurface"] = Brush(0x1A, 0x1A, 0x24);
            d["ClrSurfaceHover"] = Brush(0x2A, 0x2A, 0x35);
            d["ClrBorder"] = Brush(0x1F, 0x1F, 0x2A);
            d["ClrBorderHover"] = Brush(0x2A, 0x2A, 0x35);
            d["ClrTextPrimary"] = Brush(0xFF, 0xFF, 0xFF);
            d["ClrTextSecondary"] = Brush(0xD4, 0xD4, 0xD8);
            d["ClrTextMuted"] = Brush(0x71, 0x71, 0x7A);
            d["ClrTextDim"] = Brush(0x52, 0x52, 0x5B);
            d["ClrAccent"] = Brush(0x25, 0x63, 0xEB);
            d["ClrAccentHover"] = Brush(0x1D, 0x4E, 0xD8);
            d["ClrAccentLight"] = Brush(0x60, 0xA5, 0xFA);
            d["ClrSuccess"] = Brush(0x22, 0xC5, 0x5E);
            d["ClrWarning"] = Brush(0xFF, 0xCA, 0x28);
            d["ClrDanger"] = Brush(0xFF, 0x52, 0x52);
            d["ClrOverlay"] = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
            d["ClrInputBg"] = Brush(0x13, 0x13, 0x1A);
            d["ClrInnerBg"] = Brush(0x0A, 0x0A, 0x12);
            return d;
        }

        private static ResourceDictionary BuildLight()
        {
            var d = new ResourceDictionary();
            d["ClrWindowBg"] = Brush(0xF4, 0xF4, 0xF5);
            d["ClrWindowBgDeep"] = Brush(0xE8, 0xE8, 0xEB);
            d["ClrSidebarBg"] = Brush(0xEC, 0xEC, 0xEF);
            d["ClrHeaderBg"] = Brush(0xFA, 0xFA, 0xFB);
            d["ClrCardBg"] = Brush(0xFF, 0xFF, 0xFF);
            d["ClrCardBgDeep"] = Brush(0xF0, 0xF0, 0xF4);
            d["ClrRowBg"] = Brush(0xFA, 0xFA, 0xFC);
            d["ClrSurface"] = Brush(0xE8, 0xE8, 0xEC);
            d["ClrSurfaceHover"] = Brush(0xDC, 0xDC, 0xE0);
            d["ClrBorder"] = Brush(0xE0, 0xE0, 0xE4);
            d["ClrBorderHover"] = Brush(0xC8, 0xC8, 0xCE);
            d["ClrTextPrimary"] = Brush(0x0F, 0x0F, 0x14);
            d["ClrTextSecondary"] = Brush(0x3F, 0x3F, 0x46);
            d["ClrTextMuted"] = Brush(0x71, 0x71, 0x7A);
            d["ClrTextDim"] = Brush(0xA1, 0xA1, 0xAA);
            d["ClrAccent"] = Brush(0x25, 0x63, 0xEB);
            d["ClrAccentHover"] = Brush(0x1D, 0x4E, 0xD8);
            d["ClrAccentLight"] = Brush(0x25, 0x63, 0xEB);
            d["ClrSuccess"] = Brush(0x16, 0xA3, 0x4A);
            d["ClrWarning"] = Brush(0xCA, 0x8A, 0x04);
            d["ClrDanger"] = Brush(0xDC, 0x26, 0x26);
            d["ClrOverlay"] = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
            d["ClrInputBg"] = Brush(0xFF, 0xFF, 0xFF);
            d["ClrInnerBg"] = Brush(0xF0, 0xF0, 0xF4);
            return d;
        }

        private static SolidColorBrush Brush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}