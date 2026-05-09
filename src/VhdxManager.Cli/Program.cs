using VhdxManager.Cli;

return await CommandFactory.CreateRootCommand().Parse(args).InvokeAsync();
