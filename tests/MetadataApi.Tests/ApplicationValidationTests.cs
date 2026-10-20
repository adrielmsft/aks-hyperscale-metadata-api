using MetadataApi.Contracts;
using MetadataApi.Validation;

namespace MetadataApi.Tests;

public sealed class ApplicationValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Title_IsRequired(string? title)
    {
        var request = ApplicationValidation.Normalize(new CreateApplicationRequest { Title = title });

        Assert.Contains("title", ApplicationValidation.Validate(request));
    }

    [Fact]
    public void Normalization_TrimsTextAndPreservesInternalWhitespaceAndCasing()
    {
        var request = ApplicationValidation.Normalize(new CreateApplicationRequest
        {
            Title = "  Caf\u00e9  Portal  ",
            Version = " 1.2.3-RC.1 ",
            Company = " Example Company ",
            Website = " https://example.com/Portal ",
            Source = " https://github.com/example/Portal ",
            License = " Apache-2.0 ",
            Description = " First line\n\tSecond line "
        });

        Assert.Empty(ApplicationValidation.Validate(request));
        Assert.Equal("Caf\u00e9  Portal", request.Title);
        Assert.Equal("1.2.3-RC.1", request.Version);
        Assert.Equal("Example Company", request.Company);
        Assert.Equal("https://example.com/Portal", request.Website);
        Assert.Equal("https://github.com/example/Portal", request.Source);
        Assert.Equal("Apache-2.0", request.License);
        Assert.Equal("First line\n\tSecond line", request.Description);
    }

    [Fact]
    public void OptionalWhitespace_NormalizesToNull()
    {
        var request = ApplicationValidation.Normalize(new CreateApplicationRequest
        {
            Title = "Portal", Version = " ", Company = "", Website = "\t",
            Source = "\n", License = " ", Description = " \r\n "
        });

        Assert.Empty(ApplicationValidation.Validate(request));
        Assert.Null(request.Version);
        Assert.Null(request.Company);
        Assert.Null(request.Website);
        Assert.Null(request.Source);
        Assert.Null(request.License);
        Assert.Null(request.Description);
    }

    [Theory]
    [InlineData("title", 200)]
    [InlineData("version", 100)]
    [InlineData("company", 200)]
    [InlineData("website", 2048)]
    [InlineData("source", 2048)]
    [InlineData("license", 100)]
    [InlineData("description", 4000)]
    public void LengthLimits_AcceptExactBoundaryAfterTrimmingAndRejectOneBeyond(string field, int maximum)
    {
        var prefix = field switch
        {
            "version" => "1.2.3+",
            "website" or "source" => "https://example.com/",
            _ => ""
        };
        var value = prefix + new string('a', maximum - prefix.Length);
        var valid = ApplicationValidation.Normalize(WithField(field, $" {value} "));
        var invalid = ApplicationValidation.Normalize(WithField(field, value + "a"));

        Assert.Empty(ApplicationValidation.Validate(valid));
        Assert.Contains(field, ApplicationValidation.Validate(invalid));
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3")]
    [InlineData("2.0.0-rc.1")]
    [InlineData("1.0.0-0")]
    [InlineData("1.0.0-01a")]
    [InlineData("1.0.0-alpha-beta.2+build.001")]
    [InlineData("999999999999999999999.0.0")]
    public void SemanticVersion_AcceptsValidVersions(string version)
    {
        Assert.Empty(ApplicationValidation.Validate(WithField("version", version)));
    }

    [Theory]
    [InlineData("v1.2.3")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0-alpha..1")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0+build..1")]
    [InlineData("1.0.0-alpha_1")]
    [InlineData("1.0.0\njunk")]
    public void SemanticVersion_RejectsInvalidVersions(string version)
    {
        Assert.Contains("version", ApplicationValidation.Validate(WithField("version", version)));
    }

    [Theory]
    [InlineData("https://example.com/path?q=1#section", true)]
    [InlineData("http://localhost:5108/source", true)]
    [InlineData("https://private.example.com/repository", true)]
    [InlineData("/relative/path", false)]
    [InlineData("ftp://example.com/file", false)]
    [InlineData("file:///tmp/source", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://", false)]
    [InlineData("https://user:password@example.com/repository", false)]
    public void UrlRules_ApplyToWebsiteAndSource(string url, bool valid)
    {
        foreach (var field in new[] { "website", "source" })
        {
            var errors = ApplicationValidation.Validate(WithField(field, url));
            Assert.Equal(!valid, errors.ContainsKey(field));
        }
    }

    [Theory]
    [InlineData("title")]
    [InlineData("version")]
    [InlineData("company")]
    [InlineData("website")]
    [InlineData("source")]
    [InlineData("license")]
    [InlineData("description")]
    public void ControlCharacters_AreRejected(string field)
    {
        Assert.Contains(field, ApplicationValidation.Validate(WithField(field, "a\0b")));
    }

    [Fact]
    public void Validation_ReportsAllInvalidFields()
    {
        var errors = ApplicationValidation.Validate(new CreateApplicationRequest
        {
            Title = null, Version = "bad", Website = "file:///source",
            Source = "/relative", Company = "a\nb"
        });

        Assert.Equal(5, errors.Count);
        Assert.All(new[] { "title", "version", "website", "source", "company" },
            field => Assert.Contains(field, errors));
    }

    [Fact]
    public void Update_NormalizesEveryFieldUsingCreateRules()
    {
        var request = ApplicationValidation.Normalize(new UpdateApplicationRequest
        {
            Title = " Microsoft Garage ", Version = " 1.2.3-rc.1+build.7 ",
            Company = " Microsoft ", Website = " https://garage.example.com ",
            Source = " https://github.com/example/garage ", License = " Apache-2.0 ",
            Description = " Garage locations\n\tGarage staff "
        });

        Assert.Empty(ApplicationValidation.Validate(request));
        Assert.Equal("Microsoft Garage", request.Title);
        Assert.Equal("1.2.3-rc.1+build.7", request.Version);
        Assert.Equal("Microsoft", request.Company);
        Assert.Equal("https://garage.example.com", request.Website);
        Assert.Equal("https://github.com/example/garage", request.Source);
        Assert.Equal("Apache-2.0", request.License);
        Assert.Equal("Garage locations\n\tGarage staff", request.Description);
        var blank = ApplicationValidation.Normalize(request with
        {
            Version = " ", Company = "", Website = "\t", Source = "\n", License = " ", Description = " \r\n "
        });
        Assert.Equal(new UpdateApplicationRequest { Title = "Microsoft Garage" }, blank);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void Update_TitleIsRequired(string? title)
        => Assert.Contains("title", ApplicationValidation.Validate(
            ApplicationValidation.Normalize(new UpdateApplicationRequest { Title = title })));

    [Theory]
    [InlineData("title", 200)]
    [InlineData("version", 100)]
    [InlineData("company", 200)]
    [InlineData("website", 2048)]
    [InlineData("source", 2048)]
    [InlineData("license", 100)]
    [InlineData("description", 4000)]
    public void Update_UsesCreateLengthAndControlCharacterRules(string field, int maximum)
    {
        var prefix = field switch
        {
            "version" => "1.2.3+",
            "website" or "source" => "https://garage.example.com/",
            _ => ""
        };
        var value = prefix + new string('a', maximum - prefix.Length);
        Assert.Empty(ApplicationValidation.Validate(ApplicationValidation.Normalize(
            ToUpdate(WithField(field, $" {value} ")))));
        Assert.Contains(field, ApplicationValidation.Validate(ToUpdate(WithField(field, value + "a"))));
        Assert.Contains(field, ApplicationValidation.Validate(ToUpdate(WithField(field, "Garage\0Staff"))));
    }

    [Theory]
    [InlineData("version", "v1.2.3")]
    [InlineData("version", "1.2")]
    [InlineData("version", "01.2.3")]
    [InlineData("version", "1.0.0-01")]
    [InlineData("website", "ftp://garage.example.com")]
    [InlineData("website", "https://user:password@garage.example.com")]
    [InlineData("source", "/garage")]
    [InlineData("source", "https://")]
    public void Update_RejectsInvalidVersionsAndUrls(string field, string value)
        => Assert.Contains(field, ApplicationValidation.Validate(ToUpdate(WithField(field, value))));

    private static UpdateApplicationRequest ToUpdate(CreateApplicationRequest request) => new()
    {
        Title = request.Title, Version = request.Version, Company = request.Company,
        Website = request.Website, Source = request.Source, License = request.License,
        Description = request.Description
    };

    private static CreateApplicationRequest WithField(string field, string? value)
    {
        var request = new CreateApplicationRequest { Title = "Portal" };
        return field switch
        {
            "title" => request with { Title = value },
            "version" => request with { Version = value },
            "company" => request with { Company = value },
            "website" => request with { Website = value },
            "source" => request with { Source = value },
            "license" => request with { License = value },
            "description" => request with { Description = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown test field.")
        };
    }
}
