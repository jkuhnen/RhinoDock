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

The policy must match the GitHub workflow filename exactly. The job requests `id-token: write` and invokes `VsixPublisher.exe publish -oidc`. Do not create or store a Marketplace PAT.

## Test without republishing

After merging the workflow, open GitHub **Actions > Publish VSIX to Visual Studio Marketplace > Run workflow**, and use tag `v0.5.5`. The manual run downloads and validates the existing release VSIX, but **does not publish** to the Marketplace.

## Publishing the next version

1. Bump the version consistently in the VSIX manifest, package registration, tests and build artifact filename.
2. Merge the changes to `main` after the build and tests succeed.
3. Download the completed `main` build artifact and extract `RhinoDock-vX.Y.Z.vsix`.
4. Create and publish a GitHub **stable** release tagged `vX.Y.Z`, targeting `main`, with the matching VSIX file attached.
5. The publishing workflow validates tag, VSIX version, unchanged VSIX identity and publisher, then uploads to the Marketplace using OIDC.

Do not manually republish an existing release solely to test OIDC; publishing with a matching version can overwrite the existing Marketplace package.

## References

- [Microsoft: Trusted Publishing for Visual Studio Marketplace](https://developer.microsoft.com/blog/trusted-publishing-visual-studio-marketplace/)
- [Microsoft: Publish a Visual Studio extension via command line](https://learn.microsoft.com/en-us/visualstudio/extensibility/walkthrough-publishing-a-visual-studio-extension-via-command-line)

The first real OIDC token exchange is only verified when a **new version** is published. The workflow requires a Windows runner with a current `VsixPublisher.exe` supporting `-oidc`.
