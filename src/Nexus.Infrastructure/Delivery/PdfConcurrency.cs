namespace Nexus.Infrastructure.Delivery;

/// <summary>
/// One process-wide gate in front of every whole-document PDFsharp load — stamping and
/// upload parsing alike, because they are the same managed allocation.
/// <para>
/// PDFsharp loads a document into managed memory, so a 50 MB source can mean 150–200 MB
/// of heap. The app pool may be 512 MB and shared with five other sites, where an OOM
/// takes down six applications rather than one request. Serialising is the price of not
/// being the site that did that.
/// </para>
/// </summary>
public sealed class PdfConcurrency : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// True when the gate was taken. False means the caller must refuse with
    /// <c>503 + Retry-After</c> — and must not charge the attempt to anybody's
    /// download count.
    /// </summary>
    public async Task<bool> TryEnterAsync(TimeSpan timeout, CancellationToken ct) =>
        await _gate.WaitAsync(timeout, ct);

    public void Exit() => _gate.Release();

    public void Dispose() => _gate.Dispose();
}
