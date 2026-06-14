# Code Style — ClaudeMon

## Formatting

- Standard C# / .NET conventions; `dotnet format` for whitespace/style fixes.
- 4-space indentation, file-scoped namespaces, `using` directives inside the namespace
  (as in existing files), implicit usings enabled.
- Nullable reference types are **enabled** — respect nullability; avoid `!` unless justified.

## Naming

- **Types / methods / properties:** PascalCase. **Locals / parameters:** camelCase.
- **Private fields:** `_camelCase`. **Constants:** PascalCase.
- One public type per file; file name matches the type.

## Conventions

- Prefer immutable `record` types with `init` setters for models/settings.
- Map JSON with explicit `[JsonPropertyName(...)]` attributes (see `AppSettings`).
- Keep UI thin; put logic in `Monitoring`/`Services`/`Configuration` so it's testable.
- Dispose native/GDI resources (`Bitmap`, `Graphics`, `Font`, `Icon` handles) — see
  `IconRenderer` for the `DestroyIcon` pattern when creating icons from bitmaps.
- Match the style of surrounding code; keep changes small and focused.

## Comments & docs

- Comment the *why*, not the *what*; existing code is lightly commented — match that density.
- No need for XML doc comments on everything; add them on non-obvious public/internal APIs.
