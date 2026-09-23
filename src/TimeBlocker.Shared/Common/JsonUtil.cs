using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TimeBlocker.Shared.Common;

/// <summary>프로젝트 전체에서 동일한 JSON 옵션을 쓰기 위한 헬퍼.</summary>
public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = CreateOptions(indented: true);

    public static readonly JsonSerializerOptions CompactOptions = CreateOptions(indented: false);

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // 한글 주석/값이 \uXXXX 로 깨져 보이지 않도록.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : CompactOptions);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>
    /// 임시 파일에 쓴 뒤 교체하여, 쓰는 도중 전원이 꺼져도 기존 파일이 깨지지 않게 한다.
    /// </summary>
    public static void WriteFileAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));

        if (File.Exists(path))
        {
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
