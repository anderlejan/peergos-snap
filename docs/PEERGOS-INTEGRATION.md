# Peergos integration: sign-in, uploads and links

Peergos encrypts everything on the client. The server stores only encrypted blocks and signed pointers, so an
upload has to build the encrypted file tree itself. Peergos Snap does not re-implement that cryptography. It runs
the **official Peergos client library** (`Peergos.jar`, AGPL-3.0, release v1.35.1) through a small bridge
(`bridge/src/snap/bridge/PeergosBridge.java`) on a bundled Java runtime, so it uses exactly the same code as the
Peergos web app.

Since 2.0 every upload goes through the user's **Peergos account**. Links are created by the account, in the same
short form as the web app's Share dialog:

```
https://<link host>/secret/<owner>/<label>#<link password>?open=true
```

## Signing in once, then staying signed in

1. **Sign in** (`signin`): `UserContext.signIn(username, password, mfa, cacheMfaLoginData = true, …)`. If the
   account asks for a second factor, the bridge prints `@mfa totp` and reads the code from the app. The app asks the
   user for the code from their authenticator app. Accounts that only allow a security key (WebAuthn) are refused
   with a clear message.
2. The bridge wraps the network's account store in Peergos's own `OfflineAccountStore` with a capture `LoginCache`.
   That is how the web app's "stay logged in" keeps the login data. After sign-in it returns a **session**:
   base64(login root key) + "." + base64(encrypted entry data).
3. The app stores only that session (Windows DPAPI, current user). **The password is not stored.** 1.x kept the
   password; on the first start of 2.0 it is used once to create a session and is then deleted.
4. Each later command restores the account with `UserContext.restoreContext(username, rootKey, entryData, …)`,
   the same call the web app uses to stay logged in, without a password or second factor. If that fails (for
   example after a password change), the app asks the user to sign in again.

## Upload and link

`upload`: restore the session → `/<user>/<folder>` (created when missing) → `uploadOrReplaceFile` (streamed from
disk, with a progress line per percent) → a unique name (`name (2).ext` …) →
`createSecretLink(path, writable = false, expiry = none, maxRetrievals = none, userPassword = "", open = true)` →
`getLinkString(props)`. The link is `https://` + `getLinkHost()` + `/` + the link string + `?open=true`. This is
exactly what the web app's Share dialog builds (`SecretLink.vue → buildHref`). The link opens the file in the
viewer, read-only, and gives no access to anything else. Deleting the file in Peergos ends the share.

## Bridge protocol

```
java -cp peergos-snap-bridge.jar;Peergos.jar snap.bridge.PeergosBridge <command> [--options]
  signin --server URL --user NAME                           stdin: password [, then the two-factor code on request]
  check  --server URL --user NAME                           stdin: session
  upload --server URL --user NAME --file PATH [--folder F] [--name N]    stdin: session
stdout: one JSON line  {"ok":true,"session":"…"} | {"ok":true,"link":"…","path":"…"} | {"ok":false,"error":"…"}
stderr: "@progress N" (percent), "@mfa totp" (code needed), plus Peergos logs
```

Secrets go in on stdin only, so they never appear in process lists.

## 1.x folder links (removed)

Version 1.x could upload into a *writable folder* secret link without logging in. It then derived a read-only
capability for the uploaded file, which gave a very long link (`https://peergos.net/#<key>/<key>/<key>/<key>`).
This is not meant to be possible for a folder
writer, and such links are being disabled on the Peergos side. Peergos Snap 2.0 removed the folder mode and that
link type completely. A folder link stored by 1.x is kept untouched in the settings file (unused) for a possible
future folder feature.
