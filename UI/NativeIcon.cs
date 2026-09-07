using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ChillFocusWhitelist.UI;

internal static class NativeIcon
{
    private const uint FileAttributeNormal = 0x80;
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint ShgfiUseFileAttributes = 0x000000200;
    private const int Bm = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr HIcon;
        public int IIcon;
        public uint DwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string SzDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string SzTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool FIcon;
        public uint XHotspot;
        public uint YHotspot;
        public IntPtr HbmMask;
        public IntPtr HbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader BmiHeader;
        public uint BmiColors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan,
        uint cScanLines, IntPtr lpvBits, ref BitmapInfo lpbmi, uint uUsage);

    public static Texture2D LoadTexture(string entry)
    {
        var path = AppIcon.ResolveExecutable(entry);
        if (path == null)
            return null;

        var info = new ShFileInfo();
        try
        {
            var result = SHGetFileInfo(path, FileAttributeNormal, ref info,
                (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon | ShgfiUseFileAttributes);
            if (result == IntPtr.Zero || info.HIcon == IntPtr.Zero)
                return null;

            try
            {
                return IconToTexture(info.HIcon);
            }
            finally
            {
                DestroyIcon(info.HIcon);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] icon load failed for " + path + ": " + e.Message);
            return null;
        }
    }

    private static Texture2D IconToTexture(IntPtr hIcon)
    {
        if (!GetIconInfo(hIcon, out var iconInfo))
            return null;
        if (iconInfo.HbmColor == IntPtr.Zero)
        {
            if (iconInfo.HbmMask != IntPtr.Zero)
                DeleteObject(iconInfo.HbmMask);
            return null;
        }

        var screenDc = CreateCompatibleDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            return null;

        IntPtr oldBitmap = IntPtr.Zero;
        try
        {
            oldBitmap = SelectObject(screenDc, iconInfo.HbmColor);
            var bi = new BitmapInfo();
            bi.BmiHeader.BiSize = (uint)Marshal.SizeOf<BitmapInfoHeader>();

            // 先取 BITMAP 信息，获得宽高与每像素位数。
            GetDIBits(screenDc, iconInfo.HbmColor, 0, 0, IntPtr.Zero, ref bi, 0);

            var width = bi.BmiHeader.BiWidth;
            var height = Math.Abs(bi.BmiHeader.BiHeight);
            var bitsPerPixel = bi.BmiHeader.BiBitCount;
            if (width <= 0 || height <= 0 || (bitsPerPixel != 32 && bitsPerPixel != 24))
                return null;

            var stride = ((width * bitsPerPixel + 31) / 32) * 4;
            var pixelBytes = stride * height;
            var buffer = Marshal.AllocHGlobal(pixelBytes);

            try
            {
                bi.BmiHeader.BiHeight = -height; // top-down DIB，方便直接转 RGBA
                bi.BmiHeader.BiSizeImage = (uint)pixelBytes;
                var lines = GetDIBits(screenDc, iconInfo.HbmColor, 0, (uint)height, buffer, ref bi, 0);
                if (lines == 0)
                    return null;

                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                var colors = new Color32[width * height];
                var rowBytes = width * 4;

                for (var y = 0; y < height; y++)
                {
                    var rowStart = y * stride;
                    for (var x = 0; x < width; x++)
                    {
                        var offset = rowStart + x * 4;
                        var b = Marshal.ReadByte(buffer, offset);
                        var g = Marshal.ReadByte(buffer, offset + 1);
                        var r = Marshal.ReadByte(buffer, offset + 2);
                        var a = bitsPerPixel == 32 ? Marshal.ReadByte(buffer, offset + 3) : byte.MaxValue;
                        colors[(height - 1 - y) * width + x] = new Color32(r, g, b, a);
                    }
                }

                texture.SetPixels32(colors);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.Apply(false, true);
                return texture;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
                SelectObject(screenDc, oldBitmap);
            DeleteDC(screenDc);
            if (iconInfo.HbmColor != IntPtr.Zero)
                DeleteObject(iconInfo.HbmColor);
            if (iconInfo.HbmMask != IntPtr.Zero)
                DeleteObject(iconInfo.HbmMask);
        }
    }
}
