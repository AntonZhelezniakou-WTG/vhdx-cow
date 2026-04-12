using System.IO.Pipes;
using System.Security.Principal;
using Grpc.Net.Client;
using VhdxCow.Contracts;

namespace VhdxCow.Client;

public static class NamedPipeChannelFactory
{
	public static GrpcChannel Create(string pipeName = ServiceConstants.PipeName)
	{
		var connectionFactory = new NamedPipeConnectionFactory(pipeName);
		var socketsHttpHandler = new SocketsHttpHandler
		{
			ConnectCallback = connectionFactory.ConnectAsync,
		};

		return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
		{
			HttpHandler = socketsHttpHandler,
		});
	}

	sealed class NamedPipeConnectionFactory(string pipeName)
	{
		public async ValueTask<Stream> ConnectAsync(
			SocketsHttpConnectionContext context,
			CancellationToken cancellationToken)
		{
			var clientStream = new NamedPipeClientStream(
				serverName: ".",
				pipeName: pipeName,
				direction: PipeDirection.InOut,
				options: PipeOptions.WriteThrough | PipeOptions.Asynchronous,
				impersonationLevel: TokenImpersonationLevel.Anonymous);

			try
			{
				await clientStream.ConnectAsync(cancellationToken);
				return clientStream;
			}
			catch
			{
				await clientStream.DisposeAsync();
				throw;
			}
		}
	}
}
