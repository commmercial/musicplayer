$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem

$appId = '{9D1D8122-E700-4BBF-B80F-64388570A751}_is1'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$appId"
$preferencesKey = 'HKCU:\Software\commmercial\RiskSetup'
$githubUrl = 'https://github.com/commmercial/musicplayer'
$payload = Join-Path $PSScriptRoot 'RiskPayload.zip'

function Get-InstallFolder {
    $oldDefault = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Risk'
    $newDefault = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Risk'
    if (Test-Path $preferencesKey) {
        $remembered = (Get-ItemProperty -LiteralPath $preferencesKey -Name LastInstallDirectory -ErrorAction SilentlyContinue).LastInstallDirectory
        if ($remembered -and -not [string]::Equals($remembered.TrimEnd('\'), $oldDefault.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
            return $remembered
        }
    }
    if (Test-Path $uninstallKey) {
        $previous = (Get-ItemProperty -LiteralPath $uninstallKey -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
        if ($previous -and -not [string]::Equals($previous.TrimEnd('\'), $oldDefault.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
            return $previous
        }
    }
    return $newDefault
}

$form = New-Object Windows.Forms.Form
$form.Text = 'Risk'
$form.ClientSize = New-Object Drawing.Size(570, 428)
$form.FormBorderStyle = [Windows.Forms.FormBorderStyle]::FixedSingle
$form.MaximizeBox = $false
$form.StartPosition = [Windows.Forms.FormStartPosition]::CenterScreen
$form.BackColor = [Drawing.Color]::White
$form.Font = New-Object Drawing.Font('Segoe UI', 9)

$installerIcon = Join-Path $PSScriptRoot 'Risk.ico'
$appIcon = $null
if (Test-Path $installerIcon) {
    try {
        $appIcon = [Drawing.Icon]::ExtractAssociatedIcon($installerIcon)
        if ($appIcon) { $form.Icon = $appIcon }
    } catch { }
}

$logo = New-Object Windows.Forms.PictureBox
$logo.Location = New-Object Drawing.Point(28, 28)
$logo.Size = New-Object Drawing.Size(62, 68)
$logo.SizeMode = [Windows.Forms.PictureBoxSizeMode]::Zoom
$logoImagePath = Join-Path $PSScriptRoot 'RiskLogo.png'
if (Test-Path -LiteralPath $logoImagePath) {
    $logo.Image = [Drawing.Image]::FromFile($logoImagePath)
} elseif ($appIcon) {
    $logo.Image = $appIcon.ToBitmap()
}
$form.Controls.Add($logo)

$title = New-Object Windows.Forms.Label
$title.Text = 'RISK MUSIC PLAYER'
$title.Location = New-Object Drawing.Point(108, 38)
$title.Size = New-Object Drawing.Size(430, 36)
$title.Font = New-Object Drawing.Font('Segoe UI', 22)
$title.ForeColor = [Drawing.Color]::FromArgb(24, 24, 24)
$form.Controls.Add($title)

foreach ($item in @(
    @{ Text = 'Publisher: Risk'; Y = 110 },
    @{ Text = 'Version: 1.0.0'; Y = 139 },
    @{ Text = 'Requires: Windows 10 (version 2004) or later · 64-bit'; Y = 168 }
)) {
    $label = New-Object Windows.Forms.Label
    $label.Text = $item.Text
    $label.Location = New-Object Drawing.Point(32, $item.Y)
    $label.Size = New-Object Drawing.Size(510, 24)
    $label.ForeColor = [Drawing.Color]::FromArgb(34, 34, 34)
    $form.Controls.Add($label)
}

$source = New-Object Windows.Forms.LinkLabel
$source.Text = 'Source: GitHub'
$source.Location = New-Object Drawing.Point(32, 197)
$source.Size = New-Object Drawing.Size(220, 24)
$source.LinkColor = [Drawing.Color]::FromArgb(30, 75, 130)
$source.add_LinkClicked({ Start-Process $githubUrl })
$form.Controls.Add($source)

$folderLabel = New-Object Windows.Forms.Label
$folderLabel.Text = 'Install folder'
$folderLabel.Location = New-Object Drawing.Point(32, 239)
$folderLabel.Size = New-Object Drawing.Size(200, 23)
$form.Controls.Add($folderLabel)

$folder = New-Object Windows.Forms.TextBox
$folder.Location = New-Object Drawing.Point(28, 264)
$folder.Size = New-Object Drawing.Size(424, 28)
$folder.Text = Get-InstallFolder
$form.Controls.Add($folder)

$browse = New-Object Windows.Forms.Button
$browse.Text = 'Browse'
$browse.Location = New-Object Drawing.Point(462, 263)
$browse.Size = New-Object Drawing.Size(96, 31)
$browse.add_Click({
    $picker = New-Object Windows.Forms.FolderBrowserDialog
    $picker.Description = 'Choose where Risk should be installed.'
    $picker.ShowNewFolderButton = $true
    if (Test-Path $folder.Text) { $picker.SelectedPath = $folder.Text }
    if ($picker.ShowDialog($form) -eq [Windows.Forms.DialogResult]::OK) { $folder.Text = $picker.SelectedPath }
    $picker.Dispose()
})
$form.Controls.Add($browse)

$launch = New-Object Windows.Forms.CheckBox
$launch.Text = 'Launch when ready'
$launch.Checked = $true
$launch.AutoSize = $true
$launch.Location = New-Object Drawing.Point(28, 315)
$form.Controls.Add($launch)

$status = New-Object Windows.Forms.Label
$status.Location = New-Object Drawing.Point(28, 353)
$status.Size = New-Object Drawing.Size(530, 22)
$status.ForeColor = [Drawing.Color]::FromArgb(70, 70, 70)
$form.Controls.Add($status)

$progress = New-Object Windows.Forms.ProgressBar
$progress.Location = New-Object Drawing.Point(28, 374)
$progress.Size = New-Object Drawing.Size(532, 8)
$progress.Style = [Windows.Forms.ProgressBarStyle]::Continuous
$form.Controls.Add($progress)

$cancel = New-Object Windows.Forms.Button
$cancel.Text = 'Cancel'
$cancel.Location = New-Object Drawing.Point(272, 395)
$cancel.Size = New-Object Drawing.Size(90, 33)
$cancel.add_Click({ $form.Close() })
$form.Controls.Add($cancel)

$uninstall = New-Object Windows.Forms.Button
$uninstall.Text = 'Uninstall'
$uninstall.Location = New-Object Drawing.Point(369, 395)
$uninstall.Size = New-Object Drawing.Size(94, 33)
$uninstall.Enabled = $false
if (Test-Path $uninstallKey) {
    $existingInstall = (Get-ItemProperty -LiteralPath $uninstallKey -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
    if ($existingInstall) { $uninstall.Enabled = Test-Path (Join-Path $existingInstall 'uninstall.exe') }
}
if (-not $uninstall.Enabled) {
    $rememberedInstall = Get-InstallFolder
    $uninstall.Enabled = Test-Path (Join-Path $rememberedInstall 'uninstall.exe')
}
$uninstall.add_Click({
    $existing = (Get-ItemProperty -LiteralPath $uninstallKey -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
    if (-not $existing) { $existing = Get-InstallFolder }
    $uninstaller = Join-Path $existing 'uninstall.exe'
    if (-not (Test-Path -LiteralPath $uninstaller)) {
        [Windows.Forms.MessageBox]::Show($form, 'The Risk uninstaller could not be found in the selected installation folder.', 'Uninstall Risk', 'OK', 'Error') | Out-Null
        return
    }
    if ([Windows.Forms.MessageBox]::Show($form, 'Are you sure you want to uninstall Risk? This will delete the installed app files and shortcuts from this PC.', 'Confirm uninstall', 'YesNo', 'Warning', 'Button2') -eq [Windows.Forms.DialogResult]::Yes) {
        Start-Process -FilePath $uninstaller -ArgumentList '/confirmed' -WorkingDirectory ([IO.Path]::GetTempPath())
        $form.Close()
    }
})
$form.Controls.Add($uninstall)

$install = New-Object Windows.Forms.Button
$install.Text = 'Install'
$install.Location = New-Object Drawing.Point(470, 395)
$install.Size = New-Object Drawing.Size(90, 33)
$form.Controls.Add($install)
$form.AcceptButton = $install
$form.CancelButton = $cancel

$install.add_Click({
    try {
        $version = [Environment]::OSVersion.Version
        if ($version.Major -lt 10 -or ($version.Major -eq 10 -and $version.Build -lt 19041)) {
            [Windows.Forms.MessageBox]::Show($form, 'Risk requires Windows 10 version 2004 or later.', 'Windows version not supported', 'OK', 'Warning') | Out-Null
            return
        }
        if (-not [Environment]::Is64BitOperatingSystem) {
            [Windows.Forms.MessageBox]::Show($form, 'Risk requires 64-bit Windows.', 'Windows version not supported', 'OK', 'Warning') | Out-Null
            return
        }
        if ([string]::IsNullOrWhiteSpace($folder.Text)) {
            [Windows.Forms.MessageBox]::Show($form, 'Choose an install folder first.', 'Install Risk', 'OK', 'Warning') | Out-Null
            return
        }
        if (-not (Test-Path $payload)) { throw 'The setup files are missing from this installer.' }

        $installDir = [IO.Path]::GetFullPath($folder.Text.Trim())
        $install.Enabled = $false
        $browse.Enabled = $false
        $folder.Enabled = $false
        $status.Text = 'Installing Risk…'
        $form.Refresh()

        [IO.Directory]::CreateDirectory($installDir) | Out-Null
        New-Item -ItemType Directory -Force -Path $preferencesKey | Out-Null
        Set-ItemProperty -LiteralPath $preferencesKey -Name LastInstallDirectory -Value $installDir

        $archive = [IO.Compression.ZipFile]::OpenRead($payload)
        try {
            # Mirror the repository's src\risk organization while keeping
            # runtime dependencies together, as .NET and WinUI load them
            # relative to the app folder.
            $legacyAppDir = Join-Path $installDir 'App'
            $legacyDeps = Join-Path $legacyAppDir 'Risk.deps.json'
            $legacyRuntimeDir = Join-Path $legacyAppDir 'Runtime'
            $runtimeDir = Join-Path $legacyAppDir 'src\risk'
            if (-not (Test-Path -LiteralPath $runtimeDir) -and
                ((Test-Path -LiteralPath $legacyRuntimeDir) -or (Test-Path -LiteralPath $legacyDeps))) {
                [IO.Directory]::CreateDirectory($runtimeDir) | Out-Null
                if (Test-Path -LiteralPath $legacyRuntimeDir) {
                    Get-ChildItem -LiteralPath $legacyRuntimeDir -Force |
                        Move-Item -Destination $runtimeDir
                    Remove-Item -LiteralPath $legacyRuntimeDir -Force
                }
                if (Test-Path -LiteralPath $legacyDeps) {
                    Get-ChildItem -LiteralPath $legacyAppDir -Force |
                        Where-Object { $_.Name -ne 'src' } | Move-Item -Destination $runtimeDir
                }
            }
            $count = [Math]::Max(1, $archive.Entries.Count)
            for ($i = 0; $i -lt $archive.Entries.Count; $i++) {
                $entry = $archive.Entries[$i]
                $target = Join-Path $installDir $entry.FullName
                if ($entry.FullName.EndsWith('/')) {
                    [IO.Directory]::CreateDirectory($target) | Out-Null
                } else {
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                    $inputStream = $entry.Open()
                    try {
                        $outputStream = [IO.File]::Open($target, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
                        try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
                    } finally { $inputStream.Dispose() }
                }
                $progress.Value = [Math]::Min(95, [int](($i + 1) * 95 / $count))
                $form.Refresh()
            }
        } finally { $archive.Dispose() }

        $appDir = Join-Path $installDir 'App'
        $appExe = Join-Path $appDir 'Risk.exe'
        $runtimeDir = Join-Path $appDir 'src\risk'
        $runtimeExe = Join-Path $runtimeDir 'Risk.exe'
        $uninstaller = Join-Path $installDir 'uninstall.exe'
        if (-not (Test-Path $appExe) -or -not (Test-Path $runtimeExe)) { throw 'Risk runtime files were not found in the installed files.' }
        if (-not (Test-Path $uninstaller)) { throw 'The standalone uninstaller was not found in the installed files.' }

        $desktop = [Environment]::GetFolderPath('DesktopDirectory')
        $menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Risk'
        [IO.Directory]::CreateDirectory($menu) | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        $appIconPath = Join-Path $runtimeDir 'Assets\app-icon-v2.ico'
        foreach ($shortcutInfo in @(
            @{ Path = (Join-Path $desktop 'Risk.lnk'); Target = $appExe; WorkingDirectory = $appDir },
            @{ Path = (Join-Path $menu 'Risk.lnk'); Target = $appExe; WorkingDirectory = $appDir },
            @{ Path = (Join-Path $menu 'Uninstall Risk.lnk'); Target = $uninstaller; WorkingDirectory = [IO.Path]::GetTempPath() }
        )) {
            $shortcut = $shell.CreateShortcut($shortcutInfo.Path)
            $shortcut.TargetPath = $shortcutInfo.Target
            $shortcut.WorkingDirectory = $shortcutInfo.WorkingDirectory
            $shortcut.IconLocation = $appIconPath
            $shortcut.Save()
        }

        New-Item -ItemType Directory -Force -Path $uninstallKey | Out-Null
        Set-ItemProperty -LiteralPath $uninstallKey -Name DisplayName -Value 'Risk'
        Set-ItemProperty -LiteralPath $uninstallKey -Name DisplayVersion -Value '1.0.0'
        Set-ItemProperty -LiteralPath $uninstallKey -Name Publisher -Value 'Risk'
        Set-ItemProperty -LiteralPath $uninstallKey -Name InstallLocation -Value $installDir
        Set-ItemProperty -LiteralPath $uninstallKey -Name UninstallString -Value ('"' + $uninstaller + '"')

        $progress.Value = 100
        $status.Text = 'Risk is ready.'
        $install.Text = 'Installed'
        $install.Enabled = $false
        $uninstall.Enabled = $true
        if ($launch.Checked) {
            Start-Process -FilePath $appExe -WorkingDirectory $appDir
            $form.Close()
        }
    } catch {
        $status.Text = 'Setup could not finish.'
        $install.Enabled = $true
        $browse.Enabled = $true
        $folder.Enabled = $true
        [Windows.Forms.MessageBox]::Show($form, $_.Exception.Message, 'Install Risk', 'OK', 'Error') | Out-Null
    }
})

[void]$form.ShowDialog()
