# Content Packs

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin for [Message From Aliens](https://store.steampowered.com/app/1858720/Message_From_Aliens/) that writes out the game's text files.

## Installing

Install BepInEx first, if you haven't already, as described under [Requirements](#requirements). Then unzip the release zip into the game folder, the one that holds `MessageFromAliens.exe`. In Steam, right-click the game and choose Manage, then Browse local files, to open it. The zip holds `BepInEx/plugins/ContentPacks/`, so the mod lands in BepInEx's plugins folder. To update, unzip a newer release over it. To uninstall, delete `BepInEx/plugins/ContentPacks/`.

## Game text

To get the game's text files, enable [`DumpGameText`](#configuration). The files are written to `BepInEx/content/_dump/TxtData/`.

The `14mv` data used by `MV` is loaded directly from `Resources` and isn't included.

## Patches

| Method | Patch |
|---|---|
| `MV.Init` | Prefix: dumps the game's text files when `DumpGameText` is on. |

## Configuration

`BepInEx/config/sparr.messagefromaliens.contentpacks.cfg`:

| Key | Default | Effect |
|---|---|---|
| `Content.DumpGameText` | `false` | Write the stock game text files to `BepInEx/content/_dump/TxtData/` at startup. |

The setting is tagged with its name in glyphs, and the mod names itself in glyphs, so Mod Settings can show it in the game.

## Requirements

- BepInEx 5.4.23.6 or later, `BepInEx_win_x64` build, unzipped into the game folder. Tested with Unity 6000.6.8f1.
- On Linux under Proton, set the Steam launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
- .NET SDK 8 or later to build.

## Building

```sh
dotnet build -c Release
```

The build copies `ContentPacks.dll` to `<game>/BepInEx/plugins/ContentPacks/`. Disable the copy with `-p:DeployToGame=false`.

The build looks for the game in `<SteamLibraryDir>/steamapps/common/Message From Aliens`. `SteamLibraryDir` comes from the first of these that is set:

1. `-p:SteamLibraryDir=/path/to/SteamLibrary` on the command line.
2. A `SteamLibraryDir` environment variable.
3. `GamePaths.props`, a copy of [`GamePaths.props.example`](GamePaths.props.example). It is ignored by git.
4. Steam's default install location: `~/.local/share/Steam` on Linux, `C:\Program Files (x86)\Steam` on Windows.

For a game folder outside a Steam library, set `GameDir` to the game folder the same ways.
