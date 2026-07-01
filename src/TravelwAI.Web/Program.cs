using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Npgsql;
using System.Text.Json;
using System.IO.Compression;
using TravelwAI.Business.Interfaces;
using TravelwAI.Business.Services;
using TravelwAI.Data.Interfaces;
using TravelwAI.Data.Options;
using TravelwAI.Data.Repositories;
using TravelwAI.Data.Services;
using TravelwAI.Web.Hubs;
using TravelwAI.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SupabaseOptions>(builder.Configuration.GetSection("Supabase"));
builder.Services.PostConfigure<SupabaseOptions>(options =>
{
    var configuration = builder.Configuration;

    options.Url = FirstNonEmpty(configuration["Supabase:Url"], configuration["SUPABASE_URL"], options.Url);
    options.ProjectRef = FirstNonEmpty(configuration["Supabase:ProjectRef"], configuration["SUPABASE_PROJECT_REF"], options.ProjectRef);
    options.DatabasePassword = FirstNonEmpty(configuration["Supabase:DatabasePassword"], configuration["SUPABASE_DATABASE_PASSWORD"], options.DatabasePassword);
    options.ConnectionString = FirstNonEmpty(configuration["Supabase:ConnectionString"], configuration["SUPABASE_CONNECTION_STRING"], configuration["DATABASE_URL"], options.ConnectionString);
    options.JwtSecret = FirstNonEmpty(configuration["Supabase:JwtSecret"], configuration["SUPABASE_JWT_SECRET"], options.JwtSecret);

    options.StorageBucket = FirstNonEmpty(configuration["Supabase:StorageBucket"], configuration["SUPABASE_STORAGE_BUCKET"], options.StorageBucket, "travelwai-uploads");
    options.StorageApiKey = FirstNonEmpty(
        configuration["Supabase:StorageApiKey"],
        configuration["SUPABASE_STORAGE_API_KEY"],
        configuration["SUPABASE_SERVICE_ROLE_KEY"],
        configuration["SUPABASE_ANON_KEY"],
        options.StorageApiKey);
    options.StoragePublicUrl = FirstNonEmpty(configuration["Supabase:StoragePublicUrl"], configuration["SUPABASE_STORAGE_PUBLIC_URL"], options.StoragePublicUrl);
    options.StorageEnabled = FirstBool(configuration["Supabase:StorageEnabled"], configuration["SUPABASE_STORAGE_ENABLED"], options.StorageEnabled);
    options.StorageFallbackToLocal = FirstBool(configuration["Supabase:StorageFallbackToLocal"], configuration["SUPABASE_STORAGE_FALLBACK_TO_LOCAL"], options.StorageFallbackToLocal);
});
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.PostConfigure<EmailOptions>(options =>
{
    var configuration = builder.Configuration;

    options.Provider = FirstNonEmpty(configuration["Email:Provider"], configuration["EMAIL_PROVIDER"], configuration["MAIL_PROVIDER"], options.Provider, "Resend");
    options.DisplayName = FirstNonEmpty(configuration["Email:DisplayName"], configuration["EMAIL_DISPLAY_NAME"], configuration["RESEND_DISPLAY_NAME"], options.DisplayName, "TravelwAI");

    options.ResendApiKey = FirstNonEmpty(configuration["Resend:ApiKey"], configuration["RESEND_API_KEY"], configuration["RESEND_KEY"], configuration["Email:ResendApiKey"], configuration["EMAIL_RESEND_API_KEY"], options.ResendApiKey);
    options.ResendFrom = FirstNonEmpty(configuration["Resend:From"], configuration["RESEND_FROM"], configuration["Email:ResendFrom"], configuration["EMAIL_RESEND_FROM"], options.ResendFrom);

    options.From = FirstNonEmpty(configuration["Email:From"], configuration["EMAIL_FROM"], configuration["MAIL_FROM"], options.From);
    options.Host = FirstNonEmpty(configuration["Email:Host"], configuration["EMAIL_HOST"], options.Host);
    options.Username = FirstNonEmpty(configuration["Email:Username"], configuration["EMAIL_USERNAME"], configuration["EMAIL_USER"], options.Username);
    options.Password = FirstNonEmpty(configuration["Email:Password"], configuration["EMAIL_PASSWORD"], configuration["EMAIL_PASS"], options.Password);

    if (!string.IsNullOrWhiteSpace(options.ResendApiKey))
    {
        options.Provider = "Resend";
    }
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 1024;
});
builder.Services.AddHttpClient();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "image/svg+xml",
        "application/javascript",
        "text/css",
        "application/json"
    });
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var options = sp.GetRequiredService<IOptions<SupabaseOptions>>().Value;
    var connectionString = BuildSupabaseConnectionString(configuration, options);

    return new NpgsqlDataSourceBuilder(connectionString).Build();
});

