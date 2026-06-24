namespace TravelwAI.Models.Common;

public sealed class ApiResponse<T>
{
    public bool success { get; set; }
    public T? data { get; set; }
    public string message { get; set; } = string.Empty;
    public int? total { get; set; }

    public static ApiResponse<T> Ok(T? data, string message = "Success", int? total = null) => new()
    {
        success = true,
        data = data,
        message = message,
        total = total
    };

    public static ApiResponse<T> Fail(string message, T? data = default) => new()
    {
        success = false,
        data = data,
        message = message
    };
}
