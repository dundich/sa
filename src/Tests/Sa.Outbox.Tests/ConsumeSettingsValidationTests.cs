using Sa.Outbox.Delivery;
using Xunit;

namespace Sa.Outbox.Tests;

public class ConsumeSettingsValidationTests
{
    [Fact]
    public void Default_Settings_Are_Valid()
    {
        var settings = new ConsumeSettings();
        var result = settings.Validate();

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Zero_MaxBatchSize_Is_Invalid()
    {
        var settings = new ConsumeSettings { MaxBatchSize = 0 };
        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.Single(result.Errors);
        Assert.Contains("MaxBatchSize", result.Errors[0]);
    }

    [Fact]
    public void Negative_MaxBatchSize_Is_Invalid()
    {
        var settings = new ConsumeSettings { MaxBatchSize = -1 };
        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("MaxBatchSize", result.Errors[0]);
    }

    [Fact]
    public void Invalid_MaxProcessingIterations_Is_Invalid()
    {
        var settings = new ConsumeSettings { MaxProcessingIterations = -5 };
        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("MaxProcessingIterations", result.Errors[0]);
    }

    [Fact]
    public void Greedy_Mode_MaxProcessingIterations_MinusOne_Is_Valid()
    {
        var settings = new ConsumeSettings { MaxProcessingIterations = -1 };
        var result = settings.Validate();

        Assert.True(result.IsValid);
    }

    [Fact]
    public void LockRenewal_Greater_Than_LockDuration_Is_Invalid()
    {
        var settings = new ConsumeSettings
        {
            LockDuration = TimeSpan.FromSeconds(5),
            LockRenewal = TimeSpan.FromSeconds(10)
        };
        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("LockRenewal", result.Errors[0]);
    }

    [Fact]
    public void Equal_LockRenewal_And_LockDuration_Is_Invalid()
    {
        var settings = new ConsumeSettings
        {
            LockDuration = TimeSpan.FromSeconds(10),
            LockRenewal = TimeSpan.FromSeconds(10)
        };
        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("LockRenewal", result.Errors[0]);
    }

    [Fact]
    public void Zero_PerTenantMaxDegreeOfParallelism_Is_Invalid()
    {
        var settings = new ConsumeSettings { PerTenantMaxDegreeOfParallelism = 0 };
        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("PerTenantMaxDegreeOfParallelism", result.Errors[0]);
    }

    [Fact]
    public void Negative_MaxDeliveryAttempts_Is_Invalid()
    {
        var settings = new ConsumeSettings { MaxDeliveryAttempts = 0 };
        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("MaxDeliveryAttempts", result.Errors[0]);
    }

    [Fact]
    public void Negative_ConsumeBatchSize_Is_Invalid()
    {
        var settings = new ConsumeSettings { ConsumeBatchSize = -1 };
        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.Contains("ConsumeBatchSize", result.Errors[0]);
    }

    [Fact]
    public void ThrowIfInvalid_Does_Not_Throw_For_Valid_Settings()
    {
        var settings = new ConsumeSettings();
        var ex = Record.Exception(() => settings.ThrowIfInvalid());
        Assert.Null(ex);
    }

    [Fact]
    public void ThrowIfInvalid_Throws_For_Invalid_Settings()
    {
        var settings = new ConsumeSettings { MaxBatchSize = 0 };
        Assert.Throws<InvalidOperationException>(() => settings.ThrowIfInvalid());
    }

    [Fact]
    public void Multiple_Violations_Return_All_Errors()
    {
        var settings = new ConsumeSettings
        {
            MaxBatchSize = 0,
            MaxDeliveryAttempts = -1,
            PerTenantMaxDegreeOfParallelism = 0
        };
        var result = settings.Validate();

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 3);
    }

    [Fact]
    public void Zero_IterationDelay_Is_Valid()
    {
        var settings = new ConsumeSettings { IterationDelay = TimeSpan.Zero };
        var result = settings.Validate();

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Zero_BatchingWindow_Is_Valid()
    {
        var settings = new ConsumeSettings { BatchingWindow = TimeSpan.Zero };
        var result = settings.Validate();

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Zero_PerTenantTimeout_Is_Valid()
    {
        var settings = new ConsumeSettings { PerTenantTimeout = TimeSpan.Zero };
        var result = settings.Validate();

        Assert.True(result.IsValid);
    }
}
