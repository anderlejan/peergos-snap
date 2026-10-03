/*
 * Peergos Snap bridge - signs in to Peergos, uploads a capture and prints its secret link.
 * Copyright (C) 2026 anderlejan
 *
 * This program is free software: you can redistribute it and/or modify it under the terms of the
 * GNU Affero General Public License as published by the Free Software Foundation, version 3.
 * It is linked against Peergos (https://github.com/Peergos/Peergos), which is AGPL-3.0.
 * See bridge/LICENSE.
 */
package snap.bridge;

import peergos.server.Builder;
import peergos.server.simulation.FileAsyncReader;
import peergos.shared.Crypto;
import peergos.shared.NetworkAccess;
import peergos.shared.OnlineState;
import peergos.shared.crypto.asymmetric.PublicSigningKey;
import peergos.shared.crypto.symmetric.SymmetricKey;
import peergos.shared.login.LoginCache;
import peergos.shared.login.OfflineAccountStore;
import peergos.shared.login.mfa.MultiFactorAuthMethod;
import peergos.shared.login.mfa.MultiFactorAuthResponse;
import peergos.shared.user.LinkProperties;
import peergos.shared.user.LoginData;
import peergos.shared.user.UserContext;
import peergos.shared.user.UserStaticData;
import peergos.shared.user.FileSharedWithState;
import peergos.shared.user.fs.FileProperties;
import peergos.shared.user.fs.FileWrapper;
import peergos.shared.util.Either;
import peergos.shared.util.Futures;
import peergos.shared.util.PathUtil;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.PrintStream;
import java.lang.reflect.Field;
import java.net.URI;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.Comparator;
import java.util.List;
import java.util.HashMap;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;

/**
 * Command line bridge used by Peergos Snap (the Windows app). Secrets travel on stdin only, never on the
 * command line, so they do not show up in process listings.
 *
 * <pre>
 * signin --server URL --user NAME
 *        stdin: the password. If the account asks for a second factor, "@mfa totp" is printed on stderr
 *        and the code is read as the next stdin line.
 *        Result: {"ok":true,"session":"...","home":"/NAME"}. The session (login root key + encrypted
 *        entry data, like the web app's "stay logged in") replaces the password: it is all later
 *        commands need, and the password is not kept anywhere.
 * check  --server URL --user NAME                               stdin: session
 * upload --server URL --user NAME --file PATH [--folder F] [--name N]    stdin: session
 *        Uploads into /NAME/F (created when missing) and creates a read-only secret link to the file,
 *        in the same short form as the web app: https://HOST/secret/OWNER/ID#KEY?open=true
 * list   --server URL --user NAME [--folder F]                           stdin: session
 *        {"ok":true,"folder":"/NAME/F","files":[{"name","path","size","modified","links":[...],"dir":false}]}: the files
 *        (and folders, "dir":true) in the capture folder with their existing secret links (so older links can be shown again).
 * put    --server URL --user NAME [--folder F] [--dir D]                 stdin: session, then "LOCAL\tREL" lines, an empty line ends
 *        Uploads several files with one sign-in. Without --dir every file goes into /NAME/F and gets its own secret
 *        link; with --dir a new folder F/D (numbered when D exists) receives the files at their relative paths
 *        (REL, "/"-separated) and gets one secret link for the whole folder.
 *        {"ok":true,"files":[{"name","path","size","link"}],"folder":"/NAME/F/D"|null,"link":"..."|null,"failed":[...]}
 * folders --server URL --user NAME [--path REL]                          stdin: session
 *        The folders in /NAME/REL (to choose the capture folder): {"ok":true,"path":"/NAME/REL","exists":true,"folders":[...]}
 * delete --server URL --user NAME                                        stdin: session, then one path per line
 *        Deletes each file (Peergos removes its secret links with it): {"ok":true,"deleted":[...],"missing":[...],"failed":[...]}
 * serve  --server URL --user NAME                                        stdin: session, then JSON commands
 *        Stays running for the direct mode (sharing with a friend): see DirectServe.
 * Progress: "@progress N" lines (percent) on stderr while uploading.
 * </pre>
 *
 * Output: one JSON line on stdout, {"ok":true,...} or {"ok":false,"error":"..."}.
 */
public class PeergosBridge {

    static final PrintStream PROGRESS = System.err;
    /** The real stdout (Peergos's own logging goes to stderr). */
    static PrintStream RESULT = System.out;

