// SafeConnect Relay — minimal APK hosting service
// Deploy this to Render.com (free tier).
// SafeConnect uploads here; target devices download from here.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(
    $"http://0.0.0.0:{Environment.GetEnvironmentVariable("PORT") ?? "10000"}");

// Allow large APK uploads (up to 200 MB)
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 200 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(k =>
    k.Limits.MaxRequestBodySize = 200 * 1024 * 1024);

var app = builder.Build();

// ── In-memory APK store ───────────────────────────────────────────────────────
// Files are kept in RAM for 48 hours then discarded.
// On a free Render instance (~512 MB RAM) this comfortably holds several APKs.
var store = new System.Collections.Concurrent.ConcurrentDictionary<
    string, (string Name, byte[] Data, DateTime Expiry)>();

// Background cleanup task
_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(TimeSpan.FromHours(1));
        var expired = store
            .Where(kv => kv.Value.Expiry < DateTime.UtcNow)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired)
            store.TryRemove(key, out _);
    }
});

// ── POST /upload ──────────────────────────────────────────────────────────────
// Called by SafeConnect. Accepts multipart form with field "file".
// Returns the public download URL as plain text.
app.MapPost("/upload", async (HttpContext ctx) =>
{
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest("Expected multipart/form-data.");

    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null)
        return Results.BadRequest("No 'file' field in form.");

    using var ms = new MemoryStream((int)file.Length);
    await file.CopyToAsync(ms);

    var id   = Guid.NewGuid().ToString("N")[..12];
    var name = Path.GetFileName(file.FileName);
    store[id] = (name, ms.ToArray(), DateTime.UtcNow.AddHours(48));

    var base_ = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    return Results.Ok($"{base_}/download/{id}/{Uri.EscapeDataString(name)}");
});

// ── GET /download/{id}/{name} ─────────────────────────────────────────────────
// Opened by the target Android device. Returns the APK file directly.
app.MapGet("/download/{id}/{name}", (string id, string name) =>
{
    if (!store.TryGetValue(id, out var entry) || entry.Expiry < DateTime.UtcNow)
        return Results.NotFound("File not found or expired.");

    return Results.File(
        entry.Data,
        "application/vnd.android.package-archive",
        entry.Name);
});

// ── Health check ──────────────────────────────────────────────────────────────
app.MapGet("/", () => Results.Ok("SafeConnect Relay is running."));

app.Run();
