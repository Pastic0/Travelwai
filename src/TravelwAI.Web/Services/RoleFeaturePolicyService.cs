namespace TravelwAI.Web.Services;

public sealed class RoleFeaturePolicyService
{
    public const int UsageWindowMinutes = 10;

    public RoleFeaturePolicy GetPolicy(object? role)
    {
        var normalized = NormalizeRole(role);
        return normalized switch
        {
            "VIP" => new RoleFeaturePolicy(normalized, false, 5, 7, true, false, false),
            "Premium" => new RoleFeaturePolicy(normalized, true, 0, 0, true, false, true),
            "Admin" => new RoleFeaturePolicy(normalized, true, 0, 0, true, true, true),
            "Sales" => new RoleFeaturePolicy(normalized, true, 0, 0, true, true, true),
            "Company" => new RoleFeaturePolicy(normalized, true, 0, 0, true, true, true),
            _ => new RoleFeaturePolicy("Free", false, 2, 3, false, false, false)
        };
    }

    public static string NormalizeRole(object? role)
    {
        var value = role?.ToString()?.Trim() ?? string.Empty;
        if (value.Equals("User", StringComparison.OrdinalIgnoreCase)) return "Free";
        if (value.Equals("Business", StringComparison.OrdinalIgnoreCase)) return "Company";
        if (value.Equals("Tour Sales", StringComparison.OrdinalIgnoreCase) || value.Equals("TourSales", StringComparison.OrdinalIgnoreCase)) return "Sales";
        if (value.Equals("VIP", StringComparison.OrdinalIgnoreCase)) return "VIP";
        if (value.Equals("Premium", StringComparison.OrdinalIgnoreCase)) return "Premium";
        if (value.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return "Admin";
        if (value.Equals("Sales", StringComparison.OrdinalIgnoreCase)) return "Sales";
        if (value.Equals("Company", StringComparison.OrdinalIgnoreCase)) return "Company";
        return "Free";
    }
}

public sealed record RoleFeaturePolicy(
    string Role,
    bool CanUseAiItinerary,
    int AiPostLimitPerWindow,
    int AiChatLimitPerWindow,
    bool CanChangeChatbotStyle,
    bool HasAllChatbotStyles,
    bool CanUsePostOffer)
{
    public int WindowMinutes => RoleFeaturePolicyService.UsageWindowMinutes;
}
