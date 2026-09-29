using Npgsql;
using Sa.Data.PostgreSql;
using Sa.Outbox.Delivery;
using Sa.Outbox.PostgreSql.SqlBuilder;

namespace Sa.Outbox.PostgreSql.Commands;

internal static class NpgsqlCommandExtension
{
    public static NpgsqlCommand AddParamTenantId(this NpgsqlCommand command, int value)
    {
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.TenantId, value));
        return command;
    }

    public static NpgsqlCommand AddParamMsgPart(this NpgsqlCommand command, string value)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.MsgPart, value));
        return command;
    }

    /// <summary>Lookback floor for historical rows: "anything created at or after this".</summary>
    public static NpgsqlCommand AddParamFromDate(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.FromDate, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamWindowFrom(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowFrom, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamWindowTo(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.WindowTo, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamRetryAfter(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.RetryAfter, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamNowDate(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.NowDate, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamConsumerGroupId(this NpgsqlCommand command, string value)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.ConsumerGroupId, value));
        return command;
    }

    public static NpgsqlCommand AddParamTypeId(this NpgsqlCommand command, long value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.TypeId, value));
        return command;
    }

    public static NpgsqlCommand AddParamTransactId(this NpgsqlCommand command, string value)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.TransactId, value));
        return command;
    }

    public static NpgsqlCommand AddParamLimit(this NpgsqlCommand command, int value)
    {
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.Limit, value));
        return command;
    }

    public static NpgsqlCommand AddParamAdvisoryXactLock(this NpgsqlCommand command, int value)
    {
        command.Parameters.Add(new NpgsqlParameter<int>(SqlParam.LockOffset, value));
        return command;
    }

    public static NpgsqlCommand AddParamLockExpiresOn(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.LockExpiresOn, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamLockExpiresOn(this NpgsqlCommand command, long value, int index)
        => command.AddParam<BatchParams, long>(SqlParam.LockExpiresOn, value, index);

    public static NpgsqlCommand AddParamErrorId(this NpgsqlCommand command, long? value, int index)
        => command.AddParam<BatchParams, long>(SqlParam.ErrorId, value ?? 0, index);

    public static NpgsqlCommand AddParamTypeName(this NpgsqlCommand command, string value)
    {
        command.Parameters.Add(new NpgsqlParameter<string>(SqlParam.TypeName, value));
        return command;
    }

    public static NpgsqlCommand AddParamTypeName(this NpgsqlCommand command, string? value, int index)
        => command.AddParam<BatchParams, string>(SqlParam.TypeName, value ?? string.Empty, index);

    public static NpgsqlCommand AddParamStatusCode(this NpgsqlCommand command, DeliveryStatusCode value, int index)
        => command.AddParam<BatchParams, int>(SqlParam.StatusCode, (int)value, index);

    public static NpgsqlCommand AddParamStatusMessage(this NpgsqlCommand command, string? value, int index)
        => command.AddParam<BatchParams, string>(SqlParam.StatusMessage, value ?? string.Empty, index);

    public static NpgsqlCommand AddParamCreatedAt(this NpgsqlCommand command, DateTimeOffset value, int index)
        => command.AddParam<BatchParams, long>(SqlParam.CreatedAt, value.ToUnixTimeSeconds(), index);

    public static NpgsqlCommand AddParamPayloadId(this NpgsqlCommand command, string value, int index)
        => command.AddParam<BatchParams, string>(SqlParam.PayloadId, value, index);

    public static NpgsqlCommand AddParamTaskId(this NpgsqlCommand command, long value, int index)
        => command.AddParam<BatchParams, long>(SqlParam.TaskId, value, index);

    public static NpgsqlCommand AddParamTaskCreatedAt(this NpgsqlCommand command, DateTimeOffset value, int index)
        => command.AddParam<BatchParams, long>(SqlParam.TaskCreatedAt, value.ToUnixTimeSeconds(), index);

    public static NpgsqlCommand AddParamOffset(this NpgsqlCommand command, long value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.Offset, value));
        return command;
    }

    /// <summary>Legacy floor: the v7 message id a consumer group must not start below.</summary>
    public static NpgsqlCommand AddParamMsgId(this NpgsqlCommand command, Guid value)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid>(SqlParam.MsgId, value));
        return command;
    }

    /// <summary>Floor by date: resolve "start consuming from this moment" to a msg_seq boundary.</summary>
    public static NpgsqlCommand AddParamFloorDate(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.FloorDate, value.ToUnixTimeSeconds()));
        return command;
    }

    internal sealed class BatchParams : INamePrefixProvider
    {
        // MUST stay in lock-step with SqlCacheSplitter.DefaultMaxLen: an index at or above
        // this cache falls back to string interpolation (CachedParamNames.Get → Combine),
        // silently undoing the cache on exactly the statement it exists for. Sharing the
        // single constant makes divergence impossible by construction.
        public static int MaxIndex => SqlCacheSplitter.DefaultMaxLen;

        public static string[] GetPrefixes() =>
        [
            SqlParam.ErrorId,
            SqlParam.TypeName,
            SqlParam.StatusCode,
            SqlParam.StatusMessage,
            SqlParam.CreatedAt,
            SqlParam.PayloadId,
            SqlParam.TaskId,
            SqlParam.LockExpiresOn,
            SqlParam.TaskCreatedAt
        ];
    }
}
