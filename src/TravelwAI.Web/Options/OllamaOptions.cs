namespace TravelwAI.Web.Options;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "gemma4:31b-cloud";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxHistoryMessages { get; set; } = 12;
    public string SystemPrompt { get; set; } = "Bạn là TravelwAI, trợ lý AI du lịch Việt Nam. Hãy trả lời rõ ràng, hữu ích và an toàn bằng đúng ngôn ngữ của tin nhắn mới nhất của người dùng. Khi thông tin có thể thay đổi như giá, giờ mở cửa hoặc thời tiết, hãy nhắc người dùng kiểm tra nguồn hiện hành. Không bịa đặt dữ liệu. Nếu câu hỏi không liên quan du lịch, vẫn hỗ trợ ngắn gọn trong khả năng của bạn.";
}
