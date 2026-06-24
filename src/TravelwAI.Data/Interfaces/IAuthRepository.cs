namespace TravelwAI.Data.Interfaces;

public interface IAuthRepository
{
    Task<Dictionary<string, object?>> SignUpAsync(string email, string password, string username);
    Task<Dictionary<string, object?>> SignInAsync(string email, string password);
    Task<Dictionary<string, object?>> VerifyTokenAsync(string idToken);
    Task<Dictionary<string, object?>> RefreshTokenAsync(string refreshToken);
    Task<Dictionary<string, object?>> SendPasswordResetEmailAsync(string email);
    Task<Dictionary<string, object?>> VerifyPasswordResetOtpAsync(string email, string otp);
    Task<Dictionary<string, object?>> ResetPasswordWithTokenAsync(string email, string resetToken, string newPassword);
    Task<Dictionary<string, object?>> ChangePasswordAsync(string userId, string newPassword);
}
