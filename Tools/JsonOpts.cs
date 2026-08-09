using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComfyUIMCPSharp.Tools;

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };
}
