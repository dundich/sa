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
    public const string ToDate = "@to";
    public const string NowDate = "@now";
    public const string TransactId = "@trn";
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
}
