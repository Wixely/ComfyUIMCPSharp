using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ComfyUIMCPSharp.Hosting;

public static class McpSharpIcon
{
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int LrDefaultColor = 0;
    private const int WmSetIcon = 0x0080;
    private const uint ResourceVersion = 0x00030000;
    private const string ResourceName = "MCPSharp.wmcp.ico";

    private static readonly Lazy<byte[]> IconBytes = new(LoadIconBytes);

    public static void MapFavicon(this WebApplication app)
    {
        app.MapGet("/favicon.ico", static () => Results.File(IconBytes.Value, "image/x-icon"));
    }

    public static void ApplyConsoleWindowIcon()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var window = GetConsoleWindow();
        if (window == IntPtr.Zero)
        {
            return;
        }

        SetConsoleIcon(window, IconSmall, 16);
        SetConsoleIcon(window, IconBig, 32);
    }

    private static byte[] LoadIconBytes()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void SetConsoleIcon(IntPtr window, int iconSize, int desiredSize)
    {
        var image = SelectIconImage(IconBytes.Value, desiredSize);
        var icon = CreateIconFromResourceEx(image, (uint)image.Length, true, ResourceVersion, desiredSize, desiredSize, LrDefaultColor);
        if (icon != IntPtr.Zero)
        {
            SendMessage(window, WmSetIcon, iconSize, icon);
        }
    }

    private static byte[] SelectIconImage(byte[] ico, int desiredSize)
    {
        if (ico.Length < 6 || ReadUInt16(ico, 2) != 1)
        {
            throw new InvalidOperationException("Embedded MCPSharp icon is not a valid ICO file.");
        }

        var count = ReadUInt16(ico, 4);
        var bestOffset = 0;
        var bestLength = 0;
        var bestScore = int.MaxValue;

        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (i * 16);
            if (entry + 16 > ico.Length)
            {
                break;
            }

            var width = ico[entry] == 0 ? 256 : ico[entry];
            var height = ico[entry + 1] == 0 ? 256 : ico[entry + 1];
            var bitCount = ReadUInt16(ico, entry + 6);
            var length = (int)ReadUInt32(ico, entry + 8);
            var offset = (int)ReadUInt32(ico, entry + 12);
            var score = Math.Abs(width - desiredSize) + Math.Abs(height - desiredSize) - bitCount;

            if (offset >= 0 && length > 0 && offset + length <= ico.Length && score < bestScore)
            {
                bestScore = score;
                bestOffset = offset;
                bestLength = length;
            }
        }

        return bestLength > 0
            ? ico.AsSpan(bestOffset, bestLength).ToArray()
            : throw new InvalidOperationException("Embedded MCPSharp icon does not contain any icon images.");
    }

    private static ushort ReadUInt16(byte[] value, int startIndex) =>
        (ushort)(value[startIndex] | (value[startIndex + 1] << 8));

    private static uint ReadUInt32(byte[] value, int startIndex) =>
        (uint)(value[startIndex] | (value[startIndex + 1] << 8) | (value[startIndex + 2] << 16) | (value[startIndex + 3] << 24));

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] pbIconBits, uint cbIconBits, bool fIcon, uint dwVersion, int cxDesired, int cyDesired, int flags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);
}
