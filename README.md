# Package Hub

A Unity Editor dashboard for browsing and bulk-installing packages from one or
more personal/team package registries, instead of pasting git URLs into the
built-in Package Manager one at a time.

This tool is **not tied to any specific registry or GitHub account** — on
first use you point it at whichever `registry.json` URL(s) you want, or use
the built-in **Create New Registry** wizard to bootstrap your own from
scratch without touching git directly. See
[areezkhan79/upm-registry](https://github.com/areezkhan79/upm-registry) for a
working example of what a registry looks like.

## Install (Unity Package Manager)

Add to `Packages/manifest.json`, pinned to a released tag from this repo's
[releases](https://github.com/areezkhan79/upm-package-hub/tags):

```json
"com.areezkhan79.packagehub": "https://github.com/areezkhan79/upm-package-hub.git#v0.9.0"
```

## Usage

1. Open **Window > Package Hub**.
2. On first run, expand **Settings** and add one or more registry URLs (a raw
   URL pointing at a `registry.json` file — e.g. a `raw.githubusercontent.com`
   link).
3. The tool fetches every configured registry, merges their package lists,
   and shows each one grouped by category, with a checkbox and a version
   dropdown (versions are fetched live from that package's git tags and
   sorted newest-first — never stale, no need to update the registry when you
   cut a release).
4. Packages already present in the current project's `manifest.json` are
   pre-checked, showing their currently installed version.
5. Use **Search** to filter, **Select All** / **Select None** for bulk
   selection.
6. Check/uncheck what you want, pick versions, click **Apply to Project** —
   this shows a preview of exactly what will change (adds/removes/upgrades)
   before anything happens. Click **Confirm** to actually apply it, which
   calls Unity's own `UnityEditor.PackageManager.Client.AddAndRemove` under
   the hood, the same API the built-in Package Manager UI uses.

Registry settings are stored per-project at `ProjectSettings/PackageHubSettings.asset`,
so if that file is committed to your game project's own repo, the whole team
gets the same registries configured automatically.

## Presets

Use **Save Preset...** to export your current checked packages + chosen
versions as a portable `.json` file (via the native save dialog — put it
anywhere, share it, commit it to a separate repo). **Load Preset...** reads
one back and replaces the current selections to match it exactly (packages
not in the preset get unchecked). This is the fast way to set up a new
project: save a "starter kit" preset once, then load + apply it in every new
project going forward.

## Dependencies

If a registry entry declares `"dependencies": ["com.other.package"]`, checking
that package auto-checks its dependencies too, and locks their checkbox
(showing "Required by: ...") so they can't be unchecked while still needed.

## GitHub token

Optional — expand **Settings** and paste a GitHub Personal Access Token to
raise the unauthenticated API rate limit (60 requests/hour, easy to hit with
many packages) and to enable browsing private repos. No scopes are needed for
public repos; use the `repo` scope for private ones. Stored via `EditorPrefs`
on your machine only, never written to any file that gets committed.

## Creating a new registry

Expand **Create New Registry**, fill in a repo name/description, choose
public or private, click **Create Registry**. This calls the GitHub API to
create a new repo, adds a starter `registry.json` to it (via GitHub's
Contents API — no local git needed), and adds it to your configured
registries automatically. Requires a GitHub token (see below) with
repo-creation rights.

## Creating a new package

Expand **Create New Package**, fill in the package name (reverse-domain,
e.g. `com.yourname.module`), display name, description, optional category,
pick a local parent folder, and which registry to publish into, then click
**Create Package**. This:

1. Scaffolds a minimal valid Unity package on disk (`package.json`, a
   `Runtime/` asmdef, and a placeholder script — all with the required
   `.meta` files already generated).
2. Runs `git init`/`add`/`commit` locally.
3. Creates a new GitHub repository via the GitHub API.
4. Pushes and tags it `v0.1.0`.
5. Appends an entry for it to your chosen registry's `registry.json` via
   GitHub's Contents API.

Requires both a GitHub token (see below) and `git` installed and on your
system `PATH`. This is the full publish loop this repo's own README
originally walked through by hand — automated end to end.

## Adding a package to a registry

See [upm-registry's README](https://github.com/areezkhan79/upm-registry) for
the registry.json schema, or use the **Create New Package** wizard above.
