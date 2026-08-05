using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Npgsql;
using System.Text.Json;
using System.IO.Compression;
using TravelwAI.Business.Exceptions;
using TravelwAI.Business.Interfaces;
using TravelwAI.Business.Services;
using TravelwAI.Data.Interfaces;
using TravelwAI.Data.Options;
using TravelwAI.Data.Repositories;
using TravelwAI.Data.Services;
using TravelwAI.Web.Hubs;
using TravelwAI.Web.Services;
using TravelwAI.Web.Options;

// Container hosts such as Render can have a low inotify limit.
// Disable configuration reload watchers before CreateBuilder so startup
// does not fail while watching appsettings.json files.
if (string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable("DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE")))
{
    Environment.SetEnvironmentVariable(
        "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE",
        "false");
}

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

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
builder.Services.Configure<SePayOptions>(builder.Configuration.GetSection("SePay"));
builder.Services.PostConfigure<SePayOptions>(options =>
{
    var configuration = builder.Configuration;
    // Environment variables must take precedence over appsettings.json on Render.
    // The previous order read SePay:Enabled=false first, so SEPAY_ENABLED=true
    // was never reached and the webhook always returned HTTP 503.
    options.Enabled = FirstBool(
        configuration["SEPAY_ENABLED"],
        configuration["SePay:Enabled"],
        options.Enabled);
    options.WebhookApiKey = FirstNonEmpty(
        configuration["SEPAY_WEBHOOK_API_KEY"],
        configuration["SePay:WebhookApiKey"],
        options.WebhookApiKey);
    options.BankCode = FirstNonEmpty(
        configuration["SEPAY_BANK_CODE"],
        configuration["SePay:BankCode"],
        options.BankCode,
        "BIDV");
    // The receiving account is fixed by the application. This intentionally ignores
    // an old SEPAY_BANK_ACCOUNT_NUMBER value that may still exist on Render.
    options.BankAccountNumber = "96247Q4W8E";
    options.BankAccountName = FirstNonEmpty(
        configuration["SEPAY_BANK_ACCOUNT_NAME"],
        configuration["SePay:BankAccountName"],
        options.BankAccountName,
        "TravelwAI");
    options.PaymentCodePrefix = FirstNonEmpty(
        configuration["SEPAY_PAYMENT_CODE_PREFIX"],
        configuration["SePay:PaymentCodePrefix"],
        options.PaymentCodePrefix,
        "TWAI");
    options.ValidateAccountNumber = FirstBool(
        configuration["SEPAY_VALIDATE_ACCOUNT_NUMBER"],
        configuration["SePay:ValidateAccountNumber"],
        options.ValidateAccountNumber);

    if (int.TryParse(FirstNonEmpty(
            configuration["SEPAY_PAYMENT_CODE_SUFFIX_LENGTH"],
            configuration["SePay:PaymentCodeSuffixLength"]),
        out var paymentCodeSuffixLength))
    {
        options.PaymentCodeSuffixLength = Math.Clamp(paymentCodeSuffixLength, 8, 30);
    }
});
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.Configure<OpenRouterOptions>(builder.Configuration.GetSection("OpenRouter"));
builder.Services.Configure<PersistentTranslationOptions>(builder.Configuration.GetSection("PersistentTranslation"));
builder.Services.PostConfigure<PersistentTranslationOptions>(options =>
{
    var configuration = builder.Configuration;
    options.Enabled = FirstBool(
        configuration["PersistentTranslation:Enabled"],
        configuration["PERSISTENT_TRANSLATION_ENABLED"],
        options.Enabled);

    if (int.TryParse(FirstNonEmpty(
            configuration["PersistentTranslation:PollSeconds"],
            configuration["PERSISTENT_TRANSLATION_POLL_SECONDS"]),
        out var pollSeconds))
    {
        options.PollSeconds = Math.Clamp(pollSeconds, 1, 60);
    }

    if (int.TryParse(FirstNonEmpty(
            configuration["PersistentTranslation:BatchSize"],
            configuration["PERSISTENT_TRANSLATION_BATCH_SIZE"]),
        out var batchSize))
    {
        options.BatchSize = Math.Clamp(batchSize, 1, 20);
    }
});
builder.Services.PostConfigure<OllamaOptions>(options =>
{
    var configuration = builder.Configuration;
    options.BaseUrl = FirstNonEmpty(configuration["OLLAMA_BASE_URL"], configuration["Ollama:BaseUrl"], options.BaseUrl, "http://localhost:11434");
    options.Model = FirstNonEmpty(configuration["OLLAMA_MODEL"], configuration["Ollama:Model"], options.Model, "gemma4:31b-cloud");
    options.ApiKey = FirstNonEmpty(configuration["OLLAMA_API_KEY"], configuration["Ollama:ApiKey"], options.ApiKey);
    if (int.TryParse(FirstNonEmpty(configuration["OLLAMA_TIMEOUT_SECONDS"], configuration["Ollama:TimeoutSeconds"]), out var timeout) && timeout > 0)
        options.TimeoutSeconds = timeout;
});
builder.Services.PostConfigure<OpenRouterOptions>(options =>
{
    var configuration = builder.Configuration;
    options.BaseUrl = FirstNonEmpty(configuration["OPENROUTER_BASE_URL"], configuration["OpenRouter:BaseUrl"], options.BaseUrl, "https://openrouter.ai/api/v1");
    options.Model = FirstNonEmpty(configuration["OPENROUTER_MODEL"], configuration["OpenRouter:Model"], options.Model, "google/gemma-4-31b-it:free");
    options.ApiKey = FirstNonEmpty(configuration["OPENROUTER_API_KEY"], configuration["OpenRouter:ApiKey"], options.ApiKey);
    options.SiteUrl = FirstNonEmpty(configuration["OPENROUTER_SITE_URL"], configuration["OpenRouter:SiteUrl"], options.SiteUrl, "https://travelwai.id.vn");
    options.AppTitle = FirstNonEmpty(configuration["OPENROUTER_APP_TITLE"], configuration["OpenRouter:AppTitle"], options.AppTitle, "TravelwAI");
    if (int.TryParse(FirstNonEmpty(configuration["OPENROUTER_TIMEOUT_SECONDS"], configuration["OpenRouter:TimeoutSeconds"]), out var timeout) && timeout > 0)
        options.TimeoutSeconds = timeout;
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
builder.Services.AddHttpClient<OllamaAiService>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 10, 600));
    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
    }
});
builder.Services.AddHttpClient("OpenRouter", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 10, 600));
    if (!string.IsNullOrWhiteSpace(options.ApiKey))
    {
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey.Trim());
    }
    if (!string.IsNullOrWhiteSpace(options.SiteUrl))
        client.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", options.SiteUrl.Trim());
    if (!string.IsNullOrWhiteSpace(options.AppTitle))
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-OpenRouter-Title", options.AppTitle.Trim());
});
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
builder.Services.AddScoped<AiKnowledgeContextService>();
builder.Services.AddScoped<RoleFeaturePolicyService>();
builder.Services.AddScoped<AiUsageLimitService>();
builder.Services.AddScoped<ChatbotSettingsService>();
builder.Services.AddScoped<AiProviderSettingsService>();
builder.Services.AddScoped<AccountPlanSettingsService>();
builder.Services.AddSingleton<AiChatJobService>();
builder.Services.AddSingleton<PersistentTranslationStore>();
builder.Services.AddSingleton<ProvinceTranslationRepairService>();
builder.Services.AddSingleton<TranslationGenerationCoordinator>();
builder.Services.AddSingleton<PersistentTranslationActivityGate>();
builder.Services.AddScoped<PersistentDocumentTranslationService>();
builder.Services.AddScoped<TourOrderAutomation>();
builder.Services.AddScoped<TourOfferService>();
builder.Services.AddScoped<EmailNotificationService>();
builder.Services.AddScoped<InAppNotificationService>();
builder.Services.AddScoped<AccountPresenceService>();
builder.Services.AddScoped<PlanQueueService>();
builder.Services.AddScoped<AutomaticPaymentService>();
builder.Services.AddHostedService<TourOrderExpirationHostedService>();
builder.Services.AddHostedService<PaymentOrderExpirationHostedService>();
builder.Services.AddHostedService<UnmatchedPaymentRepairHostedService>();
builder.Services.AddHostedService<PaymentBenefitRepairHostedService>();
builder.Services.AddHostedService<PlanGroupExpirationHostedService>();
builder.Services.AddHostedService<AccountPlanQueueHostedService>();
builder.Services.AddHostedService<PersistentTranslationHostedService>();

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
    try
    {
        await next();
    }
    catch (TotalImageStorageQuotaExceededException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            success = false,
            code = "TOTAL_IMAGE_STORAGE_QUOTA_EXCEEDED",
            message = ex.Message,
            requestedBytes = ex.RequestedBytes,
            imageStorage = new
            {
                usedBytes = ex.Usage.UsedBytes,
                limitBytes = ex.Usage.LimitBytes,
                remainingBytes = ex.Usage.RemainingBytes,
                usedPercent = ex.Usage.UsedPercent,
                imageCount = ex.Usage.ImageCount
            }
        });
    }
    catch (ImageStorageQuotaExceededException ex) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            success = false,
            code = "IMAGE_STORAGE_QUOTA_EXCEEDED",
            message = ex.Message,
            requestedBytes = ex.RequestedBytes,
            imageStorage = new
            {
                usedBytes = ex.Usage.UsedBytes,
                limitBytes = ex.Usage.LimitBytes,
                remainingBytes = ex.Usage.RemainingBytes,
                usedPercent = ex.Usage.UsedPercent,
                imageCount = ex.Usage.ImageCount
            }
        });
    }
});

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

