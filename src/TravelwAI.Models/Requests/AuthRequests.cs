using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace TravelwAI.Models.Requests;

public sealed class UserAccountRequest
{
    [JsonPropertyName("email")]
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    [Required]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("offerInvite")]
    public string OfferInvite { get; set; } = string.Empty;
}

public sealed class TokenRequest
{
    [JsonPropertyName("idToken")]
    public string IdToken { get; set; } = string.Empty;
}

public sealed class RefreshTokenRequest
{
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;
}

public sealed class ForgotPasswordRequest
{
    [JsonPropertyName("email")]
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;
}

public sealed class VerifyPasswordResetOtpRequest
{
    [JsonPropertyName("email")]
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("otp")]
    [Required]
    public string Otp { get; set; } = string.Empty;
}

public sealed class ResetPasswordRequest
{
    [JsonPropertyName("email")]
    [Required, EmailAddress]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("resetToken")]
    [Required]
    public string ResetToken { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    [Required]
    public string Password { get; set; } = string.Empty;
}

public sealed class ChangePasswordRequest
{
    [JsonPropertyName("password")]
    [Required]
    public string Password { get; set; } = string.Empty;
}

public sealed class GoogleLoginRequest
{
    [JsonPropertyName("idToken")]
    public string IdToken { get; set; } = string.Empty;
}

public sealed class FriendRequestPayload
{
    [JsonPropertyName("target_user_email")]
    public string TargetUserEmail { get; set; } = string.Empty;
}

public sealed class CreateConversationRequest
{
    [JsonPropertyName("other_user_id")]
    public string OtherUserId { get; set; } = string.Empty;

    [JsonPropertyName("participant_ids")]
    public List<string> ParticipantIds { get; set; } = new();

    [JsonPropertyName("group_name")]
    public string? GroupName { get; set; }
}

public sealed class UpdateConversationNameRequest
{
    [JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = string.Empty;
}
