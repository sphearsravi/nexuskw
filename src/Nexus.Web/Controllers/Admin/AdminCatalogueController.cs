using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexus.Infrastructure.Admin;

namespace Nexus.Web.Controllers.Admin;

/// <summary>
/// Books and subjects.
/// <para>
/// Everything here is <see cref="AdminPolicies.AdminOrStaff"/> except deleting, which is
/// Admin: unpublishing is the reversible way to take a title off sale, and removing the
/// row is not.
/// </para>
/// </summary>
[ApiController]
[Route("admin/api")]
[Authorize(Policy = AdminPolicies.AdminOrStaff)]
[AutoValidateAntiforgeryToken]
public sealed class AdminCatalogueController(
    BookAdminService books,
    CategoryAdminService categories,
    CatalogueImportService import) : ControllerBase
{
    /// <summary>
    /// 64 MB. The same ceiling the form options set, restated here because an upload is
    /// refused more cheaply by a length check than by a stream that runs out of disk.
    /// </summary>
    public const long MaxUploadBytes = 64L * 1024 * 1024;

    /// <summary>Four megabytes of CSV is several thousand titles; beyond that it is a mistake.</summary>
    public const long MaxImportBytes = 4L * 1024 * 1024;

    // ---- books -------------------------------------------------------------

    [HttpGet("books")]
    public async Task<IActionResult> Books(
        [FromQuery] string? q, [FromQuery] string? cat, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await books.ListAsync(q, cat, status, ct));

    [HttpGet("books/{id:int}")]
    public async Task<IActionResult> Book(int id, CancellationToken ct) =>
        await books.GetAsync(id, ct) is { } row ? Ok(row) : NotFound(new { error = "book_not_found" });

    [HttpPost("books")]
    public async Task<IActionResult> CreateBook([FromBody] BookWrite write, CancellationToken ct)
    {
        var (created, errors) = await books.CreateAsync(write, ct);
        return errors.Count > 0 ? Invalid(errors) : Created($"/admin/api/books/{created!.Id}", created);
    }

    [HttpPut("books/{id:int}")]
    public async Task<IActionResult> UpdateBook(int id, [FromBody] BookWrite write, CancellationToken ct)
    {
        var (updated, errors, problem) = await books.UpdateAsync(id, write, ct);
        if (problem is not null) return NotFound(new { error = problem.Code });
        return errors.Count > 0 ? Invalid(errors) : Ok(updated);
    }

    /// <summary>
    /// The gate. A refusal carries <see cref="Nexus.Domain.Entities.Book.PublishBlocker"/>'s
    /// own message, because that sentence is the single statement of what makes a title
    /// sellable and restating it here would make two.
    /// </summary>
    [HttpPost("books/{id:int}/publish")]
    public async Task<IActionResult> Publish(int id, CancellationToken ct)
    {
        var (book, problem) = await books.PublishAsync(id, ct);
        return problem switch
        {
            null => Ok(book),
            { Code: "book_not_found" } => NotFound(new { error = problem.Code }),
            _ => Conflict(new { error = problem.Code, blocker = problem.Detail })
        };
    }

    [HttpPost("books/{id:int}/unpublish")]
    public async Task<IActionResult> Unpublish(int id, CancellationToken ct)
    {
        var (book, problem) = await books.UnpublishAsync(id, ct);
        return problem is null ? Ok(book) : NotFound(new { error = problem.Code });
    }

    [HttpDelete("books/{id:int}")]
    [Authorize(Policy = AdminPolicies.AdminOnly)]
    public async Task<IActionResult> DeleteBook(int id, CancellationToken ct)
    {
        var problem = await books.DeleteAsync(id, ct);
        return problem switch
        {
            null => NoContent(),
            { Code: "book_not_found" } => NotFound(new { error = problem.Code }),
            _ => Conflict(new { error = problem.Code })
        };
    }

    /// <summary>
    /// The file is validated by <c>IPdfLibrary</c>, which reads its magic bytes and
    /// structure. The browser's content type and the filename are display material and
    /// decide nothing.
    /// </summary>
    [HttpPost("books/{id:int}/pdf")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> UploadPdf(int id, IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "file_missing" });
        if (file.Length > MaxUploadBytes) return BadRequest(new { error = "file_too_large" });

        await using var stream = file.OpenReadStream();
        var (book, problem) = await books.UploadPdfAsync(id, stream, file.FileName, ct);

        return problem switch
        {
            null => Ok(book),
            { Code: "book_not_found" } => NotFound(new { error = problem.Code }),
            _ => BadRequest(new { error = problem.Code })
        };
    }

    // ---- bulk import --------------------------------------------------------

    /// <summary>A starting file, so the operator never has to guess the column names.</summary>
    [HttpGet("books/import/template")]
    public IActionResult ImportTemplate()
    {
        // The BOM is deliberate: without it Excel opens a UTF-8 CSV as the system code
        // page and the Arabic columns arrive as mojibake.
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(CatalogueImportService.TemplateCsv()))
            .ToArray();

        return File(bytes, "text/csv; charset=utf-8", "nexus-catalogue-template.csv");
    }

    /// <summary>
    /// Bulk import. <c>dryRun</c> validates and writes nothing, which is how an operator
    /// finds out a fifty-row file is wrong without having to undo half of it.
    /// </summary>
    [HttpPost("books/import")]
    [RequestSizeLimit(MaxImportBytes)]
    public async Task<IActionResult> Import(
        IFormFile? file, [FromQuery] bool dryRun = false, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "file_missing" });
        if (file.Length > MaxImportBytes) return BadRequest(new { error = "file_too_large" });

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var csv = await reader.ReadToEndAsync(ct);

        var report = await import.ImportAsync(csv, dryRun, ct);

        // 422, not 400: the request was well formed and the file was not, and the body
        // carries the line-by-line reason it was refused.
        return report.Accepted ? Ok(report) : UnprocessableEntity(report);
    }

    // ---- categories ---------------------------------------------------------

    [HttpGet("categories")]
    public async Task<IActionResult> Categories(CancellationToken ct) => Ok(await categories.ListAsync(ct));

    [HttpPost("categories/{slug}")]
    public async Task<IActionResult> CreateCategory(
        string slug, [FromBody] CategoryWrite write, CancellationToken ct)
    {
        var (row, errors) = await categories.CreateAsync(slug, write, ct);
        return errors.Count > 0 ? Invalid(errors) : Created($"/admin/api/categories/{row!.Slug}", row);
    }

    [HttpPut("categories/{slug}")]
    public async Task<IActionResult> UpdateCategory(
        string slug, [FromBody] CategoryWrite write, CancellationToken ct)
    {
        var (row, errors, problem) = await categories.UpdateAsync(slug, write, ct);
        if (problem is not null) return NotFound(new { error = problem.Code });
        return errors.Count > 0 ? Invalid(errors) : Ok(row);
    }

    public sealed record ReorderBody(IReadOnlyList<string> Slugs);

    [HttpPost("categories/reorder")]
    public async Task<IActionResult> Reorder([FromBody] ReorderBody body, CancellationToken ct)
    {
        var (rows, problem) = await categories.ReorderAsync(body.Slugs ?? [], ct);
        return problem is null ? Ok(rows) : BadRequest(new { error = problem.Code });
    }

    [HttpDelete("categories/{slug}")]
    [Authorize(Policy = AdminPolicies.AdminOnly)]
    public async Task<IActionResult> DeleteCategory(string slug, CancellationToken ct)
    {
        var problem = await categories.DeleteAsync(slug, ct);
        return problem switch
        {
            null => NoContent(),
            { Code: "category_not_found" } => NotFound(new { error = problem.Code }),
            _ => Conflict(new { error = problem.Code, books = problem.Detail })
        };
    }

    private IActionResult Invalid(IReadOnlyList<FieldError> errors) =>
        UnprocessableEntity(new { error = "validation_failed", fields = errors });
}
