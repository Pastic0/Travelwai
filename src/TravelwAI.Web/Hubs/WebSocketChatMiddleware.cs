using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TravelwAI.Business.Interfaces;

namespace TravelwAI.Web.Hubs;

public static class WebSocketChatMiddleware
{
    private static readonly ConcurrentDictionary<string, List<WebSocket>> Connections = new();

    public static async Task HandleConversationSocket(HttpContext context, IAuthService authService, IChatService chatService)
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
        if (!conversations.Any(c => c.GetValueOrDefault("id")?.ToString() == conversationId))
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
                var messageId = await chatService.SendMessageAsync(conversationId, userId, content);
                if (messageId is not null)
                {
                    await BroadcastAsync(conversationId, new
                    {
                        id = messageId,
                        sender_id = userId,
                        conversation_id = conversationId,
                        sender_name = displayName,
                        sender_info = user,
                        content,
                        timestamp = DateTime.UtcNow.ToString("O")
                    });
                }
                else
                {
                    await SendAsync(socket, new { type = "error", message = "Failed to send message." });
                }
            }
        }
        finally
        {
            lock (list) list.Remove(socket);
            if (list.Count == 0) Connections.TryRemove(conversationId, out _);

            await BroadcastAsync(conversationId, new
            {
                type = "status",
                status = "offline",
                user_id = userId,
                username = displayName,
                message = $"{displayName} offline"
            });

            if (socket.State == WebSocketState.Open)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
        }
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
