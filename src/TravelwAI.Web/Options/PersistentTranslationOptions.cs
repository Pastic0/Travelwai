namespace TravelwAI.Web.Options;

public sealed class PersistentTranslationOptions
{
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 3;
    public int BatchSize { get; set; } = 3;
    public int LockMinutes { get; set; } = 10;
    public int MaxAttempts { get; set; } = 12;
    public int MaxSourceLength { get; set; } = 20000;
    public int TranslationChunkLength { get; set; } = 950;

    public string[] Collections { get; set; } = new[]
    {
        "tours",
        "travel_posts",
        "provinces",
        "destinations",
        "business_applications",
        "tour_orders",
        "plan_orders",
        "notifications",
        "plan_status_options",
        "province_tags",
        "province_travel_tags",
        "plan_travel_tags",
        "post_tour_offers",
        "tour_offer_invites"
    };
}
