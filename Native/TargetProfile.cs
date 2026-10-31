using System;

namespace UnityObjectCopy.Native
{
    internal struct KnownBuild
    {
        public string Version;
        public int InstantiateObjectRva;
        public string Note;
    }

    /// <summary>
    /// 与 UnityPlayer 版本相关的全部数据都集中在这里：
    /// </summary>
    internal static class TargetProfile
    {
        /// <summary>
        /// <c>Object::InstantiateObject</c>（5 参数内部版本）的函数头部特征码。
        /// </summary>
        public const string InstantiateObjectSignature =
            "4C 89 4C 24 20 4C 89 44 24 18 41 57 48 81 EC 90 00 00 00 " +
            "4C 8B 84 24 C0 00 00 00 E8 ?? ?? ?? ?? 4C 8B F8 48 85 C0 0F 84 ?? ?? ?? ?? 8B 50 0C";

        /// <summary>
        /// 已确认的构建版本 -> RVA（相对 UnityPlayer.dll 的 ImageBase，本机为 0x180000000）。
        /// </summary>
        public static readonly KnownBuild[] KnownBuilds =
        {
            new KnownBuild
            {
                Version = "2022.3.62f2",
                InstantiateObjectRva = 0x44FE20,
                Note = "UnityPlayer 2022.3.62.7762112 / 游戏版本 2022.3.62f2 (7670c08855a9)",
            },
        };

        public static bool MatchesVersion(string productVersion, string fileVersion, string buildVersion)
        {
            if (string.IsNullOrEmpty(buildVersion))
                return false;

            if (!string.IsNullOrEmpty(productVersion) &&
                productVersion.IndexOf(buildVersion, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            string numericPrefix = NumericPrefix(buildVersion);
            return !string.IsNullOrEmpty(numericPrefix) &&
                   !string.IsNullOrEmpty(fileVersion) &&
                   fileVersion.StartsWith(numericPrefix + ".", StringComparison.OrdinalIgnoreCase);
        }

        private static string NumericPrefix(string version)
        {
            int end = 0;
            while (end < version.Length && (char.IsDigit(version[end]) || version[end] == '.'))
                end++;
            return version.Substring(0, end).TrimEnd('.');
        }
    }
}