using (var scope = app.Services.CreateScope())
{
    try
    {
        await scope.ServiceProvider
            .GetRequiredService<ProvinceTranslationRepairService>()
            .RepairAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("ProvinceTranslationRepairService");
        logger.LogWarning(ex, "Không thể sửa cache tên tỉnh/thành khi khởi động; API vẫn sử dụng phép chuyển tên cố định.");
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
        var isUpload = path.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase);
        var hasVersion = context.Context.Request.Query.ContainsKey("v")
            || context.Context.Request.Query.ContainsKey("brand");

        context.Context.Response.Headers["Cache-Control"] = isUpload
            ? "public,max-age=2592000"
            : hasVersion
                ? "public,max-age=31536000,immutable"
                : "no-cache,must-revalidate";
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
    "/posts",
    "/tours",
    "/cart",
    "/checkout",
    "/tour-sales",
    "/tour-management",
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
                context.Response.Cookies.Append("TravelwAIAuth", newIdToken, BuildAuthCookieOptions(context));

                if (refreshResult.GetValueOrDefault("refreshToken") is string newRefreshToken && !string.IsNullOrWhiteSpace(newRefreshToken))
                {
                    context.Response.Cookies.Append("TravelwAIRefresh", newRefreshToken, BuildAuthCookieOptions(context));
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
    var isUpload = requestPath.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase);
    var hasVersion = context.Request.Query.ContainsKey("v")
        || context.Request.Query.ContainsKey("brand");
    context.Response.Headers["Cache-Control"] = isUpload
        ? "public,max-age=2592000"
        : hasVersion
            ? "public,max-age=31536000,immutable"
            : "no-cache,must-revalidate";
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
            return TuneSupabaseConnectionString(connectionString, configuration);
        }
    }

    var explicitConnectionString = FirstNonEmpty(
        options.ConnectionString,
        configuration["Supabase:ConnectionString"],
        configuration["SUPABASE_CONNECTION_STRING"],
        configuration["DATABASE_URL"]);

    if (!string.IsNullOrWhiteSpace(explicitConnectionString))
    {
        return TuneSupabaseConnectionString(explicitConnectionString, configuration);
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

    return TuneSupabaseConnectionString(builder.ConnectionString, configuration);
}

