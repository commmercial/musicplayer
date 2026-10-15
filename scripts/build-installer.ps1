param(
    [string]$OutputDir = [Environment]::GetFolderPath('Desktop')
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
$project = Join-Path $repo 'src\risk\Vybe.App.csproj'
$publish = Join-Path $artifactRoot 'publish'
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$bootstrapper = Join-Path $repo 'installer\desktop\Bootstrapper.cs'
$launcher = Join-Path $repo 'installer\desktop\RiskLauncher.cs'
$setupScript = Join-Path $repo 'installer\desktop\Setup.ps1'
$uninstallScript = Join-Path $repo 'installer\desktop\Uninstall.ps1'
$icon = Join-Path $repo 'src\risk\Assets\app-icon-v2.ico'
$logo = Join-Path $repo 'src\risk\Assets\app-icon.png'
$obfuscatorToolDir = Join-Path $artifactRoot 'obfuscator-tools'
$obfuscatorInstallDir = Join-Path $obfuscatorToolDir 'installed'
$obfuscatorPackage = Join-Path $obfuscatorToolDir 'obfuscar.globaltool.2.2.50.nupkg'
$obfuscatorCommand = Join-Path $obfuscatorInstallDir 'obfuscar.console.exe'

if (-not (Test-Path -LiteralPath $csc)) { throw 'The Windows .NET Framework compiler was not found.' }
New-Item -ItemType Directory -Force -Path $OutputDir,$artifactRoot | Out-Null

dotnet restore $project -r win-x64 -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw "App restore failed with exit code $LASTEXITCODE." }
dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore `
    -p:Platform=x64 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
    -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw "App publish failed with exit code $LASTEXITCODE." }

if (-not (Test-Path -LiteralPath $obfuscatorCommand)) {
    New-Item -ItemType Directory -Force -Path $obfuscatorToolDir | Out-Null
    if (-not (Test-Path -LiteralPath $obfuscatorPackage)) {
        Invoke-WebRequest -Uri 'https://api.nuget.org/v3-flatcontainer/obfuscar.globaltool/2.2.50/obfuscar.globaltool.2.2.50.nupkg' `
            -OutFile $obfuscatorPackage -UseBasicParsing
    }
    & dotnet tool install --tool-path $obfuscatorInstallDir Obfuscar.GlobalTool --version 2.2.50 --source $obfuscatorToolDir
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $obfuscatorCommand)) {
        throw 'Could not install the pinned Obfuscar tool required to protect the app assembly.'
    }
}

$tag = [Guid]::NewGuid().ToString('N')
$obfuscatorConfig = Join-Path $artifactRoot ('.obfuscar-' + $tag + '.xml')
$obfuscatedOutput = Join-Path $artifactRoot ('.obfuscated-' + $tag)
$escapedPublish = [System.Security.SecurityElement]::Escape($publish)
$escapedObfuscatedOutput = [System.Security.SecurityElement]::Escape($obfuscatedOutput)
$escapedAssembly = [System.Security.SecurityElement]::Escape((Join-Path $publish 'Risk.dll'))
$configContents = @"
<?xml version="1.0" encoding="utf-8"?>
<Obfuscator>
  <Var name="InPath" value="$escapedPublish" />
  <Var name="OutPath" value="$escapedObfuscatedOutput" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="RenameProperties" value="false" />
  <Var name="RenameEvents" value="false" />
  <Var name="HideStrings" value="false" />
  <Module file="$escapedAssembly" />
</Obfuscator>
"@
Set-Content -LiteralPath $obfuscatorConfig -Value $configContents -Encoding UTF8
& $obfuscatorCommand $obfuscatorConfig
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $obfuscatedOutput 'Risk.dll'))) {
    throw 'Obfuscation failed; refusing to package an unprotected app assembly.'
}
Copy-Item -LiteralPath (Join-Path $obfuscatedOutput 'Risk.dll') -Destination (Join-Path $publish 'Risk.dll') -Force

$stage = Join-Path $artifactRoot ('.payload-' + $tag)
$payload = Join-Path $artifactRoot ('RiskPayload-' + $tag + '.zip')
$uninstaller = Join-Path $artifactRoot ('uninstall-' + $tag + '.exe')
$setupBuild = Join-Path $artifactRoot ('Risk-Setup-' + $tag + '.exe')
$desktopUninstaller = Join-Path $OutputDir 'uninstall.exe'
$setupExe = Join-Path $OutputDir 'Risk-Setup.exe'

try {
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    & $csc /nologo /target:winexe "/out:$uninstaller" "/win32icon:$icon" /define:UNINSTALL `
        /reference:System.Windows.Forms.dll "/resource:$uninstallScript,RiskSetup.Uninstall.ps1" $bootstrapper
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $uninstaller)) {
        throw "Could not compile the standalone uninstaller (exit code $LASTEXITCODE)."
    }

    $appStage = Join-Path $stage 'App'
    $runtimeStage = Join-Path $appStage 'src\risk'
    New-Item -ItemType Directory -Force -Path $runtimeStage | Out-Null
    Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $runtimeStage -Recurse -Force
    & $csc /nologo /target:winexe "/out:$(Join-Path $appStage 'Risk.exe')" "/win32icon:$icon" `
        /reference:System.Windows.Forms.dll $launcher
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $appStage 'Risk.exe'))) {
        throw "Could not compile the Risk launcher (exit code $LASTEXITCODE)."
    }
    Copy-Item -LiteralPath $uninstaller -Destination (Join-Path $stage 'uninstall.exe') -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $payload, [IO.Compression.CompressionLevel]::Optimal, $false)

    & $csc /nologo /target:winexe "/out:$setupBuild" "/win32icon:$icon" `
        "/resource:$setupScript,RiskSetup.Setup.ps1" "/resource:$payload,RiskSetup.Payload.zip" `
        "/resource:$icon,RiskSetup.Icon.ico" "/resource:$logo,RiskSetup.Logo.png" `
        /reference:System.Windows.Forms.dll $bootstrapper
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $setupBuild)) {
        throw "Could not compile the Risk installer (exit code $LASTEXITCODE)."
    }

    Copy-Item -LiteralPath $setupBuild -Destination $setupExe -Force
    Copy-Item -LiteralPath $uninstaller -Destination $desktopUninstaller -Force
}
finally {
    $stageFull = [IO.Path]::GetFullPath($stage)
    $artifactPrefix = $artifactRoot.TrimEnd('\') + '\'
    if ($stageFull.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $stageFull)) {
        Remove-Item -LiteralPath $stageFull -Recurse -Force
    }
    foreach ($temporary in @($payload,$uninstaller,$setupBuild,$obfuscatorConfig)) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
    if (Test-Path -LiteralPath $obfuscatedOutput) {
        Remove-Item -LiteralPath $obfuscatedOutput -Recurse -Force
    }
}

Write-Host "Created $setupExe"
Write-Host "Created $desktopUninstaller"
