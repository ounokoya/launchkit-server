using LaunchKit.Api.Auth;
using LaunchKit.Api.Common;
using LaunchKit.Api.Features;
using LaunchKit.Api.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ArangoOptions>(builder.Configuration.GetSection("Arango"));
builder.Services.Configure<PlansOptions>(builder.Configuration.GetSection("Plans"));
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.Configure<SeedOptions>(builder.Configuration.GetSection("Seed"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<ArangoClient>();
builder.Services.AddSingleton(Texts.Load(
    builder.Configuration["I18n:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "i18n")));
builder.Services.AddSingleton<IPasswordHasher<object>, PasswordHasher<object>>();
builder.Services.AddScoped<OrgAccess>();
builder.Services.AddHostedService<StartupInitializer>();

builder.Services.AddAuthentication(SessionAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthHandler>(SessionAuthHandler.SchemeName, null);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemHandler>();
builder.Services.AddOpenApi("v1");

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (http, next) =>
{
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseAuthentication();

app.MapOpenApi();
app.MapGet("/health", async (ArangoClient db) =>
    await db.PingAsync() ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503)).WithTags("Technique");

var api = app.MapGroup("/api/v1");
Accounts.Map(api);
Organizations.Map(api);
Members.Map(api);
Billing.Map(api);
Projects.Map(api);
Admin.Map(api);

app.Run();

/// <summary>Entry point, visible to the integration tests (WebApplicationFactory).</summary>
public partial class Program;
