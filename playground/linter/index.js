// Playground linter module, see "Linter" in CONTRACT.md in the playground-runner repo.
// Wraps fluke.Wasm, whose published _framework the Dockerfile puts next to this file.

import { dotnet } from "./_framework/dotnet.js";

export default async function () {
	const { getAssemblyExports, getConfig } = await dotnet.create();
	const exports = await getAssemblyExports(getConfig().mainAssemblyName);

	return {
		check(files, entry) {
			try {
				// Fluke has no imports, so only the entry file is checked
				const source = files[entry];
				if (typeof source !== "string") throw new Error(`file not found: ${entry}`);
				return JSON.parse(exports.fluke.Wasm.Linter.Check(source)).map((d) => ({
					file: entry,
					from: d.from,
					to: d.to,
					severity: d.severity,
					message: d.message,
				}));
			} catch (e) {
				// The contract says check never throws: report errors as a diagnostic instead
				return [{ file: entry, from: 0, to: 0, severity: "error", message: `internal linter error: ${e.message ?? e}` }];
			}
		},
	};
}
