using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Persistence;

/// <summary>
/// The context body is deliberately empty of mapping. Every entity is configured by its
/// own <c>IEntityTypeConfiguration</c> in <c>Persistence/Configurations</c>, discovered by
/// assembly scan, so adding an entity is adding a file and never an edit here — which is
/// what keeps four parallel workstreams from fighting over one method.
/// </summary>
public class NexusDbContext(DbContextOptions<NexusDbContext> options)
    : IdentityDbContext<NexusUser>(options)
{
    // Catalogue
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Book> Books => Set<Book>();

    // Orders
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    // Payments
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<OrderEvent> OrderEvents => Set<OrderEvent>();
    public DbSet<JobLease> JobLeases => Set<JobLease>();

    // Delivery
    public DbSet<DownloadGrant> DownloadGrants => Set<DownloadGrant>();
    public DbSet<DownloadLog> DownloadLogs => Set<DownloadLog>();

    // Shared
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<OutboxEmail> OutboxEmails => Set<OutboxEmail>();
    public DbSet<EmailSuppression> EmailSuppressions => Set<EmailSuppression>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Identity maps its own tables first; ours are discovered after, so a
        // configuration of ours can still refine them.
        base.OnModelCreating(b);
        b.ApplyConfigurationsFromAssembly(typeof(NexusDbContext).Assembly);
    }
}