    public static void main(String[] args) {
        PrintStream out = new PrintStream(new java.io.FileOutputStream(java.io.FileDescriptor.out), true, StandardCharsets.UTF_8);
        RESULT = out;
        // Peergos logs to stdout/stderr; keep stdout for our JSON results.
        System.setOut(System.err);
        int code = 0;
        String result;
        try {
            result = run(args);
        } catch (Throwable t) {
            Throwable root = t;
            while (root.getCause() != null && root.getCause() != root)
                root = root.getCause();
            String msg = root.getMessage() == null ? root.getClass().getSimpleName() : root.getMessage();
            result = "{\"ok\":false,\"error\":" + json(msg) + "}";
            code = 1;
        }
        out.println(result);
        out.flush();
        System.exit(code);
    }

    /** Keeps the login data Peergos hands to its account cache during sign-in (the web app stores the same). */
    static final class SessionCapture implements LoginCache {
        volatile LoginData data;

        public CompletableFuture<Boolean> setLoginData(LoginData login) { data = login; return Futures.of(true); }
        public CompletableFuture<Boolean> removeLoginData(String username) { data = null; return Futures.of(true); }
        public CompletableFuture<UserStaticData> getEntryData(String username, PublicSigningKey authorisedReader) {
            return data == null ? Futures.errored(new IllegalStateException("No cached login")) : Futures.of(data.entryPoints);
        }
    }

