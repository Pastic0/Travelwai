namespace TravelwAI.Data.Options;

public sealed class EmailOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "TravelwAI";
    public bool EnableSsl { get; set; } = true;
    public string Provider { get; set; } = "Resend";
    public string ResendApiKey { get; set; } = string.Empty;
    public string ResendFrom { get; set; } = string.Empty;
}
