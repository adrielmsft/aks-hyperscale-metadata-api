using System.Text.RegularExpressions;
using MetadataApi.Contracts;

namespace MetadataApi.Validation;

public static partial class ApplicationValidation
{
    public static CreateApplicationRequest Normalize(CreateApplicationRequest request) => request with
    {
        Title = Trim(request.Title),
        Version = Trim(request.Version),
        Company = Trim(request.Company),
        Website = Trim(request.Website),
        Source = Trim(request.Source),
        License = Trim(request.License),
        Description = Trim(request.Description)
    };

    public static Dictionary<string, string[]> Validate(CreateApplicationRequest request)
    {
        var errors = new Dictionary<string, List<string>>();
        ValidateText("title", request.Title, 200, required: true);
        ValidateText("version", request.Version, 100);
        ValidateText("company", request.Company, 200);
        ValidateText("website", request.Website, 2048);
        ValidateText("source", request.Source, 2048);
        ValidateText("license", request.License, 100);
        ValidateText("description", request.Description, 4000, multiline: true);

        if (request.Version is { Length: > 0 and <= 100 } version && !SemanticVersion().IsMatch(version))
        {
            AddError("version", "Version must follow Semantic Versioning 2.0.0, for example 1.2.0.");
        }

        ValidateUrl("website", request.Website);
        ValidateUrl("source", request.Source);
        return errors.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());

        void ValidateText(string field, string? value, int maximum, bool required = false, bool multiline = false)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required)
                {
                    AddError(field, "This field is required.");
                }

                return;
            }

            if (value.Length > maximum)
            {
                AddError(field, $"This field must not exceed {maximum} characters.");
            }

            if (value.Any(character => char.IsControl(character)
                && !(multiline && character is '\r' or '\n' or '\t')))
            {
                AddError(field, "This field contains unsupported control characters.");
            }
        }

        void ValidateUrl(string field, string? value)
        {
            if (value is null)
            {
                return;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                AddError(field, "This field must be an absolute HTTP(S) URL with a host and no embedded credentials.");
            }
        }

        void AddError(string field, string message)
        {
            if (!errors.TryGetValue(field, out var messages))
            {
                messages = [];
                errors.Add(field, messages);
            }

            messages.Add(message);
        }
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex SemanticVersion();
}