    static String run(String[] args) throws Exception {
        if (args.length == 0)
            throw new IllegalArgumentException("usage: signin|check|upload ...");
        String cmd = args[0];
        if (!java.util.Set.of("signin", "check", "upload", "put", "folders", "list", "delete", "serve").contains(cmd))
            throw new IllegalArgumentException("unknown command " + cmd);
        Map<String, String> a = parse(args);
        BufferedReader stdin = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        String server = trimSlash(a.getOrDefault("server", "https://peergos.net"));
        String user = require(a, "user").trim();
        Crypto crypto = Builder.initCrypto();
        NetworkAccess base = Builder.buildJavaNetworkAccess(new URL(server + "/"), !isLocal(server),
                Optional.of("PeergosSnap"), Optional.empty()).join();

        switch (cmd) {
            case "signin": {
                String password = line(stdin);
                if (password.isEmpty())
                    throw new IllegalArgumentException("Enter the password");
                SessionCapture capture = new SessionCapture();
                NetworkAccess network = base.withAccountCache(acc -> new OfflineAccountStore(acc, capture, new OnlineState(() -> Futures.of(true))));
                UserContext ctx = signIn(user, password, stdin, network, crypto);
                if (capture.data == null) // e.g. the account was upgraded during sign-in: sign in once more
                    ctx = signIn(user, password, stdin, network, crypto);
                if (capture.data == null)
                    throw new IllegalStateException("Peergos did not allow this account to stay signed in");
                SymmetricKey root = rootKey(ctx);
                String session = Base64.getEncoder().encodeToString(root.serialize()) + "."
                        + Base64.getEncoder().encodeToString(capture.data.entryPoints.serialize());
                return "{\"ok\":true,\"session\":" + json(session) + ",\"home\":" + json("/" + user) + "}";
            }
            case "check": {
                UserContext ctx = restore(user, line(stdin), base, crypto);
                ctx.getByPath("/" + user).join().orElseThrow(() -> new IllegalStateException("Home folder not found"));
                return "{\"ok\":true,\"home\":" + json("/" + user) + ",\"host\":" + json(linkBase(ctx, server)) + "}";
            }
            case "upload": {
                UserContext ctx = restore(user, line(stdin), base, crypto);
                String folder = a.getOrDefault("folder", "PeergosSnap").replace('\\', '/').replaceAll("^/+|/+$", "");
                FileWrapper dir = ensureFolder(ctx, "/" + user, folder, base, crypto);
                Path file = Path.of(require(a, "file"));
                String name = uniqueName(dir, a.getOrDefault("name", file.getFileName().toString()), base, crypto);
                upload(dir, name, file, base, crypto);
                String path = "/" + user + (folder.isEmpty() ? "" : "/" + folder) + "/" + name;
                // Read-only link to just this file, opened directly in the viewer (like the web app's "open" option).
                LinkProperties props = ctx.createSecretLink(path, false, Optional.empty(), Optional.empty(), "", true).join();
                String link = linkBase(ctx, server) + "/" + ctx.getLinkString(props) + "?open=true";
                return "{\"ok\":true,\"link\":" + json(link) + ",\"path\":" + json(path) + "}";
            }
            case "list": {
                UserContext ctx = restore(user, line(stdin), base, crypto);
                String folder = a.getOrDefault("folder", "PeergosSnap").replace('\\', '/').replaceAll("^/+|/+$", "");
                String dirPath = "/" + user + (folder.isEmpty() ? "" : "/" + folder);
                StringBuilder out = new StringBuilder("{\"ok\":true,\"folder\":" + json(dirPath) + ",\"files\":[");
                Optional<FileWrapper> dir = ctx.getByPath(dirPath).join();
                if (dir.isPresent() && dir.get().isDirectory()) {
                    String host = linkBase(ctx, server);
                    List<FileWrapper> kids = new ArrayList<>(dir.get().getChildren(crypto.hasher, base).join());
                    kids.sort(Comparator.comparing(f -> f.getFileProperties().name));
                    boolean first = true;
                    for (FileWrapper f : kids) {
                        FileProperties fp = f.getFileProperties();
                        if (fp.isHidden || fp.name.startsWith("."))
                            continue;
                        String path = dirPath + "/" + fp.name;
                        StringBuilder links = new StringBuilder("[");
                        try {
                            FileSharedWithState st = ctx.sharedWith(PathUtil.get(path)).join();
                            boolean firstLink = true;
                            for (LinkProperties lp : st.links) {
                                String l = host + "/" + ctx.getLinkString(lp) + (lp.open ? "?open=true" : "");
                                links.append(firstLink ? "" : ",").append(json(l));
                                firstLink = false;
                            }
                        } catch (Exception e) {
                            // a file without sharing state simply has no links
                        }
                        links.append("]");
                        out.append(first ? "" : ",")
                                .append("{\"name\":").append(json(fp.name))
                                .append(",\"path\":").append(json(path))
                                .append(",\"size\":").append(fp.size)
                                .append(",\"modified\":").append(fp.modified.toEpochSecond(ZoneOffset.UTC) * 1000)
                                .append(",\"links\":").append(links)
                                .append(",\"dir\":").append(f.isDirectory())
                                .append("}");
                        first = false;
                    }
                }
                return out.append("]}").toString();
            }
            case "put": {
                UserContext ctx = restore(user, line(stdin), base, crypto);
                String folder = cleanFolder(a.getOrDefault("folder", "PeergosSnap"));
                List<String[]> items = new ArrayList<>(); // {local, relative}
                for (String l; !(l = line(stdin)).isEmpty(); ) {
                    int t = l.indexOf('\t');
                    String local = t < 0 ? l : l.substring(0, t);
                    String rel = t < 0 ? Path.of(local).getFileName().toString() : l.substring(t + 1);
                    items.add(new String[]{local, rel});
                }
                if (items.isEmpty())
                    throw new IllegalArgumentException("Nothing to upload");
                return put(ctx, user, folder, a.get("dir"), items, server, base, crypto);
            }
            case "folders": {
                UserContext ctx = restore(user, line(stdin), base, crypto);
                String rel = cleanFolder(a.getOrDefault("path", ""));
                if (Arrays.asList(rel.split("/")).contains(".."))
                    throw new IllegalArgumentException("Not a folder in your home: " + rel);
                String dirPath = "/" + user + (rel.isEmpty() ? "" : "/" + rel);
                Optional<FileWrapper> dir = ctx.getByPath(dirPath).join();
                List<String> names = new ArrayList<>();
                if (dir.isPresent() && dir.get().isDirectory())
                    for (FileWrapper k : dir.get().getChildren(crypto.hasher, base).join()) {
                        FileProperties fp = k.getFileProperties();
                        if (k.isDirectory() && !fp.isHidden && !fp.name.startsWith("."))
                            names.add(fp.name);
                    }
                names.sort(String.CASE_INSENSITIVE_ORDER);
                return "{\"ok\":true,\"path\":" + json(dirPath) + ",\"exists\":" + (dir.isPresent() && dir.get().isDirectory())
                        + ",\"folders\":" + jsonList(names) + "}";
            }
            case "delete": {
                UserContext ctx = restore(user, line(stdin), base, crypto);
                List<String> deleted = new ArrayList<>(), missing = new ArrayList<>(), failed = new ArrayList<>();
                for (String path; !(path = line(stdin).trim()).isEmpty(); ) {
                    // Only files inside the user's own home can be removed.
                    if (!path.startsWith("/" + user + "/") || path.contains("/../")) {
                        failed.add(path + ": not in your Peergos home");
                        continue;
                    }
                    try {
                        Optional<FileWrapper> file = ctx.getByPath(path).join();
                        if (file.isEmpty()) {
                            missing.add(path);
                            continue;
                        }
                        String parentPath = path.substring(0, path.lastIndexOf('/'));
                        FileWrapper parent = ctx.getByPath(parentPath).join()
                                .orElseThrow(() -> new IllegalStateException("folder not found"));
                        file.get().remove(parent, PathUtil.get(path), ctx).join();
                        deleted.add(path);
                    } catch (Exception e) {
                        Throwable r = e;
                        while (r.getCause() != null && r.getCause() != r)
                            r = r.getCause();
                        failed.add(path + ": " + r.getMessage());
                    }
                }
                return "{\"ok\":" + failed.isEmpty() + ",\"deleted\":" + jsonList(deleted) + ",\"missing\":" + jsonList(missing)
                        + ",\"failed\":" + jsonList(failed)
                        + (failed.isEmpty() ? "" : ",\"error\":" + json(failed.size() + " could not be deleted: " + failed.get(0))) + "}";
            }
            case "serve": {
                String session = line(stdin);
                UserContext ctx = restore(user, session, base, crypto);
                return new DirectServe(ctx, user, session, base, crypto, RESULT).run(stdin);
            }
            default:
                throw new IllegalArgumentException("unknown command " + cmd);
        }
    }

