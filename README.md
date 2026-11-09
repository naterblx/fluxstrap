<div align="center">

![FluxStrap](Images/FluxStrap-Light.png#gh-light-mode-only)
![FluxStrap](Images/FluxStrap-Dark.png#gh-dark-mode-only)

</div>

## NOTE

Claude only contributed to pushing to github everything here was only 10% ai generated rest is fishstrap's code modified by me

FluxStrap is a custom bootstrapper for the [Fluxer](https://fluxr.lol) revival.
It's a fork of [Fishstrap][fishstrap], which is itself based on
[Bloxstrap][bloxstrap], so you get their settings menu, Fast Flags, mods,
bootstrapper styles and Discord Rich Presence, but for the Fluxer clients.

> [!NOTE]
> FluxStrap is an application for **Windows 10 and above.**

## How it works

FluxStrap takes over the `fluxer-player:` and `fluxerclient:` links from the
official Fluxer launcher. When you press Play on <https://fluxr.lol>:

1. It reads the client year from the link (2019, 2020, 2021, 2022, or the 2026
   Rokori client) and checks `fluxr.lol/setup/manifest.txt` for that build.
2. It downloads and verifies the client zip, or copies it from the official
   launcher's folder if that already has the same build.
3. It applies your mods and Fast Flags. Your flags are layered on top of the
   flags each Fluxer client ships with, so the client's own settings stay.
4. It launches the client with the same arguments the official launcher uses,
   including the runtime domain patching the 2022 and 2026 clients need and
   the singleton patch that lets several clients run at once.

Fluxer Studio links (`launchmode:edit`) open the matching Studio year.
Uninstalling FluxStrap hands the Fluxer links back to the official launcher.
FluxStrap never registers or changes the `roblox://` links, so a real
Roblox/Bloxstrap/Fishstrap install keeps working next to it.

## Features

- Every Fishstrap feature that applies to Fluxer: settings menu, Fast Flag
  editor, mods, custom fonts, bootstrapper styles and icons, window tweaks
- Discord Rich Presence with the game's name and icon from Fluxer's API
- Server location in the tray menu while you play
- Per-client mod tracking, so removing a mod restores the original file in
  every client year
- A Fluxer page in the settings to pick the default player and Studio year
  and see which clients are installed
- VR through SteamVR on the 2019, 2020 and 2021 clients, including headsets
  that aren't plugged in over USB (Quest over Steam Link, Air Link or Virtual
  Desktop). Turn on "VR Enabled" in Global Settings.
- Voice chat between FluxStrap users in the same game server, through the
  FluxStrap voice server. Everyone can listen; only the dev build can talk.

## Public and dev builds

There are two builds of FluxStrap, made from the same code:

- **Public** (`FluxStrap.exe`, tags `v1.2.0` and so on): the normal release.
  It can hear voice chat but has no microphone code at all.
- **Dev**: adds talking in voice chat, with push-to-talk or open mic. It's
  never published or released. It's built locally with
  `dotnet publish -p:Channel=Dev`, and it doesn't auto-update, so a public
  release can't replace it.

Talking also needs a dev key. The voice server checks it, so a modified public
build still can't talk.

## Voice chat server

The server is in [`VoiceServer/`](VoiceServer). It's a small Node.js WebSocket
relay. Fluxer itself doesn't need any changes, because FluxStrap already knows
which game server you're in from the client's log. Everyone in the same server
shares a room.

To host it on Render's free plan:

1. On render.com, choose **New > Blueprint** and pick this repository.
   `render.yaml` sets up a service called `fluxstrap-voice`.
2. When Render asks for `KEY_SECRET`, enter a secret from
   `node keygen.js --new-secret`, run inside `VoiceServer`.
3. FluxStrap connects to `wss://fluxstrap.onrender.com/voice`. If the service
   ever moves, change `VoiceChat.DefaultServer`.

Dev keys are signed with that secret, so new keys work straight away without
changing anything on the server. Make one with
`node keygen.js --secret-file secret.txt alex`. To take a key away, add the name
to the `REVOKED` environment variable (comma separated). Render restarts the
server whenever you save. `DEV_KEYS` (`name=key` pairs) is still accepted for
keys that aren't signed.

Free services go to sleep after about 15 minutes without anyone connected. The
first person to join voice after that waits up to a minute for it to wake up.

`npm test` inside `VoiceServer` checks the relay rules.

## Credits

- [Fishstrap][fishstrap] and [Bloxstrap][bloxstrap] and all of their
  contributors. Their licenses are in `LICENSE` and `LICENSE.Bloxstrap`.
- The Fluxer launcher, whose client install and launch logic FluxStrap mirrors.

[fishstrap]: https://github.com/fishstrap/fishstrap
[bloxstrap]: https://github.com/bloxstraplabs/bloxstrap
"# fluxstrap" 
