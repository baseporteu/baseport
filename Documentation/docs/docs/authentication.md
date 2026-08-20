---
title: Authentication
description: "API tokens, end user accounts, single sign-on and anonymous accounts"
---

# Authentication

There are two kinds of credential. An **API token** is a long lived string you issue to an account yourself. A **JWT** is a shorter lived token you get by signing in. Both work on the same `/api/v1` routes.

```
Authorization: Bearer <token or jwt>
```

## API tokens

Create one under **Authentication** on the account that should have it, and pick an expiry date. You see the token once, when it is generated. Only a hash is stored, so it is never shown again.

The account's **API enabled** switch controls whether its token works at all, and returns `401` when it is off. A disabled account cannot sign in and its token is rejected.

## End user accounts

Your application's own users sign in at `/auth`, or call `/api/auth/v1` directly. Both are off until you turn **Public authentication** on in Settings, and letting people sign themselves up is a separate switch.

```bash
curl -X POST http://localhost:5263/api/auth/v1/login \
  -H 'Content-Type: application/json' \
  -d '{"email_or_username":"ada","password":"..."}'
```

```json
{ "auth_token": "eyJ...", "refresh_token": "...", "expires_at": 1755680000 }
```

| Route | What it does |
| --- | --- |
| `POST /api/auth/v1/register` | Sign up, when registration is open |
| `POST /api/auth/v1/login` | Exchange credentials for a token pair |
| `POST /api/auth/v1/refresh` | Mint a new auth token from a refresh token |
| `POST /api/auth/v1/logout` | Revoke a refresh token |
| `GET /api/auth/v1/status` | Who is calling |
| `POST /api/auth/v1/change_password` | Change your own password |
| `POST /api/auth/v1/delete` | Delete your own account |
| `GET /api/auth/v1/jwks.json` | The public half of the signing key |

The role is never taken from the token. Every request looks the account up again, so if you demote someone it takes effect on their next request rather than their next sign-in.

## Anonymous accounts

Useful if you want people to try your app before signing up. Turn **Anonymous accounts** on, then ask for one:

```bash
curl -X POST http://localhost:5263/api/auth/v1/anonymous
```

You get back the same pair of tokens a normal sign-in returns. Anything the visitor creates while using them belongs to that account.

When they sign up, send the registration request with that token still attached. Registering **converts** the anonymous account rather than creating a second one, so everything they already created stays with them. All existing sessions on the account are revoked at that point, since the account now has a password.

The `anonymous-cleanup` job deletes abandoned anonymous accounts once all their sessions have expired and the retention window has passed.

## Single sign-on

Add an OpenID Connect provider under **Settings > Authentication > Single sign-on**. Anything that publishes a discovery document works, including Authelia, Authentik and Pocket ID.

Copy the redirect URL out of the sheet and register it at your provider:

```
https://baseport.example.com/api/auth/oidc/{key}/callback
```

Saving fetches and checks the discovery document immediately, so you find out about a wrong issuer URL there rather than from a button that does nothing on the sign-in screen. The flow uses PKCE and a nonce, and verifies the `id_token` against the provider's JWKS.

Choose where the provider appears: the console at `/_/auth`, your application's own sign-in at `/auth`, or both. If you enable a provider but do not show it on either, the save is rejected.

Accounts are matched on the provider's subject id rather than on a name, so renaming someone in your directory does not lock them out. The first sign-in can attach an existing account by an exact username match, or by an email address the provider says it has verified. Turn **Create accounts on first sign-in** on if you want unknown users created automatically as plain users.

:::warning
Admin accounts are never linked automatically, whatever the provider sends. Console access should not depend on a name in your directory, so no claim will ever match one.
:::

To sign in to the console with your provider, link your account yourself. Go to **Settings > Authentication > Single sign-on**, press **Link my account** on the provider row and confirm your password. You sign in at the provider once, and the identity it returns is attached to the account you are already signed in as. Nothing the provider sends picks the account, because that was decided before you were redirected.

Linking revokes every other session on the account, since you have just added another way to sign in.

You can also do it from the shell, or for an account that is not yours:

```bash
baseport accounts link <username> <provider-key> <subject>
baseport accounts unlink <username>
```

The subject id is in the log line from the sign-in that was rejected.

## CLI-only operations

The console does not allow operations that would let one operator take over another's account. Those are in the CLI, which needs shell access:

```
baseport accounts list
baseport accounts promote <account>
baseport accounts demote <account>
baseport accounts password <account> <pw>
baseport accounts rename <account> <new>
```

Use `rename` to replace the generated `admin-xxxxxxxx` username with your own. A password you set for somebody else is always single use: they have to change it on first sign-in, and every session on that account is revoked.