    static String cleanFolder(String f) {
        return f.replace('\\', '/').replaceAll("^/+|/+$", "").replaceAll("/{2,}", "/");
    }

    /** A file or folder name Peergos accepts (no path separators, not hidden). */
    static String safeName(String n) {
        String s = n.replace('/', '_').replace('\\', '_').trim();
        if (s.isEmpty() || s.equals(".") || s.equals(".."))
            s = "_";
        return s.startsWith(".") ? "_" + s.substring(1) : s;
    }

    /** Several files in one session: each with its own link, or all in a new folder with one link to it. */
    static String put(UserContext ctx, String user, String folder, String dirName, List<String[]> items, String server,
                      NetworkAccess network, Crypto crypto) throws Exception {
        String home = "/" + user;
        String base = folder.isEmpty() ? "" : folder;
        String top = null;
        if (dirName != null && !dirName.isBlank()) {
            FileWrapper parent = ensureFolder(ctx, home, base, network, crypto);
            String d = uniqueName(parent, safeName(dirName), network, crypto);
            base = base.isEmpty() ? d : base + "/" + d;
            ensureFolder(ctx, home, base, network, crypto);
            top = home + "/" + base;
        }
        long total = 0;
        for (String[] it : items)
            total += Files.size(Path.of(it[0]));
        long[] done = {0};
        long[] lastPct = {-1};
        long all = total;
        String host = linkBase(ctx, server);
        StringBuilder files = new StringBuilder("[");
        List<String> failed = new ArrayList<>();
        boolean first = true;
        for (String[] it : items) {
            Path file = Path.of(it[0]);
            try {
                // The relative path inside the new folder; without a folder only the file name counts.
                List<String> parts = new ArrayList<>();
                for (String p : it[1].replace('\\', '/').split("/"))
                    if (!p.isBlank() && !p.equals(".") && !p.equals(".."))
                        parts.add(safeName(p));
                if (parts.isEmpty())
                    parts.add(safeName(file.getFileName().toString()));
                String sub = top == null ? base : base + (parts.size() > 1 ? "/" + String.join("/", parts.subList(0, parts.size() - 1)) : "");
                FileWrapper dir = ensureFolder(ctx, home, sub, network, crypto);
                String name = uniqueName(dir, parts.get(parts.size() - 1), network, crypto);
                long before = done[0];
                upload(dir, name, file, network, crypto, x -> {
                    done[0] += x;
                    long pct = all == 0 ? 100 : Math.min(100, done[0] * 100 / all);
                    if (pct != lastPct[0]) {
                        lastPct[0] = pct;
                        PROGRESS.println("@progress " + pct);
                        PROGRESS.flush();
                    }
                });
                done[0] = before + Files.size(file);
                String path = home + (sub.isEmpty() ? "" : "/" + sub) + "/" + name;
                String link = null;
                if (top == null) {
                    LinkProperties props = ctx.createSecretLink(path, false, Optional.empty(), Optional.empty(), "", true).join();
                    link = host + "/" + ctx.getLinkString(props) + "?open=true";
                }
                files.append(first ? "" : ",").append("{\"name\":").append(json(name)).append(",\"path\":").append(json(path))
                        .append(",\"size\":").append(Files.size(file)).append(",\"local\":").append(json(it[0]))
                        .append(",\"link\":").append(link == null ? "null" : json(link)).append("}");
                first = false;
            } catch (Exception e) {
                Throwable r = e;
                while (r.getCause() != null && r.getCause() != r)
                    r = r.getCause();
                failed.add(it[0] + ": " + r.getMessage());
            }
        }
        files.append("]");
        String folderLink = null;
        if (top != null && failed.size() < items.size()) {
            LinkProperties props = ctx.createSecretLink(top, false, Optional.empty(), Optional.empty(), "", false).join();
            folderLink = host + "/" + ctx.getLinkString(props);
        }
        boolean ok = failed.size() < items.size();
        return "{\"ok\":" + ok + ",\"files\":" + files + ",\"folder\":" + (top == null ? "null" : json(top))
                + ",\"link\":" + (folderLink == null ? "null" : json(folderLink)) + ",\"failed\":" + jsonList(failed)
                + (failed.isEmpty() ? "" : ",\"error\":" + json(failed.size() + " of " + items.size() + " not uploaded: " + failed.get(0))) + "}";
    }

