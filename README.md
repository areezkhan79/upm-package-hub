# Package Hub

A Unity Editor dashboard for browsing and bulk-installing packages from your
personal [upm-registry](https://github.com/areezkhan79/upm-registry), instead
of pasting git URLs into the built-in Package Manager one at a time.

## Install (Unity Package Manager)

Add to `Packages/manifest.json`:

```json
"com.areezkhan79.packagehub": "https://github.com/areezkhan79/upm-package-hub.git#v0.1.0"
```

## Usage

Open **Window > Package Hub**. It fetches `registry.json` from `upm-registry`,
lists every package with a checkbox and a version dropdown (populated live
from that repo's git tags), and shows which ones are already installed in the
current project. Check/uncheck what you want, pick versions, click
**Apply to Project** — it calls Unity's own
`UnityEditor.PackageManager.Client.AddAndRemove` under the hood, the same API
the built-in Package Manager UI uses.

## Adding a package to the registry

See [upm-registry's README](https://github.com/areezkhan79/upm-registry).
