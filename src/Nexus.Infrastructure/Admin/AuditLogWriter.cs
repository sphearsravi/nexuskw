using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nexus.Application.Common;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Admin;

/// <summary>
/// The implementation of <see cref="IAuditLog"/>. One row per administrative write: who,
/// what, before, after.
/// <para>
/// <b>It enlists in the caller's unit of work and does not save.</b> An audit row that is
/// committed separately from the change it describes is a lie waiting to happen — the
/// change rolls back, the row survives, and the trail says a price was altered when it was
/// not. <see cref="RecordAsync"/> therefore only adds; the caller's
/// <c>SaveChangesAsync</c> commits both or neither.
/// </para>
/// </summary>
public sealed class AuditLogWriter(
    NexusDbContext db,
    IAdminActorAccessor actors,
    ILogger<AuditLogWriter> log) : IAuditLog
{
    /// <summary>
    /// Compact: this column is written on every administrative action and read by a human
    /// in a table cell, so indentation would cost storage and buy nothing.
    /// </summary>
    private static readonly JsonSerializerOptions Shape = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public Task RecordAsync(
        string entity, string entityId, string action,
        object? before, object? after,
        string? actorId, string? actorEmail, string? ipAddress,
        CancellationToken ct = default)
    {
        var ambient = actors.Current;

        db.AuditLogs.Add(new AuditLog
        {
            ActorId = actorId ?? ambient.Id,
            ActorEmail = actorEmail ?? ambient.Email,
            Entity = entity,
            EntityId = entityId,
            Action = action,
            BeforeJson = Render(before, entity, action),
            AfterJson = Render(after, entity, action),
            At = DateTimeOffset.UtcNow,
            IpAddress = ipAddress ?? ambient.IpAddress
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Serialisation must never be the reason an administrative action fails, so a cycle
    /// or an unserialisable member degrades to a note rather than throwing out of a save.
    /// </summary>
    private string? Render(object? value, string entity, string action)
    {
        if (value is null) return null;

        try
        {
            return JsonSerializer.Serialize(value, Shape);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            log.LogError(ex, "Could not serialise the audit state for {Entity} {Action}.", entity, action);
            return "{\"error\":\"not serialisable\"}";
        }
    }
}

/// <summary>Convenience over <see cref="IAuditLog"/> for the common ambient-actor case.</summary>
public static class AuditLogExtensions
{
    public static Task RecordAsync(
        this IAuditLog audit, string entity, object entityId, string action,
        object? before, object? after, CancellationToken ct = default) =>
        audit.RecordAsync(
            entity,
            Convert.ToString(entityId, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            action, before, after, null, null, null, ct);
}
