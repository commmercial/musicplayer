param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$IconPath
)

$exe = [IO.Path]::GetFullPath($ExePath)
$icon = [IO.Path]::GetFullPath($IconPath) + ',0'
$shell = New-Object -ComObject WScript.Shell
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Risk.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Programs')) 'Risk.lnk')
)

$pinnedFolder = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
if (Test-Path -LiteralPath $pinnedFolder) {
    $shortcutPaths += Get-ChildItem -LiteralPath $pinnedFolder -Filter '*.lnk' -File -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName }
}

foreach ($path in $shortcutPaths | Select-Object -Unique) {
    if (-not (Test-Path -LiteralPath $path)) { continue }
    try {
        $shortcut = $shell.CreateShortcut($path)
        if ($shortcutPaths.Count -le 2 -or
            [string]::Equals([IO.Path]::GetFullPath($shortcut.TargetPath), $exe, [StringComparison]::OrdinalIgnoreCase)) {
            $shortcut.TargetPath = $exe
            $shortcut.WorkingDirectory = Split-Path -Parent $exe
            $shortcut.IconLocation = $icon
            $shortcut.Save()
        }
    }
    catch { }
}

if (-not ('RiskShellRefresh' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RiskShellRefresh {
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
'@
}
[RiskShellRefresh]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
