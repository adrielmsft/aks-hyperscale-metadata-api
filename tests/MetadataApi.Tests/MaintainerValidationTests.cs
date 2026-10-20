using System.ComponentModel.DataAnnotations;
using MetadataApi.Contracts;
using MetadataApi.Validation;

namespace MetadataApi.Tests;

public sealed class MaintainerValidationTests
{
    [Fact]
    public void Normalize_TrimsBothRequestsAndPreservesInternalTextAndCasing()
    {
        const string name = "  Zoë  O'Connor-李  ";
        const string email = " Garage.Team@Example.COM ";
        var create = MaintainerValidation.Normalize(new CreateMaintainerRequest { Name = name, Email = email });
        var update = MaintainerValidation.Normalize(new UpdateMaintainerRequest { Name = name, Email = email });

        Assert.Equal("Zoë  O'Connor-李", create.Name);
        Assert.Equal("Garage.Team@Example.COM", create.Email);
        Assert.Equal(create.Name, update.Name);
        Assert.Equal(create.Email, update.Email);
        Assert.Empty(MaintainerValidation.Validate(create.Name, create.Email));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n ")]
    public void MissingRequiredFields_ReportBothErrors(string? value)
    {
        var request = MaintainerValidation.Normalize(new CreateMaintainerRequest { Name = value, Email = value });
        var errors = MaintainerValidation.Validate(request.Name, request.Email);

        Assert.Equal(2, errors.Count);
        Assert.Contains("name", errors.Keys);
        Assert.Contains("email", errors.Keys);
    }

    [Theory]
    [InlineData("李")]
    [InlineData("Zoë O'Connor-Santos")]
    [InlineData("Garage 👩🏽‍🔬 Team")]
    [InlineData("مريم أحمد")]
    public void UnicodeNames_AreAccepted(string name)
        => Assert.Empty(MaintainerValidation.Validate(name, "garage@example.com"));

    [Fact]
    public void LengthLimits_AreMeasuredAfterTrimmingInUtf16CodeUnits()
    {
        var email = new string('a', 64) + "@" + new string('b', 185) + ".com";
        Assert.Equal(254, email.Length);
        var request = MaintainerValidation.Normalize(new CreateMaintainerRequest
        {
            Name = " " + string.Concat(Enumerable.Repeat("😀", 100)) + " ",
            Email = " " + email + " "
        });
        Assert.Equal(200, request.Name!.Length);
        Assert.Empty(MaintainerValidation.Validate(request.Name, request.Email));

        var errors = MaintainerValidation.Validate(request.Name + "x", "a" + email);
        Assert.Equal(2, errors.Count);
        Assert.Contains("name", errors.Keys);
        Assert.Contains("email", errors.Keys);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("garage@")]
    [InlineData("garage@@example.com")]
    [InlineData("Garage Team <garage@example.com>")]
    [InlineData("<garage@example.com>")]
    [InlineData("garage@example.com\ninjected")]
    public void InvalidEmail_IsRejected(string email)
        => Assert.Contains("email", MaintainerValidation.Validate("Garage Team", email).Keys);

    [Theory]
    [InlineData("Garage.Team+Lab@Example.COM")]
    [InlineData("garage@localhost")]
    [InlineData("团队@example.com")]
    public void BasicSyntax_UsesEmailAddressAttributeWithoutDnsChecks(string email)
    {
        Assert.True(new EmailAddressAttribute().IsValid(email));
        Assert.Empty(MaintainerValidation.Validate("Garage Team", email));
    }

    [Theory]
    [InlineData("Garage\nTeam")]
    [InlineData("Garage\tTeam")]
    [InlineData("Garage\rTeam")]
    [InlineData("Garage\u0000Team")]
    [InlineData("Garage\u007fTeam")]
    public void SingleLineNames_RejectControls(string name)
        => Assert.Contains("name", MaintainerValidation.Validate(name, "garage@example.com").Keys);
}
