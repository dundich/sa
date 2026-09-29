namespace Sa.Outbox.PostgreSql.SqlBuilder;

/// <summary>
/// Named constants for every SQL parameter placeholder used across the SqlBuilder templates.
/// Each constant is a short mnemonic alias (e.g. <c>@tnt</c> for tenant id) that is injected
/// into interpolated SQL strings at construction time and later resolved to real values by
/// Npgsql parameter-adding extension methods.
/// </summary>
internal static class SqlParam
{
    public const string TenantId = "@tnt";
    public const string ConsumerGroupId = "@gr";
    public const string MsgPart = "@prt";
    public const string TypeId = "@tp_id";
    public const string TypeName = "@tp_nm";
    public const string FromDate = "@frm";
    public const string NowDate = "@now";
    // The three below replace the single ambiguous `@to` / `@frm` pair. The same
    // OutboxMessageFilter field used to drive three different meanings, and the only thing
    // distinguishing them was which SQL happened to be executing:
    //
    //   WindowFrom  — lower bound of the time window a batch is drawn from.
    //   WindowTo    — upper bound of that same window.
    //   RetryAfter  — the instant before which a task is *not* yet re-rentable. This one is
    //                 fed `now - BatchingWindow` on purpose; see SqlOutboxBuilder.SqlLockAndSelect.
    public const string WindowFrom = "@win_from";
    public const string WindowTo = "@win_to";
    public const string RetryAfter = "@rty_at";
    public const string TransactId = "@trn";
    /// <summary>Consumption cursor. BIGINT since the C2 fix: it is a <c>msg_seq</c> position, not a v7 id.</summary>
    public const string Offset = "@offset";
    public const string Limit = "@lim";
    public const string LockOffset = "@lck_id";
    public const string LockExpiresOn = "@lck_on";
    public const string PayloadId = "@p_id";
    public const string TaskId = "@tsk";
    public const string StatusCode = "@st_c";
    public const string StatusMessage = "@st_m";
    public const string CreatedAt = "@cr_at";
    public const string TaskCreatedAt = "@tsk_at";
    public const string ErrorId = "@err_id";
    public const string MsgId = "@msg_id";
    public const string FloorDate = "@flr_date";
}
