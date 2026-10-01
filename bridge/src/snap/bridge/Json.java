/*
 * Peergos Snap bridge - a minimal JSON reader and writer (objects, arrays, strings, numbers, booleans, null).
 * Copyright (C) 2026 anderlejan
 *
 * This program is free software: you can redistribute it and/or modify it under the terms of the
 * GNU Affero General Public License as published by the Free Software Foundation, version 3.
 * See bridge/LICENSE.
 */
package snap.bridge;

import java.util.ArrayList;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Objects become LinkedHashMap, arrays ArrayList, numbers Double (or Long when whole), the rest String/Boolean/null. */
final class Json {
    private final String s;
    private int i;

    private Json(String s) { this.s = s; }

    static Object parse(String text) {
        Json p = new Json(text);
        p.ws();
        Object v = p.value();
        p.ws();
        if (p.i != p.s.length())
            throw new IllegalArgumentException("JSON: unexpected text at " + p.i);
        return v;
    }

    @SuppressWarnings("unchecked")
    static Map<String, Object> parseObject(String text) {
        Object v = parse(text);
        if (!(v instanceof Map))
            throw new IllegalArgumentException("JSON: object expected");
        return (Map<String, Object>) v;
    }

    private void ws() {
        while (i < s.length() && Character.isWhitespace(s.charAt(i)))
            i++;
    }

    private Object value() {
        if (i >= s.length())
            throw new IllegalArgumentException("JSON: unexpected end");
        char c = s.charAt(i);
        switch (c) {
            case '{': return object();
            case '[': return array();
            case '"': return string();
            case 't': expect("true"); return Boolean.TRUE;
            case 'f': expect("false"); return Boolean.FALSE;
            case 'n': expect("null"); return null;
            default: return number();
        }
    }

    private void expect(String word) {
        if (!s.startsWith(word, i))
            throw new IllegalArgumentException("JSON: bad value at " + i);
        i += word.length();
    }

    private Map<String, Object> object() {
        Map<String, Object> m = new LinkedHashMap<>();
        i++;
        ws();
        if (i < s.length() && s.charAt(i) == '}') { i++; return m; }
        while (true) {
            ws();
            String k = string();
            ws();
            if (i >= s.length() || s.charAt(i) != ':')
                throw new IllegalArgumentException("JSON: ':' expected at " + i);
            i++;
            ws();
            m.put(k, value());
            ws();
            if (i < s.length() && s.charAt(i) == ',') { i++; continue; }
            if (i < s.length() && s.charAt(i) == '}') { i++; return m; }
            throw new IllegalArgumentException("JSON: ',' or '}' expected at " + i);
        }
    }

    private List<Object> array() {
        List<Object> a = new ArrayList<>();
        i++;
        ws();
        if (i < s.length() && s.charAt(i) == ']') { i++; return a; }
        while (true) {
            ws();
            a.add(value());
            ws();
            if (i < s.length() && s.charAt(i) == ',') { i++; continue; }
            if (i < s.length() && s.charAt(i) == ']') { i++; return a; }
            throw new IllegalArgumentException("JSON: ',' or ']' expected at " + i);
        }
    }

    private String string() {
        if (i >= s.length() || s.charAt(i) != '"')
            throw new IllegalArgumentException("JSON: string expected at " + i);
        i++;
        StringBuilder b = new StringBuilder();
        while (i < s.length()) {
            char c = s.charAt(i++);
            if (c == '"') return b.toString();
            if (c != '\\') { b.append(c); continue; }
            char e = s.charAt(i++);
            switch (e) {
                case 'n': b.append('\n'); break;
                case 'r': b.append('\r'); break;
                case 't': b.append('\t'); break;
                case 'b': b.append('\b'); break;
                case 'f': b.append('\f'); break;
                case 'u': b.append((char) Integer.parseInt(s.substring(i, i + 4), 16)); i += 4; break;
                default: b.append(e);
            }
        }
        throw new IllegalArgumentException("JSON: unterminated string");
    }

    private Object number() {
        int start = i;
        while (i < s.length() && "+-0123456789.eE".indexOf(s.charAt(i)) >= 0)
            i++;
        String n = s.substring(start, i);
        if (n.isEmpty())
            throw new IllegalArgumentException("JSON: bad value at " + start);
        if (n.matches("-?\\d+"))
            return Long.parseLong(n);
        return Double.parseDouble(n);
    }

    // ---------- writing ----------

    static String write(Object v) {
        StringBuilder b = new StringBuilder();
        write(v, b);
        return b.toString();
    }

    @SuppressWarnings("unchecked")
    private static void write(Object v, StringBuilder b) {
        if (v == null) b.append("null");
        else if (v instanceof String) b.append(PeergosBridge.json((String) v));
        else if (v instanceof Boolean || v instanceof Long || v instanceof Integer) b.append(v);
        else if (v instanceof Number) {
            double d = ((Number) v).doubleValue();
            b.append(d == Math.rint(d) && Math.abs(d) < 1e15 ? Long.toString((long) d) : Double.toString(d));
        } else if (v instanceof Map) {
            b.append('{');
            boolean first = true;
            for (Map.Entry<String, Object> e : ((Map<String, Object>) v).entrySet()) {
                if (!first) b.append(',');
                first = false;
                b.append(PeergosBridge.json(e.getKey())).append(':');
                write(e.getValue(), b);
            }
            b.append('}');
        } else if (v instanceof Iterable) {
            b.append('[');
            boolean first = true;
            for (Object o : (Iterable<Object>) v) {
                if (!first) b.append(',');
                first = false;
                write(o, b);
            }
            b.append(']');
        } else b.append(PeergosBridge.json(String.valueOf(v)));
    }

    // ---------- reading helpers ----------

    static String str(Map<String, Object> m, String k) {
        Object v = m.get(k);
        return v == null ? null : String.valueOf(v);
    }

    static boolean bool(Map<String, Object> m, String k) {
        return Boolean.TRUE.equals(m.get(k));
    }

    static long num(Map<String, Object> m, String k, long fallback) {
        Object v = m.get(k);
        return v instanceof Number ? ((Number) v).longValue() : fallback;
    }
}
