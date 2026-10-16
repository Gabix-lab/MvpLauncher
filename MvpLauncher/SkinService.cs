using System;

namespace MvpLauncher
{
    public static class SkinService
    {
        public static string GetBodyUrl(string uuidOrName)
        {
            if (string.IsNullOrEmpty(uuidOrName)) return "";
            return $"https://mc-heads.net/body/{Uri.EscapeDataString(uuidOrName)}/180";
        }

        public static string GetHeadUrl(string uuidOrName)
        {
            if (string.IsNullOrEmpty(uuidOrName)) return "";
            return $"https://mc-heads.net/avatar/{Uri.EscapeDataString(uuidOrName)}/64";
        }
    }
}