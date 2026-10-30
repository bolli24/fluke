// Shared linter interface: load() resolves to a checker whose check(main, files) returns diagnostics
// with UTF-16 offsets into files[path]. See linter.d.ts.
import { dotnet } from "./_framework/dotnet.js";

let checker; // The .NET runtime can only be created once per page
export function load() {
	checker ??= (async () => {
		const { getAssemblyExports, getConfig } = await dotnet.create();
		const exports = await getAssemblyExports(getConfig().mainAssemblyName);
		return {
			check(main, files) {
				const source = files[main];
				if (typeof source !== "string") throw new Error(`main file '${main}' is not in files`);
				// Fluke has no imports yet, so only the main file is checked
				return JSON.parse(exports.fluke.Wasm.Linter.Check(source))
					.map(d => ({ path: main, ...d }));
			},
		};
	})();
	return checker;
}