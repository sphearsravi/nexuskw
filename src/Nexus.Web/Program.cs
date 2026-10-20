using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddHealthChecks();

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

// The storefront is the static demo, served verbatim from wwwroot.
// Phase 1 swaps assets/js/data.js for an API client; the markup does not change.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

// Exposed so Nexus.IntegrationTests can host the app with WebApplicationFactory.
public partial class Program;
