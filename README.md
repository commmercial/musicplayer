build and install
Extract this folder.
Keep global.json beside Install-VYBE.bat and the src folder.
Run Install-VYBE.bat.
The setup automatically downloads the .NET SDK version selected by global.json when it is missing, restores packages, publishes a self-contained x64 build to %LOCALAPPDATA%\Programs\Risk, creates a Risk desktop shortcut with the supplied icon, and launches the app.

Discord activity
Open Discord Desktop, enable Show what I’m listening to on Discord in Risk settings, and play a song. Risk sends the song title, artist, cover art, and playback timestamps to the running Discord desktop client over its local Rich Presence connection; no browser authorization or account token is required. Use Connect to Discord to check that the desktop client is reachable.
