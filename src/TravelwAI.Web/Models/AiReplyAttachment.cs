namespace TravelwAI.Web.Models;

public sealed record AiReplyAttachment(
    string Url,
    string Name,
    string ContentType,
    long Size,
    string Type);
