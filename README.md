# Content Packs

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin for [Message From Aliens](https://store.steampowered.com/app/1858720/Message_From_Aliens/) that loads text content packs.

## Installing

Install BepInEx first, if you haven't already, as described under [Requirements](#requirements). Then unzip the release zip into the game folder, the one that holds `MessageFromAliens.exe`. In Steam, right-click the game and choose Manage, then Browse local files, to open it. The zip holds `BepInEx/plugins/ContentPacks/`, so the mod lands in BepInEx's plugins folder. To update, unzip a newer release over it. To uninstall, delete `BepInEx/plugins/ContentPacks/`.

## Content packs

```
BepInEx/content/<pack_name>/TxtData/<files>
```

A file's name, up to the first dot, names a game message category (or other resource). The rest of the name dictates the effect the file has:

| File | Effect |
|---|---|
| `<name>.txt` | Replaces `<name>` if it already exists. Otherwise, adds a new message category with that name. |

Any other file is skipped with a warning.

Packs apply in order of their folder (pack) names, and later packs override earlier ones. Within a pack, files apply in name order.

Folders whose names start with `_` are skipped. This includes the `_dump` folder created by [`DumpGameText`](#configuration).

Problems are logged with the pack and file, and loading continues without the part that failed.

To get the original files as a starting point, enable [`DumpGameText`](#configuration). The files are written to `BepInEx/content/_dump/TxtData/`.

The `14mv` data used by `MV` is loaded directly from `Resources` and cannot be overridden by content packs.

### New pages

A new `.txt` file adds a message category, a set of pages. The name is arbitrary. A page can be any of three kinds: an ordinary page of text, a quiz, or a menu linking to other pages. A chapter is a menu.

#### Page text

```
#<title>
<line>
<line>
```

- A line starting with `#` begins a page, and its words are the page's title. Pages are found by title, so a title must be unique across all files.
- Each following line, up to the next `#`, is one sentence: word IDs separated by spaces, drawn as glyphs. Long lines wrap automatically in-game; don't wrap them in the file.
- Tokens starting with `(` or `[` aren't drawn. Number tokens like `5e2` are drawn as numbers.

#### Quizzes

A quiz is a page with answer slots:

- A line starting with `@` lists the glyphs the quiz offers to fill the slots.
- A line starting with `%` gives the correct answers, the glyphs that fill the slots in order. Multiple `%` lines are alternate solutions to the whole quiz. Each must fill the same number of slots as the first.
- `?` in a line is an answer slot.

Every quiz title in the game has the form `<words> riddle <n>`: `riddle` is the game's word for quiz, `<n>` is the quiz's number, and `<words>` names its chapter. Count gates count pages by that title pattern. See [Count gates](#count-gates).

#### Menus

A page whose text contains a `[` is a menu. Each of its lines links to the page whose title is that line's words, leaving out tokens starting with `[`. An empty `[]` token makes a page a menu without affecting any line.

A new page can only be reached through a menu, so a pack has to change a menu to link to it.

#### Gates

A gate is a token like `[word-word-word]`: a page's title, with its words joined by `-`.

A menu line holding a gate shows only once that page has been won. A line with several gates needs all of them. The game puts gates at the start of the line, as in `[<gate>] <title>`.

In an ordinary page, a line starting with a gate is hidden until it passes.

A page is won:

- for a quiz, when its slots are all filled correctly;
- for an ordinary page reached through the chapter menus, when its last screen of text is shown;
- for a menu, when every page it links to has been won.

A menu draws the lines of won pages dimmed.

##### Count gates

The game reads a gate as a count when it contains `riddle-` followed by a number. A count gate `[<words>-riddle-<n>]` passes once at least `<n>` won pages have titles starting with `<words> riddle`. It doesn't matter which pages they are, what kind of page, or in what order they were won.

Every quiz title in the game has the form `<words> riddle <n>`, so in the game a count gate counts a chapter's quizzes: `[<chaptername>-riddle-5]` means any five of that chapter's quizzes, not its fifth.

To use count gates in a pack:

- Give the pages to be counted titles starting with the same words followed by `riddle`, as in `<words> riddle 1`, `<words> riddle 2`, etc. They're usually quizzes, but an ordinary page with such a title counts once it's read.
- Pick `<words>` that no other title starts with followed by `riddle`, or those pages count too.
- A gate can't require one particular page whose title has `riddle` and a number after it, since naming that page makes the gate a count.

#### Chapters

A chapter is a menu of lessons and quizzes, usually linked from one of the game's two chapter lists. Gate the line that links to it to control when it unlocks. For example, the game opens its second chapter with a count gate, after any five of the first chapter's quizzes. Gate its own lines on its quizzes to open its pages as the player progresses. Its menu is won when all its pages are, which can gate later content.

New chapters can't award achievements.

## Patches

| Method | Patch |
|---|---|
| `MV.Init` | Prefix: dumps the game's text files when `DumpGameText` is on, then applies content packs to `Global.textfiles` before the game parses them. |

## Configuration

`BepInEx/config/sparr.messagefromaliens.contentpacks.cfg`:

| Key | Default | Effect |
|---|---|---|
| `Content.LoadContentPacks` | `true` | Load content packs from `BepInEx/content/`. Takes effect at the next launch. |
| `Content.DumpGameText` | `false` | Write the stock game text files to `BepInEx/content/_dump/TxtData/` at startup. |

Each setting is tagged with its name in glyphs, and the mod names itself in glyphs, so Mod Settings can show them in the game.

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
