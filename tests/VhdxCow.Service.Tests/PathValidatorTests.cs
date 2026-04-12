using VhdxCow.Service.Security;

namespace VhdxCow.Service.Tests;

[TestFixture]
public class PathValidatorTests
{
	static PathValidator CreateValidator(
		string[]? parentPaths = null,
		string[]? mountPaths = null,
		string[]? childPaths = null)
	{
		var config = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["VhdxCow:AllowedParentPaths:0"] = parentPaths?.ElementAtOrDefault(0) ?? @"C:\VhdxDisks\Parents",
				["VhdxCow:AllowedMountBasePaths:0"] = mountPaths?.ElementAtOrDefault(0) ?? @"C:\Mounts",
				["VhdxCow:AllowedChildBasePaths:0"] = childPaths?.ElementAtOrDefault(0) ?? @"C:\VhdxDisks\Children",
			})
			.Build();

		return new PathValidator(config, NullLogger<PathValidator>.Instance);
	}

	// --- ValidateParentPath ---

	[Test]
	public void ValidateParentPath_WithinAllowedDirectory_ReturnsTrue()
	{
		var validator = CreateValidator();

		var result = validator.ValidateParentPath(@"C:\VhdxDisks\Parents\main.vhdx", out var error);

		result.Should().BeTrue();
		error.Should().BeEmpty();
	}

	[Test]
	public void ValidateParentPath_OutsideAllowedDirectory_ReturnsFalse()
	{
		var validator = CreateValidator();

		var result = validator.ValidateParentPath(@"C:\Other\sneaky.vhdx", out var error);

		result.Should().BeFalse();
		error.Should().Contain("not within any allowed directory");
	}

	[Test]
	public void ValidateParentPath_EmptyPath_ReturnsFalse()
	{
		var validator = CreateValidator();

		var result = validator.ValidateParentPath("", out var error);

		result.Should().BeFalse();
		error.Should().Contain("empty");
	}

	[Test]
	public void ValidateParentPath_NoAllowedPaths_ReturnsFalse()
	{
		var config = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>())
			.Build();
		var validator = new PathValidator(config, NullLogger<PathValidator>.Instance);

		var result = validator.ValidateParentPath(@"C:\Any\path.vhdx", out var error);

		result.Should().BeFalse();
		error.Should().Contain("No allowed");
	}

	// --- ValidateChildPath ---

	[Test]
	public void ValidateChildPath_WithinAllowedDirectory_ReturnsTrue()
	{
		var validator = CreateValidator();

		var result = validator.ValidateChildPath(@"C:\VhdxDisks\Children\wt1.vhdx", out var error);

		result.Should().BeTrue();
		error.Should().BeEmpty();
	}

	[Test]
	public void ValidateChildPath_OutsideAllowedDirectory_ReturnsFalse()
	{
		var validator = CreateValidator();

		var result = validator.ValidateChildPath(@"C:\VhdxDisks\Parents\child.vhdx", out var error);

		result.Should().BeFalse();
		error.Should().Contain("not within any allowed directory");
	}

	[Test]
	public void ValidateChildPath_EmptyPath_ReturnsFalse()
	{
		var validator = CreateValidator();

		validator.ValidateChildPath("", out var error).Should().BeFalse();
		error.Should().Contain("empty");
	}

	// --- ValidateMountPath ---

	[Test]
	public void ValidateMountPath_WithinAllowedDirectory_ReturnsTrue()
	{
		var validator = CreateValidator();

		var result = validator.ValidateMountPath(@"C:\Mounts\worktree1\bin", out var error);

		result.Should().BeTrue();
		error.Should().BeEmpty();
	}

	[Test]
	public void ValidateMountPath_OutsideAllowedDirectory_ReturnsFalse()
	{
		var validator = CreateValidator();

		var result = validator.ValidateMountPath(@"C:\Windows\System32", out var error);

		result.Should().BeFalse();
		error.Should().Contain("not within any allowed directory");
	}
}