builder.Services.AddScoped<IAuthRepository, SupabaseAuthRepository>();
builder.Services.AddScoped<IDataRepository, SupabaseDocumentRepository>();
builder.Services.AddSingleton<SupabaseSchemaInitializer>();

builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ITravelService, TravelService>();
builder.Services.AddScoped<IScheduleService, ScheduleService>();
builder.Services.AddScoped<IMemoryService, MemoryService>();
builder.Services.AddScoped<IFriendService, FriendService>();
builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<TourOrderAutomation>();
builder.Services.AddScoped<TourOfferService>();
builder.Services.AddScoped<EmailNotificationService>();
builder.Services.AddScoped<PlanQueueService>();
builder.Services.AddScoped<HeritageKnowledgeService>();
builder.Services.AddHostedService<TourOrderExpirationHostedService>();
builder.Services.AddHostedService<PlanGroupExpirationHostedService>();
builder.Services.AddHostedService<AccountPlanQueueHostedService>();

builder.Services.AddControllersWithViews()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = null;
        options.JsonSerializerOptions.DictionaryKeyPolicy = null;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.WriteIndented = false;
    });

builder.Services.AddCors(options =>
{
    options.AddPolicy("DevCors", policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseForwardedHeaders();
EnsureUploadFolders(app.Environment);

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var requestPath = context.Request.Path.Value ?? string.Empty;
        if (ShouldPreventPageCache(requestPath))
        {
            context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
        }
        return Task.CompletedTask;
    });

    await next();
});

using (var scope = app.Services.CreateScope())
{
    try
    {
        await scope.ServiceProvider.GetRequiredService<SupabaseSchemaInitializer>().EnsureCreatedAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SupabaseSchemaInitializer");
        logger.LogError(ex, "Không thể khởi tạo bảng Supabase khi khởi động. Web vẫn tiếp tục chạy; hãy kiểm tra biến môi trường Supabase trên Render.");
    }
}

app.UseCors("DevCors");
app.UseResponseCompression();
app.Use(async (context, next) =>
{
    if (await TryServeWebpVersionAsync(context, app.Environment))
    {
        return;
    }

    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var path = context.Context.Request.Path.Value ?? string.Empty;
        var maxAge = path.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromDays(30)
            : TimeSpan.FromDays(365);

        context.Context.Response.Headers["Cache-Control"] = $"public,max-age={(int)maxAge.TotalSeconds}";
        if (IsWebpCandidateImagePath(path))
        {
            context.Context.Response.Headers["Vary"] = "Accept";
        }
        context.Context.Response.Headers.Remove("Pragma");
        context.Context.Response.Headers.Remove("Expires");
    }
});
app.UseRouting();

var protectedPagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "/home",
    "/provinces",
    "/detail",
    "/schedule",
    "/plans",
    "/profile",
    "/messaging",
    "/contact",
    "/notifications",
    "/posts",
    "/tours",
    "/cart",
    "/checkout",
    "/tour-sales",
    "/manage",
    "/business",
    "/admin"
};

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (path.Length > 1) path = path.TrimEnd('/');

    if (!protectedPagePaths.Contains(path))
    {
        await next();
        return;
    }

    var authService = context.RequestServices.GetRequiredService<IAuthService>();

    if (context.Request.Cookies.TryGetValue("TravelwAIAuth", out var idToken) && !string.IsNullOrWhiteSpace(idToken))
    {
        try
        {
            var verifyResult = await authService.VerifyTokenAsync(Uri.UnescapeDataString(idToken));
            if (IsAuthSuccess(verifyResult))
            {
                if (!HasPageRoleAccess(path, verifyResult))
                {
                    await WriteForbiddenAsync(context, path);
                    return;
                }

                await next();
                return;
            }
        }
        catch
        {

        }
    }

    if (context.Request.Cookies.TryGetValue("TravelwAIRefresh", out var refreshToken) && !string.IsNullOrWhiteSpace(refreshToken))
    {
        try
        {
            var refreshResult = await authService.RefreshTokenAsync(Uri.UnescapeDataString(refreshToken));
            if (IsAuthSuccess(refreshResult)
                && refreshResult.GetValueOrDefault("idToken") is string newIdToken
                && !string.IsNullOrWhiteSpace(newIdToken))
            {
                context.Response.Cookies.Append("TravelwAIAuth", newIdToken, BuildAuthCookieOptions(context, TimeSpan.FromDays(7)));

                if (refreshResult.GetValueOrDefault("refreshToken") is string newRefreshToken && !string.IsNullOrWhiteSpace(newRefreshToken))
                {
                    context.Response.Cookies.Append("TravelwAIRefresh", newRefreshToken, BuildAuthCookieOptions(context, TimeSpan.FromDays(30)));
                }

                var refreshedVerifyResult = await authService.VerifyTokenAsync(newIdToken);
                if (!IsAuthSuccess(refreshedVerifyResult) || !HasPageRoleAccess(path, refreshedVerifyResult))
                {
                    await WriteForbiddenAsync(context, path);
                    return;
                }

                await next();
                return;
            }
        }
        catch
        {

        }
    }

    ClearAuthCookiesAndRedirectToLogin(context);
});

