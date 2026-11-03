# Signing Windows releases

Windows releases are signed through [SignPath](https://about.signpath.io)'s free program for open-source projects, with a certificate from [SignPath Foundation](https://signpath.org). Smart App Control blocks unsigned apps it hasn't seen before, and each new version counts as unseen. A signature lets every version run.

The `sign` job in `.github/workflows/build.yml` runs on tag builds. It sends both exes to SignPath, waits up to an hour for an approver to confirm the request, checks that the signatures are valid, and then zips them. If the setup below is missing, the release still goes out, unsigned, with a warning on the run.

## Setup, once SignPath approves the project

1. **In SignPath**, open the Roamwise project. The Foundation usually creates the project and the trusted build system link to GitHub.
   - Under **Artifact configurations**, add one with the XML below and make it the default.
   - Under **Signing policies**, use the release-signing policy the Foundation set up, and note its slug, for example `release-signing`. Requests need manual approval, so you confirm each release in SignPath or by email.
   - Under your user profile, create an **API token**. Give that user (or a CI user) **Submitter** permission on the signing policy.
2. **In GitHub**, go to **Settings › Secrets and variables › Actions** and add:

   | Kind | Name | Value |
   | --- | --- | --- |
   | Secret | `SIGNPATH_API_TOKEN` | The API token |
   | Variable | `SIGNPATH_ORGANIZATION_ID` | Organization ID (SignPath › Organization settings) |
   | Variable | `SIGNPATH_PROJECT_SLUG` | Project slug, for example `roamwise` |
   | Variable | `SIGNPATH_POLICY_SLUG` | Signing policy slug, for example `release-signing` |

3. **Push a tag.** When the run reaches **Sign**, approve the request in SignPath. **Check signatures** prints each exe's signer.

### Artifact configuration

```xml
<?xml version="1.0" encoding="utf-8"?>
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <zip-file>
    <pe-file path="win-x64/Roamwise.exe" product-name="Roamwise">
      <authenticode-sign />
    </pe-file>
    <pe-file path="win-arm64/Roamwise.exe" product-name="Roamwise">
      <authenticode-sign />
    </pe-file>
  </zip-file>
</artifact-configuration>
```
