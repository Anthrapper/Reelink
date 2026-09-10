# AGENTS.md

## Project overview

Reelink is a Jellyfin 12 plugin targeting .NET 10. It groups duplicate series by provider identity and combines duplicate movies and episodes as selectable versions without deleting media or library records.

## Important paths

- `Jellyfin.Plugin.Reelink/` — plugin source.
- `Jellyfin.Plugin.Reelink/Configuration/configPage.html` — embedded admin UI.
- `build.yaml` — package metadata and the authoritative release version/changelog.
- `.github/workflows/package.yml` — build, validation, release, and repository-manifest publication.
- `repo/manifest.json` — stable Jellyfin repository catalog. Generated ZIPs do not belong in Git.

## Build and validation

The project requires the .NET 10 SDK.

```sh
dotnet build Jellyfin.Plugin.Reelink.slnx -c Release
```

Before finishing changes, run the most relevant checks available. For admin-page JavaScript changes, extract the embedded `<script>` and run `node --check`. For workflow changes, parse the YAML, run `git diff --check`, and validate changed shell fragments with `bash -n` when possible.

## Release process

1. Change `version` and `changelog` in `build.yaml`. Versions use four components, for example `0.0.2.0`.
2. Commit and push the release changes.
3. Create and push the exact matching tag, prefixed with `v`, for example `v0.0.2.0`.
4. GitHub Actions builds the package, publishes `reelink_0.0.2.0.zip` as a GitHub Release asset, and updates `repo/manifest.json` on `main`.

Do not commit package ZIPs under `repo/`; only `repo/manifest.json` is versioned. Do not rebuild or republish a released version after changing code—increment the version instead. The stable catalog URL is:

```text
https://raw.githubusercontent.com/Anthrapper/Reelink/main/repo/manifest.json
```

## Coding conventions

- Nullable reference types and warnings-as-errors are enabled.
- Preserve cancellation handling and structured logging in long-running operations.
- Preview selections must be revalidated server-side before modifying library data.
- Never delete user media files or library records.
- The admin UI must accept both camelCase and PascalCase API property names because Jellyfin serialization behavior can vary.
