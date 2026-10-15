param([switch]$Confirmed, [string]$InstallRoot)

$ErrorActionPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms
$appId = '{9D1D8122-E700-4BBF-B80F-64388570A751}_is1'
$keyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"

if (-not $Confirmed) {
    $answer = [Windows.Forms.MessageBox]::Show(
        'Are you sure you want to uninstall Risk? This will delete the installed app files and shortcuts from this PC.',
        'Confirm uninstall', 'YesNo', 'Warning', 'Button2')
    if ($answer -ne [Windows.Forms.DialogResult]::Yes) { exit 0 }
}

$installDir = (Get-ItemProperty -LiteralPath $keyPath -Name InstallLocation).InstallLocation
if (-not $installDir) { $installDir = $InstallRoot }
if (-not $installDir) {
    [Windows.Forms.MessageBox]::Show('The Risk installation folder could not be found.', 'Uninstall Risk', 'OK', 'Error') | Out-Null
    exit 1
}

$desktop = [Environment]::GetFolderPath('DesktopDirectory')
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Risk'

$installDir = [IO.Path]::GetFullPath($installDir.TrimEnd('\'))
$installPrefix = $installDir.TrimEnd('\') + '\'
Get-Process -Name 'Risk' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $processPath = $_.Path
        if ($processPath -and $processPath.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            if ($_.MainWindowHandle -ne 0) { [void]$_.CloseMainWindow() }
            if (-not $_.WaitForExit(5000)) { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        }
    } catch { }
}

$cleanupScript = Join-Path ([IO.Path]::GetTempPath()) ('RiskCleanup-' + [Guid]::NewGuid().ToString('N') + '.ps1')
$cleanupBody = @'
param([string]$InstallDirectory, [string]$ScriptPath, [string]$UninstallKey, [string]$DesktopShortcut, [string]$StartMenuFolder)
Start-Sleep -Seconds 3
$removed = $false
$lastError = $null
for ($attempt = 0; $attempt -lt 20; $attempt++) {
    try {
        if (Test-Path -LiteralPath $InstallDirectory) {
            Remove-Item -LiteralPath $InstallDirectory -Recurse -Force -ErrorAction Stop
        }
        if (-not (Test-Path -LiteralPath $InstallDirectory)) { $removed = $true; break }
    } catch { $lastError = $_.Exception.Message; Start-Sleep -Milliseconds 750 }
}
if ($removed) {
    Remove-Item -LiteralPath $DesktopShortcut -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $StartMenuFolder -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show('Risk was uninstalled successfully.', 'Risk Uninstall', 'OK', 'Information') | Out-Null
} else {
    Add-Type -AssemblyName System.Windows.Forms
    $message = 'Risk could not remove all files from:' + [Environment]::NewLine + $InstallDirectory + [Environment]::NewLine + [Environment]::NewLine + 'Close any open Risk windows and try uninstalling again.'
    if ($lastError) { $message += [Environment]::NewLine + [Environment]::NewLine + $lastError }
    [Windows.Forms.MessageBox]::Show($message, 'Risk Uninstall Incomplete', 'OK', 'Error') | Out-Null
}
Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue
'@
Set-Content -LiteralPath $cleanupScript -Value $cleanupBody -Encoding UTF8
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$cleanupArgs = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $cleanupScript + '" "' + $installDir + '" "' + $cleanupScript + '" "' + $keyPath + '" "' + (Join-Path $desktop 'Risk.lnk') + '" "' + $menu + '"'
Start-Process -FilePath $powershell -ArgumentList $cleanupArgs -WorkingDirectory ([IO.Path]::GetTempPath()) -WindowStyle Hidden
