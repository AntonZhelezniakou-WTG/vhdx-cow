using Spectre.Console;
using VhdxManager.Contracts;

namespace VhdxManager.Cli;

/// <summary>
/// Renders <see cref="ProgressEvent"/>s as live single-line status with a spinner
/// animated by an internal timer between STARTED and COMPLETED/FAILED.
///
/// <para>
/// Layout per step:
/// <code>
///   ⠋ {step} ({detail})    (animated spinner while running, in-place)
///   ✓ {step} ({detail})    (committed once COMPLETED arrives)
///   ✗ {step} — {detail}    (committed on FAILED, with the error)
/// </code>
/// </para>
///
/// <para>
/// In a non-interactive console (output redirected to a file/pipe) the spinner is
/// suppressed and only the COMPLETED/FAILED lines are printed.
/// </para>
/// </summary>
internal sealed class ProgressRenderer : IDisposable
{
	static readonly char[] SpinnerFrames =
		['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
	const int FrameIntervalMs = 80;

	// ANSI escape: ESC + "[2K" erases the current line; "\r" returns cursor to col 0.
	const string ClearLine = "[2K\r";

	readonly bool isInteractive;
	readonly Lock @lock = new();

	Timer? spinnerTimer;
	string? currentStep;
	string currentDetail = "";
	int frameIndex;

	public ProgressRenderer()
	{
		isInteractive = !Console.IsOutputRedirected;
	}

	public void Handle(ProgressEvent ev)
	{
		switch (ev.Phase)
		{
			case ProgressPhase.Started:
				StartSpinner(ev.Step, ev.Detail);
				break;

			case ProgressPhase.Completed:
				StopSpinner();
				AnsiConsole.MarkupLine(
					$"[green]✓[/] {Markup.Escape(ev.Step)}{FormatDetailMarkup(ev.Detail)}");
				break;

			case ProgressPhase.Failed:
				StopSpinner();
				var detail = string.IsNullOrEmpty(ev.Detail) ? "" : $" — [red]{Markup.Escape(ev.Detail)}[/]";
				AnsiConsole.MarkupLine($"[red]✗[/] {Markup.Escape(ev.Step)}{detail}");
				break;
		}
	}

	void StartSpinner(string step, string detail)
	{
		if (!isInteractive) return;

		lock (@lock)
		{
			currentStep = step;
			currentDetail = detail;
			frameIndex = 0;
			DrawFrameLocked();

			spinnerTimer = new Timer(_ =>
			{
				lock (@lock)
				{
					if (currentStep is null) return; // already stopped
					frameIndex = (frameIndex + 1) % SpinnerFrames.Length;
					DrawFrameLocked();
				}
			}, state: null, dueTime: FrameIntervalMs, period: FrameIntervalMs);
		}
	}

	void StopSpinner()
	{
		Timer? toDispose;
		lock (@lock)
		{
			toDispose = spinnerTimer;
			spinnerTimer = null;
			if (currentStep is not null && isInteractive)
			{
				Console.Write(ClearLine);
			}
			currentStep = null;
			currentDetail = "";
		}
		toDispose?.Dispose();
	}

	void DrawFrameLocked()
	{
		if (currentStep is null) return;
		Console.Write(ClearLine);
		AnsiConsole.Markup(
			$"[grey]{SpinnerFrames[frameIndex]} {Markup.Escape(currentStep)}{FormatDetailMarkup(currentDetail)}[/]");
	}

	static string FormatDetailMarkup(string detail)
		=> string.IsNullOrEmpty(detail) ? "" : $" [grey]({Markup.Escape(detail)})[/]";

	public void Dispose() => StopSpinner();
}
