using System.Text.Json.Serialization;

namespace TravelwAI.Web.Models;

public sealed class AiChatRequest
{
    public string Message { get; set; } = string.Empty;
    public List<AiChatHistoryItem> History { get; set; } = new();
    public string ReferenceContext { get; set; } = string.Empty;
    public List<string> Images { get; set; } = new();
    public string Language { get; set; } = "auto";
}

public sealed class AiChatHistoryItem
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public sealed class LocationImageAnalysisRequest
{
    public string Image { get; set; } = string.Empty;
    public string Language { get; set; } = "vi";
}

public sealed class AiTextTranslationRequest
{
    public string Text { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = "vi";
}

public sealed class OllamaChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OllamaMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OllamaGenerationOptions? Options { get; set; }
}

public sealed class OllamaGenerationOptions
{
    [JsonPropertyName("num_predict")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NumPredict { get; set; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }
}

public sealed class OllamaMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("images")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Images { get; set; }
}

public sealed class OllamaChatResponse
{
    [JsonPropertyName("message")]
    public OllamaMessage? Message { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
