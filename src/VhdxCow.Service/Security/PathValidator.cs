namespace VhdxCow.Service.Security;

/// <summary>
/// Validates that requested paths are within configured allow-lists.
/// Prevents path traversal attacks and restricts operations to approved directories.
/// </summary>
public sealed class PathValidator(IConfiguration configuration, ILogger<PathValidator> logger)
{
	readonly string[] allowedParentPaths
		= ExpandEnvVars(configuration.GetSection("VhdxCow:AllowedParentPaths").Get<string[]>());

	readonly string[] allowedMountBasePaths
		= ExpandEnvVars(configuration.GetSection("VhdxCow:AllowedMountBasePaths").Get<string[]>());

	readonly string[] allowedChildBasePaths
		= ExpandEnvVars(configuration.GetSection("VhdxCow:AllowedChildBasePaths").Get<string[]>());

	readonly string[] allowedConvertSourcePaths
		= ExpandEnvVars(configuration.GetSection("VhdxCow:AllowedConvertSourcePaths").Get<string[]>());

	static string[] ExpandEnvVars(string[]? values)
		=> values is null
			? []
			: [.. values.Select(Environment.ExpandEnvironmentVariables)];

	public bool ValidateParentPath(string path, out string error)
		=> ValidateAgainstAllowList(path, allowedParentPaths, "parent VHDX", out error);

	public bool ValidateMountPath(string path, out string error)
		=> ValidateAgainstAllowList(path, allowedMountBasePaths, "mount", out error);

	public bool ValidateChildPath(string path, out string error)
		=> ValidateAgainstAllowList(path, allowedChildBasePaths, "child VHDX", out error);

	public bool ValidateConvertSourcePath(string path, out string error)
		=> ValidateAgainstAllowList(path, allowedConvertSourcePaths, "convert source", out error);

	bool ValidateAgainstAllowList(string path, string[] allowedPaths, string pathType, out string error)
	{
		error = string.Empty;

		if (string.IsNullOrWhiteSpace(path))
		{
			error = $"The {pathType} path is empty";
			return false;
		}

		string fullPath;
		try
		{
			fullPath = Path.GetFullPath(path);
		}
		catch (Exception ex)
		{
			error = $"Invalid {pathType} path: {ex.Message}";
			return false;
		}

		if (fullPath != path && fullPath.Contains("..", StringComparison.Ordinal))
		{
			error = $"Path traversal detected in {pathType} path";
			logger.LogWarning("Path traversal attempt: {RequestedPath} resolved to {FullPath}", path, fullPath);
			return false;
		}

		if (allowedPaths.Length == 0)
		{
			logger.LogWarning("No allowed {PathType} paths configured — all paths are rejected", pathType);
			error = $"No allowed {pathType} paths configured";
			return false;
		}

		if (allowedPaths.Select(Path.GetFullPath).Any(allowedFull => fullPath.StartsWith(allowedFull, StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		error = $"The {pathType} path is not within any allowed directory";
		logger.LogWarning(
			"Path {Path} not in allowed {PathType} directories: [{AllowedDirs}]",
				fullPath, // Path
				pathType, // PathType
				string.Join(", ", allowedPaths)); // AllowedDirs
		return false;
	}
}