    static String jsonList(List<String> items) {
        StringBuilder b = new StringBuilder("[");
        for (int i = 0; i < items.size(); i++)
            b.append(i == 0 ? "" : ",").append(json(items.get(i)));
        return b.append("]").toString();
    }

    static UserContext signIn(String user, String password, BufferedReader stdin, NetworkAccess network, Crypto crypto) {
        return UserContext.signIn(user, password, req -> {
            // Ask the app for a code from the user's authenticator app.
            Optional<MultiFactorAuthMethod> totp = req.methods.stream().filter(m -> m.type.name().equals("TOTP")).findFirst();
            if (totp.isEmpty())
                return Futures.errored(new IllegalStateException(
                        "This account only allows a security key as second factor, which Peergos Snap cannot use. Add an authenticator app in Peergos."));
            PROGRESS.println("@mfa totp");
            PROGRESS.flush();
            String code;
            try {
                code = line(stdin).trim();
            } catch (Exception e) {
                return Futures.errored(e);
            }
            if (code.isEmpty())
                return Futures.errored(new IllegalStateException("Sign-in cancelled: no two-factor code"));
            return Futures.of(new MultiFactorAuthResponse(totp.get().credentialId, Either.a(code)));
        }, true, false, network, crypto, s -> {}).join();
    }

    static UserContext restore(String user, String session, NetworkAccess network, Crypto crypto) {
        String[] parts = session.trim().split("\\.");
        if (parts.length != 2)
            throw new IllegalStateException("Not signed in");
        SymmetricKey root;
        UserStaticData entry;
        try {
            root = SymmetricKey.fromByteArray(Base64.getDecoder().decode(parts[0]));
            entry = UserStaticData.fromByteArray(Base64.getDecoder().decode(parts[1]));
        } catch (Exception e) {
            throw new IllegalStateException("Not signed in");
        }
        try {
            return UserContext.restoreContext(user, root, entry, network, crypto, s -> {}).join();
        } catch (Exception e) {
            Throwable r = e;
            while (r.getCause() != null && r.getCause() != r)
                r = r.getCause();
            String m = String.valueOf(r.getMessage());
            if (m.contains("Legacy accounts"))
                throw new IllegalStateException(m);
            if (m.contains("decrypt") || m.contains("Decrypt") || m.contains("MAC") || m.contains("Invalid") || m.contains("key"))
                throw new IllegalStateException("Session expired: sign in again (" + m + ")");
            throw e;
        }
    }

    /** The login root key is what the web app keeps to stay signed in; UserContext does not expose it. */
    static SymmetricKey rootKey(UserContext ctx) throws Exception {
        Field f = UserContext.class.getDeclaredField("rootKey");
        f.setAccessible(true);
        return (SymmetricKey) f.get(ctx);
    }

