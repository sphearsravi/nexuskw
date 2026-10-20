using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nexus.Infrastructure;
using Nexus.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddHealthChecks();
builder.Services.AddNexusInfrastructure(builder.Configuration);

// Keys must outlive a deployment, or every deploy invalidates auth cookies and
// antiforgery tokens. App_Data is excluded from the FTP sync (see PLAN.md §10).
var keyRing = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "dpkeys");
Directory.CreateDirectory(keyRing);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRing))
    .SetApplicationName("Nexus");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// The storefront is static HTML, CSS and JS served verbatim from wwwroot.
// It reads the catalogue from /api — see assets/js/api.js.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
// No server-side response cache. The catalogue is ~150 rows, so a database
// read per request is cheap, and a shared cache would keep serving a title
// the operator just unpublished. Cache-Control headers still let the
// browser hold the list for a minute, which is what CAT-30 allows.
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapHealthChecks("/health");

await MigrateAndSeedAsync(app);

app.Run();

static async Task MigrateAndSeedAsync(WebApplication app)
{
    // Integration tests host the app without a database; they set this off.
    if (!app.Configuration.GetValue("Nexus:MigrateOnStartup", true)) return;

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
    await db.Database.MigrateAsync();

    // Decision D4 / CAT-35: the demo titles exist everywhere so the operator
    // edits rather than retypes, but they stay UNPUBLISHED in production until
    // rights are cleared per title. Nothing unlicensed is ever offered for sale.
    var publish = !app.Environment.IsProduction();
    await scope.ServiceProvider.GetRequiredService<CatalogueSeeder>().SeedAsync(publish);
}

// Exposed so Nexus.IntegrationTests can host the app with WebApplicationFactory.
public partial class Program;
