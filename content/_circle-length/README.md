# Example content pack

A small pack that demonstrates the features of Content Packs. Its folder name starts with `_`, so Content Packs skips it. Content Packs installs it as `BepInEx/content/_circle-length/`. To try it, rename the folder to `circle-length`, without the `_`, restart the game, then open chapter 1. Its menu gets a new line, the octagon glyph, that opens a page. Once you've read that page to its end, a second line appears below it, the octagon beside the glyph for pi, opening a second page.

| File | Feature |
|---|---|
| [`TxtData/circumference.txt`](TxtData/circumference.txt) | A new message category, `circumference`, with two pages, `diameter` and `circumference`. |
| [`TxtData/math0.patch`](TxtData/math0.patch) | A patch to the chapter 1 menu that inserts lines linking to the two pages. The second line starts with the gate `[diameter]`, so it appears only once the `diameter` page has been read to its end. |
| [`TxtData/newalien.csv`](TxtData/newalien.csv) | Adds a basic glyph, a compound built from it, and a scratch word. |
| [`TxtData/newalien.remove.csv`](TxtData/newalien.remove.csv) | Removes the scratch word. |
| [`TxtData/newalien.update.csv`](TxtData/newalien.update.csv) | Changes the definitions of `circumference` and `pi`. |

The merge files apply in this order: `newalien.csv` first, then the others by name.

The new definitions use game word IDs, which might be spoilers if you haven't finished chapter 2 and side chapter 2.
