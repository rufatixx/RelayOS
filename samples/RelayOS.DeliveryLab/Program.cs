using System.Net;
using RelayOS.DeliveryLab;

var builder = WebApplication.CreateBuilder(args);
// A local teaching tool, never a remotely accessible messaging service.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5088);
    options.Limits.MaxRequestBodySize = 8192;
});
builder.Services.AddSingleton<DeliveryLabSession>();
var app = builder.Build();

app.Use(async (context, next) =>
{
    var host = context.Request.Host.Host;
    if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)
        || (host != "localhost" && host != "127.0.0.1" && host != "[::1]"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    if (HttpMethods.IsPost(context.Request.Method))
    {
        var origin = context.Request.Headers.Origin.ToString();
        var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        if (context.Request.Headers["X-RelayOS-Lab"] != "1"
            || (origin.Length > 0 && !string.Equals(origin, expectedOrigin, StringComparison.Ordinal))
            || !context.Request.HasJsonContentType())
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }
    await next(context);
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/state", async (DeliveryLabSession session, CancellationToken cancellationToken) =>
    await session.GetSnapshotAsync(cancellationToken));
app.MapPost("/api/step", async (LabRequest request, DeliveryLabSession session, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await session.ExecuteAsync(request.SessionId, request.Action, request.Message, cancellationToken));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Conflict(new { error = exception.Message });
    }
});

Console.WriteLine("RelayOS Delivery Lab: http://localhost:5088");
Console.WriteLine("In-process simulation only. No real radios or device-to-device networking. Use sample data, not secrets.");
await app.RunAsync();

internal sealed record LabRequest(Guid SessionId, string Action, string? Message);
