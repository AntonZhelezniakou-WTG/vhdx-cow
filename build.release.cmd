dotnet publish src/VhdxManager.Service/VhdxManager.Service.csproj -c Release -r win-x64 --self-contained true -o publish/service

dotnet publish src/VhdxManager.Cli/VhdxManager.Cli.csproj -c Release -r win-x64 --self-contained true -o publish/cli

dotnet build installer/VhdxManager.Installer.wixproj -c Release