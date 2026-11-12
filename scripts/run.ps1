$ErrorActionPreference = 'Stop'
$CliArguments = @($args)
Set-Location (Join-Path $PSScriptRoot '..')

dotnet src\MediaArchive.CLI\bin\Debug\net10.0\MediaArchive.CLI.dll @CliArguments
exit $LASTEXITCODE
