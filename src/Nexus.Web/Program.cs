using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Nexus.Infrastructure;
using Nexus.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddNexusInfrastructure(builder.Configuration);

// The four streams register themselves, each in its own file, so adding one is
// adding a line here and never an edit inside somebody else's registration.
// Payments is absent deliberately: it is held until a Tap sandbox sample exists.
builder.Services.AddNexusIdentity();
builder.Services.AddNexusOrders();
builder.Services.AddNexusDelivery();
builder.Services.AddNexusEmail(builder.Configuration);
builder.Services.AddNexusAccounts();
builder.Services.AddNexusAdmin();

// ---------------------------------------------------------------------------
// Keys must outlive a deployment, or every deploy invalidates auth cookies and
// antiforgery tokens and signs everyone out, administrator included. App_Data
// is excluded from the deploy (PLAN.md §10), which is what makes this work.
// ---------------------------------------------------------------------------
var keyRing = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "dpkeys");
Directory.CreateDirectory(keyRing);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRing))
    .SetApplicationName("Nexus");

// ---------------------------------------------------------------------------
// Behind IIS and, in production, a shared front end. Without this every client
// looks like the proxy, which silently turns per-IP rate limits into one global
// bucket and makes the download log useless.
// ---------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownNetworks.Clear();
    foreach (var ip in builder.Configuration.GetSection("Hosting:KnownProxies").Get<string[]>() ?? [])
        if (System.Net.IPAddress.TryParse(ip, out var parsed)) o.KnownProxies.Add(parsed);
});

// ---------------------------------------------------------------------------
// Rate limits. Configured, not merely named: a policy that exists but permits
// everything reads as protection in a review and is none.
//
// The webhook is deliberately NOT limited. Tap retries, and throttling the
// gateway is how a paid order silently fails to complete; it is protected by
// its signature instead.
// ---------------------------------------------------------------------------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static string ClientKey(HttpContext c) =>
        c.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // The limits below are the production defaults. They are overridable from
    // configuration for one reason: an end-to-end suite drives the real sign-in
    // form dozens of times in a minute from a single address, and a fixed budget
    // of 8 makes that suite fail for a reason unrelated to what it is testing.
    //
    // The limiter itself is NOT weakened — only its numbers, and only where they
    // are set. The integration tests still exhaust the configured budget and
    // assert 429, so the behaviour stays covered wherever the limit is.
    int Limit(string name, int production) =>
        builder.Configuration.GetValue($"RateLimits:{name}", production);

    // Creating an order costs a database write and, in Phase 3, a gateway call.
    o.AddPolicy("checkout", c => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(c), _ => new FixedWindowRateLimiterOptions
        { PermitLimit = Limit("checkout", 10), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // Downloads are per token, not per IP: a household behind one address must
    // not throttle itself, and a shared token is caught by the grant's own count.
    o.AddPolicy("download", c => RateLimitPartition.GetFixedWindowLimiter(
        c.Request.RouteValues["token"]?.ToString() ?? ClientKey(c),
        _ => new FixedWindowRateLimiterOptions
        { PermitLimit = Limit("download", 30), Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));

    // Sign-in and password reset: slow enough to make guessing useless.
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(c), _ => new FixedWindowRateLimiterOptions
        { PermitLimit = Limit("auth", 8), Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));

    // The catalogue is cheap and cached; this is a flood stop, not a quota.
    o.AddPolicy("catalogue", c => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(c), _ => new FixedWindowRateLimiterOptions
        { PermitLimit = Limit("catalogue", 300), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// An upload cap exists so a large PDF cannot exhaust a 512 MB shared app pool.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 64L * 1024 * 1024;
    o.ValueLengthLimit = 1024 * 1024;
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<NexusDbContext>("database");

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// ---------------------------------------------------------------------------
// Security headers, set once for every response.
//
// Referrer-Policy is load-bearing rather than hygiene: the success page carries
// an order reference in its query string and loads a font from a third party,
// so without this the reference leaks to that third party in the Referer
// header. A per-page meta tag was rejected because the next new page forgets it.
// ---------------------------------------------------------------------------
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["Referrer-Policy"] = "no-referrer";
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    await next();
});

// ---------------------------------------------------------------------------
// The Tap webhook's signature is computed over the raw body, so the body must
// be re-readable after model binding. Buffering is enabled only for that one
// path — doing it globally would buffer every PDF upload into memory.
// ---------------------------------------------------------------------------
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/payments/tap", StringComparison.OrdinalIgnoreCase))
        ctx.Request.EnableBuffering();
    await next();
});

// The storefront is static HTML, CSS and JS served verbatim from wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new { status = e.Value.Status.ToString(), error = e.Value.Exception?.Message })
        });
    }
});

await MigrateAndSeedAsync(app);

app.Run();

static async Task MigrateAndSeedAsync(WebApplication app)
{
    // Integration tests own their own schema and seed, so they turn this off.
    if (!app.Configuration.GetValue("Nexus:MigrateOnStartup", true)) return;

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
    await db.Database.MigrateAsync();

    // Decision D4 / CAT-35: the demo titles exist everywhere so the operator
    // edits rather than retypes, but they stay UNPUBLISHED in production until
    // rights are cleared per title. Nothing unlicensed is ever offered for sale.
    var publish = !app.Environment.IsProduction();
    await scope.ServiceProvider.GetRequiredService<CatalogueSeeder>().SeedAsync(publish);

    // Roles, and the first administrator when one is configured.
    await scope.ServiceProvider.GetRequiredService<IdentitySeeder>()
        .SeedAsync(app.Environment.IsProduction());
}

// Exposed so Nexus.IntegrationTests can host the app with WebApplicationFactory.
public partial class Program;
