using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MvpLauncher
{
    public static class ThemeManager
    {
        public static void ApplyTheme(string theme)
        {
            string effective = theme;
            if (theme == "System")
            {
                effective = IsWindowsLightTheme() ? "Light" : "Dark";
            }

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
            d["ClrWindowBg"] = Brush(0x12, 0x12, 0x14);
            d["ClrCardBg"] = Brush(0x18, 0x18, 0x1B);
            d["ClrSurface"] = Brush(0x27, 0x27, 0x2A);
            d["ClrBorder"] = Brush(0x3F, 0x3F, 0x46);
            d["ClrBorderHover"] = Brush(0x52, 0x52, 0x5B);
            d["ClrTextPrimary"] = Brush(0xFF, 0xFF, 0xFF);
            d["ClrTextSecondary"] = Brush(0xD4, 0xD4, 0xD8);
            d["ClrTextMuted"] = Brush(0x71, 0x71, 0x7A);
            d["ClrTextDim"] = Brush(0x52, 0x52, 0x5B);
            d["ClrAccent"] = Brush(0x25, 0x63, 0xEB);
            d["ClrAccentHover"] = Brush(0x1D, 0x4E, 0xD8);
            d["ClrAccentLight"] = Brush(0x3B, 0x82, 0xF6);
            d["ClrSuccess"] = Brush(0x4C, 0xAF, 0x50);
            d["ClrWarning"] = Brush(0xFF, 0xCA, 0x28);
            d["ClrDanger"] = Brush(0xFF, 0x52, 0x52);
            d["ClrOverlay"] = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
            d["ClrInputBg"] = Brush(0x27, 0x27, 0x2A);
            d["ClrRowBg"] = Brush(0x27, 0x27, 0x2A);
            d["ClrInnerBg"] = Brush(0x12, 0x12, 0x14);
            return d;
        }

        private static ResourceDictionary BuildLight()
        {
            var d = new ResourceDictionary();
            d["ClrWindowBg"] = Brush(0xF4, 0xF4, 0xF5);
            d["ClrCardBg"] = Brush(0xFF, 0xFF, 0xFF);
            d["ClrSurface"] = Brush(0xE4, 0xE4, 0xE7);
            d["ClrBorder"] = Brush(0xD4, 0xD4, 0xD8);
            d["ClrBorderHover"] = Brush(0xA1, 0xA1, 0xAA);
            d["ClrTextPrimary"] = Brush(0x18, 0x18, 0x1B);
            d["ClrTextSecondary"] = Brush(0x3F, 0x3F, 0x46);
            d["ClrTextMuted"] = Brush(0x71, 0x71, 0x7A);
            d["ClrTextDim"] = Brush(0xA1, 0xA1, 0xAA);
            d["ClrAccent"] = Brush(0x25, 0x63, 0xEB);
            d["ClrAccentHover"] = Brush(0x1D, 0x4E, 0xD8);
            d["ClrAccentLight"] = Brush(0x3B, 0x82, 0xF6);
            d["ClrSuccess"] = Brush(0x16, 0xA3, 0x4A);
            d["ClrWarning"] = Brush(0xCA, 0x8A, 0x04);
            d["ClrDanger"] = Brush(0xDC, 0x26, 0x26);
            d["ClrOverlay"] = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00));
            d["ClrInputBg"] = Brush(0xFA, 0xFA, 0xFA);
            d["ClrRowBg"] = Brush(0xF4, 0xF4, 0xF5);
            d["ClrInnerBg"] = Brush(0xE4, 0xE4, 0xE7);
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