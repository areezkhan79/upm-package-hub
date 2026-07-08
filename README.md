# Package Hub

A Unity Editor dashboard for browsing and bulk-installing packages from one or
more personal/team package registries, instead of pasting git URLs into the
built-in Package Manager one at a time.

This tool is **not tied to any specific registry or GitHub account** — on
first use you point it at whichever `registry.json` URL(s) you want, and it
works from there. See [areezkhan79/upm-registry](https://github.com/areezkhan79/upm-registry)
for a working example of what a registry looks like, or the "New Registry"
wizard inside the tool (coming in a later version) to create your own without
touching git directly.

## Install (Unity Package Manager)

Add to `Packages/manifest.json`, pinned to a released tag from this repo's
[releases](https://github.com/areezkhan79/upm-package-hub/tags):

```json
"com.areezkhan79.packagehub": "https://github.com/areezkhan79/upm-package-hub.git#v0.2.0"
```

## Usage

1. Open **Window > Package Hub**.
2. On first run, expand **Settings** and add one or more registry URLs (a raw
   URL pointing at a `registry.json` file — e.g. a `raw.githubusercontent.com`
   link).
3. The tool fetches every configured registry, merges their package lists,
   and shows each one with a checkbox and a version dropdown (versions are
   fetched live from that package's git tags — never stale, no need to update
   the registry when you cut a release).
4. Packages already present in the current project's `manifest.json` are
   pre-checked, showing their currently installed version.
5. Check/uncheck what you want, pick versions, click **Apply to Project** —
   this calls Unity's own `UnityEditor.PackageManager.Client.AddAndRemove`
   under the hood, the same API the built-in Package Manager UI uses.

Registry settings are stored per-project at `ProjectSettings/PackageHubSettings.asset`,
so if that file is committed to your game project's own repo, the whole team
gets the same registries configured automatically.

## Adding a package to a registry

See [upm-registry's README](https://github.com/areezkhan79/upm-registry) for
the registry.json schema.
