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
  list   --server URL --user NAME [--folder F]              stdin: session
  delete --server URL --user NAME                           stdin: session, then one path per line
  serve  --server URL --user NAME                           stdin: session, then JSON commands (direct mode, below)
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

## Uploading files and folders (2.3)

`put` uploads several files with one sign-in (`--folder` = the capture folder): without `--dir` each file gets its own
read-only secret link (like a capture); with `--dir NAME` a new folder (numbered when NAME exists) gets the files at
their relative paths and one read-only secret link for the folder (not opened in the viewer: the link shows the
folder). `list` also reports folders (`"dir":true`), so the history can tell whether an uploaded folder still exists.
Deleting an uploaded folder from the history uses the same `delete` (`FileWrapper.remove`, which also removes the
links of everything inside it). `folders --path REL` lists the folders of `/<me>/REL` for choosing the capture folder.
The original files on the PC are only read; the history remembers where they came from but never changes them.

## Direct mode (2.2): sharing with a friend

Two users who are **friends in Peergos** (a follow request sent and accepted with reciprocation) share pictures,
videos and other files without links:

- Each user has `/<me>/PeergosSnap/Direct/<friend>/<yyyy-MM>/` (since 2.3, inside the app's main folder; 2.2 used
  `/<me>/PeergosSnap-Direct/…`). These folders are **not shared**. Sending = uploading
  into one's own month folder, then sharing **that one file read-only with that friend** (`shareReadAccessWith`).
  The friend therefore sees exactly the files sent to them (2.2 development builds shared the whole folder for
  writing).
- The receiver lists `/<friend>/PeergosSnap/Direct/<me>/<month>`: Peergos shows the folders on the path to a file
  shared with you, and in them only the shared files. Each new file is **copied** into the receiver's own
  `/<me>/PeergosSnap/Direct/<friend>/<month>/received/` (`FileWrapper.copyTo(dir, ctx)`), once: `"got": true` in the
  receiver's meta file remembers it. From then on the receiver owns that copy; the sender cannot affect it.
- **Delete** only ever deletes one's own copy (what I sent, or my copy of what I received) in my Peergos; the app also
  deletes the PC copy. A deleted received copy is not copied again ("got" stays while the sender's original exists).
- Labels, pins and stars are kept by each user in **their own** month folder for that friend:
  `.snapmeta-<user>.json` = `{"v":2,"items":{"<sender>/<name>":{"label","labelAt","pin","pinAt","star","got"}}}`
  (the same key for the sender's original and the receiver's copy), shared read-only with the friend once and
  afterwards **overwritten in place** (`overwriteFile`), so the friend's share stays valid. Each side only writes its
  own file; on reading, the newest label and pin win and stars are the union. Neither side ever writes into the
  other's folders.
- Received files are downloaded to the PC when they arrive only with *Also keep received files on this PC* (default on).
- Peergos Snap does not manage friendships beyond checking them and offering a friend request / accept: it never
  unfriends, and it does not repair sharing broken by unfriending elsewhere.
- The app keeps one bridge process running (`serve`): it restores the session once and then answers JSON commands on
  stdin (`friends`, `discover`, `add`, `accept`, `decline`, `open`, `list`, `send`, `get`, `delete`, `meta`, `watch`,
  `quit`), one JSON answer per line on stdout. With `watch`, it lists the current month of both sides of each
  watched friend every few seconds and prints a `changed` event whenever anything differs. "Instant" therefore means
  within the check interval (3 s by default) plus Peergos' own caching of folder versions.
- A long-running session caches folder versions. When the account changed a folder elsewhere, a write is refused
  ("concurrent modification") or fails on a missing parent; the session then signs in again from the session data
  (`NetworkAccess.clear()` + `restoreContext`) and retries. `discover` (every 5 minutes) also starts from a fresh
  session, so files shared since the last check are seen.
- Some Peergos calls never complete – for example reading a friend's folder after that friend unfriended you
  (revoked access): the future simply never finishes. The session therefore waits at most 60 s for a read and
  5 minutes for a write. A friend whose folder times out is checked again only after 2 minutes, on a fresh session,
  and `discover` skips them, so one such friend never holds up the others or the user's commands.
- `discover` lists friends who have shared something with this user, so the receiving side is set up without doing
  anything. The app only keeps the session running while there are friends to share with.
- Only paths of the shape `/<owner>/PeergosSnap/Direct/<other>/<yyyy-MM>/[received/]<name>` (or the same under
  `PeergosSnap-Direct`) with this user as owner or other are accepted by `get`, `delete` and `meta`.
- **The move in 2.3.** New files, copies and meta files go only to `PeergosSnap/Direct`. The 2.2 folders
  (`PeergosSnap-Direct`, on both sides) are still listed, merged into the same month, and their files can be labelled,
  pinned, starred and deleted as before (labels of those files are kept in the new meta file from then on). Nothing
  is moved: a move would create new capabilities and break what the friend was given. Consequence: a friend still
  running **2.2** does not see what a 2.3 user sends (2.2 only looks at `PeergosSnap-Direct`) until their Peergos
  Snap updates itself (at most a day with automatic updates on). Once both 2.2 folders are empty they can be deleted
  in the Peergos web app; Peergos Snap does not delete them.

### Peergos social behaviour worth knowing (found while testing)

- `UserContext.unfollow(user)` also **blocks** the user (appends to the blocked-usernames file); `unblock(user)` undoes
  it. `removeFollower(user)` revokes everything ever shared with that user and deletes `/<me>/shared/<user>`.
- If a follow request to someone is still pending when `removeFollower` deletes that folder, their later reply can
  never be processed (`processFollowRequests` throws a NullPointerException on `ourDirForThem`) and every
  `getSocialState()` fails until the pending entry is removed (private `removeFromPendingOutgoing`) or the reply is
  discarded (`network.social.removeFollowRequest` with the signed request).
- Two sessions of the same account changing social state at the same time (e.g. the web app and Peergos Snap) can
  leave a friendship half done; sign in again (fresh session) before retrying.