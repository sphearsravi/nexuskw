namespace Nexus.Domain;

/* Every value is explicit and is never renumbered: these are persisted as ints,
 * so renumbering silently rewrites history. Add new members at the end. */

public enum OrderStatus
{
    Pending = 0,
    Paid = 1,
    Failed = 2,
    Refunded = 3,
    Cancelled = 4,
    PartiallyRefunded = 5
}

public enum PaymentAttemptStatus
{
    Initiated = 0,
    InProgress = 1,
    Captured = 2,
    Authorized = 3,
    Declined = 4,
    Failed = 5,
    Cancelled = 6,
    Abandoned = 7,
    Unknown = 99
}

public enum TapObjectType
{
    Charge = 0,
    Refund = 1,
    Other = 99
}

public enum RefundStatus
{
    Pending = 0,
    Refunded = 1,
    Failed = 2
}

public enum RefundOrigin
{
    Admin = 0,
    TapDashboard = 1
}

public enum PaymentSource
{
    Webhook = 0,
    Reconciliation = 1,
    Admin = 2,
    Checkout = 3
}

public enum WebhookOutcome
{
    Accepted = 0,
    Duplicate = 1,
    RejectedSignature = 2,
    RejectedAmount = 3,
    RejectedCurrency = 4,
    UnknownOrder = 5,
    IllegalTransition = 6,
    Unparseable = 7
}

public enum GrantState
{
    Active = 0,
    Exhausted = 1,
    Expired = 2,
    Revoked = 3
}

public enum RevocationReason
{
    None = 0,
    Refunded = 1,
    Chargeback = 2,
    AbuseSuspected = 3,
    OperatorRequest = 4,
    RightsWithdrawn = 5,
    CustomerDataDeleted = 6,
    ReIssued = 7
}

public enum DownloadOutcome
{
    Served = 0,
    Aborted = 1,
    Expired = 2,
    Exhausted = 3,
    Revoked = 4,
    RateLimited = 5,
    StampFailed = 6,
    Busy = 7
}

public enum EmailKind
{
    OrderConfirmation = 0,
    DownloadLinks = 1,
    RefundNotice = 2,
    AccountConfirm = 10,
    PasswordReset = 11
}

public enum EmailStatus
{
    Queued = 0,
    Sending = 1,
    Sent = 2,
    Failed = 3,
    Abandoned = 4,
    Suppressed = 5
}

public enum SuppressionReason
{
    HardBounce = 0,
    Complaint = 1,
    RepeatedSoftBounce = 2,
    ManualBlock = 3
}
