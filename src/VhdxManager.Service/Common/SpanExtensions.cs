namespace VhdxManager.Service;

public static class SpanExtensions
{
	public static ReadOnlySpan<char> SliceAtNull(this ReadOnlySpan<char> span)
	{
		var nullIndex = span.IndexOf('\0');
		return nullIndex >= 0 ? span[..nullIndex] : span;
	}
}
