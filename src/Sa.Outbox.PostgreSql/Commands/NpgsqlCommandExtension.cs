using Npgsql;
using Sa.Data.PostgreSql;
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

    public static NpgsqlCommand AddParamFromDate(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.FromDate, value.ToUnixTimeSeconds()));
        return command;
    }

    public static NpgsqlCommand AddParamToDate(this NpgsqlCommand command, DateTimeOffset value)
    {
        command.Parameters.Add(new NpgsqlParameter<long>(SqlParam.ToDate, value.ToUnixTimeSeconds()));
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

    public static NpgsqlCommand AddParamOffset(this NpgsqlCommand command, Guid value)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid>(SqlParam.Offset, value));
        return command;
    }

    sealed class BatchParams : INamePrefixProvider
    {
        public static int MaxIndex => 512;

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
