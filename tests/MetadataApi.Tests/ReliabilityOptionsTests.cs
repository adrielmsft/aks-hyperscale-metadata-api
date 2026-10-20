using System.ComponentModel.DataAnnotations;
using MetadataApi.Reliability;
using Microsoft.Extensions.Options;

namespace MetadataApi.Tests;

public sealed class ReliabilityOptionsTests
{
    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(1, 0)]
    [InlineData(1, 86401)]
    public void InvalidRateLimits_FailValidation(int permits, int seconds)
        => Assert.False(IsValid(new RateLimitingOptions { PermitLimit = permits, WindowSeconds = seconds }));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16777217)]
    public void InvalidBodyLimits_FailValidation(int bytes)
        => Assert.False(IsValid(new RequestLimitOptions { MaxRequestBodyBytes = bytes }));

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.True(IsValid(new RateLimitingOptions()));
        Assert.True(IsValid(new RequestLimitOptions()));
    }

    [Theory]
    [InlineData("RateLimiting:PermitLimit", "0")]
    [InlineData("RateLimiting:WindowSeconds", "0")]
    [InlineData("Limits:MaxRequestBodyBytes", "0")]
    public void InvalidConfiguration_FailsStartup(string key, string value)
    {
        using var factory = new MetadataApiFactory(new Dictionary<string, string> { [key] = value });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    private static bool IsValid(object options)
        => Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);
}
