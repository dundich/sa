using Sa.HybridFileStorage.S3;

namespace Sa.HybridFileStorage.S3Tests;

/// <summary>
/// The S3 options are validated at registration so that a bad endpoint surfaces as a named
/// <see cref="ArgumentException"/> instead of a bare <see cref="UriFormatException"/> thrown
/// later from inside the bucket client setup.
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

    [Fact]
    public void Validate_Rejects_NullEndpoint_AsArgumentNull()
    {
        var options = ValidOptions() with { Endpoint = null! };

        // ArgumentException.ThrowIfNullOrWhiteSpace follows the BCL convention: null is a
        // missing value (ArgumentNullException), whitespace is a malformed one (ArgumentException).
        // The old code used ArgumentNullException.ThrowIfNullOrWhiteSpace and reported
        // "Value cannot be null" for a blank endpoint.
        var ex = Assert.Throws<ArgumentNullException>(() => options.Validate());
        Assert.Equal("options.Endpoint", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_Rejects_BlankEndpoint_AsArgumentException(string endpoint)
    {
        var options = ValidOptions() with { Endpoint = endpoint };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal("options.Endpoint", ex.ParamName);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("localhost:9000")]          // parses as a relative reference
    [InlineData("ftp://localhost:9000")]
    [InlineData("/tmp/files")]
    public void Validate_Rejects_EndpointThatIsNotAnAbsoluteHttpUrl(string endpoint)
    {
        var options = ValidOptions() with { Endpoint = endpoint };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal("options.Endpoint", ex.ParamName);
        Assert.Contains("absolute http or https URL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_UsesTheCallerParamName()
    {
        var options = ValidOptions() with { Bucket = " " };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate("myOptions"));
        Assert.Equal("myOptions.Bucket", ex.ParamName);
    }

    [Fact]
    public void Validate_Rejects_BlankCredentials()
    {
        Assert.Throws<ArgumentException>(() => (ValidOptions() with { AccessKey = "  " }).Validate());
        Assert.Throws<ArgumentException>(() => (ValidOptions() with { SecretKey = "  " }).Validate());
        Assert.Throws<ArgumentNullException>(() => (ValidOptions() with { AccessKey = null! }).Validate());
    }

    [Fact]
    public void Validate_Rejects_EmptyBucket()
    {
        var options = ValidOptions() with { Bucket = "" };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_Rejects_MalformedStorageType()
    {
        var options = ValidOptions() with { StorageType = "s3://" };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal("options.StorageType", ex.ParamName);
    }

    [Fact]
    public void Validate_Rejects_MalformedBasket()
    {
        var options = ValidOptions() with { Basket = "ab" };

        var ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Equal("options.Basket", ex.ParamName);
    }

    // ---------- ToBucketClientSettings ----------

    [Fact]
    public void ToBucketClientSettings_CopiesCredentialsAndTrimsEndpoint()
    {
        var settings = (ValidOptions() with { Endpoint = "http://localhost:9000/" }).ToBucketClientSettings();

        Assert.Equal("http://localhost:9000", settings.Endpoint);
        Assert.Equal("ROOTUSER", settings.AccessKey);
        Assert.Equal("ChangeMe123", settings.SecretKey);
        Assert.Equal("mybucket", settings.Bucket);
        Assert.Equal(S3Defaults.DefaultRegion, settings.Region);
    }

    [Fact]
    public void ToBucketClientSettings_KeepsUpstreamDefaults_WhenClientSettingsIsNull()
    {
        var settings = ValidOptions().ToBucketClientSettings();
        var upstream = new Sa.Data.S3.S3BucketClientSetupSettings
        {
            AccessKey = "a",
            SecretKey = "b",
            Bucket = "c",
            Endpoint = "http://x",
        };

        Assert.Equal(upstream.TotalRequestTimeout, settings.TotalRequestTimeout);
        Assert.Equal(upstream.ConnectionPoolLifetime, settings.ConnectionPoolLifetime);
        Assert.Equal(upstream.HandlerLifetime, settings.HandlerLifetime);
    }

    [Fact]
    public void ToBucketClientSettings_AppliesClientSettings()
    {
        var timeout = TimeSpan.FromSeconds(30);
        var pool = TimeSpan.FromMinutes(5);
        var handler = TimeSpan.FromHours(2);

        var options = ValidOptions() with
        {
            ClientSettings = new Sa.Data.S3.S3BucketClientSetupSettings
            {
                AccessKey = "ROOTUSER",
                SecretKey = "ChangeMe123",
                Bucket = "mybucket",
                Endpoint = "http://localhost:9000",
                TotalRequestTimeout = timeout,
                ConnectionPoolLifetime = pool,
                HandlerLifetime = handler,
            },
        };

        var settings = options.ToBucketClientSettings();

        Assert.Equal(timeout, settings.TotalRequestTimeout);
        Assert.Equal(pool, settings.ConnectionPoolLifetime);
        Assert.Equal(handler, settings.HandlerLifetime);
    }

    // ---------- storage identity (what the registration guard compares) ----------

    [Fact]
    public void HasSameStorageIdentity_IgnoresCredentialsAndTransportTuning()
    {
        var a = ValidOptions();
        var b = (a with { AccessKey = "OTHERUSER", SecretKey = "OtherSecret456" })
            with
            {
                ClientSettings = new Sa.Data.S3.S3BucketClientSetupSettings
                {
                    AccessKey = "OTHERUSER",
                    SecretKey = "OtherSecret456",
                    Bucket = "mybucket",
                    Endpoint = "http://localhost:9000",
                },
            };

        Assert.NotEqual(a, b);                                  // record equality differs
        Assert.True(a.HasSameStorageIdentity(b));              // ...but the storage is the same
    }

    [Theory]
    [InlineData("uploads", "s3", false)]
    [InlineData("share", "minio", false)]
    [InlineData("share", "s3", true)]
    public void HasSameStorageIdentity_DetectsADifferentStorage(
        string basket, string storageType, bool isReadOnly)
    {
        var a = ValidOptions();

        Assert.False(a.HasSameStorageIdentity(
            a with { Basket = basket, StorageType = storageType, IsReadOnly = isReadOnly }));
    }

    [Fact]
    public void HasSameStorageIdentity_IsFalse_ForNull()
    {
        Assert.False(ValidOptions().HasSameStorageIdentity(null));
    }

    // ---------- value equality (needed by the registration guard) ----------

    [Fact]
    public void Options_HaveValueEquality()
    {
        var a = ValidOptions();
        var b = ValidOptions();

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { Basket = "uploads" });
    }

    [Fact]
    public void Defaults_AreTheDocumentedOnes()
    {
        var options = ValidOptions();

        Assert.Equal("s3", options.StorageType);
        Assert.Equal(Sa.HybridFileStorage.StorageNaming.DefaultBasket, options.Basket);
        Assert.Equal(S3Defaults.DefaultRegion, options.Region);
        Assert.False(options.IsReadOnly);
        Assert.Null(options.ClientSettings);
    }
}
