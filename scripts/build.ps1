$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\risk\Vybe.App.csproj'
$output = Join-Path $PSScriptRoot '..\artifacts\publish'
$project = [System.IO.Path]::GetFullPath($project)
$output = [System.IO.Path]::GetFullPath($output)
dotnet restore $project
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true -o $output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Published <3 to $output"

