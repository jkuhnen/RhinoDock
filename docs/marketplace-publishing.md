# Visual Studio Marketplace release automation

The workflow `.github/workflows/publish-marketplace.yml` publishes **future stable GitHub releases** of Rhino Dock to the Visual Studio Marketplace, without a stored Personal Access Token (PAT).

## One-time Trusted Publishing setup

Go to [Visual Studio Marketplace publisher management](https://marketplace.visualstudio.com/manage/publishers), select publisher `jkuhnen`, then **Trusted Publishing > Create**.

| Field | Value |
| --- | --- |
| Policy name | Rhino Dock Releases |
| Repository owner | jkuhnen |
| Repository | RhinoDock |
| Workflow | publish-marketplace.yml |
| Environment | leave blank |

The policy must match the GitHub workflow filename exactly. The job requests `id-token: write`, exchanges the GitHub OIDC token through the Marketplace API (`POST /_apis/gallery/token?api-version=7.2-preview.1`), and uses the returned short-lived Marketplace credential with `VsixPublisher.exe publish`. No long-lived PAT is created or stored. This is a workaround for the `VsixPub0047` API-version error observed with Visual Studio 2026's built-in `VsixPublisher.exe -oidc`.

## Test without republishing

After merging the workflow, open GitHub **Actions > Publish VSIX to Visual Studio Marketplace > Run workflow**. For validation only, use the release tag and leave **publish** unchecked (default); nothing is uploaded. To recover from a previously failed automatic release publication, choose the existing release tag, set **publish** to `true`, and run. This intentionally republishes the same version; only use it when the Marketplace still has the previous version.

## Publishing the next version

1. Bump the version consistently in the VSIX manifest, package registration, tests and build artifact filename.
2. Merge the changes to `main` after the build and tests succeed.
3. Download the completed `main` build artifact and extract `RhinoDock-vX.Y.Z.vsix`.
4. Create and publish a GitHub **stable** release tagged `vX.Y.Z`, targeting `main`, with the matching VSIX file attached.
5. The publishing workflow validates tag, VSIX version, unchanged VSIX identity and publisher, then uploads to the Marketplace using OIDC.

Only enable manual **publish** when an existing release was not published to Marketplace due to a failed workflow; a matching version can overwrite an existing Marketplace package. Release events remain automatically published.

## References

- [Microsoft: Trusted Publishing for Visual Studio Marketplace](https://developer.microsoft.com/blog/trusted-publishing-visual-studio-marketplace/)
- [Microsoft: Publish a Visual Studio extension via command line](https://learn.microsoft.com/en-us/visualstudio/extensibility/walkthrough-publishing-a-visual-studio-extension-via-command-line)

The OIDC token exchange is verified by a real publication (either a new stable release or an explicitly opted-in retry). The workflow uses Microsoft's versioned Marketplace token endpoint, as implemented by [microsoft/vscode-vsce](https://github.com/microsoft/vscode-vsce/blob/main/src/oidc.ts), then passes the short-lived credential to the Windows VSIX publisher.
