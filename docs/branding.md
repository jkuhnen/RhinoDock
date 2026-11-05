# Rhino Dock branding and rename

**Rhino Dock** (formerly Rhino VS Bridge) connects Microsoft Visual Studio's Python development tools to Rhino 8 through RhinoCode, with script execution, IntelliSense, output, cancellation and error navigation.

- Public product name: **Rhino Dock**
- Descriptor: *Python Development for Rhino 8 in Visual Studio*
- Image mark: `assets/branding/rhino-dock-icon.svg` (the original trion circular mark, vectorized from the provided reference)
- Wordmark: none; render **Rhino Dock** as regular text.

## Compatibility and release history

Version v0.4.0 was released under the old **Rhino VS Bridge** name. Its VSIX, release notes, download links and SHA-256 checksums remain unchanged. Subsequent builds use Rhino Dock as the displayed product name and version 0.4.1.

The assembly name, C# namespace, project paths, command identifiers, VSIX extension Identity ID, menu resource, package GUIDs, and `.rhino-vs-bridge` folder remain unchanged to preserve upgrade compatibility, automation, settings and existing user projects.

After CI and local installation testing, publish a new Rhino Dock release rather than replacing the previous release asset. The GitHub repository is named `RhinoDock`. Local remotes pointing to the old path should be updated.

The project is independently licensed under MIT, with the original RhinoPythonForVS acknowledged separately.