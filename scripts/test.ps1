$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet test MediaArchive.sln --configuration Debug --no-build -m:1 --disable-build-servers
exit $LASTEXITCODE
