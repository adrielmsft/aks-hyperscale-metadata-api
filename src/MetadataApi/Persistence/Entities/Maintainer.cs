namespace MetadataApi.Persistence.Entities;

public sealed class Maintainer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Email { get; set; }
}
