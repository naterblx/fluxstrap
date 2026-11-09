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
- Proximity voice chat between FluxStrap users, like Roblox's: voices get
  quieter with distance, pan left and right, and go silent past 80 studs.
  Everyone can listen; only the dev build can talk, with a mute button over
  the game. A bubble over whoever's talking shows how loud they are. Works on
  the 2019, 2020 and 2021 clients.
- In the dev build, a "Who's online" window: how many people are in game with
  FluxStrap, which servers they're in and their usernames. It needs a dev key,
  since it lists who's where.

## Credits

- [Fishstrap][fishstrap] and [Bloxstrap][bloxstrap] and all of their
  contributors. Their licenses are in `LICENSE` and `LICENSE.Bloxstrap`.
- The Fluxer launcher, whose client install and launch logic FluxStrap mirrors.

[fishstrap]: https://github.com/fishstrap/fishstrap
[bloxstrap]: https://github.com/bloxstraplabs/bloxstrap
"# fluxstrap" 
