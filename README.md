# Risk Music Player

Risk is a Windows desktop music player built with .NET 8 and WinUI.

## Features

- Search and play music through YouTube Music and SoundCloud.
- Import playlists from SoundCloud, YouTube Music, and public Spotify playlist links.
- Spotify playlist imports use Spotify's public embed data and include up to 50 tracks. The tracks are matched to YouTube Music versions when played; private Spotify playlists are not available.
- The download folder defaults to `%USERPROFILE%\Music\Risk` and can be changed in Settings.
- Settings and playlists are stored under `%LOCALAPPDATA%\Risk`; existing `%LOCALAPPDATA%\VYBE` data is migrated on first launch.

## Build the installer

Install the .NET 8 SDK, then run `scripts\build-installer.ps1` in PowerShell. The script publishes a self-contained x64 app and creates `Risk-Setup.exe` and `uninstall.exe` on the Desktop by default. You can specify another output folder with `-OutputDir`.

The setup defaults to `%LOCALAPPDATA%\Programs\Risk`, remembers the last selected install folder, and supports Windows 10 version 2004 or later and Windows 11. Runtime files are organized under `App\src\risk` with the launcher at `App\Risk.exe`.
