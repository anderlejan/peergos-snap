/*
 * Peergos Snap bridge - direct sharing between two friends ("serve" mode).
 * Copyright (C) 2026 anderlejan
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

import javax.imageio.IIOImage;
import javax.imageio.ImageIO;
import javax.imageio.ImageWriteParam;
import javax.imageio.ImageWriter;
import javax.imageio.stream.ImageOutputStream;
import java.awt.Color;
import java.awt.Graphics2D;
import java.awt.RenderingHints;
import java.awt.image.BufferedImage;
import java.io.BufferedReader;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.OutputStream;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.time.ZoneOffset;
import java.util.*;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionException;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;

/**
 * Long-running session for the direct mode. Each user keeps one folder per friend,
 * <code>/ME/PeergosSnap/Direct/FRIEND</code> (inside the app's main folder since 2.3; 2.2 used
 * <code>/ME/PeergosSnap-Direct/FRIEND</code>, which is still read), with one subfolder per month (<code>2026-10</code>). The folders are not
 * shared: every file sent to a friend is uploaded there and then that one file is shared read-only with that friend.
 * The friend's session copies it into their own <code>MONTH/received</code> folder: from then on each side has its own
 * copy and can only ever delete its own (deleting what you sent does not touch the copy your friend already has).
 * Labels, pins and stars of both sides' files are kept by each user in their own month folder
 * (<code>.snapmeta-USER.json</code>, keyed "SENDER/NAME", also remembering which files were copied: "got"), shared
 * read-only with the friend, so the two sides never write into each other's folders.
 *
 * <pre>
 * stdin:  the session, then one JSON command per line: {"id":"1","cmd":"...", ...}
 * stdout: one JSON line per answer {"id":"1","ok":true,...} | {"id":"1","ok":false,"error":"..."}
 *         and events {"event":"ready"|"changed"|"problem", ...}
 * commands: friends · discover · add {user} · accept {user} · decline {user} · open {friend} · list {friend, month}
 *           send {friend, file, [name], [month], [thumb]} · get {path, to | inline} · delete {path}
 *           preview {path} · thumb {path, thumb}
 *           meta {path, [label], [pin], [star]} · watch {friends:"a,b", month, [interval]} · quit
 * </pre>
 * While watching, the folders of the watched friends are checked every few seconds; a "changed" event with the full
 * list is printed whenever something in them changed (a new picture, a delete, a label …).
 */
final class DirectServe {
    /** Where new direct files go: inside the app's main folder. */
    static final String ROOT = "PeergosSnap/Direct";
    /** Where 2.2 kept them: still read (and deletable), never written to. */
    static final String LEGACY = "PeergosSnap-Direct";
    static final String META_PREFIX = ".snapmeta-";

