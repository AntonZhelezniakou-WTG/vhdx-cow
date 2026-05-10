using Grpc.Core;
using VhdxManager.Contracts;

namespace VhdxManager.Service.Services;

/// <summary>
/// Convenience helper for streaming RPC handlers: emits a STARTED event before
/// each step, COMPLETED on success or FAILED on exception, and supports a
/// fluent <see cref="StepAsync(string, Func{Task}, string)"/> wrapper.
/// </summary>
/// <remarks>
/// Each <typeparamref name="TStream"/> is the per-RPC oneof wrapper from the
/// proto (e.g. <c>CreateVhdxStream</c>). Subclasses provide the projection
/// from <see cref="ProgressEvent"/> to a fully-formed stream message because
/// proto-gen oneof setters are strongly typed and don't share a base type.
/// </remarks>
public sealed class ProgressReporter<TStream>(
	IServerStreamWriter<TStream> writer,
	Func<ProgressEvent, TStream> wrapProgress)
{
	public async Task StartedAsync(string step, string detail = "", CancellationToken ct = default)
		=> await writer.WriteAsync(wrapProgress(new ProgressEvent
		{
			Step = step,
			Phase = ProgressPhase.Started,
			Detail = detail,
		}), ct);

	public async Task CompletedAsync(string step, string detail = "", CancellationToken ct = default)
		=> await writer.WriteAsync(wrapProgress(new ProgressEvent
		{
			Step = step,
			Phase = ProgressPhase.Completed,
			Detail = detail,
		}), ct);

	public async Task FailedAsync(string step, string detail, CancellationToken ct = default)
		=> await writer.WriteAsync(wrapProgress(new ProgressEvent
		{
			Step = step,
			Phase = ProgressPhase.Failed,
			Detail = detail,
		}), ct);

	/// <summary>
	/// Wraps a single step: emits STARTED, runs the work, emits COMPLETED.
	/// On exception emits FAILED and rethrows so the handler can produce a
	/// final-result message with the error.
	/// </summary>
	public async Task StepAsync(string step, Func<Task> work, string startDetail = "", CancellationToken ct = default)
	{
		await StartedAsync(step, startDetail, ct);
		try
		{
			await work();
		}
		catch (Exception ex)
		{
			await FailedAsync(step, ex.Message, ct);
			throw;
		}
		await CompletedAsync(step, ct: ct);
	}

	/// <summary>
	/// Step wrapper that returns a value from the work delegate.
	/// </summary>
	public async Task<T> StepAsync<T>(string step, Func<Task<T>> work, string startDetail = "", CancellationToken ct = default)
	{
		await StartedAsync(step, startDetail, ct);
		T result;
		try
		{
			result = await work();
		}
		catch (Exception ex)
		{
			await FailedAsync(step, ex.Message, ct);
			throw;
		}
		await CompletedAsync(step, ct: ct);
		return result;
	}
}
