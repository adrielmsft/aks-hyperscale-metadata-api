using System.ComponentModel.DataAnnotations;
using MetadataApi.Contracts;

namespace MetadataApi.Validation;

public static class MaintainerValidation
{
    public static CreateMaintainerRequest Normalize(CreateMaintainerRequest request) => request with
    {
        Name = request.Name?.Trim(),
        Email = request.Email?.Trim()
    };

    public static UpdateMaintainerRequest Normalize(UpdateMaintainerRequest request) => request with
    {
        Name = request.Name?.Trim(),
        Email = request.Email?.Trim()
    };

    public static Dictionary<string, string[]> Validate(string? name, string? email)
    {
        var errors = new Dictionary<string, List<string>>();
        ValidateText("name", name, 200);
        ValidateText("email", email, 254);

        if (!string.IsNullOrWhiteSpace(email)
            && (!new EmailAddressAttribute().IsValid(email) || email.Contains('<') || email.Contains('>')))
        {
            AddError("email", "Email must be a valid email address without a display name.");
        }

        return errors.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());

        void ValidateText(string field, string? value, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AddError(field, "This field is required.");
                return;
            }

            if (value.Length > maximum)
            {
                AddError(field, $"This field must not exceed {maximum} characters.");
            }

            if (value.Any(char.IsControl))
            {
                AddError(field, "This field contains unsupported control characters.");
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
}