    UserContext ctx;
    NetworkAccess net;
    final String me;
    final Crypto crypto;
    final String session;
    final PrintStream out;
    /** Friends whose folder on my side exists (checked once per session). */
    final Set<String> opened = new HashSet<>();
    final Map<String, String> lastSignature = new HashMap<>();
    final Map<String, Object[]> metaCache = new HashMap<>(); // path -> {modified, size, parsed}
    /** Friends whose folders did not answer: not checked again before this time (ms). */
    final Map<String, Long> pausedUntil = new HashMap<>();
    static final long PAUSE_MS = 120_000;
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
                if (!"list".equals(cmd) && !"friends".equals(cmd) && !"discover".equals(cmd) && !"get".equals(cmd))
                    next = System.currentTimeMillis();
            } catch (Throwable t) {
                t.printStackTrace(); // stderr: the app's log keeps the last lines of a failure
                emit(map("id", id, "ok", false, "error", message(t)));
            }
        }
    }

    static Throwable rootCause(Throwable t) {
        Throwable r = t;
        while (r.getCause() != null && r.getCause() != r)
            r = r.getCause();
        return r;
    }

    static String message(Throwable t) {
        Throwable r = rootCause(t);
        return r.getMessage() == null ? r.getClass().getSimpleName() : r.getMessage();
    }

    Map<String, Object> handle(String cmd, Map<String, Object> c) throws Exception {
        switch (cmd) {
            case "friends": return friends();
            case "discover": {
                // Friends who have sent me something directly: the app watches them without being asked.
                // A fresh session, so files shared with me since the last check are seen.
                refresh();
                opened.clear();
                Map<String, Object> r = friends();
                List<String> direct = new ArrayList<>();
                for (Object f : (List<?>) r.get("friends"))
                    try {
                        if (await(ctx.getByPath(theirs((String) f))).isPresent()
                                || await(ctx.getByPath(theirsLegacy((String) f))).isPresent())
                            direct.add((String) f);
                    } catch (CompletionException e) {
                        System.err.println("direct: discover " + f + ": " + message(e)); // one friend does not stop the others
                    }
                r.put("direct", direct);
                return r;
            }
            case "add": {
                String user = name(Json.str(c, "user"));
                if (user.equals(me))
                    throw new IllegalArgumentException("That is your own username");
                boolean sent = await(ctx.sendInitialFollowRequest(user), WRITE);
                return map("sent", sent);
            }
            case "accept":
            case "decline": {
                String user = name(Json.str(c, "user"));
                SocialState st = await(ctx.getSocialState());
                for (FollowRequestWithCipherText r : st.pendingIncoming)
                    if (user.equals(r.getEntry().ownerName)) {
                        boolean yes = cmd.equals("accept");
                        await(ctx.sendReplyFollowRequest(r, yes, yes), WRITE);
                        return map("done", true);
                    }
                throw new IllegalStateException("No friend request from " + user);
            }
            case "open": return map("folder", ensureOpen(name(Json.str(c, "friend"))));
            case "list": {
                String friend = name(Json.str(c, "friend"));
                String month = month(Json.str(c, "month"));
                return map("friend", friend, "month", month, "items", list(friend, month), "months", months(friend));
            }
            case "send": return map("item", send(c));
            case "get": {
                String path = direct(Json.str(c, "path"));
                // "inline": the bytes in the answer (base64) – for showing a picture without writing it to disk.
                if (Json.bool(c, "inline")) {
                    FileWrapper f = await(ctx.getByPath(path)).orElseThrow(() -> new IllegalStateException("Not found: " + path));
                    return map("data", Base64.getEncoder().encodeToString(read(f, 40_000_000)));
                }
                return map("file", get(path, Path.of(Objects.requireNonNull(Json.str(c, "to"), "to"))).toString());
            }
            case "delete": {
                String path = direct(Json.str(c, "path"));
                // Only my own copies: what I sent, or my copy of what I received. The friend's copy is theirs.
                if (!parse(path)[0].equals(me))
                    throw new IllegalArgumentException("That is your friend's copy: only they can delete it");
                return map("deleted", retry(() -> {
                    Optional<FileWrapper> f = await(ctx.getByPath(path));
                    if (f.isEmpty())
                        return false;
                    FileWrapper parent = folder(parent(path));
                    await(f.get().remove(parent, PathUtil.get(path), ctx), WRITE);
                    return true;
                }));
            }
            case "meta": return map("meta", retry(() -> meta(c)));
            case "preview": {
                // The small picture of one of my copies: the one Peergos keeps, else (a picture) made now from the file
                // and kept with it, so it is there next time – for me and in the web app.
                String path = direct(Json.str(c, "path"));
                if (!parse(path)[0].equals(me))
                    throw new IllegalArgumentException("Only your own copies get a preview here");
                FileWrapper f = await(ctx.getByPath(path)).orElseThrow(() -> new IllegalStateException("Not found: " + path));
                String thumb = thumbOf(f);
                if (thumb == null && isPicture(f.getFileProperties().name) && f.getSize() <= 30_000_000) {
                    thumb = makeThumb(readAll(f, 30_000_000, net, crypto));
                    if (thumb != null)
                        setThumb(f, thumb);
                }
                return map("thumb", thumb);
            }
            case "thumb": {
                // A small picture the app made (of a video), kept with my copy.
                String path = direct(Json.str(c, "path"));
                if (!parse(path)[0].equals(me))
                    throw new IllegalArgumentException("Only your own copies can be changed");
                FileWrapper f = await(ctx.getByPath(path)).orElseThrow(() -> new IllegalStateException("Not found: " + path));
                setThumb(f, Json.str(c, "thumb"));
                return map("thumb", thumbOf(f) != null);
            }
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
                pausedUntil.clear();
                return map("watching", fs, "month", watchMonth);
            }
            default:
                throw new IllegalArgumentException("unknown command " + cmd);
        }
    }

    // ---------- friends ----------

    Map<String, Object> friends() {
        SocialState st = await(ctx.getSocialState());
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

    /** My folder for this friend exists (files can only be shared with friends). */
    String ensureOpen(String friend) {
        String path = mine(friend);
        requireUnexposed(friend); // the folder's name alone tells who the friend is
        if (opened.contains(friend))
            return path;
        if (!await(ctx.getSocialState()).getFriends().contains(friend))
            throw new IllegalStateException("Not friends with " + friend + " yet: send a friend request and wait until " + friend + " accepts it");
        PeergosBridge.ensureFolder(ctx, "/" + me, ROOT + "/" + friend, net, crypto);
        opened.add(friend);
        return path;
    }
    // ---------- listing ----------

    /** Folders found unshared, and until when that is trusted (looked at again every minute). */
    final Map<String, Long> unexposedUntil = new HashMap<>();
    /** Per friend, the last "folder is shared" problem reported while listing (said once, not at every check). */
    final Map<String, String> exposureSaid = new HashMap<>();

    /**
     * The direct folders lie inside PeergosSnap. If PeergosSnap, PeergosSnap/Direct or the friend's folder there is
     * shared with someone or has a secret link, everything sent and received directly would be visible to them too.
     * Returns what is shared (a short message), or null. A check that fails (e.g. the folder does not exist yet, or
     * Peergos does not answer) counts as not shared and is not repeated for a minute.
     */
    String exposure(String friend) {
        for (String p : List.of("/" + me + "/PeergosSnap", "/" + me + "/" + ROOT, mine(friend))) {
            Long until = unexposedUntil.get(p);
            if (until != null && until > System.currentTimeMillis())
                continue;
            FileSharedWithState s;
            try {
                s = await(ctx.sharedWith(PathUtil.get(p)));
            } catch (Exception e) {
                System.err.println("direct: sharing state of " + p + ": " + message(e));
                unexposedUntil.put(p, System.currentTimeMillis() + 60_000);
                continue;
            }
            List<String> who = new ArrayList<>(s.readAccess);
            for (String w : s.writeAccess)
                if (!who.contains(w))
                    who.add(w);
            if (!who.isEmpty() || !s.links.isEmpty()) {
                String how = (who.isEmpty() ? "" : " with " + String.join(", ", who))
                        + (s.links.isEmpty() ? "" : (who.isEmpty() ? " through " : " and through ") + s.links.size()
                        + (s.links.size() == 1 ? " secret link" : " secret links"));
                return p.substring(me.length() + 2) + " is shared" + how + ": direct sharing is paused until you remove that in Peergos.";
            }
            unexposedUntil.put(p, System.currentTimeMillis() + 60_000);
        }
        return null;
    }

    /** Nothing is created or written below a shared or linked folder (see {@link #exposure}). */
    void requireUnexposed(String friend) {
        String exposed = exposure(friend);
        if (exposed != null)
            throw new IllegalStateException(exposed);
    }

    /**
     * Each user's meta of 2.2 and of 2.3 combined field by field. Label and pin keep their rule "the latest change
     * wins" (labelAt, pinAt), also across the two files; the other fields (star, got) take the 2.3 file's value: a star
     * taken away since the update (written only to the new file) does not come back, and a 2.2 label stays when only a
     * star was added.
     */
    @SuppressWarnings("unchecked")
    static Map<String, Object> combine(Map<String, Object> older, Map<String, Object> newer) {
        Map<String, Object> out = new LinkedHashMap<>(older);
        for (Map.Entry<String, Object> e : newer.entrySet()) {
            if (out.get(e.getKey()) instanceof Map<?, ?> o && e.getValue() instanceof Map<?, ?> n) {
                Map<String, Object> old = (Map<String, Object>) o, neu = (Map<String, Object>) n;
                Map<String, Object> m = new LinkedHashMap<>(old);
                m.putAll(neu);
                for (String[] f : new String[][]{{"label", "labelAt"}, {"pin", "pinAt"}})
                    if (old.containsKey(f[0]) && neu.containsKey(f[0]) && stamp(old.get(f[1])) > stamp(neu.get(f[1]))) {
                        m.put(f[0], old.get(f[0]));
                        m.put(f[1], old.get(f[1]));
                    }
                out.put(e.getKey(), m);
            } else
                out.put(e.getKey(), e.getValue());
        }
        return out;
    }

    static long stamp(Object v) {
        return v instanceof Number x ? x.longValue() : 0;
    }

    String mine(String friend) { return "/" + me + "/" + ROOT + "/" + friend; }
    String theirs(String friend) { return "/" + friend + "/" + ROOT + "/" + me; }
    String mineLegacy(String friend) { return "/" + me + "/" + LEGACY + "/" + friend; }
    String theirsLegacy(String friend) { return "/" + friend + "/" + LEGACY + "/" + me; }

    List<String> months(String friend) {
        TreeSet<String> ms = new TreeSet<>(Comparator.reverseOrder());
        for (String dir : List.of(mine(friend), theirs(friend), mineLegacy(friend), theirsLegacy(friend))) {
            Optional<FileWrapper> d = await(ctx.getByPath(dir));
            if (d.isEmpty())
                continue;
            for (FileWrapper k : await(d.get().getChildren(crypto.hasher, net))) {
                String n = k.getFileProperties().name;
                if (k.isDirectory() && n.matches("\\d{4}-\\d{2}"))
                    ms.add(n);
            }
        }
        return new ArrayList<>(ms);
    }

    /** The files of one month folder: my own folder, or the files of the friend's folder that are shared with me
     * (Peergos shows only those). Empty when the folder does not exist or nothing in it is shared with me. */
    Set<FileWrapper> files(String dir) {
        Optional<FileWrapper> d = await(ctx.getByPath(dir));
        if (d.isEmpty() || !d.get().isDirectory())
            return Set.of();
        return await(d.get().getChildren(crypto.hasher, net));
    }

    /** Received copies live in my own month folder for the friend, in this subfolder (never shared). */
    static final String RECEIVED = "received";

    /**
     * All files of one month between me and a friend – what I sent, and my own copies of what they sent – with labels,
     * pins and stars of both sides merged. Files the friend shared with me that I have no copy of yet are copied into
     * my <code>received</code> folder first: from then on that copy is mine (the sender deleting theirs does not touch
     * it). Each file is copied once – "got" in my meta file remembers it, so a copy I deleted does not come back.
     */
    List<Object> list(String friend, String month) {
        return list(friend, month, true);
    }

    List<Object> list(String friend, String month, boolean copyNew) {
        // The current folders first, then those of 2.2 (still read, so nothing sent before the move is lost).
        List<String> myDirs = List.of(mine(friend) + "/" + month, mineLegacy(friend) + "/" + month);
        List<String> theirDirs = List.of(theirs(friend) + "/" + month, theirsLegacy(friend) + "/" + month);
        Map<String, Map<String, Object>> meta = new HashMap<>();
        // Each user's meta of 2.3 and of 2.2, combined field by field with 2.3 winning (see combine).
        Map<String, Object> myNew = new LinkedHashMap<>(), myOld = new LinkedHashMap<>();
        Map<String, Object> theirNew = new LinkedHashMap<>(), theirOld = new LinkedHashMap<>();
        Set<String> got = new HashSet<>(); // "SENDER/NAME" of the friend's files I copied before (in either folder)
        List<Object[]> found = new ArrayList<>(); // {file, folder, sender}
        Set<String> have = new HashSet<>();
        for (String myDir : myDirs) {
            Map<String, Object> myInto = myDir.equals(myDirs.get(0)) ? myNew : myOld;
            for (FileWrapper k : files(myDir)) {
                String n = k.getFileProperties().name;
                if (k.isDirectory())
                    continue;
                if (n.equals(META_PREFIX + me + ".json")) {
                    Map<String, Object> m = readMeta(myDir + "/" + n, k);
                    for (Map.Entry<String, Object> e : m.entrySet())
                        if (e.getValue() instanceof Map<?, ?> it && Boolean.TRUE.equals(it.get("got")))
                            got.add(e.getKey());
                    myInto.putAll(m);
                } else if (!n.startsWith("."))
                    found.add(new Object[]{k, myDir, me});
            }
            String inDir = myDir + "/" + RECEIVED;
            for (FileWrapper k : files(inDir)) {
                String n = k.getFileProperties().name;
                if (!k.isDirectory() && !n.startsWith(".")) {
                    found.add(new Object[]{k, inDir, friend});
                    have.add(n);
                }
            }
        }
        Map<String, FileWrapper> shared = new HashMap<>(); // the friend's originals shared with me
        for (String theirDir : theirDirs) {
            Map<String, Object> theirInto = theirDir.equals(theirDirs.get(0)) ? theirNew : theirOld;
            for (FileWrapper k : files(theirDir)) {
                String n = k.getFileProperties().name;
                if (k.isDirectory())
                    continue;
                if (n.equals(META_PREFIX + friend + ".json"))
                    theirInto.putAll(readMeta(theirDir + "/" + n, k));
                else if (!n.startsWith("."))
                    shared.putIfAbsent(n, k);
            }
        }
        mergeMeta(meta, me, combine(myOld, myNew));
        mergeMeta(meta, friend, combine(theirOld, theirNew));
        if (copyNew) {
            List<String> fresh = new ArrayList<>();
            for (String n : shared.keySet())
                if (!have.contains(n) && !got.contains(friend + "/" + n))
                    fresh.add(n);
            String exposed = fresh.isEmpty() ? null : exposure(friend);
            if (exposed != null) {
                // Nothing is copied into folders others can see; the files that are there are still listed.
                if (!exposed.equals(exposureSaid.get(friend)))
                    emit(map("event", "problem", "friend", friend, "error", exposed));
                exposureSaid.put(friend, exposed);
                fresh.clear();
            } else
                exposureSaid.remove(friend);
            if (!fresh.isEmpty()) {
                Collections.sort(fresh);
                List<String> copied = new ArrayList<>();
                for (String n : fresh) {
                    try {
                        retry(() -> {
                            FileWrapper in = PeergosBridge.ensureFolder(ctx, "/" + me, ROOT + "/" + friend + "/" + month + "/" + RECEIVED, net, crypto);
                            return await(shared.get(n).copyTo(in, ctx), WRITE);
                        });
                        copied.add(friend + "/" + n);
                        // My copy keeps the small picture of the original (when the copy did not take it along).
                        String thumb = thumbOf(shared.get(n));
                        if (thumb != null) {
                            FileWrapper in = PeergosBridge.ensureFolder(ctx, "/" + me, ROOT + "/" + friend + "/" + month + "/" + RECEIVED, net, crypto);
                            Optional<FileWrapper> mine = await(in.getChild(n, crypto.hasher, net));
                            if (mine.isPresent() && mine.get().getFileProperties().thumbnail.isEmpty())
                                setThumb(mine.get(), thumb);
                        }
                    } catch (Exception e) {
                        System.err.println("direct: copy " + friend + "/" + month + "/" + n + ": " + message(e)); // tried again at the next check
                    }
                }
                if (!copied.isEmpty()) {
                    refresh();
                    try {
                        retry(() -> updateMyMeta(friend, month, items -> {
                            for (String key : copied)
                                items.put(key, withEntry(items.get(key), "got", true));
                            return null;
                        }));
                    } catch (Exception e) {
                        // the copies exist, so they are not copied again; "got" is written with the next change
                        System.err.println("direct: remember copies: " + message(e));
                    }
                    return list(friend, month, false);
                }
            }
        }
        List<Object> items = new ArrayList<>();
        // Peergos returns folder contents as an unordered set: a fixed order keeps the "changed" check from firing
        // on every poll when nothing changed.
        found.sort(Comparator.comparing((Object[] f) -> (String) f[2]).thenComparing(f -> ((FileWrapper) f[0]).getFileProperties().name));
        for (Object[] f : found) {
            FileProperties fp = ((FileWrapper) f[0]).getFileProperties();
            Map<String, Object> m = meta.getOrDefault(f[2] + "/" + fp.name, Map.of());
            Map<String, Object> item = map("name", fp.name, "path", f[1] + "/" + fp.name, "from", f[2], "size", fp.size,
                    "modified", fp.modified.toEpochSecond(ZoneOffset.UTC) * 1000,
                    "label", m.getOrDefault("label", ""), "pinned", m.getOrDefault("pinned", false),
                    "stars", m.getOrDefault("stars", List.of()));
            String thumb = thumbOf((FileWrapper) f[0]);
            if (thumb != null)
                item.put("thumb", thumb);
            items.add(item);
        }
        return items;
    }

    /** The small picture Peergos keeps with a file ("data:image/…;base64,…"), or null. */
    static String thumbOf(FileWrapper f) {
        try {
            return f.getFileProperties().thumbnail.isPresent() ? f.getBase64Thumbnail() : null;
        } catch (Exception e) {
            return null;
        }
    }

    /** Gives a file a small picture (a JPEG or WebP data URL) – only my own files can be changed. */
    void setThumb(FileWrapper f, String thumb) {
        setThumb(f, thumb, net);
    }

    static void setThumb(FileWrapper f, String thumb, NetworkAccess net) {
        if (thumb == null || !(thumb.startsWith("data:image/jpeg;base64,") || thumb.startsWith("data:image/webp;base64,")))
            return;
        try {
            await(f.updateThumbnail(thumb, net), WRITE);
        } catch (Exception e) {
            System.err.println("thumbnail " + f.getFileProperties().name + ": " + message(e));
        }
    }

    /** A picture Java can read (WebP cannot be read here; such a file simply gets no preview from this side). */
    static boolean isPicture(String name) {
        String n = name.toLowerCase(Locale.ROOT);
        return n.endsWith(".png") || n.endsWith(".jpg") || n.endsWith(".jpeg") || n.endsWith(".gif") || n.endsWith(".bmp");
    }

    /**
     * A small JPEG of a picture – at most 256 pixels on its longer side, on white, quality 0.72 – as
     * "data:image/jpeg;base64,…", the same as the app makes when it sends. Null when the bytes are not a picture Java
     * can read. Large pictures are halved step by step first, so the result stays smooth.
     */
    static String makeThumb(byte[] picture) {
        try {
            BufferedImage img = ImageIO.read(new ByteArrayInputStream(picture));
            if (img == null || img.getWidth() <= 0 || img.getHeight() <= 0)
                return null;
            double scale = Math.min(1.0, 256.0 / Math.max(img.getWidth(), img.getHeight()));
            int tw = Math.max(1, (int) Math.round(img.getWidth() * scale)), th = Math.max(1, (int) Math.round(img.getHeight() * scale));
            while (img.getWidth() / 2 >= tw && img.getHeight() / 2 >= th)
                img = scaled(img, img.getWidth() / 2, img.getHeight() / 2);
            img = scaled(img, tw, th);
            ImageWriter writer = ImageIO.getImageWritersByFormatName("jpeg").next();
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            try (ImageOutputStream ios = ImageIO.createImageOutputStream(out)) {
                writer.setOutput(ios);
                ImageWriteParam p = writer.getDefaultWriteParam();
                p.setCompressionMode(ImageWriteParam.MODE_EXPLICIT);
                p.setCompressionQuality(0.72f);
                writer.write(null, new IIOImage(img, null, null), p);
            } finally {
                writer.dispose();
            }
            return "data:image/jpeg;base64," + Base64.getEncoder().encodeToString(out.toByteArray());
        } catch (Exception | OutOfMemoryError e) {
            System.err.println("thumbnail: " + e);
            return null;
        }
    }

    static BufferedImage scaled(BufferedImage src, int w, int h) {
        BufferedImage dst = new BufferedImage(w, h, BufferedImage.TYPE_INT_RGB);
        Graphics2D g = dst.createGraphics();
        try {
            g.setColor(Color.WHITE);
            g.fillRect(0, 0, w, h);
            g.setRenderingHint(RenderingHints.KEY_INTERPOLATION, RenderingHints.VALUE_INTERPOLATION_BILINEAR);
            g.setRenderingHint(RenderingHints.KEY_RENDERING, RenderingHints.VALUE_RENDER_QUALITY);
            g.drawImage(src, 0, 0, w, h, null);
        } finally {
            g.dispose();
        }
        return dst;
    }

    /** One user's meta file: {"v":2,"items":{"SENDER/NAME":{"label":"…","labelAt":ms,"pin":true,"pinAt":ms,"star":true}}}. */
    @SuppressWarnings("unchecked")
    Map<String, Object> readMeta(String path, FileWrapper f) {
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
            if (Json.bool(it, "star") && !((List<String>) m.get("stars")).contains(user))
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
        String thumb = Json.str(c, "thumb"); // a JPEG data URL made by the app: the friend sees the file without downloading it
        if (wanted == null || wanted.isBlank())
            wanted = file.getFileName().toString();
        wanted = wanted.replace('/', '_').replace('\\', '_');
        if (wanted.startsWith("."))
            wanted = "_" + wanted.substring(1);
        ensureOpen(friend);
        String rel = ROOT + "/" + friend + "/" + month;
        String finalWanted = wanted;
        String name = retry(() -> {
            FileWrapper dir = PeergosBridge.ensureFolder(ctx, "/" + me, rel, net, crypto);
            String n = PeergosBridge.uniqueName(dir, finalWanted, net, crypto);
            FileWrapper up = PeergosBridge.upload(dir, n, file, net, crypto);
            setThumb(up, thumb);
            return n;
        });
        // Only this file, read-only, only with this friend.
        String path = "/" + me + "/" + rel + "/" + name;
        retry(() -> await(ctx.shareReadAccessWith(PathUtil.get(path), Set.of(friend)), WRITE));
        return map("name", name, "path", path, "from", me, "size", Files.size(file),
                "modified", System.currentTimeMillis(), "label", "", "pinned", false, "stars", List.of());
    }

    Path get(String path, Path to) throws Exception {
        FileWrapper f = await(ctx.getByPath(path)).orElseThrow(() -> new IllegalStateException("Not found: " + path));
        return download(f, to, net, crypto, null);
    }

    /** Downloads a file to "to" (through "to.part", so a broken download never looks complete). */
    static Path download(FileWrapper f, Path to, NetworkAccess net, Crypto crypto, java.util.function.LongConsumer onBytes) throws Exception {
        Files.createDirectories(to.toAbsolutePath().getParent());
        Path part = to.resolveSibling(to.getFileName() + ".part");
        try (OutputStream o = Files.newOutputStream(part)) {
            AsyncReader in = await(f.getInputStream(net, crypto, x -> {}));
            byte[] buf = new byte[1 << 20];
            long left = f.getSize();
            while (left > 0) {
                int n = await(in.readIntoArray(buf, 0, (int) Math.min(buf.length, left)));
                if (n <= 0)
                    break;
                o.write(buf, 0, n);
                left -= n;
                if (onBytes != null)
                    onBytes.accept(n);
            }
            in.close();
        }
        Files.move(part, to, StandardCopyOption.REPLACE_EXISTING);
        return to;
    }

    byte[] read(FileWrapper f) throws Exception {
        return read(f, 4_000_000);
    }

    byte[] read(FileWrapper f, long limit) throws Exception {
        return readAll(f, limit, net, crypto);
    }

    static byte[] readAll(FileWrapper f, long limit, NetworkAccess net, Crypto crypto) throws Exception {
        long size = f.getSize();
        if (size > limit)
            throw new IllegalStateException("too large");
        byte[] all = new byte[(int) size];
        AsyncReader in = await(f.getInputStream(net, crypto, x -> {}));
        int off = 0;
        while (off < all.length) {
            int n = await(in.readIntoArray(all, off, all.length - off));
            if (n <= 0)
                break;
            off += n;
        }
        in.close();
        return all;
    }

    /** label / pin / star for one file (mine or my copy of the friend's); the key is "SENDER/NAME" on both sides. */
    @SuppressWarnings("unchecked")
    Map<String, Object> meta(Map<String, Object> c) throws Exception {
        String[] p = parse(direct(Json.str(c, "path"))); // owner, other, month, received|null, name
        boolean copy = p[3] != null;
        String friend = p[0].equals(me) ? p[1] : p[0];
        requireUnexposed(friend); // labels are written into the month folder
        String sender = p[0].equals(me) && !copy ? me : friend;
        String item = sender + "/" + p[4];
        long now = System.currentTimeMillis();
        return (Map<String, Object>) updateMyMeta(friend, p[2], items -> {
            Map<String, Object> it = items.get(item) instanceof Map ? new LinkedHashMap<>((Map<String, Object>) items.get(item)) : new LinkedHashMap<>();
            if (c.containsKey("label")) { it.put("label", Json.str(c, "label")); it.put("labelAt", now); }
            if (c.containsKey("pin")) { it.put("pin", Json.bool(c, "pin")); it.put("pinAt", now); }
            if (c.containsKey("star")) it.put("star", Json.bool(c, "star"));
            items.put(item, it);
            return it;
        });
    }

    @SuppressWarnings("unchecked")
    static Map<String, Object> withEntry(Object existing, String key, Object value) {
        Map<String, Object> m = existing instanceof Map ? new LinkedHashMap<>((Map<String, Object>) existing) : new LinkedHashMap<>();
        m.put(key, value);
        return m;
    }

    interface MetaChange { Object apply(Map<String, Object> items) throws Exception; }

    /**
     * Changes my meta file for one friend and month (in my own folder: labels, pins, stars – also for my copies of
     * the friend's files – and which of their files I have copied, "got"). Shared read-only with the friend once, then
     * overwritten in place so that share stays valid.
     */
    @SuppressWarnings("unchecked")
    Object updateMyMeta(String friend, String month, MetaChange change) throws Exception {
        String dirPath = mine(friend) + "/" + month;
        FileWrapper dir = PeergosBridge.ensureFolder(ctx, "/" + me, ROOT + "/" + friend + "/" + month, net, crypto);
        String metaName = META_PREFIX + me + ".json";
        Map<String, Object> doc = new LinkedHashMap<>(Map.of("v", 2L, "items", new LinkedHashMap<String, Object>()));
        Optional<FileWrapper> existing = await(dir.getChild(metaName, crypto.hasher, net));
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
        Object result = change.apply(items);
        // Entries of files that exist nowhere any more are dropped, so the file stays small. "got" stays as long as
        // the friend's original is still shared with me (so a copy I deleted is not copied again).
        Set<String> present = new HashSet<>();
        for (FileWrapper k : await(dir.getChildren(crypto.hasher, net)))
            present.add(me + "/" + k.getFileProperties().name);
        for (FileWrapper k : files(dirPath + "/" + RECEIVED))
            present.add(friend + "/" + k.getFileProperties().name);
        for (FileWrapper k : files(theirs(friend) + "/" + month))
            present.add(friend + "/" + k.getFileProperties().name);
        // Files still in the folders of 2.2 keep their labels, pins and stars (kept in this file from now on).
        String old = mineLegacy(friend) + "/" + month;
        for (FileWrapper k : files(old))
            present.add(me + "/" + k.getFileProperties().name);
        for (FileWrapper k : files(old + "/" + RECEIVED))
            present.add(friend + "/" + k.getFileProperties().name);
        for (FileWrapper k : files(theirsLegacy(friend) + "/" + month))
            present.add(friend + "/" + k.getFileProperties().name);
        items.keySet().removeIf(k -> !present.contains(k));
        byte[] bytes = Json.write(doc).getBytes(StandardCharsets.UTF_8);
        if (existing.isPresent()) {
            // Overwritten in place, so the read-only share my friend already has stays valid.
            await(existing.get().overwriteFile(AsyncReader.build(bytes), bytes.length, net, crypto, x -> {}), WRITE);
        } else {
            await(dir.uploadOrReplaceFile(metaName, AsyncReader.build(bytes), bytes.length, net, crypto, () -> false, x -> {}), WRITE);
            refresh();
            await(ctx.shareReadAccessWith(PathUtil.get(dirPath + "/" + metaName), Set.of(friend)), WRITE);
        }
        metaCache.remove(dirPath + "/" + metaName);
        return result;
    }

    /** Seconds to wait for Peergos: reads, and writes (uploads, shares, friend requests). */
    static final long READ = 60, WRITE = 300;

    /**
     * Waits for a Peergos result, but not forever: some calls never complete, e.g. reading a friend's folder after
     * that friend has revoked access (unfriended). A plain join() would then block this whole session for good.
     */
    static <T> T await(CompletableFuture<T> f) { return await(f, READ); }

    static <T> T await(CompletableFuture<T> f, long seconds) {
        try {
            return f.get(seconds, TimeUnit.SECONDS);
        } catch (TimeoutException e) {
            throw new CompletionException(new TimeoutException("Peergos did not answer within " + seconds + " s"));
        } catch (ExecutionException e) {
            throw new CompletionException(e.getCause());
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new CompletionException(e);
        }
    }

    interface Step<T> { T run() throws Exception; }

    /** When the account changed a folder since this session last saw it (e.g. the web app or another PC),
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
        return await(ctx.getByPath(path)).orElseThrow(() -> new IllegalStateException("Folder not found: " + path));
    }

    // ---------- watching ----------

    void poll() {
        if (watchFriends.isEmpty())
            return;
        polls++;
        for (String friend : watchFriends) {
            Long until = pausedUntil.get(friend);
            if (until != null && until > System.currentTimeMillis())
                continue;
            try {
                List<Object> items = list(friend, watchMonth);
                String sig = Json.write(items);
                if (!sig.equals(lastSignature.get(friend))) {
                    lastSignature.put(friend, sig);
                    emit(map("event", "changed", "friend", friend, "month", watchMonth, "items", items));
                }
                lastProblem = "";
                pausedUntil.remove(friend);
            } catch (Throwable t) {
                String m = message(t);
                if (rootCause(t) instanceof TimeoutException) {
                    // Their folder does not answer (e.g. they unfriended): check it only now and then, on a fresh
                    // session, so commands and the other friends are not held up.
                    pausedUntil.put(friend, System.currentTimeMillis() + PAUSE_MS);
                    try { refresh(); } catch (Throwable ignored) { }
                }
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
        String[] p = parse(path);
        boolean common = p != null && p[2].matches("\\d{4}-\\d{2}") && !p[4].isEmpty() && !p[4].equals("..");
        boolean file = common && p[3] == null && (p[0].equals(me) || p[1].equals(me));
        boolean copy = common && p[3] != null && p[0].equals(me);
        if (!file && !copy)
            throw new IllegalArgumentException("Not a direct-sharing path: " + path);
        return path;
    }

    /**
     * The parts of a direct path: {owner, other, month, "received" or null, name}, or null when it is not one.
     * <pre>
     * /OWNER/PeergosSnap/Direct/OTHER/MONTH/[received/]NAME    (since 2.3)
     * /OWNER/PeergosSnap-Direct/OTHER/MONTH/[received/]NAME    (2.2)
     * </pre>
     */
    static String[] parse(String path) {
        if (path == null)
            return null;
        String[] p = path.split("/", -1);
        if (p.length < 6 || !p[0].isEmpty())
            return null;
        int i;
        if (p[2].equals(LEGACY))
            i = 3;
        else if (p.length >= 7 && (p[2] + "/" + p[3]).equals(ROOT))
            i = 4;
        else
            return null;
        int rest = p.length - i; // other, month, [received], name
        if (rest == 3)
            return new String[]{p[1], p[i], p[i + 1], null, p[i + 2]};
        if (rest == 4 && p[i + 2].equals(RECEIVED))
            return new String[]{p[1], p[i], p[i + 1], RECEIVED, p[i + 3]};
        return null;
    }

    static String parent(String path) {
        return path.substring(0, path.lastIndexOf('/'));
    }
}
