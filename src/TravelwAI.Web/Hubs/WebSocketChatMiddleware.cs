using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TravelwAI.Business.Interfaces;
using TravelwAI.Web.Services;

namespace TravelwAI.Web.Hubs;

public static class WebSocketChatMiddleware
{
    private static readonly ConcurrentDictionary<string, List<WebSocket>> Connections = new();

    public static async Task HandleConversationSocket(
        HttpContext context,
        IAuthService authService,
        IChatService chatService,
        InAppNotificationService notifications)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var conversationId = context.Request.RouteValues["conversationId"]?.ToString();
        var token = context.Request.Query["token"].ToString();
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var verify = await authService.VerifyTokenAsync(token);
        if (verify.GetValueOrDefault("success") is not bool ok || !ok || verify.GetValueOrDefault("user") is not Dictionary<string, object?> authUser)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var userId = authService.GetUserId(authUser);
        if (string.IsNullOrWhiteSpace(userId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var conversations = await chatService.GetConversationsAsync(userId);
        var conversation = conversations.FirstOrDefault(c => c.GetValueOrDefault("id")?.ToString() == conversationId);
        if (conversation is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var list = Connections.GetOrAdd(conversationId, _ => new List<WebSocket>());
        lock (list) list.Add(socket);

        var user = await chatService.GetUserByIdAsync(userId);
        var displayName = user?.GetValueOrDefault("username")?.ToString();
        if (string.IsNullOrWhiteSpace(displayName)) displayName = user?.GetValueOrDefault("name")?.ToString();
        if (string.IsNullOrWhiteSpace(displayName)) displayName = user?.GetValueOrDefault("email")?.ToString()?.Split('@').FirstOrDefault();
        if (string.IsNullOrWhiteSpace(displayName)) displayName = "Người dùng";

        try
        {
            await BroadcastAsync(conversationId, new
            {
                type = "status",
                status = "online",
                user_id = userId,
                username = displayName,
                message = $"{displayName} online"
            });

            while (socket.State == WebSocketState.Open)
            {
                var received = await ReceiveTextMessageAsync(socket);
                var result = received.result;
                if (result.MessageType == WebSocketMessageType.Close) break;
                var content = received.content;
                var sendResult = await chatService.SendMessageAsync(conversationId, userId, content);
                if (sendResult.Success && sendResult.MessageId is not null)
                {
                    await BroadcastAsync(conversationId, new
                    {
                        id = sendResult.MessageId,
                        sender_id = userId,
                        conversation_id = conversationId,
                        sender_name = displayName,
                        sender_info = user,
                        content = sendResult.Content,
                        timestamp = DateTime.UtcNow.ToString("O")
                    });

                    await CreateMessageNotificationsAsync(
                        notifications,
                        conversation,
                        conversationId,
                        userId,
                        displayName,
                        sendResult.MessageId);
                }
                else
                {
                    await SendAsync(socket, new
                    {
                        type = "error",
                        code = sendResult.ErrorCode,
                        message = sendResult.ErrorMessage ?? "Không thể gửi tin nhắn."
                    });
                }
            }
        }
        finally
        {
            lock (list) list.Remove(socket);
            if (list.Count == 0) Connections.TryRemove(conversationId, out _);

            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
        }
    }

    private static async Task CreateMessageNotificationsAsync(
        InAppNotificationService notifications,
        Dictionary<string, object?> conversation,
        string conversationId,
        string senderId,
        string senderName,
        string messageId)
    {
        var recipientIds = ReadStringList(conversation.GetValueOrDefault("participant_ids"));
        if (recipientIds.Count == 0)
        {
            recipientIds.Add(conversation.GetValueOrDefault("created_user")?.ToString() ?? string.Empty);
            recipientIds.Add(conversation.GetValueOrDefault("other_user")?.ToString() ?? string.Empty);
        }

        foreach (var recipientId in recipientIds
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Select(id => id.Trim())
                     .Where(id => !string.Equals(id, senderId, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal))
        {
            await notifications.CreateForUserAsync(
                recipientId,
                "message",
                InAppNotificationService.MessageNewCategory,
                "Tin nhắn mới",
                $"Bạn có tin nhắn mới từ {senderName}.",
                $"/messaging?conversationId={Uri.EscapeDataString(conversationId)}",
                "message",
                messageId,
                "message-new",
                metadata: new Dictionary<string, object?>
                {
                    ["conversation_id"] = conversationId,
                    ["sender_id"] = senderId,
                    ["sender_name"] = senderName
                });
        }
    }

    private static List<string> ReadStringList(object? value)
    {
        if (value is null) return new List<string>();
        if (value is string text) return string.IsNullOrWhiteSpace(text) ? new List<string>() : new List<string> { text };
        if (value is System.Collections.IEnumerable items)
        {
            var result = new List<string>();
            foreach (var item in items)
            {
                var itemText = item?.ToString();
                if (!string.IsNullOrWhiteSpace(itemText)) result.Add(itemText);
            }
            return result;
        }
        return new List<string>();
    }

    private static async Task BroadcastAsync(string conversationId, object payload)
    {
        if (!Connections.TryGetValue(conversationId, out var list)) return;
        WebSocket[] sockets;
        lock (list) sockets = list.Where(s => s.State == WebSocketState.Open).ToArray();
        foreach (var socket in sockets) await SendAsync(socket, payload);
    }

    private static Task SendAsync(WebSocket socket, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<(WebSocketReceiveResult result, string content)> ReceiveTextMessageAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) return (result, string.Empty);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (result, Encoding.UTF8.GetString(stream.ToArray()));
    }
}
