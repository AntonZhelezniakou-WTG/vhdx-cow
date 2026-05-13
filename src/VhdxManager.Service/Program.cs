using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;
using VhdxManager.Contracts;
using VhdxManager.Service.Configuration;
using VhdxManager.Service.Diagnostics;
using VhdxManager.Service.Reconciliation;
using VhdxManager.Service.Security;
using VhdxManager.Service.Services;
using VhdxManager.Service.State;
using VhdxManager.Service.VhdxOperations;

Log.Logger = new LoggerConfiguration()
		.WriteTo.Console()
		.CreateBootstrapLogger();

try
{
	Log.Information("Starting VhdxManager Service");

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

	var pipeName = builder.Configuration.GetValue<string>("VhdxManager:PipeName") ?? ServiceConstants.PipeName;

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

		// LocalSystem (the service account) MUST be in the DACL — otherwise
		// CreateNamedPipe returns the handle to us with the requested access mask,
		// the kernel checks SYSTEM against the DACL, finds no allow ACE, and fails
		// with ERROR_ACCESS_DENIED. SYSTEM is not a member of BUILTIN\Users.
		pipeSecurity.AddAccessRule(new PipeAccessRule(
			new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
			PipeAccessRights.FullControl,
			AccessControlType.Allow));

		// Administrators full control — for management/diagnostic clients running elevated.
		pipeSecurity.AddAccessRule(new PipeAccessRule(
			new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
			PipeAccessRights.FullControl,
			AccessControlType.Allow));

		// Standard users get read+write so the CLI/Client can talk to the service.
		pipeSecurity.AddAccessRule(new PipeAccessRule(
			new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
			PipeAccessRights.ReadWrite,
			AccessControlType.Allow));

		options.PipeSecurity = pipeSecurity;
	});

	builder.Services.AddGrpc();
	builder.Services.AddSingleton<IVirtDiskManager, VirtDiskManager>();
	builder.Services.AddSingleton<IVolumeManager, VolumeManager>();
	builder.Services.AddSingleton<IDiskInitializer, Win32DiskInitializer>();
	builder.Services.AddSingleton<Robocopy>();
	builder.Services.AddSingleton<IFolderTransferOrchestrator, FolderTransferOrchestrator>();
	builder.Services.AddSingleton<IStateStore, JsonStateStore>();
	builder.Services.AddSingleton<PathValidator>();
	builder.Services.AddSingleton<IDefenderExclusionManager, DefenderExclusionManager>();
	builder.Services.AddSingleton<IServiceSettingsStore, ServiceSettingsStore>();

	// Re-attach VHDXs and re-mount folders that were active before reboot.
	builder.Services.AddHostedService<MountReconciler>();

	var app = builder.Build();

	app.MapGrpcService<VhdxGrpcService>();

	app.Run();
}
catch (Exception ex)
{
	// Write a self-contained fatal report (file + Event Log) BEFORE touching Serilog —
	// host startup may have failed before Serilog sinks were ready.
	var reportPath = FatalDiagnostics.Report(ex);

	Log.Fatal(ex, "VhdxManager Service terminated unexpectedly. Fatal report: {ReportPath}", reportPath ?? "(write failed)");
}
finally
{
	Log.CloseAndFlush();
}
