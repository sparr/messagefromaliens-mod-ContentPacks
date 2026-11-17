# Content Packs

A [BepInEx 5](https://github.com/BepInEx/BepInEx) plugin for [Message From Aliens](https://store.steampowered.com/app/1858720/Message_From_Aliens/) that loads text content packs.

## Installing

Install BepInEx first, if you haven't already, as described under [Requirements](#requirements). Then unzip the release zip into the game folder, the one that holds `MessageFromAliens.exe`. In Steam, right-click the game and choose Manage, then Browse local files, to open it. The zip holds `BepInEx/plugins/ContentPacks/`, so the mod lands in BepInEx's plugins folder, and the example pack in `BepInEx/content/_circle-length/`. To update, unzip a newer release over it. To uninstall, delete `BepInEx/plugins/ContentPacks/` and `BepInEx/content/_circle-length/`.

## Content packs

```
BepInEx/content/<pack_name>/TxtData/<files>
```

A file's name, up to the first dot, names a game message category (or other resource). The rest of the name dictates the effect the file has:

| File | Effect |
|---|---|
| `<name>.txt` | Replaces `<name>` if it already exists. Otherwise, adds a new message category with that name. |
| `<name>.patch` | Applies a unified diff to the file. See [Text patches](#text-patches). |
| `newalien.csv`, `newalien.<anything>.csv` | Merges rows into the word table. See [Word table merges](#word-table-merges). |

Any other file is skipped with a warning.

Every replacement in every pack applies first, then every patch, then every merge. So a patch or merge sees the files as all packs' replacements left them.

Within each of those steps, packs apply in order of their folder (pack) names, and later packs override earlier ones. Within a pack, files apply in name order, except that `newalien.csv` comes before the pack's other `newalien.foo.csv` files.

Folders whose names start with `_` are skipped. This includes the `_dump` folder created by [`DumpGameText`](#configuration).

Each other pack gets a setting to turn it off. In Mod Settings, Content Packs' page has a line that opens a page of these settings, in loading order. A pack whose folder name is game words separated by hyphens, like `circle-length`, is labeled with those glyphs. Any other pack is labeled with its place in loading order, as a number. Packs are read as the game starts, so a change takes effect at the next launch. The settings are stored in the `Packs` section of the config file, one per folder name.

Problems are logged with the pack, file, and line, and loading continues without the part that failed.

To get the original files as a starting point, enable [`DumpGameText`](#configuration). The files are written to `BepInEx/content/_dump/TxtData/`.

The `14mv` data used by `MV` is loaded directly from `Resources` and cannot be overridden by content packs.

Content Packs comes with a small example pack in `BepInEx/content/_circle-length/`. It's skipped until you rename its folder without the `_`. See [its README](content/_circle-length/README.md).

### New pages

A new `.txt` file adds a message category, a set of pages. The name is arbitrary, only relevant to future patches that modify it. A page can be any of three kinds: an ordinary page of text, a quiz, or a menu linking to other pages. A chapter is a menu.

#### Page text

```
#<title>
<line>
<line>
```

- A line starting with `#` begins a page, and its words are the page's title. Pages are found by title, so a title must be unique across all files.
- Each following line, up to the next `#`, is one sentence: word IDs separated by spaces, drawn as glyphs. Long lines wrap automatically in-game; don't wrap them in the file.
- Tokens starting with `(` or `[` aren't drawn. Number tokens like `5e2` are drawn as numbers. See [Numbers](#numbers).

#### Quizzes

A quiz is a page with answer slots:

- A line starting with `@` lists the glyphs the quiz offers to fill the slots.
- A line starting with `%` gives the correct answers, the glyphs that fill the slots in order. Multiple `%` lines are alternate solutions to the whole quiz. Each must fill the same number of slots as the first.
- `?` in a line is an answer slot.

Every quiz title in the game has the form `<words> riddle <n>`: `riddle` is the game's word for quiz, `<n>` is the quiz's number, and `<words>` names its chapter. Count gates count pages by that title pattern. See [Count gates](#count-gates).

#### Menus

A page whose text contains a `[` is a menu. Each of its lines links to the page whose title is that line's words, leaving out tokens starting with `[`. An empty `[]` token makes a page a menu without affecting any line.

A new page can only be reached through a menu. To show a page, patch a line naming it into an existing menu. The example pack adds its pages to chapter 1's menu.

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

### Text patches

A `.patch` file is a unified diff of one file, as made by `diff -u` or `git diff`. Its file names are not used. Hunks apply in order, each to the result of the ones before it. If any hunk fails, the whole patch is skipped. Hunk line numbers are never enforced, and context lines are only enforced if [`EnforcePatchContext`](#configuration) is enabled or the hunk only adds lines with no removals. When a hunk matches several places, a place where its context matches too wins first, then the one nearest its header's line number, then the one where the most context lines match.

### Word table merges

The word table is a CSV file with one row per word. A `.csv` merge file changes rows by their word:

```
# A new basic glyph and a compound that uses it.
word,meaning,wordtype,struct,component1,component2,modifier
diameter,diameter equal radius multiply 2,noun,s,-1 -2 1 -2 2 -1 2 1 1 2 -1 2 -2 1 -2 -1 -1 -2;-2 0 2 0,,
circumference,circumference equal diameter multiply pi,noun,lr,diameter,pi,
```

This adds two nouns. `diameter` is a basic glyph, an octagon with a line through it. `circumference` is that glyph beside the game's glyph for pi. The definitions and word type are the game's own word IDs.

- The first line names the columns the file sets, using the names in the word table's own header row, case insensitive: `word`, `meaning`, `wordtype`, `redirect`, `struct`, `component1`, `component2`, `modifier`, and `rename`. Column order doesn't matter, except the first column must be `word`.
- A row whose word exists overwrites the columns the file names and keeps the rest. A row with a new word adds it, with the other columns empty. A cell left empty sets that field to empty. To leave a field alone, leave its column out of the file. If different words have different column requirements, put them in separate files.
- `!<word>` in the word column removes that row. A word that other rows use as a component, as a basic glyph's redirect, or as a number's digit can't be removed. Removing a word that the game's text still uses is logged as a warning.
- Lines starting with `#` and blank lines are skipped. No cell can contain a comma.
- The game removes every `:` from a row before reading it, so that fields like `1e1` can be stored as `:1e1` to prevent spreadsheet programs from mangling them. A `:` found not at the start of a word is logged as a warning.

The `struct`, `component1`, `component2`, and `modifier` columns set the word's glyph. See [Glyph structure](#glyph-structure).

After all merges, each added or changed row is checked. A row fails when:
- it has no word type or no structure code, since the game stops loading on a word with no word type;
- a component that must be a word names no word;
- a basic glyph's strokes hold anything but numbers, or an empty or odd-length path;
- its components lead back to the word itself;
- a basic glyph's redirect names no word, since the game stops loading on one;
- it's a number word whose name isn't a digit, `e`, and a place, like `5e2`, or whose digit names no word, since the game stops loading on one.

A changed row that fails keeps its earlier definition, and a new row that fails is dropped. These checks are applied repeatedly, because one failed row could cause other rows to fail.

New words, and words whose shape changed, get new sprites. See [Glyph sprites](#glyph-sprites).

#### Glyph structure

A word's glyph comes from four columns of its row. `struct` is a code that says how the glyph is built. `component1` and `component2` hold its strokes or name its parts. `modifier` adjusts the layout. Parts are other words, so glyphs nest: the game builds each part's glyph first, then arranges the parts.

##### Basic glyphs

| Struct | Components |
|---|---|
| `s` | `component1` is the glyph's strokes. `component2` is empty. |
| `solid` | same as `s` inside a compound glyph, otherwise ignored |

Strokes are paths separated by `;`. Each path is a list of numbers read as x y pairs and drawn as straight lines from point to point, so a closed shape repeats its first point. x grows to the right and y grows upward. The game's glyphs keep to a half-unit grid, mostly within -3 to 3. The glyph is centered on its bounding box. Drawn alone, it's scaled down to fit its box if it's larger, as every glyph on that grid is. Inside other glyphs, parts are always rescaled, so only proportions matter there. An empty `component1` draws the game's default, a single diagonal stroke. Every glyph is drawn with the same line width, whatever its size.

One game word uses `solid`, the notebook's cursor marker, which has a sprite that is usually never seen. Content Packs draws `solid` words using that sprite when they stand alone, or draws their strokes when they are inside a compound glyph.

##### One part

These take one word in `component1` and leave `component2` empty, drawing one or more copies of `component1`'s glyph. Multiple copies and `shrink` are scaled to just under half size.

| Struct | Arrangement |
|---|---|
| `d` | two copies side by side |
| `vd` | two copies, one above the other |
| `t` | three copies in a triangle, one on top |
| `q` | four copies in a 2 by 2 grid |
| `shrink` | one copy |
| `flr` | mirrored left to right |
| `fud` | mirrored top to bottom |
| `fb` | turned 180 degrees |

##### Two parts

These take two words: `component1` is the first part and `component2` the second.

| Struct | Arrangement |
|---|---|
| `lr` | first part on the left, second on the right |
| `ud` | first part above the second |
| `oi` | first part outside, second inside it, centered |
| `our`, `oul`, `odr`, `odl` | second part in the upper right, upper left, lower right, or lower left corner of the first |
| `ou`, `od`, `ol`, `or` | second part at the top, bottom, left, or right side of the first |
| `over` | second part on top of the first, at full size |

Before they're arranged, both parts are scaled so their longer side is the same length and centered on their bounding boxes. A part whose own modifier holds `!` isn't centered.

- **Side by side or stacked** (`lr`, `ud`): the box is split between the parts along that direction, with a gap of 15% of the box. By default the split gives each part a share in proportion to its shape, so that both fill the box's full height (`lr`) or width (`ud`). A narrow part beside a wide one gets a narrow share.
- **Outside and inside** (`oi`, the corners and sides, `over`): the first part fills the box. The second is scaled to half the box, centered (`oi`) or moved until it touches the named side or corner. `over` keeps it at full size.

##### Numbers

| Struct | Glyph |
|---|---|
| `exp` | a number glyph, built from the word itself |

The plain digits are basic `s` glyphs. Number words of the form `<digit>e<place>` use the `exp` struct to indicate they are dynamically generated according to the number drawing rules and based on the basic digit glyph. The components are ignored.

Adding `exp` words has no visible effect. The game already has an `exp` glyph for every digit from 1 to 6 at places 1 to 5 and -1 to -5, so a new one would be at place 6 or more, or -6 or less. Page text never shows those: the game writes a number at place 4 or more, or -4 or less, in scientific form, as digits, the `E` glyph, and an exponent, instead of drawing its glyph.

##### Modifiers

The `modifier` field is a string of characters, each setting one option. Most apply to two-part structures.

| Modifier | Applies to | Effect | Game words using it |
|---|---|---|---|
| `=` | `lr`, `ud` | Split the box evenly, each part filling its half. | 195 |
| `l` | `lr`, `ud` | Give the first part 65% of the box. | 24 |
| `r` | `lr`, `ud` | Give the first part 35% of the box. | 14 |
| `s` | `lr`, `ud` | Widen the gap to 30%. | 3 |
| `z` | `lr`, `ud` | Narrow the gap to 10%. | 2 |
| `n` | `lr`, `ud` | Narrow the gap to 5%. | 3 |
| `+` | `o*` except `over` | Make the second part 70% of the box. | 70 |
| `-` | `o*` except `over` | Make the second part 40% of the box. | 9 |
| `x` | `o*` except `over` | Make the second part 25% of the box. | 7 |
| `x` | all two-component structs | Move the second part down by 5% of the box. | 7 |
| `!` | basic glyphs, components of two-part compounds | Don't center the glyph. | 3 |

#### Glyph sprites

The game draws each stock glyph from a sprite baked from the word's strokes when the game was built. After the game loads, the plugin draws new sprites for:
- words the packs added;
- words whose structure code, components, or modifier the packs changed;
- words built from either of those, at any depth.

A `solid` word isn't drawn. It's shown with the game's own sprite for its one `solid` word, a filled, half-transparent cell. See [Basic glyphs](#basic-glyphs).

A word with a stroke list that produces no drawing will use its original sprite if available, or the stock fallback sprite otherwise.

A number word like `5e2` is drawn from its digit's glyph by name, not through its components. A change to a digit's glyph doesn't redraw its number words, which keep their stock sprites.

## Patches

| Method | Patch |
|---|---|
| `MV.Init` | Prefix: dumps the game's text files when `DumpGameText` is on, then applies content packs to `Global.textfiles` before the game parses them. |
| `Draw.DrawACToSlot` | Postfix: shows a drawn sprite in the slot, with its material, or puts the game's material back in a slot that showed one. |

## Configuration

`BepInEx/config/sparr.messagefromaliens.contentpacks.cfg`:

| Key | Default | Effect |
|---|---|---|
| `Content.LoadContentPacks` | `true` | Load content packs from `BepInEx/content/`. Takes effect at the next launch. |
| `Content.EnforcePatchContext` | `false` | Place each patch hunk only where all its context lines match. When off, a hunk that changes lines is placed by its removed lines alone. Takes effect at the next launch. |
| `Content.DumpGameText` | `false` | Write the stock game text files to `BepInEx/content/_dump/TxtData/` at startup. |
| `Packs.<pack>` | `true` | Load the content pack in `BepInEx/content/<pack>/`. Added for each pack when it's found. Takes effect at the next launch. |

Each setting is tagged with its name in glyphs, and the mod names itself in glyphs, so Mod Settings can show them in the game.

## Requirements

- BepInEx 5.4.23.6 or later, `BepInEx_win_x64` build, unzipped into the game folder. Tested with Unity 6000.6.8f1.
- On Linux under Proton, set the Steam launch options to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
- .NET SDK 8 or later to build.

## Building

```sh
dotnet build -c Release
```

The build copies `ContentPacks.dll` to `<game>/BepInEx/plugins/ContentPacks/`, and the example pack from [`content/_circle-length/`](content/_circle-length/) to `<game>/BepInEx/content/_circle-length/`. Disable the copy with `-p:DeployToGame=false`.

The build looks for the game in `<SteamLibraryDir>/steamapps/common/Message From Aliens`. `SteamLibraryDir` comes from the first of these that is set:

1. `-p:SteamLibraryDir=/path/to/SteamLibrary` on the command line.
2. A `SteamLibraryDir` environment variable.
3. `GamePaths.props`, a copy of [`GamePaths.props.example`](GamePaths.props.example). It is ignored by git.
4. Steam's default install location: `~/.local/share/Steam` on Linux, `C:\Program Files (x86)\Steam` on Windows.

For a game folder outside a Steam library, set `GameDir` to the game folder the same ways.
