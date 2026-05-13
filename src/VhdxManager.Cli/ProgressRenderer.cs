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

	// In-place redraw without flicker:
	//   `\r`           — move the cursor back to column 0 (does NOT erase anything;
	//                    the previous frame stays on screen until we overwrite it)
	//   {new content}  — overwrites the existing frame character-by-character
	//   `\x1b[K`       — CSI "Erase in Line, to end" — clears any tail left over
	//                    when a longer previous frame is replaced by a shorter one
	// Critically, we do NOT use `\x1b[2K` (erase entire line) BEFORE writing —
	// that leaves a visible blank between erase and write each frame, which is
	// exactly the flicker the user reported.
	const string CarriageReturn = "\r";
	const string ClearToEnd = "\x1b[K";
	// Final commit (✓/✗ line): erase the whole spinner line so AnsiConsole.MarkupLine
	// starts on a clean slate and Spectre's own newline can print the permanent line.
	const string EraseLineAndReturn = "\x1b[2K\r";
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
		if (!isInteractive)
		{
			return;
		}

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
					if (currentStep is null)
					{
						return; // already stopped
					}
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
				// Now we DO want a full erase — the permanent ✓/✗ line is about to
				// be printed by AnsiConsole.MarkupLine and we don't want any tail
				// of the spinner frame to bleed through underneath it.
				Console.Write(EraseLineAndReturn);
			}
			currentStep = null;
			currentDetail = "";
			RestoreCursorIfNeededLocked();
		}
		toDispose?.Dispose();
	}

	void DrawFrameLocked()
	{
		if (currentStep is null)
		{
			return;
		}
		// In-place redraw: cursor to col 0, paint the new frame, clear any tail
		// from a previous longer frame. No intermediate "blank line" state, so
		// no flicker.
		Console.Write(CarriageReturn);
		AnsiConsole.Markup(
			$"[green]{SpinnerFrames[frameIndex]}[/] {Markup.Escape(currentStep)}{FormatDetailMarkup(currentDetail)}");
		Console.Write(ClearToEnd);
	}

	void HideCursorIfNeededLocked()
	{
		if (!isInteractive || cursorHidden)
		{
			return;
		}
		Console.Write(HideCursor);
		cursorHidden = true;
	}

	void RestoreCursorIfNeededLocked()
	{
		if (!cursorHidden)
		{
			return;
		}
		Console.Write(ShowCursor);
		cursorHidden = false;
	}

	static string FormatDetailMarkup(string detail)
		=> string.IsNullOrEmpty(detail) ? "" : $" [grey]({Markup.Escape(detail)})[/]";

	public void Dispose() => StopSpinner();
}
