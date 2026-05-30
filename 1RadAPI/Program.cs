using System.Text;
using System.Threading.RateLimiting;
using _1Rad.Application;
using _1Rad.Infrastructure;
using _1Rad.Infrastructure.Middleware;
using _1RadAPI.Middleware;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        // Always emit fields (including null/default) so API responses are shape-stable for clients.
        options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
    });
builder.Services.AddFluentValidationAutoValidation()
                .AddFluentValidationClientsideAdapters();

// Tactical: Increase payload capacity for large DICOM/ZIP clinical assets (500MB)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 524288000; // 500MB
    options.ValueLengthLimit = 524288000;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 524288000; // 500MB
});

builder.Services.AddEndpointsApiExplorer();

// Enhanced Swagger with Security Definitions
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "1Rad Clinical Hub API", Version = "v1" });
    
    // Include XML Comments
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (System.IO.File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath);
    }

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

// Configure Rate Limiting (OTP Protection)
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter(policyName: "OtpRateLimit", opt =>
    {
        opt.PermitLimit = 5;
        opt.Window = TimeSpan.FromMinutes(10);
        opt.QueueLimit = 0;
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Configure CORS — pulls the allow-list from Cors:AllowedOrigins in
// appsettings.{Environment}.json. Combining a wildcard origin with
// AllowCredentials() is a CSRF risk, so we restrict to the explicit list.
var corsOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>()
    ?? Array.Empty<string>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowedOrigins", policy =>
    {
        if (corsOrigins.Length == 0)
        {
            // Defensive: in misconfigured environments we keep the API
            // reachable from same-origin only. Logged to startup so it's
            // obvious during smoke tests if the list wasn't loaded.
            Console.WriteLine("[CORS] WARNING — Cors:AllowedOrigins is empty. No cross-origin requests will be accepted.");
        }
        else
        {
            Console.WriteLine($"[CORS] Allowed origins: {string.Join(", ", corsOrigins)}");
        }

        policy.WithOrigins(corsOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials()
              .SetPreflightMaxAge(TimeSpan.FromMinutes(10));
    });
});

// JWT Authentication Configuration
// Reads Jwt:Secret from appsettings.{Environment}.json or environment
// variables (App Service Configuration in Prod). Fails fast if missing —
// no hardcoded fallback secret in the binary, otherwise anyone with read
// access to the repo could forge tokens for environments that forgot to
// configure the secret.
var jwtSettings = builder.Configuration.GetSection("Jwt");
var secretKey = jwtSettings["Secret"]
    ?? throw new InvalidOperationException(
        "Jwt:Secret is required but was not configured. " +
        "Set it via App Service Configuration (Prod), appsettings.Development.json (local dev), " +
        "or the Jwt__Secret environment variable.");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings["Issuer"] ?? "1RadAPI",
        ValidAudience = jwtSettings["Audience"] ?? "1RadClient",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey))
    };
});

// Authorization Policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("InitiationOnly", policy => 
        policy.RequireClaim("type", "initiation"));
    
    options.AddPolicy("AccessOnly", policy => 
        policy.RequireClaim("type", "access"));
});

// Clean Architecture Registrations
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
// CorrelationIdMiddleware MUST run before ExceptionHandlingMiddleware so the
// exception handler can include the correlation ID in error responses.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>(); // Custom Global Exception Handler

// Enable Swagger in all environments for testing on Azure
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "1Rad API V1");
    c.RoutePrefix = "swagger"; // Standard /swagger path
});

// Redirect root URL (/) to Swagger UI
app.MapGet("/", context =>
{
    context.Response.Redirect("/swagger");
    return Task.CompletedTask;
});

app.UseHttpsRedirection();

app.UseCors("AllowedOrigins"); // Enable CORS

app.UseRateLimiter(); // Apply Rate Limiting

app.UseAuthentication(); // Must be before UseAuthorization
app.UseAuthorization();
// SessionValidationMiddleware runs AFTER UseAuthorization so the principal
// is already populated and signature-validated by the time we check the
// session id. Order matters: this must fire BEFORE any controller that
// touches user state, so we sit it just after the auth pipeline.
app.UseMiddleware<SessionValidationMiddleware>();
app.UseMiddleware<ContextualSentinelMiddleware>();
app.UseMiddleware<SubscriptionValidationMiddleware>();

app.MapControllers();

app.Run();
