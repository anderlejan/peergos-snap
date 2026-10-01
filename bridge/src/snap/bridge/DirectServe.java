/*
 * Peergos Snap bridge - direct sharing between two friends ("serve" mode).
 * Copyright (C) 2026 Jan Anderle
 *
 * This program is free software: you can redistribute it and/or modify it under the terms of the
 * GNU Affero General Public License as published by the Free Software Foundation, version 3.
 * It is linked against Peergos (https://github.com/Peergos/Peergos), which is AGPL-3.0.
 * See bridge/LICENSE.
 */
package snap.bridge;

import peergos.shared.Crypto;
import peergos.shared.NetworkAccess;
import peergos.shared.social.FollowRequestWithCipherText;
import peergos.shared.user.FileSharedWithState;
import peergos.shared.user.SocialState;
import peergos.shared.user.UserContext;
import peergos.shared.user.fs.AsyncReader;
import peergos.shared.user.fs.FileProperties;
import peergos.shared.user.fs.FileWrapper;
import peergos.shared.util.PathUtil;

import java.io.BufferedReader;
import java.io.OutputStream;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.time.ZoneOffset;
import java.util.*;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

/**
 * Long-running session for the direct mode. Each user keeps one folder per friend,
 * <code>/ME/PeergosSnap-Direct/FRIEND</code>, shared for writing with that friend only, with one subfolder per month
 * (<code>2026-10</code>). Pictures sent to a friend go into the sender's folder; both sides can label, pin, star and
 * delete in both folders. Labels, pins and stars live next to the pictures in one small file per user
 * (<code>.snapmeta-USER.json</code>), so the two sides never overwrite each other.
 *
 * <pre>
 * stdin:  the session, then one JSON command per line: {"id":"1","cmd":"...", ...}
 * stdout: one JSON line per answer {"id":"1","ok":true,...} | {"id":"1","ok":false,"error":"..."}
 *         and events {"event":"ready"|"changed"|"problem", ...}
 * commands: friends · add {user} · accept {user} · decline {user} · open {friend} · list {friend, month}
 *           send {friend, file, [name], [month]} · get {path, to} · delete {path}
 *           meta {path, [label], [pin], [star]} · watch {friends:"a,b", month, [interval]} · quit
 * </pre>
 * While watching, the folders of the watched friends are checked every few seconds; a "changed" event with the full
 * list is printed whenever something in them changed (a new picture, a delete, a label …).
 */
final class DirectServe {
    static final String ROOT = "PeergosSnap-Direct";
    static final String META_PREFIX = ".snapmeta-";

    UserContext ctx;
    NetworkAccess net;
    final String me;
    final Crypto crypto;
    final String session;
    final PrintStream out;
    final Set<String> sharedWith = new HashSet<>();
    final Map<String, String> lastSignature = new HashMap<>();
    final Map<String, Object[]> metaCache = new HashMap<>(); // path -> {modified, size, parsed}
    List<String> watchFriends = new ArrayList<>();
    String watchMonth = "";
    long interval = 3000;
    long polls;
    String lastProblem = "";

    DirectServe(UserContext ctx, String me, String session, NetworkAccess net, Crypto crypto, PrintStream out) {
        this.ctx = ctx;
        this.me = me;
        this.session = session;
        this.net = net;
        this.crypto = crypto;
        this.out = out;
    }

    /** Signs in again from the session on a cleared network: forgets every cached folder version. */
    void refresh() {
        net = net.clear();
        ctx = PeergosBridge.restore(me, session, net, crypto);
    }

    synchronized void emit(Map<String, Object> m) {
        out.println(Json.write(m));
        out.flush();
    }

    static Map<String, Object> map(Object... kv) {
        Map<String, Object> m = new LinkedHashMap<>();
        for (int i = 0; i + 1 < kv.length; i += 2)
            m.put((String) kv[i], kv[i + 1]);
        return m;
    }

