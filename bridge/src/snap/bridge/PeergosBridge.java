/*
 * Peergos Snap bridge - uploads a capture into Peergos and prints a share link.
 * Copyright (C) 2026 Jan Anderle
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
import peergos.shared.login.mfa.MultiFactorAuthMethod;
import peergos.shared.login.mfa.MultiFactorAuthResponse;
import peergos.shared.user.LinkProperties;
import peergos.shared.user.UserContext;
import peergos.shared.user.fs.AsyncReader;
import peergos.shared.user.fs.FileWrapper;
import peergos.shared.util.Either;
import peergos.shared.util.Futures;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.PrintStream;
import java.net.URI;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.HashMap;
import java.util.Map;
import java.util.Optional;
import java.util.concurrent.CompletableFuture;

/**
 * Command line bridge used by Peergos Snap (the Windows app). Secrets are read from stdin, never from
 * the command line, so they do not show up in process listings.
 *
 * <pre>
 * folder  --server URL --link SECRET_LINK --file PATH [--name NAME]
 *         stdin line 1: the link's user password (empty line if none)
 * account --server URL --user NAME --file PATH [--folder SUBFOLDER] [--name NAME]
 *         stdin line 1: account password, line 2: TOTP code (empty if none)
 * check   --server URL --link SECRET_LINK           (stdin as for folder)
 * account-check --server URL --user NAME            (stdin as for account)
 * Progress: "@progress N" lines (percent) on stderr while uploading.
 * </pre>
 *
 * Output: one JSON line on stdout, {"ok":true,"link":"...","path":"..."} or {"ok":false,"error":"..."}.
 */
public class PeergosBridge {

    static final PrintStream PROGRESS = System.err;

    public static void main(String[] args) {
        PrintStream out = System.out;
        // Peergos logs to stdout/stderr; keep stdout for our single JSON result.
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

    static String run(String[] args) throws Exception {
        if (args.length == 0)
            throw new IllegalArgumentException("usage: folder|account|check ...");
        String cmd = args[0];
        Map<String, String> a = parse(args);
        BufferedReader stdin = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        String server = trimSlash(a.getOrDefault("server", "https://peergos.net"));
        Crypto crypto = Builder.initCrypto();
        NetworkAccess network = Builder.buildJavaNetworkAccess(new URL(server + "/"), !isLocal(server),
                Optional.of("PeergosSnap"), Optional.empty()).join();

        switch (cmd) {
            case "folder":
            case "check": {
                String link = require(a, "link");
                String userPassword = line(stdin);
                String relative = secretLinkPath(link, server);
                UserContext ctx = UserContext.fromSecretLinkV2(relative, () -> Futures.of(userPassword), network, crypto).join();
                String entry = ctx.getEntryPath().join();
                FileWrapper dir = ctx.getByPath(entry).join()
                        .orElseThrow(() -> new IllegalStateException("The shared folder could not be opened"));
                if (!dir.isDirectory())
                    throw new IllegalStateException("The link points to a file, not a folder");
                if (!dir.isWritable())
                    throw new IllegalStateException("The shared folder link is read-only (it must be a writable link)");
                if (cmd.equals("check"))
                    return "{\"ok\":true,\"path\":" + json(entry) + "}";
                Path file = Path.of(require(a, "file"));
                String name = a.getOrDefault("name", file.getFileName().toString());
                name = uniqueName(dir, name, network, crypto);
                FileWrapper uploaded = upload(dir, name, file, network, crypto);
                // A read-only capability for just this file; the folder stays private.
                String fileLink = server + "/" + uploaded.toLink() + "?open=true";
                return "{\"ok\":true,\"link\":" + json(fileLink) + ",\"path\":" + json(entry + "/" + name) + "}";
            }
            case "account":
            case "account-check": {
                String user = require(a, "user");
                String password = line(stdin);
                String totp = line(stdin);
                UserContext ctx = UserContext.signIn(user, password, req -> {
                    if (totp.isEmpty())
                        return Futures.errored(new IllegalStateException("This account needs a two-factor (TOTP) code"));
                    MultiFactorAuthMethod m = req.methods.stream()
                            .filter(x -> x.type == MultiFactorAuthMethod.Type.TOTP)
                            .findFirst()
                            .orElseThrow(() -> new IllegalStateException("Only TOTP two-factor login is supported"));
                    return Futures.of(new MultiFactorAuthResponse(m.credentialId, Either.a(totp)));
                }, network, crypto).join();
                String folder = a.getOrDefault("folder", "PeergosSnap").replace('\\', '/');
                folder = folder.replaceAll("^/+|/+$", "");
                if (cmd.equals("account-check"))
                    return "{\"ok\":true,\"path\":" + json("/" + user) + "}";
                FileWrapper dir = ensureFolder(ctx, "/" + user, folder, network, crypto);
                Path file = Path.of(require(a, "file"));
                String name = uniqueName(dir, a.getOrDefault("name", file.getFileName().toString()), network, crypto);
                upload(dir, name, file, network, crypto);
                String path = "/" + user + (folder.isEmpty() ? "" : "/" + folder) + "/" + name;
                LinkProperties props = ctx.createSecretLink(path, false, Optional.empty(), Optional.empty(), "", true).join();
                String link = server + "/" + ctx.getLinkString(props);
                return "{\"ok\":true,\"link\":" + json(link) + ",\"path\":" + json(path) + "}";
            }
            default:
                throw new IllegalArgumentException("unknown command " + cmd);
        }
    }

    static FileWrapper upload(FileWrapper dir, String name, Path file, NetworkAccess network, Crypto crypto) throws Exception {
        long size = Files.size(file);
        long[] done = {0};
        long[] lastPct = {-1};
        FileWrapper updated = dir.uploadOrReplaceFile(name, new FileAsyncReader(file.toFile()), size,
                network, crypto, () -> false, x -> {
                    done[0] += x;
                    long pct = size == 0 ? 100 : Math.min(100, done[0] * 100 / size);
                    if (pct != lastPct[0]) {
                        lastPct[0] = pct;
                        PROGRESS.println("@progress " + pct);
                        PROGRESS.flush();
                    }
                }).join();
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

    /** "https://host/secret/owner/label#pw" -> "/secret/owner/label#pw" */
    static String secretLinkPath(String link, String server) {
        link = link.trim();
        int i = link.indexOf("/secret/");
        if (i < 0)
            throw new IllegalArgumentException("Not a Peergos secret link (expected .../secret/<owner>/<id>#<key>)");
        return link.substring(i);
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
