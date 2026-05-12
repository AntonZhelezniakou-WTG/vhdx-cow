using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VhdxManager.Service.Native;

/// <summary>
/// A Windows Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>:
/// any child process started via <see cref="Start"/> is guaranteed to die when this
/// instance is disposed, when the service process exits (the kernel closes outstanding
/// job-object handles on termination, which fires KILL_ON_JOB_CLOSE), or on explicit
/// <see cref="TerminateAll"/>.
///
/// <para>
/// Use as a per-operation scope:
/// <code>
///     using var group = new ProcessGroup();
///     using var process = group.Start(new ProcessStartInfo { ... });
///     // ... wait, read output, etc.
/// </code>
/// </para>
///
/// <para>
/// Approach borrowed from WiseTechGlobal/Personal/ProcessGroup
/// (<c>WindowsJobObject.cs</c>) — simplified to a single Windows-only class
/// since this service does not run on Unix.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessGroup : IDisposable
{
	readonly nint jobHandle;
	bool disposed;

	public ProcessGroup()
	{
		jobHandle = CreateJobObjectW(nint.Zero, null);
		if (jobHandle == nint.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
		}

		var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
		{
			BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
			{
				LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
			},
		};

		var len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
		if (!SetInformationJobObject(jobHandle, JobObjectInfoClass.ExtendedLimitInformation, ref info, len))
		{
			var err = Marshal.GetLastWin32Error();
			CloseHandle(jobHandle);
			throw new Win32Exception(err, "SetInformationJobObject(KILL_ON_JOB_CLOSE) failed.");
		}
	}

	/// <summary>
	/// Starts a process via <c>Process.Start</c> and assigns it to the job.
	/// If the assignment fails, the process is killed before the exception propagates,
	/// to avoid leaking an orphan that escaped the job.
	/// </summary>
	public Process Start(ProcessStartInfo startInfo)
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		ArgumentNullException.ThrowIfNull(startInfo);

		var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Process.Start returned null.");
		try
		{
			return AssignProcessToJobObject(jobHandle, process.Handle)
				? process
				: throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
		}
		catch
		{
			try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
			process.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Terminates every process currently assigned to this job. Synchronous —
	/// returns once the kernel has scheduled the kills.
	/// </summary>
	public void TerminateAll()
	{
		ObjectDisposedException.ThrowIf(disposed, this);
		_ = TerminateJobObject(jobHandle, uExitCode: 1);
	}

	public void Dispose()
	{
		if (disposed) return;
		disposed = true;
		if (jobHandle != nint.Zero)
		{
			// Closing the last handle to a KILL_ON_JOB_CLOSE job kills every
			// remaining child synchronously inside the kernel.
			_ = CloseHandle(jobHandle);
		}
	}

	// ---------- P/Invoke ----------

	const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

	enum JobObjectInfoClass
	{
		ExtendedLimitInformation = 9,
	}

	[StructLayout(LayoutKind.Sequential)]
	struct JOBOBJECT_BASIC_LIMIT_INFORMATION
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public nuint MinimumWorkingSetSize;
		public nuint MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public nint Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct IO_COUNTERS
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
		public IO_COUNTERS IoInfo;
		public nuint ProcessMemoryLimit;
		public nuint JobMemoryLimit;
		public nuint PeakProcessMemoryUsed;
		public nuint PeakJobMemoryUsed;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool SetInformationJobObject(
		nint hJob,
		JobObjectInfoClass infoClass,
		ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info,
		int cbInfoLength);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool TerminateJobObject(nint hJob, uint uExitCode);

	[DllImport("kernel32.dll", SetLastError = true)]
	static extern bool CloseHandle(nint hObject);
}
