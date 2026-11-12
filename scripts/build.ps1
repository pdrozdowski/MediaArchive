$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

dotnet build MediaArchive.sln --configuration Debug -m:1 --disable-build-servers
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
