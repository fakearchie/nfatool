# UI languages

Each `<code>.json` in this folder is one UI language. English (`en.json`) is the only one so far.

The app loads languages from three places, later ones overriding earlier ones with the same `code`:

1. The language packs built into the program.
2. `Languages\*.json` next to the program (this folder, shipped with each release).
3. `%AppData%\nfa.pub Loader\Languages\*.json`, for trying a translation without rebuilding.

## Adding a language

1. Copy `en.json` to `<code>.json`, using a BCP-47 code such as `de`, `fr` or `pt-BR`.
2. In the header, set `code` to that code, `name` to the language's name in its own language (for example `Deutsch`), and keep `fallback` as `"en"`.
3. Translate every value under `strings`. Keep the keys, and keep placeholders such as `{0}` and `{1}` in the same order.
4. Put the file in this folder or in `%AppData%\nfa.pub Loader\Languages\`, then restart.

A key you have not translated yet falls back to English, so a translation can be done a bit at a time.