    String run(BufferedReader stdin) throws Exception {
        BlockingQueue<String> lines = new LinkedBlockingQueue<>();
        String eof = "\u0000eof";
        Thread reader = new Thread(() -> {
            try {
                for (String l; (l = stdin.readLine()) != null; )
                    lines.add(l);
            } catch (Exception ignored) {
            }
            lines.add(eof);
        }, "stdin");
        reader.setDaemon(true);
        reader.start();
        emit(map("event", "ready", "user", me));
        long next = System.currentTimeMillis() + interval;
        while (true) {
            String line = lines.poll(Math.max(0, next - System.currentTimeMillis()), TimeUnit.MILLISECONDS);
            if (line == null) {
                poll();
                next = System.currentTimeMillis() + interval;
                continue;
            }
            if (line.equals(eof))
                return "{\"ok\":true,\"event\":\"closed\"}";
            if (line.isBlank())
                continue;
            String id = null;
            try {
                Map<String, Object> c = Json.parseObject(line);
                id = Json.str(c, "id");
                String cmd = Json.str(c, "cmd");
                if ("quit".equals(cmd)) {
                    emit(map("id", id, "ok", true));
                    return "{\"ok\":true,\"event\":\"closed\"}";
                }
                Map<String, Object> r = handle(cmd == null ? "" : cmd, c);
                Map<String, Object> answer = map("id", id, "ok", true);
                answer.putAll(r);
                emit(answer);
                // A change made here shows up at once, not only at the next check.
                if (!"list".equals(cmd) && !"friends".equals(cmd) && !"get".equals(cmd))
                    next = System.currentTimeMillis();
            } catch (Throwable t) {
                t.printStackTrace(); // stderr: the app's log keeps the last lines of a failure
                emit(map("id", id, "ok", false, "error", message(t)));
            }
        }
    }

    static String message(Throwable t) {
        Throwable r = t;
        while (r.getCause() != null && r.getCause() != r)
            r = r.getCause();
        return r.getMessage() == null ? r.getClass().getSimpleName() : r.getMessage();
    }

    Map<String, Object> handle(String cmd, Map<String, Object> c) throws Exception {
        switch (cmd) {
            case "friends": return friends();
            case "add": {
                String user = name(Json.str(c, "user"));
                if (user.equals(me))
                    throw new IllegalArgumentException("That is your own username");
                boolean sent = ctx.sendInitialFollowRequest(user).join();
                return map("sent", sent);
            }
            case "accept":
            case "decline": {
                String user = name(Json.str(c, "user"));
                SocialState st = ctx.getSocialState().join();
                for (FollowRequestWithCipherText r : st.pendingIncoming)
                    if (user.equals(r.getEntry().ownerName)) {
                        boolean yes = cmd.equals("accept");
                        ctx.sendReplyFollowRequest(r, yes, yes).join();
                        return map("done", true);
                    }
                throw new IllegalStateException("No friend request from " + user);
            }
            case "open": return map("folder", ensureShared(name(Json.str(c, "friend"))));
            case "list": {
                String friend = name(Json.str(c, "friend"));
                String month = month(Json.str(c, "month"));
                return map("friend", friend, "month", month, "items", list(friend, month), "months", months(friend));
            }
            case "send": return map("item", send(c));
            case "get": return map("file", get(direct(Json.str(c, "path")), Path.of(Objects.requireNonNull(Json.str(c, "to"), "to"))).toString());
            case "delete": {
                String path = direct(Json.str(c, "path"));
                return map("deleted", retry(() -> {
                    Optional<FileWrapper> f = ctx.getByPath(path).join();
                    if (f.isEmpty())
                        return false;
                    FileWrapper parent = folder(parent(path));
                    f.get().remove(parent, PathUtil.get(path), ctx).join();
                    return true;
                }));
            }
            case "meta": return map("meta", retry(() -> meta(c)));
            case "watch": {
                List<String> fs = new ArrayList<>();
                String list = Json.str(c, "friends");
                if (list != null)
                    for (String f : list.split(","))
                        if (!f.isBlank())
                            fs.add(name(f));
                watchFriends = fs;
                watchMonth = month(Json.str(c, "month"));
                interval = Math.max(1000, Math.min(60000, Json.num(c, "interval", 3000)));
                lastSignature.clear();
                return map("watching", fs, "month", watchMonth);
            }
            default:
                throw new IllegalArgumentException("unknown command " + cmd);
        }
    }

    // ---------- friends ----------

    Map<String, Object> friends() {
        SocialState st = ctx.getSocialState().join();
        List<String> incoming = new ArrayList<>();
        for (FollowRequestWithCipherText r : st.pendingIncoming)
            incoming.add(r.getEntry().ownerName);
        return map("friends", sorted(st.getFriends()), "followers", sorted(st.getFollowers()), "following", sorted(st.getFollowing()),
                "incoming", sorted(incoming), "outgoing", sorted(st.pendingOutgoing));
    }

    static List<String> sorted(Collection<String> c) {
        List<String> l = new ArrayList<>(c);
        Collections.sort(l);
        return l;
    }

