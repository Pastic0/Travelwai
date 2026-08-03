namespace TravelwAI.Web.Options;

public sealed class ExternalKnowledgeOptions
{
    public bool Enabled { get; set; } = true;
    public bool AutoImportOnStartup { get; set; } = true;
    public bool RefreshOnStartup { get; set; }
    public string DataDirectory { get; set; } = "App_Data/ai-knowledge";
    public int RequestTimeoutMinutes { get; set; } = 30;
    public int MaxInMemoryDocuments { get; set; } = 60000;
    public int MaxContextDocuments { get; set; } = 8;
    public int MaxContextCharacters { get; set; } = 12000;
    public int MinimumMatchScore { get; set; } = 4;
    public string KaggleApiToken { get; set; } = string.Empty;
    public string KaggleUsername { get; set; } = string.Empty;
    public string KaggleApiKey { get; set; } = string.Empty;
    public List<ExternalKnowledgeSourceOptions> Sources { get; set; } = new();
}

public sealed class ExternalKnowledgeSourceOptions
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Format { get; set; } = "json";
    public bool Enabled { get; set; } = true;
    public int MaxDocuments { get; set; } = 30000;
    public string Attribution { get; set; } = string.Empty;
    public string License { get; set; } = string.Empty;
}