static bool ShouldPreventPageCache(string requestPath)
{
    if (string.IsNullOrWhiteSpace(requestPath) || requestPath == "/") return true;
    if (requestPath.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return false;

    var extension = Path.GetExtension(requestPath);
    return string.IsNullOrWhiteSpace(extension);
}

static bool IsWebpCandidateImagePath(string requestPath)
{
    var extension = Path.GetExtension(requestPath);
    return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
}

static async Task<bool> TryServeWebpVersionAsync(HttpContext context, IWebHostEnvironment environment)
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) return false;

    var requestPath = context.Request.Path.Value ?? string.Empty;
    if (!IsWebpCandidateImagePath(requestPath)) return false;

    var webRoot = environment.WebRootPath;
    if (string.IsNullOrWhiteSpace(webRoot)) return false;

    var relativePath = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
    var originalPath = Path.GetFullPath(Path.Combine(webRoot, relativePath));
    var webRootPath = Path.GetFullPath(webRoot);
    if (!originalPath.StartsWith(webRootPath, StringComparison.OrdinalIgnoreCase)) return false;

    var webpPath = Path.ChangeExtension(originalPath, ".webp");
    var originalExists = File.Exists(originalPath);
    var webpExists = File.Exists(webpPath);
    var accept = context.Request.Headers.Accept.ToString();
    var acceptsWebp = accept.Contains("image/webp", StringComparison.OrdinalIgnoreCase);

    if (acceptsWebp && webpExists && (!originalExists || File.GetLastWriteTimeUtc(webpPath) >= File.GetLastWriteTimeUtc(originalPath)))
    {
        await SendOptimizedImageAsync(context, webpPath, "image/webp", requestPath);
        return true;
    }

    if (!originalExists && webpExists)
    {
        await SendOptimizedImageAsync(context, webpPath, "image/webp", requestPath);
        return true;
    }

    return false;
}

static async Task SendOptimizedImageAsync(HttpContext context, string filePath, string contentType, string requestPath)
{
    context.Response.ContentType = contentType;
    context.Response.Headers["Cache-Control"] = requestPath.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase)
        ? "public,max-age=2592000"
        : "public,max-age=31536000";
    context.Response.Headers["Vary"] = "Accept";

    if (!HttpMethods.IsHead(context.Request.Method))
    {
        await context.Response.SendFileAsync(filePath);
    }
}

static string BuildSupabaseConnectionString(IConfiguration configuration, SupabaseOptions options)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection");

    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        connectionString = connectionString
            .Replace("{ProjectRef}", options.ProjectRef ?? string.Empty)
            .Replace("{DatabasePassword}", options.DatabasePassword ?? string.Empty);

        if (!connectionString.Contains("PASTE_DATABASE_PASSWORD_HERE", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("<your-supabase-db-password>", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("[YOUR-PASSWORD]", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }
    }

    var explicitConnectionString = FirstNonEmpty(
        options.ConnectionString,
        configuration["Supabase:ConnectionString"],
        configuration["SUPABASE_CONNECTION_STRING"],
        configuration["DATABASE_URL"]);

    if (!string.IsNullOrWhiteSpace(explicitConnectionString))
    {
        return explicitConnectionString;
    }

    var projectRef = string.IsNullOrWhiteSpace(options.ProjectRef) ? ExtractProjectRef(options.Url) : options.ProjectRef.Trim();
    var databasePassword = options.DatabasePassword?.Trim() ?? string.Empty;

    if (string.IsNullOrWhiteSpace(projectRef))
    {
        throw new InvalidOperationException("Chưa cấu hình Supabase:ProjectRef trong appsettings.json.");
    }

    if (string.IsNullOrWhiteSpace(databasePassword)
        || databasePassword.Equals("PASTE_DATABASE_PASSWORD_HERE", StringComparison.OrdinalIgnoreCase)
        || databasePassword.Equals("YOUR-PASSWORD", StringComparison.OrdinalIgnoreCase)
        || databasePassword.Equals("<your-supabase-db-password>", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Chưa nhập Supabase:DatabasePassword trong appsettings.json.");
    }

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = $"db.{projectRef}.supabase.co",
        Port = 5432,
        Database = "postgres",
        Username = "postgres",
        Password = databasePassword,
        SslMode = SslMode.Require
    };

    return builder.ConnectionString;
}

static string FirstNonEmpty(params string?[] values)
{
    foreach (var value in values)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }
    }

    return string.Empty;
}