    /** https://HOST as the web app builds it (the owner's link host), falling back to the server address. */
    static String linkBase(UserContext ctx, String server) {
        String host = "";
        try {
            host = ctx.getLinkHost().join();
        } catch (Exception ignored) {
        }
        if (host == null || host.isBlank() || host.equals("localhost"))
            return server;
        host = host.trim();
        if (host.startsWith("http://") || host.startsWith("https://"))
            return trimSlash(host);
        return (host.startsWith("localhost:") ? "http://" : "https://") + host;
    }

    static FileWrapper upload(FileWrapper dir, String name, Path file, NetworkAccess network, Crypto crypto) throws Exception {
        long size = Files.size(file);
        long[] done = {0};
        long[] lastPct = {-1};
        return upload(dir, name, file, network, crypto, x -> {
            done[0] += x;
            long pct = size == 0 ? 100 : Math.min(100, done[0] * 100 / size);
            if (pct != lastPct[0]) {
                lastPct[0] = pct;
                PROGRESS.println("@progress " + pct);
                PROGRESS.flush();
            }
        });
    }

    static FileWrapper upload(FileWrapper dir, String name, Path file, NetworkAccess network, Crypto crypto,
                              java.util.function.LongConsumer onBytes) throws Exception {
        long size = Files.size(file);
        FileWrapper updated = dir.uploadOrReplaceFile(name, new FileAsyncReader(file.toFile()), size,
                network, crypto, () -> false, onBytes::accept).join();
        return updated.getChild(name, crypto.hasher, network).join()
                .orElseThrow(() -> new IllegalStateException("Upload finished but the file is missing"));
    }

    static FileWrapper ensureFolder(UserContext ctx, String home, String rel, NetworkAccess network, Crypto crypto) {
        FileWrapper dir = ctx.getByPath(home).join().orElseThrow(() -> new IllegalStateException("Home folder not found"));
        if (rel.isEmpty())
            return dir;
        for (String part : rel.split("/")) {
            if (part.isEmpty())
                continue;
            Optional<FileWrapper> child = dir.getChild(part, crypto.hasher, network).join();
            if (child.isEmpty()) {
                dir = dir.mkdir(part, network, false, dir.mirrorBatId(), crypto).join();
                child = dir.getChild(part, crypto.hasher, network).join();
            }
            dir = child.orElseThrow(() -> new IllegalStateException("Could not create folder " + part));
        }
        return dir;
    }

    static String uniqueName(FileWrapper dir, String name, NetworkAccess network, Crypto crypto) {
        String base = name, ext = "";
        int dot = name.lastIndexOf('.');
        if (dot > 0) {
            base = name.substring(0, dot);
            ext = name.substring(dot);
        }
        String candidate = name;
        for (int i = 2; dir.getChild(candidate, crypto.hasher, network).join().isPresent(); i++)
            candidate = base + " (" + i + ")" + ext;
        return candidate;
    }

    static boolean isLocal(String server) {
        try {
            String h = URI.create(server).getHost();
            return h == null || h.equals("localhost") || h.startsWith("127.") || h.equals("[::1]");
        } catch (Exception e) {
            return false;
        }
    }

    static String line(BufferedReader r) throws Exception {
        String s = r.readLine();
        return s == null ? "" : s;
    }

    static String require(Map<String, String> a, String k) {
        String v = a.get(k);
        if (v == null || v.isEmpty())
            throw new IllegalArgumentException("missing --" + k);
        return v;
    }

    static String trimSlash(String s) {
        s = s.trim();
        while (s.endsWith("/"))
            s = s.substring(0, s.length() - 1);
        return s;
    }

    static Map<String, String> parse(String[] args) {
        Map<String, String> m = new HashMap<>();
        for (int i = 1; i + 1 < args.length; i += 2) {
            if (!args[i].startsWith("--"))
                throw new IllegalArgumentException("unexpected " + args[i]);
            m.put(args[i].substring(2), args[i + 1]);
        }
        return m;
    }

    static String json(String s) {
        StringBuilder b = new StringBuilder("\"");
        for (char c : s.toCharArray()) {
            switch (c) {
                case '"': b.append("\\\""); break;
                case '\\': b.append("\\\\"); break;
                case '\n': b.append("\\n"); break;
                case '\r': b.append("\\r"); break;
                case '\t': b.append("\\t"); break;
                default:
                    if (c < 0x20) b.append(String.format("\\u%04x", (int) c));
                    else b.append(c);
            }
        }
        return b.append('"').toString();
    }
}
