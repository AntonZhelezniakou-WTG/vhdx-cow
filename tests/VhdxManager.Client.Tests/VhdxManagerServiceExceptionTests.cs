namespace VhdxManager.Client.Tests;

[TestFixture]
public class VhdxManagerServiceExceptionTests
{
	[Test]
	public void Constructor_SetsMessage()
	{
		var ex = new VhdxManagerServiceException("service down");

		ex.Message.Should().Be("service down");
		ex.InnerException.Should().BeNull();
	}

	[Test]
	public void Constructor_SetsInnerException()
	{
		var inner = new InvalidOperationException("pipe broken");
		var ex = new VhdxManagerServiceException("service down", inner);

		ex.Message.Should().Be("service down");
		ex.InnerException.Should().BeSameAs(inner);
	}

	[Test]
	public void Constructor_CreatesClientWithoutThrowing()
	{
		// VhdxManagerClient constructor should not throw — it only creates the channel lazily
		using var client = new VhdxManagerClient("TestPipe");
		client.Should().NotBeNull();
	}

	[Test]
	public void Constructor_AcceptsTimeout()
	{
		using var client = new VhdxManagerClient("TestPipe", TimeSpan.FromSeconds(5));
		client.Should().NotBeNull();
	}
}
