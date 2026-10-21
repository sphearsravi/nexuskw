using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrdersPaymentsDeliveryEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PdfIsPlaceholder",
                table: "Books",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PdfOriginalFileName",
                table: "Books",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PdfPageCount",
                table: "Books",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PdfSha256",
                table: "Books",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PdfSizeBytes",
                table: "Books",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PdfStorageKey",
                table: "Books",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PdfUploadedAt",
                table: "Books",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PdfUploadedByAdminUserId",
                table: "Books",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ActorId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ActorEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Entity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BeforeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AfterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalisedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Country = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    PreferredLanguage = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    IdentityUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailSuppressions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NormalisedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<int>(type: "int", nullable: false),
                    LastClassification = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SoftBounceCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSuppressions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobLeases",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    HolderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    HeldUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobLeases", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsSecret = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    BuyerNameSnapshot = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    BuyerEmailSnapshot = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StatusDetail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReviewReason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Language = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    SubtotalKwd = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                    VatAmount = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    TotalKwd = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    FxRate = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TotalCharged = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    RefundedTotalKwd = table.Column<decimal>(type: "decimal(18,3)", nullable: false, defaultValue: 0m),
                    TapChargeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TapPaymentReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TapGatewayReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PaymentMethod = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PaymentAttemptCount = table.Column<int>(type: "int", nullable: false),
                    PaymentExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastPaymentFailureCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.CheckConstraint("CK_Order_Charged", "[TotalCharged] = [TotalKwd]");
                    table.CheckConstraint("CK_Order_Currency_V1", "[Currency] = 'KWD'");
                    table.CheckConstraint("CK_Order_Fx_V1", "[FxRate] = 1");
                    table.CheckConstraint("CK_Order_RefundBound", "[RefundedTotalKwd] >= 0 AND [RefundedTotalKwd] <= [TotalCharged]");
                    table.CheckConstraint("CK_Order_TotalIdentity", "[TotalKwd] = [SubtotalKwd] + [VatAmount]");
                    table.CheckConstraint("CK_Order_TotalPositive", "[TotalKwd] > 0");
                    table.CheckConstraint("CK_Order_Vat_V1", "[VatRate] = 0 AND [VatAmount] = 0");
                    table.ForeignKey(
                        name: "FK_Orders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DownloadGrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    BookId = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TokenProtected = table.Column<byte[]>(type: "varbinary(512)", maxLength: 512, nullable: true),
                    MaxDownloads = table.Column<int>(type: "int", nullable: false),
                    UsedCount = table.Column<int>(type: "int", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    RevocationReason = table.Column<int>(type: "int", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FlaggedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadGrants_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DownloadGrants_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    BookId = table.Column<int>(type: "int", nullable: false),
                    UnitPriceKwd = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    TitleEnSnapshot = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    TitleArSnapshot = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AuthorEnSnapshot = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AuthorArSnapshot = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItems", x => x.Id);
                    table.CheckConstraint("CK_OrderItem_PricePositive", "[UnitPriceKwd] > 0");
                    table.ForeignKey(
                        name: "FK_OrderItems_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderItems_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OutboxEmails",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: true),
                    CustomerId = table.Column<int>(type: "int", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    ToEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    PublicRef = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxEmails_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OutboxEmails_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAttempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    IdempotentReference = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TapChargeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TapPaymentReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TapGatewayReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PaymentMethod = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    FailureCode = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    FailureMessage = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    AmountCharged = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    TransactionUrl = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    LastPolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastReturnPolledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentAttempts_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WebhookEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TapObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ObjectType = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OrderId = table.Column<int>(type: "int", nullable: true),
                    HashValid = table.Column<bool>(type: "bit", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    RejectionReason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RawBody = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawBodySha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    RemoteIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebhookEvents_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DownloadLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GrantId = table.Column<int>(type: "int", nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IpPrefix = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    BytesSent = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadLogs_DownloadGrants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "DownloadGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Refunds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    PaymentAttemptId = table.Column<int>(type: "int", nullable: true),
                    TapRefundId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AmountKwd = table.Column<decimal>(type: "decimal(18,3)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Origin = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    RequestedByAdminUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Refunds_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Refunds_PaymentAttempts_PaymentAttemptId",
                        column: x => x.PaymentAttemptId,
                        principalTable: "PaymentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrderEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OrderId = table.Column<int>(type: "int", nullable: false),
                    FromStatus = table.Column<int>(type: "int", nullable: true),
                    ToStatus = table.Column<int>(type: "int", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    WebhookEventId = table.Column<long>(type: "bigint", nullable: true),
                    PaymentAttemptId = table.Column<int>(type: "int", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderEvents_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrderEvents_PaymentAttempts_PaymentAttemptId",
                        column: x => x.PaymentAttemptId,
                        principalTable: "PaymentAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OrderEvents_WebhookEvents_WebhookEventId",
                        column: x => x.WebhookEventId,
                        principalTable: "WebhookEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Entity_EntityId_At",
                table: "AuditLogs",
                columns: new[] { "Entity", "EntityId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_IdentityUserId",
                table: "Customers",
                column: "IdentityUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_NormalisedEmail",
                table: "Customers",
                column: "NormalisedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadGrants_BookId",
                table: "DownloadGrants",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadGrants_FlaggedAt",
                table: "DownloadGrants",
                column: "FlaggedAt",
                filter: "[FlaggedAt] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadGrants_OrderId",
                table: "DownloadGrants",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadGrants_OrderId_BookId_Sequence",
                table: "DownloadGrants",
                columns: new[] { "OrderId", "BookId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadGrants_State_ExpiresAt",
                table: "DownloadGrants",
                columns: new[] { "State", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadGrants_TokenHash",
                table: "DownloadGrants",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadLogs_AtUtc",
                table: "DownloadLogs",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadLogs_GrantId_AtUtc",
                table: "DownloadLogs",
                columns: new[] { "GrantId", "AtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadLogs_GrantId_IpPrefix",
                table: "DownloadLogs",
                columns: new[] { "GrantId", "IpPrefix" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailSuppressions_NormalisedEmail",
                table: "EmailSuppressions",
                column: "NormalisedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderEvents_OrderId_At",
                table: "OrderEvents",
                columns: new[] { "OrderId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderEvents_PaymentAttemptId",
                table: "OrderEvents",
                column: "PaymentAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderEvents_WebhookEventId",
                table: "OrderEvents",
                column: "WebhookEventId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_BookId",
                table: "OrderItems",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_OrderId_BookId",
                table: "OrderItems",
                columns: new[] { "OrderId", "BookId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_CustomerId_CreatedAt",
                table: "Orders",
                columns: new[] { "CustomerId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_IdempotencyKey",
                table: "Orders",
                column: "IdempotencyKey",
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_OrderNumber",
                table: "Orders",
                column: "OrderNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status_CreatedAt",
                table: "Orders",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status_PaymentExpiresAt",
                table: "Orders",
                columns: new[] { "Status", "PaymentExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_TapChargeId",
                table: "Orders",
                column: "TapChargeId",
                unique: true,
                filter: "[TapChargeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmails_CustomerId",
                table: "OutboxEmails",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmails_IdempotencyKey",
                table: "OutboxEmails",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmails_OrderId_CreatedAt",
                table: "OutboxEmails",
                columns: new[] { "OrderId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmails_PublicRef",
                table: "OutboxEmails",
                column: "PublicRef",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmails_Status_NextAttemptAt",
                table: "OutboxEmails",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "[Status] IN (0,1)");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEmails_ToEmail_CreatedAt",
                table: "OutboxEmails",
                columns: new[] { "ToEmail", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_IdempotentReference",
                table: "PaymentAttempts",
                column: "IdempotentReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_OrderId",
                table: "PaymentAttempts",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_OrderId_AttemptNumber",
                table: "PaymentAttempts",
                columns: new[] { "OrderId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_Status_CreatedAt",
                table: "PaymentAttempts",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAttempts_TapChargeId",
                table: "PaymentAttempts",
                column: "TapChargeId",
                unique: true,
                filter: "[TapChargeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_OrderId",
                table: "Refunds",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentAttemptId",
                table: "Refunds",
                column: "PaymentAttemptId");

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_TapRefundId",
                table: "Refunds",
                column: "TapRefundId",
                unique: true,
                filter: "[TapRefundId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_OrderId",
                table: "WebhookEvents",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_TapObjectId_Status",
                table: "WebhookEvents",
                columns: new[] { "TapObjectId", "Status" },
                unique: true,
                filter: "[HashValid] = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "DownloadLogs");

            migrationBuilder.DropTable(
                name: "EmailSuppressions");

            migrationBuilder.DropTable(
                name: "JobLeases");

            migrationBuilder.DropTable(
                name: "OrderEvents");

            migrationBuilder.DropTable(
                name: "OrderItems");

            migrationBuilder.DropTable(
                name: "OutboxEmails");

            migrationBuilder.DropTable(
                name: "Refunds");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "DownloadGrants");

            migrationBuilder.DropTable(
                name: "WebhookEvents");

            migrationBuilder.DropTable(
                name: "PaymentAttempts");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "Customers");

            migrationBuilder.DropColumn(
                name: "PdfIsPlaceholder",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "PdfOriginalFileName",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "PdfPageCount",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "PdfSha256",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "PdfSizeBytes",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "PdfStorageKey",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "PdfUploadedAt",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "PdfUploadedByAdminUserId",
                table: "Books");
        }
    }
}
