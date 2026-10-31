using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UnityObjectCopy.Native
{
    internal struct ImageSection
    {
        public string Name;
        public IntPtr Address;
        public int VirtualSize;
        public uint Characteristics;

        public bool IsExecutable { get { return (Characteristics & 0x20000000u) != 0u; } }
    }

    internal sealed class UnityPlayerImage
    {
        private const string ModuleName = "UnityPlayer.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder fileName, uint size);

        public IntPtr BaseAddress { get; private set; }
        public int SizeOfImage { get; private set; }
        public string FilePath { get; private set; }
        public string FileVersion { get; private set; }
        public string ProductVersion { get; private set; }
        public ImageSection[] Sections { get; private set; }

        private UnityPlayerImage() { }

        public static UnityPlayerImage TryLoad()
        {
            IntPtr module = GetModuleHandleW(ModuleName);
            if (module == IntPtr.Zero)
                return null;

            return FromBaseAddress(module, GetModulePath(module));
        }

        internal static UnityPlayerImage FromBaseAddress(IntPtr baseAddress, string filePath)
        {
            var image = new UnityPlayerImage { BaseAddress = baseAddress };
            if (!image.ReadHeaders())
                return null;

            image.FilePath = filePath;
            if (!string.IsNullOrEmpty(filePath))
            {
                try
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(filePath);
                    image.FileVersion = info.FileVersion;
                    image.ProductVersion = info.ProductVersion;
                }
                catch
                {
                }
            }

            return image;
        }

        public IntPtr RvaToPointer(int rva)
        {
            return new IntPtr(BaseAddress.ToInt64() + rva);
        }

        private bool ReadHeaders()
        {
            try
            {
                int lfanew = Marshal.ReadInt32(BaseAddress, 0x3C);
                IntPtr ntHeaders = new IntPtr(BaseAddress.ToInt64() + lfanew);
                if (Marshal.ReadInt32(ntHeaders) != 0x00004550) // "PE\0\0"
                    return false;

                short numberOfSections = Marshal.ReadInt16(ntHeaders, 6);
                short sizeOfOptionalHeader = Marshal.ReadInt16(ntHeaders, 20);
                IntPtr optionalHeader = new IntPtr(ntHeaders.ToInt64() + 24);
                if (Marshal.ReadInt16(optionalHeader) != 0x20B) // PE32+ only
                    return false;

                SizeOfImage = Marshal.ReadInt32(optionalHeader, 56);

                IntPtr sectionBase = new IntPtr(optionalHeader.ToInt64() + sizeOfOptionalHeader);
                var sections = new ImageSection[numberOfSections];
                for (int i = 0; i < numberOfSections; i++)
                {
                    IntPtr entry = new IntPtr(sectionBase.ToInt64() + i * 40);
                    sections[i] = new ImageSection
                    {
                        Name = ReadSectionName(entry),
                        VirtualSize = Marshal.ReadInt32(entry, 8),
                        Address = new IntPtr(BaseAddress.ToInt64() + Marshal.ReadInt32(entry, 12)),
                        Characteristics = (uint)Marshal.ReadInt32(entry, 36),
                    };
                }

                Sections = sections;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadSectionName(IntPtr nameField)
        {
            var builder = new StringBuilder(8);
            for (int i = 0; i < 8; i++)
            {
                byte c = Marshal.ReadByte(nameField, i);
                if (c == 0)
                    break;
                builder.Append((char)c);
            }
            return builder.ToString();
        }

        private static string GetModulePath(IntPtr module)
        {
            var buffer = new StringBuilder(1024);
            uint length = GetModuleFileNameW(module, buffer, (uint)buffer.Capacity);
            return length == 0 ? null : buffer.ToString();
        }
    }
}
