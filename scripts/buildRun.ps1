$ErrorActionPreference = 'Stop'
$CliArguments = @($args)
Set-Location (Join-Path $PSScriptRoot '..')

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project src\MediaArchive.CLI\MediaArchive.CLI.csproj --configuration Debug --no-build -- @CliArguments
exit $LASTEXITCODE
