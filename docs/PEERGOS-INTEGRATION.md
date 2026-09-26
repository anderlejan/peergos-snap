# Peergos integration: how uploads and links work

Peergos encrypts everything on the client. The server stores only encrypted blocks and signed pointers, so an
upload must build the encrypted file tree itself. Peergos Snap does not re-implement that cryptography. It runs the
**official Peergos client library** (`Peergos.jar`, AGPL-3.0, release v1.35.1) through a small bridge
(`bridge/src/snap/bridge/PeergosBridge.java`) on a bundled Java runtime. That way it uses exactly the same code as
the Peergos web app.

## Mode B: shared writable folder link (no login)

Link format: `https://<host>/secret/<owner>/<label>#<linkPassword>`

- `<owner>`: the owner's public-key hash (base58, starts with `z`).
- `<label>`: a number that identifies the stored link on the owner's server.
- `<linkPassword>`: the part after `#`. The browser never sends it to the server.

Steps (all verified on peergos.net on 2026-09-26):

1. **Resolve the link.** `UserContext.fromSecretLinkV2("/secret/<owner>/<label>#<pw>", userPassword, network, crypto)`
   fetches the **encrypted capability** stored under `<owner>/<label>` (`network.getSecretLink`). It then decrypts
   the capability with a key derived by scrypt from `label` + `linkPassword` (+ the optional user password; links
   with a password ask for it). The result is a `WritableAbsoluteCapability` for the shared folder: owner, writer
   key, map key, BAT, read key and write key.
2. **Open the folder.** `getEntryPath()` → `getByPath(entry)`. The folder must be `isWritable()`. Otherwise the app
   says the link is read-only.
3. **Upload encrypted.** `FileWrapper.uploadOrReplaceFile(name, reader, size, …)` splits the file into chunks.
   Each chunk is encrypted with a fresh random key under the folder's cryptree, stored in the owner's storage, and
   signed with the folder's writer key (from the capability). The new file is then linked into the folder's
   directory node. If the name is already taken, the app uses `name (2).ext` and so on.
4. **Make the share link for that one file.** A new *stored* secret link (`/secret/…`) can only be registered by
   the owner's account, because it is stored in the owner's signed link table. A writer who only has a folder
   capability cannot create one. Instead, the bridge derives the file's **read-only capability** and encodes it
   the way Peergos encodes capability links:

   ```
   https://<host>/#<owner>/<writer>/<mapKey+BAT>/<readBaseKey>?open=true
   ```

   (`FileWrapper.toLink()` = `AbsoluteCapability.readOnly().toLink()`, all parts base58.) The Peergos web app opens
   this directly: `App.vue` → `getSecretLinkProps()` → *legacy secret link* → `UserContext.fromSecretLink(link)`.
   With `?open=true` it shows the file in the viewer.
   - The link grants **read access to that file only**. The folder and its other files stay private, and nothing
     writable is ever handed out.
   - It works immediately, with no server-side registration. It stays valid for as long as the file exists.
     Deleting the file in Peergos revokes it.

Checked: a picture uploaded with the bridge opened in a fresh browser with no login and no cookies, and showed
`bridge-test.png` in the Peergos image viewer.

## Mode A: account

1. `UserContext.signIn(username, password, mfa, network, crypto)`. Two-factor accounts are refused, because
   automatic uploads cannot answer a code prompt.
2. `/<user>/<folder>` is created if missing (`mkdir`), then `uploadOrReplaceFile`.
3. `createSecretLink(path, writable=false, expiry=none, maxRetrievals=none, userPassword="", open=true)` stores a
   new encrypted capability on the owner's server. `getLinkString` gives `secret/<owner>/<label>#<pw>`, and the link
   is `https://<server>/` followed by that string. This is the same kind of link the web app's Share dialog creates.

## Bridge protocol

```
java -cp peergos-snap-bridge.jar;Peergos.jar snap.bridge.PeergosBridge <command> [--options]
  folder        --server URL --link LINK --file PATH [--name NAME]      stdin: link user password
  check         --server URL --link LINK                                stdin: link user password
  account       --server URL --user NAME --file PATH [--folder F]       stdin: password, TOTP (empty)
  account-check --server URL --user NAME                                stdin: password, TOTP (empty)
stdout: one JSON line  {"ok":true,"link":"…","path":"…"}  |  {"ok":false,"error":"…"}
stderr: "@progress N" (percent) while uploading, plus Peergos logs
```

Secrets go in on stdin only, so they never appear in process lists. Settings store them encrypted with Windows
DPAPI (current user). The server is taken from the folder link itself (e.g. `https://peergos.net`), so links to a
self-hosted Peergos or to a local daemon (`http://localhost:8000/secret/…`) work too.

## Files bigger than memory

The bridge streams from disk (`FileAsyncReader`) and runs with `-Xmx1g`, so long recordings don't need to fit in
memory.
