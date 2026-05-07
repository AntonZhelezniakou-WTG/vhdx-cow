using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;
using VhdxCow.Contracts;
using VhdxCow.Service.Diagnostics;
using VhdxCow.Service.Security;
using VhdxCow.Service.Services;
using VhdxCow.Service.State;
using VhdxCow.Service.VhdxOperations;

Log.Logger = new LoggerConfiguration()
		.WriteTo.Console()
		.CreateBootstrapLogger();

try
{
	Log.Information("Starting VhdxCow Service");

	var builder = WebApplication.CreateBuilder(args);

	builder.Services.AddWindowsService(options =>
	{
		options.ServiceName = ServiceConstants.ServiceName;
	});

	builder.Host.UseSerilog((context, config) =>
	{
		config.ReadFrom.Configuration(context.Configuration);

		if (Environment.UserInteractive)
		{
			config.WriteTo.Console();
		}
	});

	var pipeName = builder.Configuration.GetValue<string>("VhdxCow:PipeName") ?? ServiceConstants.PipeName;

	builder.WebHost.ConfigureKestrel(kestrel =>
	{
		kestrel.ListenNamedPipe(pipeName, listenOptions =>
		{
			listenOptions.Protocols = HttpProtocols.Http2;
		});
	});

	builder.WebHost.UseNamedPipes(options =>
	{
		// Custom PipeSecurity is incompatible with the default CurrentUserOnly flag.
		options.CurrentUserOnly = false;

		var pipeSecurity = new PipeSecurity();
		pipeSecurity.AddAccessRule(new PipeAccessRule(
			new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
			PipeAccessRights.ReadWrite,
			AccessControlType.Allow));
		options.PipeSecurity = pipeSecurity;
	});

	builder.Services.AddGrpc();
	builder.Services.AddSingleton<IVhdxManager, VhdxManager>();
	builder.Services.AddSingleton<IVolumeManager, VolumeManager>();
	builder.Services.AddSingleton<IStateStore, JsonStateStore>();
	builder.Services.AddSingleton<PathValidator>();

	var app = builder.Build();

	app.MapGrpcService<VhdxGrpcService>();

	app.Run();
}
catch (Exception ex)
{
	// Write a self-contained fatal report (file + Event Log) BEFORE touching Serilog —
	// host startup may have failed before Serilog sinks were ready.
	var reportPath = FatalDiagnostics.Report(ex);

	Log.Fatal(ex, "VhdxCow Service terminated unexpectedly. Fatal report: {ReportPath}", reportPath ?? "(write failed)");
}
finally
{
	Log.CloseAndFlush();
}
