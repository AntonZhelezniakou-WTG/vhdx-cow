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
sealed class ProgressRenderer : IDisposable
{
	// Reuse Spectre.Console's canonical Dots spinner frames + interval rather than
	// hard-coding our own braille array. The animation is identical — we just stop
	// owning a constant we shouldn't own.
	static readonly IReadOnlyList<string> SpinnerFrames = Spinner.Known.Dots.Frames;
	static readonly int FrameIntervalMs = (int)Spinner.Known.Dots.Interval.TotalMilliseconds;

	// CSI "Erase in Line" — `ESC [ 2 K` clears the entire current line; `\r`
	// returns the cursor to column 0. The ESC byte (\x1b) is essential — without
	// it the terminal would just print the literal characters "[2K".
	const string ClearLine = "\x1b[2K\r";
	// CSI cursor-visibility toggles — DECTCEM. We hide the cursor while spinning
	// so it doesn't blink at the next-write position behind the glyph.
	const string HideCursor = "\x1b[?25l";
	const string ShowCursor = "\x1b[?25h";

	readonly bool isInteractive = !Console.IsOutputRedirected;
	readonly Lock @lock = new();

	Timer? spinnerTimer;
	string? currentStep;
	string currentDetail = "";
	int frameIndex;
	bool cursorHidden;

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
			HideCursorIfNeededLocked();
			DrawFrameLocked();

			spinnerTimer = new Timer(_ =>
			{
				lock (@lock)
				{
					if (currentStep is null) return; // already stopped
					frameIndex = (frameIndex + 1) % SpinnerFrames.Count;
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
			RestoreCursorIfNeededLocked();
		}
		toDispose?.Dispose();
	}

	void DrawFrameLocked()
	{
		if (currentStep is null) return;
		Console.Write(ClearLine);
		// Bright green glyph + default-colour label so the spinner glyph pops; the
		// detail piece carries its own grey style via FormatDetailMarkup.
		AnsiConsole.Markup(
			$"[green]{SpinnerFrames[frameIndex]}[/] {Markup.Escape(currentStep)}{FormatDetailMarkup(currentDetail)}");
	}

	void HideCursorIfNeededLocked()
	{
		if (!isInteractive || cursorHidden)
			return;
		Console.Write(HideCursor);
		cursorHidden = true;
	}

	void RestoreCursorIfNeededLocked()
	{
		if (!cursorHidden) return;
		Console.Write(ShowCursor);
		cursorHidden = false;
	}

	static string FormatDetailMarkup(string detail)
		=> string.IsNullOrEmpty(detail) ? "" : $" [grey]({Markup.Escape(detail)})[/]";

	public void Dispose() => StopSpinner();
}
