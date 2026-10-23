# LinkedIn setup

You need your own LinkedIn developer application with the **Member Data Portability API (Member)** product, and a
token for your own account. The product is available only to members in the **EEA and Switzerland**. This follows
LinkedIn's [official onboarding](https://learn.microsoft.com/en-us/linkedin/dma/member-data-portability/member-data-portability-member/?view=li-dma-data-portability-2026-08).

## 1. Create the developer application

1. Go to [LinkedIn Developers → My apps → Create app](https://www.linkedin.com/developers/apps/new).
2. Name it anything, for example "My data export".
3. For **LinkedIn Page**, choose
   [Member Data Portability (Member) Default Company](https://www.linkedin.com/company/member-data-portability-member-default-company).
   Do **not** create a company page of your own: LinkedIn grants the product only to apps that use this page.
4. Add a logo (any image) and accept the API terms.

## 2. Request the product

On the app's **Products** tab, click **Request access** next to **Member Data Portability API (Member)** and accept
the terms. Access is granted immediately.

## 3. Generate a token

1. Open the [OAuth Token Generator](https://www.linkedin.com/developers/tools/oauth) (Docs and tools → OAuth Token
   Tools), click **Create token** and select your app.
2. Select the scope **`r_dma_portability_self_serve`** and click **Request access token**.
3. Sign in to LinkedIn if asked, read the consent text and click **Allow**.
4. Copy the access token. Write down the lifetime the page shows, because `member` cannot find it out later.

Consenting also starts LinkedIn's archiving. Snapshot data for some domains takes minutes or longer to appear, and
changelog events are only recorded from now on.

## 4. Give the token to `member`

```sh
member auth import-token --expires-in-days 60   # paste at the hidden prompt; use the lifetime shown
```

or, from the clipboard without the token ever appearing on screen or in shell history:

```sh
pbpaste | member auth import-token --stdin --expires-in-days 60                    # macOS
Get-Clipboard | member auth import-token --stdin --expires-in-days 60              # Windows PowerShell
```

`import-token` checks the token with LinkedIn before storing it. Never pass the token as a command argument, and
never paste it into chats, issues or AI agents.

Check the result:

```sh
member auth status          # offline: is a token stored, when does it expire
member auth registration    # online: is the member registered for DMA data generation
```

If `registration` still reports nothing a few minutes after you generated the token, run `member auth register`.

## When the token expires or is revoked

LinkedIn answers 401, and `member` exits with code 3 and asks you to generate a new token (step 3) and import it
again (step 4). There is no automatic refresh, because LinkedIn documents no refresh flow for this product.
Revoking the app under LinkedIn Settings → Data privacy → Permitted services invalidates the token.

## Servers without a desktop session

Headless Linux usually has no unlocked Secret Service, so the keychain is unavailable there. You can explicitly
accept the risk of environment variables instead (see [security-and-privacy.md](security-and-privacy.md)):

```sh
export MEMBERDATA_CREDENTIAL_SOURCE=environment
export MEMBERDATA_ACCESS_TOKEN=…            # from a secrets manager, not from shell history
export MEMBERDATA_TOKEN_EXPIRES_AT=2026-12-22T00:00:00Z   # optional
member sync
```
