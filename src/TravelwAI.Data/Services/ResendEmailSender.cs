using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TravelwAI.Data.Options;

namespace TravelwAI.Data.Services;

public static class ResendEmailSender
{
    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("https://api.resend.com/")
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static async Task<string?> SendPlainEmailAsync(
        EmailOptions options,
        string toEmail,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        toEmail = (toEmail ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(toEmail)) return "Không có email người nhận.";

        var configError = GetConfigError(options);
        if (!string.IsNullOrWhiteSpace(configError)) return configError;

        var apiKey = NormalizeApiKey(options.ResendApiKey);
        var from = ResolveFrom(options);
        var payload = new ResendEmailPayload(
            From: from,
            To: new[] { toEmail },
            Subject: subject ?? string.Empty,
            Text: body ?? string.Empty);

        using var request = new HttpRequestMessage(HttpMethod.Post, "emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        try
        {
            using var response = await HttpClient.SendAsync(request, cancellationToken);
            var result = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode) return null;

            return FormatResendError(response.StatusCode, result);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return "Resend gửi email quá thời gian chờ: " + ex.Message;
        }
        catch (Exception ex)
        {
            return "Không gửi được email qua Resend: " + ex.Message;
        }
    }

    public static string? GetConfigError(EmailOptions options)
    {
        if (options is null) return "Chưa cấu hình Resend.";

        var apiKey = NormalizeApiKey(options.ResendApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "Chưa cấu hình Resend. Trên Render cần đặt Resend__ApiKey và Resend__From.";
        }

        if (!apiKey.StartsWith("re_", StringComparison.OrdinalIgnoreCase))
        {
            return "Resend API Key không đúng. API Key thường bắt đầu bằng re_.";
        }

        var from = ResolveFrom(options);
        if (string.IsNullOrWhiteSpace(from))
        {
            return "Chưa cấu hình email gửi của Resend. Trên Render cần đặt Resend__From, ví dụ: TravelwAI <no-reply@travelwai.id.vn>.";
        }

        if (from.Contains("@gmail.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Resend__From không được dùng Gmail. Hãy dùng email thuộc domain đã xác minh trên Resend, ví dụ: TravelwAI <no-reply@travelwai.id.vn>.";
        }

        return null;
    }

    private static string ResolveFrom(EmailOptions options)
    {
        var from = FirstNonEmpty(options.ResendFrom, options.From);
        if (!string.IsNullOrWhiteSpace(from)) return from.Trim();

        return string.Empty;
    }

    private static string NormalizeApiKey(string? apiKey)
    {
        return string.Concat((apiKey ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return string.Empty;
    }

    private static string FormatResendError(HttpStatusCode statusCode, string responseBody)
    {
        var detail = string.IsNullOrWhiteSpace(responseBody) ? statusCode.ToString() : responseBody.Trim();
        if (detail.Contains("domain", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("verify", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return "Resend chưa xác minh domain hoặc Resend__From sai. Hãy verify domain trên Resend và dùng dạng TravelwAI <no-reply@travelwai.id.vn>. Chi tiết: " + detail;
        }

        if (detail.Contains("api key", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
            || statusCode == HttpStatusCode.Unauthorized)
        {
            return "Resend API Key sai hoặc đã bị thu hồi. Hãy tạo API Key mới và đặt lại biến Resend__ApiKey trên Render.";
        }

        if (detail.Contains("rate", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("limit", StringComparison.OrdinalIgnoreCase)
            || statusCode == (HttpStatusCode)429)
        {
            return "Resend đã vượt giới hạn gửi email gói hiện tại. Chi tiết: " + detail;
        }

        return "Resend gửi email lỗi: " + detail;
    }

    private sealed record ResendEmailPayload(string From, string[] To, string Subject, string Text);
}