static string TuneSupabaseConnectionString(string connectionString, IConfiguration configuration)
{
    if (string.IsNullOrWhiteSpace(connectionString)) return connectionString;

    try
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var configured = FirstNonEmpty(
            configuration["Database:MaxPoolSize"],
            configuration["DATABASE_MAX_POOL_SIZE"],
            configuration["SUPABASE_MAX_POOL_SIZE"]);
        var maxPoolSize = int.TryParse(configured, out var parsed)
            ? Math.Clamp(parsed, 1, 14)
            : 10;

        // Supabase session pool hiện giới hạn 15 client. Giữ lại vài slot cho
        // migration, dashboard và tác vụ nền, đồng thời để Npgsql xếp hàng
        // thay vì mở quá số session mà máy chủ cho phép.
        builder.MaxPoolSize = Math.Min(builder.MaxPoolSize, maxPoolSize);
        builder.MinPoolSize = 0;
        builder.Timeout = Math.Max(builder.Timeout, 15);
        builder.CommandTimeout = Math.Max(builder.CommandTimeout, 30);
        builder.ConnectionIdleLifetime = Math.Min(builder.ConnectionIdleLifetime, 60);
        builder.ConnectionPruningInterval = Math.Min(builder.ConnectionPruningInterval, 10);
        return builder.ConnectionString;
    }
    catch
    {
        // Giữ nguyên chuỗi dạng URI hoặc định dạng do nền tảng cung cấp nếu
        // NpgsqlConnectionStringBuilder không phân tích được.
        return connectionString;
    }
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

static CookieOptions BuildAuthCookieOptions(HttpContext context) => new()
{
    Path = "/",
    SameSite = SameSiteMode.Lax,
    Secure = context.Request.IsHttps,
    HttpOnly = false,
    IsEssential = true
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

    if (string.Equals(path, "/business", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/tour-management", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/tour-sales", StringComparison.OrdinalIgnoreCase))
    {
        return string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Sales", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Tour Sales", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Company", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "Business", StringComparison.OrdinalIgnoreCase);
    }

    return true;
}

static async Task WriteForbiddenAsync(HttpContext context, string path)
{
    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    context.Response.ContentType = "text/html; charset=utf-8";
    var homeLink = path.Equals("/admin", StringComparison.OrdinalIgnoreCase) ? "/business" : "/home";
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
app.Map("/ws/conversations/{conversationId}", async (
    HttpContext context,
    IAuthService authService,
    IChatService chatService,
    InAppNotificationService notifications) =>
{
    await WebSocketChatMiddleware.HandleConversationSocket(context, authService, chatService, notifications);
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
