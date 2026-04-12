using VhdxCow.Cli;

return await CommandFactory.CreateRootCommand().Parse(args).InvokeAsync();
