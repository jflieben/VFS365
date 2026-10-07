# App registration

VFS365 signs in as the user (delegated) through a public client app registration. No secrets or certificates.

## Default: the JSolve app

Without a `ClientId` policy VFS365 uses JSolve's multi-tenant app, client ID `08e570ea-9711-4dc6-b450-2e00fc550fca`. No app registration of your own is needed. An admin consents once per tenant:

1. Open https://login.microsoftonline.com/organizations/adminconsent?client_id=08e570ea-9711-4dc6-b450-2e00fc550fca and sign in as a Cloud Application Administrator, Application Administrator, Privileged Role Administrator or Global Administrator of your tenant.
2. Check the permissions (table below) and accept. The browser is then sent on to the app's sign-in address and may show an error page; the consent is already saved.
3. VFS365 now appears under Entra admin center, Enterprise applications, with the permissions under Permissions.

Until then users see "Need admin approval" (user consent for these scopes is blocked by default since MC1097272), and the agent log shows the consent link.

## Permissions

| API | Permission | Used for |
|---|---|---|
| Microsoft Graph | `User.Read` | Sign-in, own OneDrive URL (finds the tenant's SharePoint host) |
| Microsoft Graph | `Files.ReadWrite.All` | Read, upload, rename, move and delete files in every library the user can reach |
| Microsoft Graph | `Sites.Read.All` | Resolve a discovered library to its Graph drive |
| SharePoint | `AllSites.Read` | Discovery: SharePoint Search, site and library metadata. Not used with `Scope` = `OneDrive` |

## Your own registration

For tenants that only allow apps registered in their own directory, and for forks of VFS365.

1. Entra admin center, App registrations, New registration. Name `VFS365`. Choose **Accounts in any organizational directory** (multi-tenant): devices that aren't Entra joined or registered then sign in without a forced tenant.
2. Authentication, Add a platform, **Mobile and desktop applications**. Redirect URIs:
   - `ms-appx-web://microsoft.aad.brokerplugin/<client id>` (Windows sign-in broker; fill in the Application ID after registering)
   - `http://localhost` (browser fallback)
3. Authentication, **Allow public client flows**: Yes.
4. API permissions: the delegated permissions above, then **Grant admin consent**.
5. The **Application (client) ID** goes into the `ClientId` policy ([DEPLOYMENT.md](DEPLOYMENT.md)); for development into `vfs365.local.json` in the repo root (git-ignored): `{ "ClientId": "<application id>" }`.

## Tenant

Detected, not configured: the device's Entra join or registration, else the tenant of the account that signs in (through `organizations`, which needs a multi-tenant registration). The SharePoint host comes from the user's OneDrive URL. The `TenantId` policy forces a tenant.
