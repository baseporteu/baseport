---
title: Authentication
description: "API tokens, end user accounts, single sign-on, two-factor and anonymous accounts"
---

# Authentication

Two credential types authenticate the `/api/v1` routes:

| Credential | Lifetime | Issued by | Typical caller |
| --- | --- | --- | --- |
| **API token** | Until its expiry date | An operator, per account | Server-side integrations |
| **JWT** | Short, refreshable | A sign-in | Application users |

```
Authorization: Bearer <token or jwt>
```

## API tokens

Tokens are generated under **Authentication** on the owning account, with an expiry date. The token is displayed once; only its hash is stored.

The account's **API enabled** switch gates the token: `401` when off. A disabled account cannot sign in and its token is refused.

## End user accounts

Application users sign in at `/auth` or through `/api/auth/v1`. Both require **Public authentication** in Settings; self-registration is a separate switch.

```bash
curl -X POST http://localhost:5000/api/auth/v1/login \
  -H 'Content-Type: application/json' \
  -d '{"email_or_username":"ada","password":"..."}'
```

```json
{ "auth_token": "eyJ...", "refresh_token": "...", "expires_at": 1755680000 }
```

| Route | Action |
| --- | --- |
| `POST /api/auth/v1/register` | Sign up, when registration is open |
| `POST /api/auth/v1/login` | Exchange credentials for a token pair |
| `POST /api/auth/v1/anonymous` | Create an anonymous account, when enabled |
| `POST /api/auth/v1/refresh` | Mint a new auth token from a refresh token |
| `POST /api/auth/v1/logout` | Revoke a refresh token |
| `GET /api/auth/v1/status` | The calling account |
| `POST /api/auth/v1/change_password` | Change the caller's password |
| `DELETE /api/auth/v1/delete` | Delete the caller's account |
| `GET /api/auth/v1/jwks.json` | Public signing key |

The role is not read from the token. Every request re-reads the account, so a demotion applies on the next request.

Five failed sign-ins lock the account for five minutes, per account and client address.

## Anonymous accounts

With **Anonymous accounts** enabled, `POST /api/auth/v1/anonymous` returns a token pair for a new anonymous account:

```bash
curl -X POST http://localhost:5000/api/auth/v1/anonymous
```

Records created with that token belong to the anonymous account. A registration request sent with the token attached **converts** the account instead of creating a second one; its records are kept and all existing sessions are revoked.

The `anonymous-cleanup` job deletes anonymous accounts whose sessions have all expired, after the retention window.

## Single sign-on

OpenID Connect providers are configured under **Settings > Authentication > Single sign-on**. Any provider with a discovery document is supported, including Authelia, Authentik and Pocket ID.

Redirect URL to register at the provider:

```
https://baseport.example.com/api/auth/oidc/{key}/callback
```

The discovery document is fetched and validated on save. The flow uses PKCE and a nonce, binds the `state` to the browser with a cookie, and verifies the `id_token` against the provider's JWKS.

Each provider is shown on the console sign-in (`/_/auth`), the application sign-in (`/auth`), or both. An enabled provider shown on neither is refused on save.

Accounts are matched on the provider's subject id. On first sign-in, an existing account is attached by exact username match or by an email address the provider marks as verified. **Create accounts on first sign-in** creates unknown users with the `user` role.

:::warning
Admin accounts are never linked automatically.
:::

An operator links their own admin account under **Settings > Authentication > Single sign-on** with **Link my account** and a password confirmation. The provider identity returned by that sign-in is attached to the signed-in account. Linking revokes every other session on the account.

Shell equivalents, also for other accounts:

```bash
baseport accounts link <username> <provider-key> <subject>
baseport accounts unlink <username>
```

The subject id is written to the log by the refused sign-in.

## Two-factor sign-in

Admin accounts can require an authenticator code after the password. Setup: account menu (sidebar) > **Two-factor**, add the key or `otpauth://` link to an authenticator app, confirm with a code. Enabling it revokes every other session on the account.

| Sign-in | Code field |
| --- | --- |
| Console, `POST /api/auth/login` | `code` |
| Public, `POST /api/auth/v1/login` | `totp_code` |

A `401` with `"totp": true` means the password was accepted and the code is missing or invalid. Each code is accepted once; an invalid code counts toward the lockout. One-time codes and single sign-on do not ask for a code.

Disabling requires the password and a current code. Recovery for a lost device:

```bash
baseport accounts totp-reset <account>
```

This also revokes every session on the account.

## CLI-only operations

Operations that could let one operator take over another's account are not available in the console. They require shell access:

```
baseport accounts list
baseport accounts promote <account>
baseport accounts demote <account>
baseport accounts password <account> <pw>
baseport accounts rename <account> <new>
baseport accounts totp-reset <account>
```

`rename` replaces the generated `admin-xxxxxxxx` username. A password set for another account is single use: it must be changed at the next sign-in, and every session on that account is revoked.
