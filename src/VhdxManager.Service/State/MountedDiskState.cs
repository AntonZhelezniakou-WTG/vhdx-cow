namespace VhdxManager.Service.State;

public sealed record MountedDiskState
{
	public required string ChildVhdxPath { get; init; }

	public required string ParentVhdxPath { get; init; }

	public required string MountPath { get; init; }

	public required string VolumeGuidPath { get; init; }

	public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
