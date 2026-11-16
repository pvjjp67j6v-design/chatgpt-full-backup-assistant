# Third-party components

## ChatGPT Exporter 2.38.3

- Source: https://github.com/pionxzh/chatgpt-exporter
- Copyright (c) 2022-Present Pionxzh; MIT, see `程序源码/LICENSE-exporter.txt`.
- Original bundle: `程序源码/upstream-reference.user.js`.
- Adaptation: `build.ps1` disables the original UI entry, inserts `bridge.js`, and scopes the fetch wrapper to the copied engine. The renderer and conversation processing functions are reused.

## JSZip 3.9.1

- Source: https://github.com/Stuk/jszip
- Copyright Stuart Knightley and contributors; used under MIT, see `程序源码/LICENSE-jszip.txt` (including bundled dependencies).
- Vendored file: `程序源码/jszip.min.js`.

`程序源码/bridge.user.js` is generated from these files and the local bridge. Original license notices are retained. The root MIT license applies to this project's adaptation, without replacing the third-party notices.