    /** The folder for this friend exists and is shared for writing with them (only friends can be shared with). */
    String ensureShared(String friend) {
        String path = "/" + me + "/" + ROOT + "/" + friend;
        if (sharedWith.contains(friend))
            return path;
        if (!ctx.getSocialState().join().getFriends().contains(friend))
            throw new IllegalStateException("Not friends with " + friend + " yet: send a friend request and wait until " + friend + " accepts it");
        PeergosBridge.ensureFolder(ctx, "/" + me, ROOT + "/" + friend, net, crypto);
        FileSharedWithState st = ctx.sharedWith(PathUtil.get(path)).join();
        if (!st.writeAccess.contains(friend))
            ctx.shareWriteAccessWith(PathUtil.get(path), Set.of(friend)).join();
        sharedWith.add(friend);
        return path;
    }

    // ---------- listing ----------

    String mine(String friend) { return "/" + me + "/" + ROOT + "/" + friend; }
    String theirs(String friend) { return "/" + friend + "/" + ROOT + "/" + me; }

    List<String> months(String friend) {
        TreeSet<String> ms = new TreeSet<>(Comparator.reverseOrder());
        for (String dir : List.of(mine(friend), theirs(friend))) {
            Optional<FileWrapper> d = ctx.getByPath(dir).join();
            if (d.isEmpty())
                continue;
            for (FileWrapper k : d.get().getChildren(crypto.hasher, net).join()) {
                String n = k.getFileProperties().name;
                if (k.isDirectory() && n.matches("\\d{4}-\\d{2}"))
                    ms.add(n);
            }
        }
        return new ArrayList<>(ms);
    }

    /** All pictures of one month between me and a friend (both folders), with labels, pins and stars merged. */
    List<Object> list(String friend, String month) {
        List<Object> items = new ArrayList<>();
        for (String[] side : new String[][]{{mine(friend) + "/" + month, me}, {theirs(friend) + "/" + month, friend}}) {
            Optional<FileWrapper> d = ctx.getByPath(side[0]).join();
            if (d.isEmpty() || !d.get().isDirectory())
                continue;
            Set<FileWrapper> kids = d.get().getChildren(crypto.hasher, net).join();
            Map<String, Map<String, Object>> meta = new HashMap<>();
            List<String[]> metaUsers = new ArrayList<>();
            for (FileWrapper k : kids) {
                String n = k.getFileProperties().name;
                if (!k.isDirectory() && n.startsWith(META_PREFIX) && n.endsWith(".json"))
                    metaUsers.add(new String[]{n.substring(META_PREFIX.length(), n.length() - 5), side[0] + "/" + n});
            }
            for (String[] mu : metaUsers)
                mergeMeta(meta, mu[0], readMeta(mu[1], kids));
            for (FileWrapper k : kids) {
                FileProperties fp = k.getFileProperties();
                if (k.isDirectory() || fp.name.startsWith("."))
                    continue;
                Map<String, Object> m = meta.getOrDefault(fp.name, Map.of());
                items.add(map("name", fp.name, "path", side[0] + "/" + fp.name, "from", side[1], "size", fp.size,
                        "modified", fp.modified.toEpochSecond(ZoneOffset.UTC) * 1000,
                        "label", m.getOrDefault("label", ""), "pinned", m.getOrDefault("pinned", false),
                        "stars", m.getOrDefault("stars", List.of())));
            }
        }
        return items;
    }

    /** One user's meta file: {"v":1,"items":{"NAME":{"label":"…","labelAt":ms,"pin":true,"pinAt":ms,"star":true}}}. */
    @SuppressWarnings("unchecked")
    Map<String, Object> readMeta(String path, Set<FileWrapper> kids) {
        String name = path.substring(path.lastIndexOf('/') + 1);
        FileWrapper f = kids.stream().filter(k -> k.getFileProperties().name.equals(name)).findFirst().orElse(null);
        if (f == null)
            return Map.of();
        FileProperties fp = f.getFileProperties();
        long modified = fp.modified.toEpochSecond(ZoneOffset.UTC);
        Object[] cached = metaCache.get(path);
        // Re-read when it changed, and now and then anyway (the modified time only has seconds).
        if (cached != null && (long) cached[0] == modified && (long) cached[1] == fp.size && polls % 10 != 0)
            return (Map<String, Object>) cached[2];
        try {
            Map<String, Object> doc = Json.parseObject(new String(read(f), StandardCharsets.UTF_8));
            Object items = doc.get("items");
            Map<String, Object> parsed = items instanceof Map ? (Map<String, Object>) items : Map.of();
            metaCache.put(path, new Object[]{modified, fp.size, parsed});
            return parsed;
        } catch (Exception e) {
            return Map.of(); // a damaged meta file only loses labels, never pictures
        }
    }

