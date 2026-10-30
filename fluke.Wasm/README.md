# fluke.Wasm

The fluke checker compiled to WebAssembly, for showing errors in a browser editor such as CodeMirror.

`linter.js` exposes `load()`, which resolves to a checker with `check(main, files)`. See `linter.d.ts` for the
diagnostic format. Fluke has no imports yet, so only `main` is checked.

## Demo

`index.html` lints a single file, `main.fl`, in CodeMirror. Requires the `wasm-tools` workload
(`dotnet workload install wasm-tools`). From the repository root:

```sh
dotnet publish fluke.Wasm -c Release
cd fluke.Wasm/bin/Release/net10.0/publish/wwwroot
python -m http.server 8123
```

Then open <http://127.0.0.1:8123/>. The page must be served over HTTP, since browsers don't load wasm from `file://`.
