namespace MetadataApi.Persistence.Entities;

public sealed class ApplicationMaintainer
{
    public Guid ApplicationId { get; set; }
    public Guid MaintainerId { get; set; }
}