    /** Label and pin: the newest change of either side wins. Stars: everyone who starred. */
    @SuppressWarnings("unchecked")
    static void mergeMeta(Map<String, Map<String, Object>> into, String user, Map<String, Object> items) {
        for (Map.Entry<String, Object> e : items.entrySet()) {
            if (!(e.getValue() instanceof Map))
                continue;
            Map<String, Object> it = (Map<String, Object>) e.getValue();
            Map<String, Object> m = into.computeIfAbsent(e.getKey(), k -> new HashMap<>(Map.of("labelAt", -1L, "pinAt", -1L, "stars", new ArrayList<String>())));
            long la = Json.num(it, "labelAt", -1), pa = Json.num(it, "pinAt", -1);
            if (it.containsKey("label") && la > (long) m.get("labelAt")) {
                m.put("label", Json.str(it, "label"));
                m.put("labelAt", la);
            }
            if (it.containsKey("pin") && pa > (long) m.get("pinAt")) {
                m.put("pinned", Json.bool(it, "pin"));
                m.put("pinAt", pa);
            }
            if (Json.bool(it, "star"))
                ((List<String>) m.get("stars")).add(user);
        }
        for (Map<String, Object> m : into.values())
            Collections.sort((List<String>) m.get("stars"));
    }

    // ---------- changes ----------

    Map<String, Object> send(Map<String, Object> c) throws Exception {
        String friend = name(Json.str(c, "friend"));
        Path file = Path.of(Objects.requireNonNull(Json.str(c, "file"), "file"));
        String month = month(Json.str(c, "month"));
        String wanted = Json.str(c, "name");
        if (wanted == null || wanted.isBlank())
            wanted = file.getFileName().toString();
        wanted = wanted.replace('/', '_').replace('\\', '_');
        if (wanted.startsWith("."))
            wanted = "_" + wanted.substring(1);
        ensureShared(friend);
        String rel = ROOT + "/" + friend + "/" + month;
        String finalWanted = wanted;
        String name = retry(() -> {
            FileWrapper dir = PeergosBridge.ensureFolder(ctx, "/" + me, rel, net, crypto);
            String n = PeergosBridge.uniqueName(dir, finalWanted, net, crypto);
            PeergosBridge.upload(dir, n, file, net, crypto);
            return n;
        });
        return map("name", name, "path", "/" + me + "/" + rel + "/" + name, "from", me, "size", Files.size(file),
                "modified", System.currentTimeMillis(), "label", "", "pinned", false, "stars", List.of());
    }

    Path get(String path, Path to) throws Exception {
        FileWrapper f = ctx.getByPath(path).join().orElseThrow(() -> new IllegalStateException("Not found: " + path));
        Files.createDirectories(to.toAbsolutePath().getParent());
        Path part = to.resolveSibling(to.getFileName() + ".part");
        try (OutputStream o = Files.newOutputStream(part)) {
            AsyncReader in = f.getInputStream(net, crypto, x -> {}).join();
            byte[] buf = new byte[1 << 20];
            long left = f.getSize();
            while (left > 0) {
                int n = in.readIntoArray(buf, 0, (int) Math.min(buf.length, left)).join();
                if (n <= 0)
                    break;
                o.write(buf, 0, n);
                left -= n;
            }
            in.close();
        }
        Files.move(part, to, StandardCopyOption.REPLACE_EXISTING);
        return to;
    }

    byte[] read(FileWrapper f) throws Exception {
        long size = f.getSize();
        if (size > 4_000_000)
            throw new IllegalStateException("too large");
        byte[] all = new byte[(int) size];
        AsyncReader in = f.getInputStream(net, crypto, x -> {}).join();
        int off = 0;
        while (off < all.length) {
            int n = in.readIntoArray(all, off, all.length - off).join();
            if (n <= 0)
                break;
            off += n;
        }
        in.close();
        return all;
    }

