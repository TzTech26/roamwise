# Signing Windows releases

Windows releases are signed with [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/overview) so Smart App Control and SmartScreen let Roamwise run. Smart App Control blocks unsigned apps it hasn't seen before, and every new version counts as unseen.

The `sign` job in `.github/workflows/build.yml` signs both exes on tag builds. If the setup below is missing, the release still goes out, unsigned, with a warning on the run.

## One-time setup (about 30 minutes, plus identity checks)

Individual developers must be in the United States or Canada. The Basic tier costs about $10 a month.

1. **Azure subscription.** Sign in at [portal.azure.com](https://portal.azure.com) and create a pay-as-you-go subscription if you don't have one. Under **Cost Management + Billing › Billing accounts**, the account type must be **Individual**, and the legal name and sold-to address must match your government ID.
2. **Register the provider.** Open **Subscriptions** and select your subscription. Under **Settings › Resource providers**, find **Microsoft.CodeSigning** and select **Register**.
3. **Create the signing account.** Search for **Artifact Signing Accounts** and select **Create**.
   - Resource group: new, for example `roamwise-signing`.
   - Account name: for example `roamwisesigning`. It must be 3–24 letters and digits and unique across Azure.
   - Region: for example **East US**. Note the endpoint for your region, such as `https://eus.codesigning.azure.net/`.
   - Pricing: **Basic**.
4. **Allow yourself to verify.** In the new account, open **Access control (IAM)** and select **Add role assignment**. Give your own user **Artifact Signing Identity Verifier**.
5. **Verify your identity.** Under **Objects › Identity validations**, choose **Individual**, then **New identity**, then **Public**, and pick your billing account. When the status shows **Action Required**, open the link. You verify with a photo ID through AU10TIX and Microsoft Authenticator on your phone. The status changes to **Completed** when it's done, usually within minutes, but it can take days if documents are requested.
6. **Certificate profile.** Under **Objects › Certificate profiles**, select **Create**, choose **Public Trust**, and name it, for example `roamwise`. For **Verified CN and O**, pick your validation.
7. **An identity for GitHub.** Open **Microsoft Entra ID › App registrations › New registration** and name it, for example `roamwise-github-signing`. Then go to **Certificates & secrets › Federated credentials › Add credential**:
   - Scenario: **GitHub Actions deploying Azure resources**.
   - Organization `TzTech26`, repository `roamwise`.
   - Entity type **Environment**, name `release`.

   Back on the app's overview page, note the **Application (client) ID** and the **Directory (tenant) ID**.
8. **Let it sign.** In the signing account, open **Access control (IAM) › Add role assignment**. Assign **Artifact Signing Certificate Profile Signer** and, under members, search for `roamwise-github-signing`.
9. **Tell GitHub.** In the repository, go to **Settings › Secrets and variables › Actions**.

   | Kind | Name | Value |
   | --- | --- | --- |
   | Secret | `AZURE_CLIENT_ID` | Application (client) ID from step 7 |
   | Secret | `AZURE_TENANT_ID` | Directory (tenant) ID from step 7 |
   | Secret | `AZURE_SUBSCRIPTION_ID` | Your subscription ID |
   | Variable | `ARTIFACT_SIGNING_ENDPOINT` | Endpoint from step 3, for example `https://eus.codesigning.azure.net/` |
   | Variable | `ARTIFACT_SIGNING_ACCOUNT` | Account name from step 3 |
   | Variable | `ARTIFACT_SIGNING_PROFILE` | Certificate profile name from step 6 |

The next tag builds signed exes. The run's **Check signatures** step prints each exe's signer and fails if either one isn't signed.
