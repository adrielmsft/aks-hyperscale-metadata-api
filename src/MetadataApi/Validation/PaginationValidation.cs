namespace MetadataApi.Validation;

public static class PaginationValidation
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;
    public const int MaxOffset = 10_000;

    public static Dictionary<string, string[]> Validate(int limit, int offset)
    {
        var errors = new Dictionary<string, string[]>();
        if (limit < 1 || limit > MaxLimit)
        {
            errors["limit"] = ["Limit must be between 1 and 100."];
        }

        if (offset < 0 || offset > MaxOffset)
        {
            errors["offset"] = ["Offset must be between 0 and 10000."];
        }

        return errors;
    }

    public static int? GetNextOffset(int limit, int offset, bool hasMore)
        => hasMore && (long)offset + limit <= MaxOffset ? offset + limit : null;
}
