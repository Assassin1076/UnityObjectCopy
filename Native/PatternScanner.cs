using System;
using System.Globalization;

namespace UnityObjectCopy.Native
{
    /// <summary>
    /// 带通配符（??）的字节特征码扫描器。
    /// </summary>
    internal static unsafe class PatternScanner
    {
        /// <summary>
        /// 把 "4C 89 ?? E8 ?? ?? ?? ??" 这类文本解析为字节串 + 掩码（1 = 需要精确匹配）。
        /// </summary>
        public static bool TryParse(string text, out byte[] pattern, out byte[] mask, out string error)
        {
            pattern = null;
            mask = null;
            error = null;

            if (string.IsNullOrEmpty(text))
            {
                error = "特征码为空";
                return false;
            }

            string[] tokens = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[tokens.Length];
            var bits = new byte[tokens.Length];

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (token == "?" || token == "??")
                {
                    bytes[i] = 0;
                    bits[i] = 0;
                    continue;
                }

                byte value;
                if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                {
                    error = "无法解析特征码片段: " + token;
                    return false;
                }

                bytes[i] = value;
                bits[i] = 1;
            }

            pattern = bytes;
            mask = bits;
            return true;
        }

        public static bool Matches(IntPtr address, byte[] pattern, byte[] mask)
        {
            byte* p = (byte*)address;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (mask[i] != 0 && p[i] != pattern[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 执行雷霆大扫描，尝试通过特征码在 UnityPlayer.dll 的可执行段中找到匹配的函数地址。
        /// </summary>
        public static IntPtr Find(UnityPlayerImage image, byte[] pattern, byte[] mask, out int hitCount)
        {
            hitCount = 0;
            IntPtr firstHit = IntPtr.Zero;

            byte firstByte = pattern[0];
            bool firstByteFixed = mask[0] != 0;

            foreach (ImageSection section in image.Sections)
            {
                if (!section.IsExecutable || section.VirtualSize <= pattern.Length)
                    continue;

                byte* begin = (byte*)section.Address;
                int limit = section.VirtualSize - pattern.Length;

                for (int offset = 0; offset <= limit; offset++)
                {
                    if (firstByteFixed && begin[offset] != firstByte)
                        continue;

                    int k = 1;
                    for (; k < pattern.Length; k++)
                    {
                        if (mask[k] != 0 && begin[offset + k] != pattern[k])
                            break;
                    }

                    if (k != pattern.Length)
                        continue;

                    hitCount++;
                    if (firstHit == IntPtr.Zero)
                        firstHit = new IntPtr(section.Address.ToInt64() + offset);
                }
            }

            return firstHit;
        }
    }
}
