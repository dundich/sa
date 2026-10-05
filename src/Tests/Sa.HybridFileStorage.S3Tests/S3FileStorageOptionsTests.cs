using System.ComponentModel.DataAnnotations;
using Sa.Data.S3;
using Sa.HybridFileStorage.S3;

namespace Sa.HybridFileStorage.S3Tests;

/// <summary>
/// <see cref="S3FileStorageOptions"/> is a single mutable type served by the options pipeline, so
/// the checks live on the type itself and throw <see cref="ValidationException"/> — the pipeline
/// turns them into an <c>OptionsValidationException</c> at start-up. They stay usable outside DI.
/// </summary>
public sealed class S3FileStorageOptionsTests
{
    private static S3FileStorageOptions ValidOptions() => new()
    {
        Endpoint = "http://localhost:9000",
        AccessKey = "ROOTUSER",
        SecretKey = "ChangeMe123",
        Bucket = "mybucket",
    };

    [Fact]
    public void Validate_Accepts_Minimal()
    {
        ValidOptions().Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_Rejects_BlankEndpoint(string endpoint)
    {
        var options = ValidOptions();
        options.Endpoint = endpoint;

        // Normalisation deliberately leaves a blank endpoint untouched, so this reports the missing
        // option by name instead of silently turning it into a relative path.
        options.Normalize();

        var ex = Assert.Throws<ValidationException>(() => options.Validate());
        Assert.Contains("Endpoint", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("localhost:9000")]          // parses as a relative reference
    [InlineData("ftp://localhost:9000")]
    [InlineData("/tmp/files")]
    public void Validate_Rejects_EndpointThatIsNotAnAbsoluteHttpUrl(string endpoint)
    {
        var options = ValidOptions();
        options.Endpoint = endpoint;

        var ex = Assert.Throws<ValidationException>(() => options.Validate());
        Assert.Contains("absolute http or https URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_NamesTheOffendingType_AndNotTheBaseClass()
    {
        // The message is built from GetType().Name so that a failure raised for the storage provider
        // says S3FileStorageOptions, not the base class the check lives in.
        var options = ValidOptions();
        options.AccessKey = "  ";

        var ex = Assert.Throws<ValidationException>(() => options.Validate());

        Assert.StartsWith("S3FileStorageOptions:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_BlankCredentials()
    {
        var options = ValidOptions();
        options.AccessKey = "  ";

        Assert.Throws<ValidationException>(() => options.Validate());

        options = ValidOptions();
        options.SecretKey = "  ";

        Assert.Throws<ValidationException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Rejects_EmptyBucket()
    {
        var options = ValidOptions();
        options.Bucket = "";

        Assert.Throws<ValidationException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Rejects_MalformedStorageType()
    {
        var options = ValidOptions();
        options.StorageType = "s3://";

        var ex = Assert.Throws<ValidationException>(() => options.Validate());
        Assert.Contains("StorageType", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_MalformedBasket()
    {
        var options = ValidOptions();
        options.Basket = "ab";

        var ex = Assert.Throws<ValidationException>(() => options.Validate());
        Assert.Contains("Basket", ex.Message, StringComparison.Ordinal);
    }

    // ---------- transport settings ----------

    [Fact]
    public void Validate_Rejects_NonPositiveTotalRequestTimeout()
    {
        var options = ValidOptions();
        options.TotalRequestTimeout = TimeSpan.Zero;

        var ex = Assert.Throws<ValidationException>(() => options.Validate());
        Assert.Contains("TotalRequestTimeout", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_NonPositiveConnectionPoolLifetime()
    {
        var options = ValidOptions();
        options.ConnectionPoolLifetime = TimeSpan.Zero;

        var ex = Assert.Throws<ValidationException>(() => options.Validate());
        Assert.Contains("ConnectionPoolLifetime", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_Rejects_HandlerLifetimeBelowInfinite()
    {
        var options = ValidOptions();
        options.HandlerLifetime = TimeSpan.FromMilliseconds(-2);
        var ex = Assert.Throws<ValidationException>(() => options.Validate());
        Assert.Contains("HandlerLifetime", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsInfiniteHandlerLifetime()
    {
        // The default: never recycle the handler, which is what a long-running service wants.
        var options = ValidOptions();

        Assert.Equal(Timeout.InfiniteTimeSpan, options.HandlerLifetime);
        options.Validate();
    }

    // ---------- normalisation ----------

    [Fact]
    public void Normalize_CanonicalisesTheEndpoint()
    {
        var options = ValidOptions();
        options.Endpoint = "  HTTP://Localhost:9000/  ";
        options.Bucket = " mybucket ";
        options.StorageType = " s3 ";

        options.Normalize();

        Assert.Equal("http://localhost:9000", options.Endpoint);
        Assert.Equal("mybucket", options.Bucket);
        Assert.Equal("s3", options.StorageType);
    }

    [Fact]
    public void Normalize_KeepsAValidPathInTheEndpoint()
    {
        // Only the trailing slash is canonical away — a prefix path is part of the target and must
        // survive normalisation.
        var options = ValidOptions();
        options.Endpoint = "http://localhost:9000/gateway/";

        options.Normalize();

        Assert.Equal("http://localhost:9000/gateway", options.Endpoint);
    }

    // ---------- no projection: the options *are* the client settings ----------

    [Fact]
    public void Options_AreThemselvesTheClientSettings()
    {
        // The projection ToBucketClientSettings() used to copy eight properties by hand, and is how a
        // setting (UseHttp2) could end up configured but never applied. The options instance is now
        // the very object the bucket client is constructed from.
        S3BucketSettings settings = ValidOptions();

        Assert.IsAssignableFrom<S3BucketClientSetupOptions>(settings);

        settings.Endpoint = "http://localhost:9000/";
        settings.Region = "us-east-1";
        settings.UseHttp2 = true;

        ((S3BucketClientSetupOptions)settings).Normalize();

        Assert.Equal("http://localhost:9000", settings.Endpoint);
        Assert.Equal("us-east-1", settings.Region);
        Assert.True(settings.UseHttp2);
    }

    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        var options = ValidOptions();

        Assert.Equal("s3", options.StorageType);
        Assert.Equal(Sa.HybridFileStorage.StorageNaming.DefaultBasket, options.Basket);
        Assert.Equal(S3Defaults.DefaultRegion, options.Region);
        Assert.False(options.IsReadOnly);

        // Inherited transport defaults, unchanged from the previous S3BucketClientSetupSettings.
        Assert.Equal(TimeSpan.FromSeconds(180), options.TotalRequestTimeout);
        Assert.Equal(TimeSpan.FromMinutes(15), options.ConnectionPoolLifetime);
        Assert.Equal(Timeout.InfiniteTimeSpan, options.HandlerLifetime);
        Assert.False(options.UseHttp2);
        Assert.Equal("s3", options.Service);
    }

    [Fact]
    public void Region_DefaultsToTheS3ProviderValue_NotTheBaseClassOne()
    {
        // The base class defaults to us-east-1 for the standalone client; the storage provider has
        // always defaulted to eu-central-1, and setting it in the constructor (rather than
        // redeclaring the property) keeps a single Region on the instance.
        Assert.Equal("us-east-1", new S3BucketClientSetupOptions().Region);
        Assert.Equal(S3Defaults.DefaultRegion, new S3FileStorageOptions().Region);
    }
}
