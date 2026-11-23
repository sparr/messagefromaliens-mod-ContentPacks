# Changelog

## Unreleased

## [0.2.0](https://github.com/sparr/messagefromaliens-mod-ContentPacks/releases/tag/v0.2.0) - 2026-11-23

- Pack text, word table rows, and glyph strokes are checked as the game reads them, accepting what the game accepts and refusing what would break it.
- Text patches are placed more reliably, each hunk within the page it changes.
- Word table merges keep every word in a category and refuse words built from themselves.
- Fixed long glyphs missing their last stroke, and number words not redrawing when their digit's glyph changes.
- Text dumping is separate from loading packs, and pack files match game files in any case.
- A pack whose name starts with `#` always loads.
- Documented number tokens, units, quizzes, menus, and the checks.
- Faster checks and glyph drawing.

## [0.1.0](https://github.com/sparr/messagefromaliens-mod-ContentPacks/releases/tag/v0.1.0) - 2026-11-16

- Initial release: content packs of replaced and new text files, text patches, word table merges, glyph sprites, and a setting for each pack, with an example pack.
