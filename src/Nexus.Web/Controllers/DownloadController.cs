using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Nexus.Application.Delivery;
using Nexus.Domain;
using Nexus.Infrastructure.Delivery;

namespace Nexus.Web.Controllers;

/// <summary>
/// The product being handed over.
/// <para>
/// Anonymous by necessity: the emailed link has to work from a phone's mail app with no
/// session. The token <i>is</i> the credential, so every check lives in the service and
/// this controller only turns a decision into HTTP.
/// </para>
/// </summary>
[ApiController]
public sealed class DownloadController(
    IDeliveryService delivery,
    IGrantStatusReader status) : ControllerBase
{
    [HttpGet("/download/{token}")]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        var decision = await delivery.AuthoriseDownloadAsync(
            token,
            ClientAddress.Prefix(HttpContext.Connection.RemoteIpAddress),
            Request.Headers.UserAgent.ToString(),
            ct);

        // Grant is null is the only not-found signal the frozen DownloadDecision can
        // carry, so it is read before the outcome.
        if (decision.Grant is null) return Gone(StatusCodes.Status404NotFound, "not_found");

        switch (decision.Outcome)
        {
            case DownloadOutcome.Served when decision.AbsolutePath is not null:
                return Stream(decision.AbsolutePath, decision.SuggestedFileName!);

            case DownloadOutcome.Busy:
                // Nothing was produced, so nothing was counted. Say so with a wait.
                Response.Headers.RetryAfter = "10";
                return Gone(StatusCodes.Status503ServiceUnavailable, "busy");

            case DownloadOutcome.StampFailed:
                // The row says sold and the shelf is empty: an operator incident.
                return Gone(StatusCodes.Status500InternalServerError, "file_missing");

            case DownloadOutcome.Expired:
            case DownloadOutcome.Exhausted:
            case DownloadOutcome.Revoked:
                return Gone(StatusCodes.Status410Gone, decision.Outcome.ToString().ToLowerInvariant());

            default:
                return Gone(StatusCodes.Status500InternalServerError, "unavailable");
        }
    }

    /// <summary>
    /// ASP.NET Core maps HEAD onto GET handlers. Left alone, a link checker following
    /// the emailed URL would spend one of the buyer's three downloads before they had
    /// read the message.
    /// </summary>
    [HttpHead("/download/{token}")]
    public IActionResult Head(string token)
    {
        _ = token;
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    /// <summary>Counts nothing. This is what the landing page and the library read.</summary>
    [HttpGet("/api/downloads/{token}/status")]
    public async Task<IActionResult> Status(string token, CancellationToken ct)
    {
        NoStore();
        var found = await status.DescribeAsync(token, ct);
        return found is null ? NotFound(new { error = "not_found" }) : Ok(found);
    }

    private IActionResult Stream(string path, string fileName)
    {
        // FileShare.Delete as well as Read, so a revoke's cache purge can win against an
        // in-flight read instead of throwing and being retried forever.
        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);

        NoStore();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        // One request is one download, which is the only reading that matches the
        // three-downloads promise. A manager splitting the file into eight ranges would
        // otherwise exhaust a grant on the first click.
        Response.Headers.AcceptRanges = "none";

        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(fileName);
        Response.Headers.ContentDisposition = disposition.ToString();

        // Content-Length comes from the stream, so the browser shows real progress and
        // no proxy re-chunks the body. The 64 KB copy keeps peak managed memory per
        // download in the tens of kilobytes rather than the size of the book.
        return new FileStreamResult(stream, "application/pdf") { EnableRangeProcessing = false };
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
    }

    private IActionResult Gone(int statusCode, string reason)
    {
        NoStore();
        // A code, never prose: the storefront owns every string a human reads, in both
        // languages, and an API that invents English breaks that the first time it fires.
        return StatusCode(statusCode, new { error = reason });
    }
}
