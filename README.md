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

A `.patch` file can also have other parts in its name, as in `<name>.<anything>.patch`. A `.csv` file for any other name is skipped with an error, and any other file with a warning. A name that differs from one of the game's files only in letter case is for that file. A pack without a `TxtData` folder loads nothing, with a warning.

Every replacement in every pack applies first, then every patch, then every merge. So a patch or merge sees the files as all packs' replacements left them.

Within each of those steps, packs apply in order of their folder (pack) names, and later packs override earlier ones. Within a pack, files apply in name order, except that `newalien.csv` comes before the pack's other `newalien.foo.csv` files.

Folders whose names start with `_` are skipped. This includes the `_dump` folder created by [`DumpGameText`](#configuration).

Each other pack gets a setting to turn it off. In Mod Settings, Content Packs' page has a line that opens a page of these settings, in loading order. A pack whose folder name is game words separated by hyphens, like `circle-length`, is labeled with those glyphs. Any other pack is labeled with its place in loading order, as a number. Packs are read as the game starts, so a change takes effect at the next launch. The settings are stored in the `Packs` section of the config file, one per folder name.

Problems are logged with the pack, file, and line, and loading continues without the part that failed. After all replacements and patches, each changed text file is read the way the game reads it, and a file the game couldn't read, which would stop the game while it loads, is skipped: a pack's new file is left out, and a changed game file goes back to the game's own text.

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
- Each following line, up to the next `#`, is one sentence: word IDs separated by spaces, drawn as glyphs. Long lines wrap automatically in-game; don't wrap them in the file. A blank line inside a page counts as an empty sentence; blank lines that end a page, and a final line break, are removed.
- Tokens starting with `(` or `[` aren't drawn. Number tokens like `5e2` are drawn as numbers. See [Number tokens](#number-tokens). A number can carry a unit. See [Units](#units).

#### Number tokens

A token that matches one of these forms is drawn as a base-7 number. Integers, decimals, and the `e` and `E` forms can start with `-`, which adds the negative glyph.

| Token | Value | Drawn as | Labeled |
|---|---|---|---|
| `48` | the integer | its digits, or scientific form from 343 up or -343 down | yes |
| `1.5` | the decimal | its digits to two base-7 places, rounded, or scientific form from 2401 up or when negative | yes |
| `<d>e<k>` | d * 7^k | the glyph of the single digit `d` at place `k`, or scientific form when `k` is 4 or more, or -4 or less | yes |
| `<d>x<k>` | d * 7^k | like `<d>e<k>` | no, if the page has a `(` token |
| `<d>A<k>` | d * 7^k | the glyph of the single digit `d` at place `k` | no, if the page has a `(` token |
| `<m>E<k>` | m * 7^k | scientific form, two significant digits | yes |
| `<m>F<k>` | m * 7^k | its digits to two base-7 places | yes |
| `<m>D<k>` | m * 10^k | scientific form, two significant digits | yes |
| `<m>DS<k>` | m * 10^k | scientific form, three significant digits | yes |
| `<a>div<b>` | a / b | its exact digits, with a repeat glyph before the repeating digits | yes, as `a/b` |
| `<n>N` | the integer | its digits, never scientific form | yes |
| `Infinity` | infinity | the infinity glyph | no |

Scientific form is the digits, rounded, then the `E` glyph and the exponent. `<d>` is one digit from 1 to 6. `<m>`, `<a>`, `<b>`, `<n>`, and `<k>` are written in decimal.

A labeled number's last glyph gets the value as a label suffix, as in `6 (48)`. An `e` token at a negative place is labeled as a fraction, as in `(3/7)`. The suffix appears only when the game's value conversion setting is on and the player has won 11 quizzes in chapter 1.

Some forms don't work:

- Some tokens stop the game while it loads the text, before the menus appear: `<m>ee<k>`, a line that starts with a decimal whose digits round away, such as `0.01`, a line that starts with `1F-3` or `5div5`, `1div0`, and integers past 19 digits. A huge exponent, such as in `5E400` or `1D400`, makes the game loop forever instead. Content Packs skips a file with any of these. Elsewhere in a line, `0.01` loads but draws no digits.
- `<m>es<k>`, `<a>/<b>`, and `<d>e<k>` with more than one digit in `<d>` are each drawn as one unknown word.

A line containing `(` anywhere skips number parsing, so every token in it is read as a word ID.

#### Units

A number token can end with a unit tag: `H` or `A` and then a unit code, with no space, as in `5Hm` or `3Acel`. The game draws the number, then the unit's glyph.

- `H` means the number is in human units. The game converts it to alien units and draws the converted value. Only integers, decimals, and the `D`, `DS`, `E`, and `F` forms are converted. Other number forms, like `5e1`, ignore `H` and are read as alien units, as with `A`. A `div` fraction loses its divisor when it has a unit tag: `6div2Hm` is read as `6Hm`.
- `A` means the number is already in alien units, and is drawn as written. Integers, decimals, and the `E`, `F`, `D`, and `DS` forms work. Other forms get a wrong label: `1div3Am` is labeled as 1, and `5A2Am` and `InfinityAm` as 0.

The unit's last glyph gets a label suffix with the value in human units and the unit's symbol. For example, `3Am` draws 3 and the length glyph, which gets the suffix `4.79 m`. The suffix appears only when the game's value conversion setting is on and the player has won enough quizzes of the chapter the game counts for the unit's code: five of one chapter's quizzes for most codes, two of a second chapter's for six codes, and three of a third chapter's for ten codes. Pages a pack titles like that chapter's quizzes count too. See [Count gates](#count-gates).

Some of the codes:

| Code | Quantity | Symbol | 1 alien unit |
|---|---|---|---|
| `m` | length | m | 1.596 m |
| `m2` | area | m² | 2.547 m² |
| `m3` | volume | m³ | 4.065 m³ |
| `kg` | mass | kg | 11.85 kg |
| `s` | time | s | 0.64 s |
| `mps` | speed | m/s | 2.494 m/s |
| `cel` | temperature | °C | 2.041 °C |

The game has about 40 codes in all. Their conversions are in `Global.HumanUnitToAlienUnit` and their glyphs and symbols in `Global.StringToListString`. Some don't work:

- Kelvin (`k`) is converted by multiplying instead of adding an offset, so both its drawn value and its label are wrong. `0Hk` draws 0, and `3Ak` is labeled `0.02 K`.
- A few codes have no symbol of their own. Their label shows the code instead, and they draw a placeholder glyph, or for three of them, the unrelated word whose ID is the code.

Test any code not in the table before using it.

Content packs can't add new units.

#### Quizzes

A quiz is a page with answer slots:

- A line starting with `@` lists the glyphs the quiz offers to fill the slots.
- A line starting with `%` gives the correct answers, the glyphs that fill the slots in order. Multiple `%` lines are alternate solutions to the whole quiz. Each must fill the same number of slots as the first.
- `?` in a line is an answer slot.

Every quiz title in the game has the form `<words> quiz <n>`: here and below, `quiz` stands for the game's own word for quiz, the word before the number in every quiz title in the game text that [`DumpGameText`](#configuration) writes. `<n>` is the quiz's number, and `<words>` names its chapter. A quiz's title must contain `quiz` after a space: the game treats only such pages as quizzes, so on any other page a click on an answer slot looks the glyph up instead of filling the slot, and a title that starts with `quiz` stops the game when the quiz is won. Count gates count pages by that title pattern. See [Count gates](#count-gates).

#### Menus

A menu's lines open the pages they name only where the game treats the page as a menu, which it decides by the page's depth: a chapter list, and each page linked from one. Each of a menu's lines links to the page whose title is that line's words, leaving out tokens starting with `[`. A menu page must also contain a `[` somewhere in its text, or the game can't find it as a quiz's parent, and stops on every redraw of a solved quiz it links. An empty `[]` token supplies one without affecting any line, but only as the first token of the page's first line, followed by words: as the first token of another line it hides that line, and alone on a line it stops the page from drawing. A page linked from a menu deeper down shows its lines as plain text, and is won as soon as it is shown.

A new page can only be reached through a menu. To show a page, patch a line naming it into an existing menu. The example pack adds its pages to chapter 1's menu.

#### Gates

A gate is a token like `[word-word-word]`: a page's title, with its words joined by `-`.

A line starting with a gate, as in `[<gate>] <title>`, shows only once that page has been won. This works in menus and ordinary pages alike. The game checks only a line's first token to draw the line, so a gate anywhere else in the line, including a second gate after the first, is not drawn and doesn't hide it, but it does keep the page the line opens out of its parent menus' chain until that gate passes. The first line of a page's text is never hidden this way. Two of the game's menus gate every line, so they always show their first line. Content Packs keeps this behavior. To gate a line a pack adds, don't make it the first line of its page.

A page is won:

- for a quiz, when its slots are all filled correctly;
- for an ordinary page reached through the chapter menus, when its last screen of text is shown;
- for a menu, when every page it links to has been won.

A menu draws the lines of won pages dimmed.

##### Count gates

The game reads a gate as a count when it contains `quiz-` followed by a number. A count gate `[<words>-quiz-<n>]` passes once at least `<n>` won pages have titles starting with `<words> quiz`. It doesn't matter which pages they are, what kind of page, or in what order they were won.

Every quiz title in the game has the form `<words> quiz <n>`, so in the game a count gate counts a chapter's quizzes: `[<chaptername>-quiz-5]` means any five of that chapter's quizzes, not its fifth.

To use count gates in a pack:

- Give the pages to be counted titles starting with the same words followed by `quiz`, as in `<words> quiz 1`, `<words> quiz 2`, etc. They're usually quizzes, but an ordinary page with such a title counts once it's read.
- Pick `<words>` that no other title starts with followed by `quiz`, or those pages count too.
- A gate can't require one particular page whose title has `quiz` and a number after it, since naming that page makes the gate a count.

#### Chapters

A chapter is a menu of lessons and quizzes, linked from one of the game's two chapter lists, as its lines work only at that depth. Gate the line that links to it to control when it unlocks. For example, the game opens its second chapter with a count gate, after any five of the first chapter's quizzes. Gate its own lines on its quizzes to open its pages as the player progresses. Its menu is won when all its pages are, which can gate later content.

New chapters can't award achievements.

### Text patches

A `.patch` file is a unified diff of one file, as made by `diff -u` or `git diff`. Its file names are not used. Hunks apply in order, each to the result of the ones before it. If any hunk fails, the whole patch is skipped. Hunk line numbers are never enforced, and context lines are only enforced if [`EnforcePatchContext`](#configuration) is enabled. That is a player's setting, which applies to every pack's patches; a pack can't turn it on for its own. When it's off, a hunk that removes lines is placed by its removed lines, and a hunk that only adds lines is placed where all its context lines match, or else where the most of them match. A hunk with no context or removed lines, as `diff -U0` writes an insertion, is placed by its line number alone. When a hunk matches several places, a place where its context matches too wins first, then the one where the most context lines match, then the one nearest its header's line number.

The game's files repeat lines across pages, so a hunk is kept to the page it changes. When a hunk's context and removed lines match the game's own file at its header's line number, its page is the one it changes there, and it is placed within that page in the current file by the rules above. If it matches nowhere on that page, as when another pack changed its lines, it is placed anywhere in the file, with a warning in the log naming both pages. If no page has that title anymore, it is also placed anywhere, with a warning. A hunk that doesn't match the game's file at its line number, as in a patch made against another pack's output, and a hunk for a file a pack added or replaced, are kept to a page only when their context holds that page's `#` title line, and fail when they match only outside it.

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
- A later line that names columns as the first line does starts a new set of columns for the rows after it.
- A row whose word exists overwrites the columns the file names and keeps the rest. A row with a new word adds it, with the other columns empty. A cell left empty sets that field to empty. To leave a field alone, leave its column out of the file. If different words have different column requirements, put them in separate files.
- `!<word>` in the word column removes that row. A dictionary category's word, or a word that other rows use as a component, as a basic glyph's redirect, or as a number's digit, can't be removed. Removing a word that the game's text still uses is logged as a warning.
- Lines starting with `#` and blank lines are skipped. No cell can contain a comma.
- The game removes every `:` from a row before reading it, so that fields like `1e1` can be stored as `:1e1` to prevent spreadsheet programs from mangling them. A `:` found not at the start of a word is logged as a warning.

The `struct`, `component1`, `component2`, and `modifier` columns set the word's glyph. See [Glyph structure](#glyph-structure).

After all merges, each added or changed row is checked. A row fails when:
- it's a new word that ends in `e0` or contains `;`, `<`, or `>`, which the game changes wherever its text names the word;
- it has no word type, since the game stops loading on one;
- its meaning or word type has a token the game can't read;
- it's a dictionary category's word, and it's renamed or marked as no word;
- a component that must be a word names no word;
- a basic glyph's strokes hold a number the game can't read, such as one with non-ASCII digits or too large for a float;
- its glyph is built from itself, through its components or a number word's digit, since the game stops loading on one;
- it's a dictionary category's word built from one part, and that part falls under the same category, as through a basic glyph whose redirect names the word, since the game stops loading on one;
- a basic glyph's redirect names no word, since the game stops loading on one;
- it leaves a word in no dictionary category, as a compound built only from glyphs marked as no word does, since the dictionary page fails to draw while that word is shown;
- it leaves one of the game's dictionary categories with no words, since the dictionary page and the note editor then fail to open;
- it's a number word whose name isn't a digit, `e`, and a place, like `5e2`, or whose digit names no word, since the game stops loading on one.

When a row fails, the change that made it fail is undone: the row keeps its definition from before that line, and a new row that fails is dropped. When a category is lost from one of the game's own rows, the changes to the rows its categories come from are undone instead. These checks are applied repeatedly, because one failed row could cause other rows to fail. Rows changed by a replaced or patched `newalien` file are checked the same way, against the game's own rows, before any merge applies. The table is read as the game reads it: header names must match exactly, rows with an empty word are skipped, and a row with fewer cells than the header fails. When a replaced or patched table has several rows for one word, the game uses the last, in the place of the first. Content Packs does the same, and logs a warning for each earlier row it drops, so merges and `!<word>` act on the row the game uses.

New words, and words whose shape changed, get new sprites. See [Glyph sprites](#glyph-sprites).

#### Glyph structure

A word's glyph comes from four columns of its row. `struct` is a code that says how the glyph is built. `component1` and `component2` hold its strokes or name its parts. `modifier` adjusts the layout. Parts are other words, so glyphs nest: the game builds each part's glyph first, then arranges the parts. The game draws a word whose `struct` is empty or a code not listed below as a basic glyph, and Content Packs logs a warning for it.

##### Basic glyphs

| Struct | Components |
|---|---|
| `s` | `component1` is the glyph's strokes. `component2` is empty. |
| `solid` | same as `s` inside a compound glyph, otherwise ignored |

Strokes are paths separated by `;`. Each path is a list of numbers read as x y pairs and drawn as straight lines from point to point, so a closed shape repeats its first point. The game finds the numbers with the regex `[+-]?\d+(\.\d+)?` and ignores any other text, so `1e2` reads as 1 and 2. Content Packs logs a warning for text other than numbers and spaces. The game also ignores a path's last number when the count is odd, and Content Packs logs a warning for that too. x grows to the right and y grows upward. The game's glyphs keep to a half-unit grid, mostly within -3 to 3. The glyph is centered on its bounding box. Drawn alone, it's scaled down to fit its box if it's larger, as every glyph on that grid is. Inside other glyphs, parts are always rescaled, so only proportions matter there. An empty `component1` draws the game's default, a single diagonal stroke. An empty path, as a trailing `;` makes, draws nothing. Every glyph is drawn with the same line width, whatever its size.

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

Adding `exp` words rarely has a visible effect. The game already has an `exp` glyph for every digit from 1 to 6 at places 1 to 5 and -1 to -5, so a new one would be at place 6 or more, or -6 or less. Page text shows those only through the `A` form, as in `5A6`, or an `F` form that reaches that place: other forms write a number at place 4 or more, or -4 or less, in scientific form, as digits, the `E` glyph, and an exponent, instead of drawing its glyph.

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
| `+` | `o*` except `over` | Make the second part 70% of the box. | 69 |
| `-` | `o*` except `over` | Make the second part 40% of the box. | 9 |
| `x` | `o*` except `over` | Make the second part 25% of the box. | 7 |
| `x` | all two-component structs | Move the second part down by 5% of the box. | 7 |
| `!` | basic glyphs, components of two-part compounds | Don't center the glyph. | 3 |

#### Glyph sprites

The game draws each stock glyph from a sprite baked from the word's strokes when the game was built. After the game loads, the plugin draws new sprites for:
- words the packs added;
- words whose structure code, components, or modifier the packs changed;
- words built from either of those, at any depth, including number words built from a digit.

A `solid` word isn't drawn. It's shown with the game's own sprite for its one `solid` word, a filled, half-transparent cell. See [Basic glyphs](#basic-glyphs).

A word with a stroke list that produces no drawing will use its original sprite if available, or the stock fallback sprite otherwise.

A number word like `5e2` is drawn from its digit's glyph by name, not through its components, and gets a new sprite when its digit's glyph changes.

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
| `Content.EnforcePatchContext` | `false` | Place each patch hunk only where all its context lines match. When off, a hunk that removes lines is placed by its removed lines alone, and one that only adds lines is placed where the most of its context lines match. Takes effect at the next launch. |
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

With `-p:DeployToGame=true`, the build also copies `ContentPacks.dll` to `<game>/BepInEx/plugins/ContentPacks/`, and the example pack from [`content/_circle-length/`](content/_circle-length/) to `<game>/BepInEx/content/_circle-length/`.

The build looks for the game in `<SteamLibraryDir>/steamapps/common/Message From Aliens`. `SteamLibraryDir` comes from the first of these that is set:

1. `-p:SteamLibraryDir=/path/to/SteamLibrary` on the command line.
2. A `SteamLibraryDir` environment variable.
3. `GamePaths.props`, a copy of [`GamePaths.props.example`](GamePaths.props.example). It is ignored by git.
4. Steam's default install location: `~/.local/share/Steam` on Linux, `C:\Program Files (x86)\Steam` on Windows.

For a game folder outside a Steam library, set `GameDir` to the game folder the same ways.
