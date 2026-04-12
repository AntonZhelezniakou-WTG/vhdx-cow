namespace VhdxCow.Client.Tests;

[TestFixture]
public class VhdxCowServiceExceptionTests
{
	[Test]
	public void Constructor_SetsMessage()
	{
		var ex = new VhdxCowServiceException("service down");

		ex.Message.Should().Be("service down");
		ex.InnerException.Should().BeNull();
	}

	[Test]
	public void Constructor_SetsInnerException()
	{
		var inner = new InvalidOperationException("pipe broken");
		var ex = new VhdxCowServiceException("service down", inner);

		ex.Message.Should().Be("service down");
		ex.InnerException.Should().BeSameAs(inner);
	}

	[Test]
	public void Constructor_CreatesClientWithoutThrowing()
	{
		// VhdxCowClient constructor should not throw — it only creates the channel lazily
		using var client = new VhdxCowClient("TestPipe");
		client.Should().NotBeNull();
	}

	[Test]
	public void Constructor_AcceptsTimeout()
	{
		using var client = new VhdxCowClient("TestPipe", TimeSpan.FromSeconds(5));
		client.Should().NotBeNull();
	}
}