    @SuppressWarnings("unchecked")
    Map<String, Object> meta(Map<String, Object> c) throws Exception {
        String path = direct(Json.str(c, "path"));
        String dirPath = parent(path), item = path.substring(path.lastIndexOf('/') + 1);
        FileWrapper dir = folder(dirPath);
        String metaName = META_PREFIX + me + ".json";
        Map<String, Object> doc = new LinkedHashMap<>(Map.of("v", 1L, "items", new LinkedHashMap<String, Object>()));
        Optional<FileWrapper> existing = dir.getChild(metaName, crypto.hasher, net).join();
        if (existing.isPresent()) {
            try {
                Map<String, Object> d = Json.parseObject(new String(read(existing.get()), StandardCharsets.UTF_8));
                if (d.get("items") instanceof Map)
                    doc.put("items", new LinkedHashMap<>((Map<String, Object>) d.get("items")));
            } catch (Exception ignored) {
                // start a fresh file; the other side's labels are in its own file
            }
        }
        Map<String, Object> items = (Map<String, Object>) doc.get("items");
        Map<String, Object> it = items.get(item) instanceof Map ? new LinkedHashMap<>((Map<String, Object>) items.get(item)) : new LinkedHashMap<>();
        long now = System.currentTimeMillis();
        if (c.containsKey("label")) { it.put("label", Json.str(c, "label")); it.put("labelAt", now); }
        if (c.containsKey("pin")) { it.put("pin", Json.bool(c, "pin")); it.put("pinAt", now); }
        if (c.containsKey("star")) it.put("star", Json.bool(c, "star"));
        items.put(item, it);
        // Entries of pictures that are gone are dropped, so the file stays small.
        Set<String> present = new HashSet<>();
        for (FileWrapper k : dir.getChildren(crypto.hasher, net).join())
            present.add(k.getFileProperties().name);
        items.keySet().removeIf(k -> !present.contains(k));
        byte[] bytes = Json.write(doc).getBytes(StandardCharsets.UTF_8);
        dir.uploadOrReplaceFile(metaName, AsyncReader.build(bytes), bytes.length, net, crypto, () -> false, x -> {}).join();
        metaCache.remove(dirPath + "/" + metaName);
        return it;
    }

    interface Step<T> { T run() throws Exception; }

    /** Both sides write into the same folders. When the other side changed a folder since this session last saw it,
     * Peergos refuses the write ("concurrent modification"): the session then reloads and writes on the newest version. */
    <T> T retry(Step<T> step) throws Exception {
        for (int attempt = 1; ; attempt++) {
            try {
                return step.run();
            } catch (Exception e) {
                String m = message(e);
                boolean stale = m != null && (m.toLowerCase(Locale.ROOT).contains("concurrent") || m.equals("No value present"));
                if (attempt >= 3 || !stale)
                    throw e;
                System.err.println("direct: reloading after: " + m);
                refresh();
            }
        }
    }

    FileWrapper folder(String path) {
        return ctx.getByPath(path).join().orElseThrow(() -> new IllegalStateException("Folder not found: " + path));
    }

    // ---------- watching ----------

    void poll() {
        if (watchFriends.isEmpty())
            return;
        polls++;
        for (String friend : watchFriends) {
            try {
                List<Object> items = list(friend, watchMonth);
                String sig = Json.write(items);
                if (!sig.equals(lastSignature.get(friend))) {
                    lastSignature.put(friend, sig);
                    emit(map("event", "changed", "friend", friend, "month", watchMonth, "items", items));
                }
                lastProblem = "";
            } catch (Throwable t) {
                String m = message(t);
                if (!m.equals(lastProblem)) // say it once, not every few seconds
                    emit(map("event", "problem", "friend", friend, "error", m));
                lastProblem = m;
            }
        }
    }

    // ---------- checks ----------

    /** A Peergos username (never a path). */
    static String name(String s) {
        String n = s == null ? "" : s.trim().toLowerCase(Locale.ROOT);
        if (!n.matches("[a-z0-9_-]{1,64}"))
            throw new IllegalArgumentException("Not a Peergos username: " + s);
        return n;
    }

    static String month(String s) {
        if (s == null || s.isBlank())
            return java.time.YearMonth.now().toString();
        if (!s.matches("\\d{4}-\\d{2}"))
            throw new IllegalArgumentException("Not a month: " + s);
        return s;
    }

    /** Only files in the direct folders between me and a friend can be read, changed or deleted here. */
    String direct(String path) {
        if (path == null || path.contains("/../") || path.contains("\\") || path.endsWith("/.."))
            throw new IllegalArgumentException("Not a direct-sharing path: " + path);
        String[] p = path.split("/");
        // "", owner, ROOT, other, month, name
        boolean shape = p.length == 6 && p[0].isEmpty() && p[2].equals(ROOT) && p[4].matches("\\d{4}-\\d{2}") && !p[5].isEmpty();
        if (!shape || !(p[1].equals(me) || p[3].equals(me)))
            throw new IllegalArgumentException("Not a direct-sharing path: " + path);
        return path;
    }

    static String parent(String path) {
        return path.substring(0, path.lastIndexOf('/'));
    }
}