static bool FirstBool(string? first, string? second, bool defaultValue)
{
    foreach (var value in new[] { first, second })
    {
        if (bool.TryParse(value, out var parsed)) return parsed;
        if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.Equals(value, "0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "n", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    return defaultValue;
}

static string ExtractProjectRef(string? supabaseUrl)
{
    if (string.IsNullOrWhiteSpace(supabaseUrl)) return string.Empty;

    if (Uri.TryCreate(supabaseUrl, UriKind.Absolute, out var uri))
    {
        var host = uri.Host;
        const string suffix = ".supabase.co";
        if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return host[..^suffix.Length];
        }
    }

    return string.Empty;
}

static bool IsAuthSuccess(Dictionary<string, object?> result)
{
    return result.TryGetValue("success", out var success) && success is bool ok && ok;
}

static CookieOptions BuildAuthCookieOptions(HttpContext context, TimeSpan maxAge) => new()
{
    Path = "/",
    MaxAge = maxAge,
    SameSite = SameSiteMode.Lax,
    Secure = context.Request.IsHttps,
    HttpOnly = false
};

static void ClearAuthCookiesAndRedirectToLogin(HttpContext context)
{
    context.Response.Cookies.Delete("TravelwAIAuth", new CookieOptions { Path = "/" });
    context.Response.Cookies.Delete("TravelwAIRefresh", new CookieOptions { Path = "/" });

    var path = context.Request.Path.Value ?? "/home";
    if (path.Length > 1) path = path.TrimEnd('/');
    var returnUrl = $"{path}{context.Request.QueryString}";
    if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/'))
    {
        returnUrl = "/home";
    }

    context.Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
}

static bool HasPageRoleAccess(string path, Dictionary<string, object?> authResult)
{
    var user = authResult.GetValueOrDefault("user") as Dictionary<string, object?>;
    var role = user?.GetValueOrDefault("role")?.ToString() ?? "Free";

    if (string.Equals(path, "/admin", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "/manage", StringComparison.OrdinalIgnoreCase))
    {
        return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
    }

    if (string.Equals(path, "/tour-sales", StringComparison.OrdinalIgnoreCase))
    {
        return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Sales", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Tour Sales", StringComparison.OrdinalIgnoreCase);
    }

    if (string.Equals(path, "/business", StringComparison.OrdinalIgnoreCase))
    {
        return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Business", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Company", StringComparison.OrdinalIgnoreCase);
    }

    return true;
}

static async Task WriteForbiddenAsync(HttpContext context, string path)
{
    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    context.Response.ContentType = "text/html; charset=utf-8";
    var homeLink = path.Equals("/admin", StringComparison.OrdinalIgnoreCase) ? "/tour-sales" : "/home";
    await context.Response.WriteAsync($"""
        <!doctype html>
        <html lang="vi">
        <head><meta charset="utf-8"></head>
        <body style="font-family:Arial,sans-serif;background:#0f172a;color:white;display:grid;place-items:center;min-height:100vh;margin:0">
            <div style="max-width:520px;background:rgba(255,255,255,.08);border:1px solid rgba(255,255,255,.15);border-radius:22px;padding:28px;text-align:center">
                <h1>Không có quyền truy cập</h1>
                <p>Tài khoản hiện tại không đủ quyền để mở trang này.</p>
                <a href="{homeLink}" style="color:#fff;background:#2563eb;padding:12px 18px;border-radius:999px;text-decoration:none;display:inline-block;margin-top:10px">Quay lại</a>
            </div>
        </body>
        </html>
        """);
}

app.UseWebSockets();

app.MapGet("/health", () => Results.Ok(new { success = true, app = "TravelwAI", time = DateTime.UtcNow }));
app.MapControllers();
app.Map("/ws/conversations/{conversationId}", async (HttpContext context, IAuthService authService, IChatService chatService) =>
{
    await WebSocketChatMiddleware.HandleConversationSocket(context, authService, chatService);
});

app.Run();

static void EnsureUploadFolders(IWebHostEnvironment environment)
{
    var webRoot = environment.WebRootPath;
    if (string.IsNullOrWhiteSpace(webRoot))
    {
        webRoot = Path.Combine(environment.ContentRootPath, "wwwroot");
    }

    var uploadsRoot = Path.Combine(webRoot, "uploads");
    foreach (var folder in new[] { "", "memories", "tours", "profiles", "chat" })
    {
        var directory = string.IsNullOrWhiteSpace(folder)
            ? uploadsRoot
            : Path.Combine(uploadsRoot, folder);
        Directory.CreateDirectory(directory);

        var gitKeep = Path.Combine(directory, ".gitkeep");
        if (!File.Exists(gitKeep))
        {
            File.WriteAllText(gitKeep, string.Empty);
        }
    }
}
