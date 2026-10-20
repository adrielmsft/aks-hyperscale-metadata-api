using MetadataApi.Validation;

namespace MetadataApi.Tests;

public sealed class PaginationValidationTests
{
    [Fact]
    public void Defaults_MatchTheDocumentedContract()
    {
        Assert.Equal(20, PaginationValidation.DefaultLimit);
        Assert.Equal(100, PaginationValidation.MaxLimit);
        Assert.Equal(10_000, PaginationValidation.MaxOffset);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(20, 0)]
    [InlineData(100, 10000)]
    public void ValidBoundaries_HaveNoErrors(int limit, int offset)
        => Assert.Empty(PaginationValidation.Validate(limit, offset));

    [Theory]
    [InlineData(0, 0, "limit")]
    [InlineData(-1, 0, "limit")]
    [InlineData(101, 0, "limit")]
    [InlineData(int.MaxValue, 0, "limit")]
    [InlineData(20, -1, "offset")]
    [InlineData(20, 10001, "offset")]
    [InlineData(20, int.MaxValue, "offset")]
    public void InvalidBoundaries_ReportFieldErrors(int limit, int offset, string field)
        => Assert.Single(PaginationValidation.Validate(limit, offset)[field]);

    [Fact]
    public void InvalidLimitAndOffset_ReportsBoth()
        => Assert.Equal(2, PaginationValidation.Validate(0, -1).Count);

    [Theory]
    [InlineData(20, 0, true, 20)]
    [InlineData(20, 0, false, null)]
    [InlineData(100, 9900, true, 10000)]
    [InlineData(100, 9901, true, null)]
    [InlineData(1, 9999, true, 10000)]
    [InlineData(1, 10000, true, null)]
    [InlineData(int.MaxValue, int.MaxValue, true, null)]
    public void NextOffset_RequiresMoreResultsAndStaysWithinCap(
        int limit, int offset, bool hasMore, int? expected)
        => Assert.Equal(expected, PaginationValidation.GetNextOffset(limit, offset, hasMore));
}
