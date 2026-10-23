namespace NexMesh.AI.Configuration;

/// <summary>NexMesh AI 配置节(NexMesh:AI)。MaxToolsPerRequest 由注册器直接读取同节键值(默认 10)。</summary>
public class NexMeshAiOptions
{
    public const string SectionName = "NexMesh:AI";
    public string DefaultProvider { get; set; } = "openai-compatible";
    public Dictionary<string, LlmProviderConfig> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Provider 配置。Type:openai-compatible(覆盖 OpenAI/DeepSeek/Ollama 等)/ stub(离线开发,固定回复)。
/// </summary>
public class LlmProviderConfig
{
    public string Type { get; set; } = "openai-compatible";
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
}
