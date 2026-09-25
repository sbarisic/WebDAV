using System.Xml.Linq;
using Microsoft.AspNetCore.Http;

namespace VirtualWebDav;

public sealed partial class WebDavServer
{
    private sealed record DavLock(string Path, string Token, bool InfiniteDepth, XElement? Owner, DateTimeOffset Expires);
    private sealed record IfTerm(bool Negated, string Value, bool ETag);
    private sealed record IfList(string Path, List<IfTerm> Terms);

    // Parse the DAV If grammar rather than accepting token substrings. Lists are OR,
    // terms are AND; tagged resource groups must each satisfy at least one list.
    private List<IfList> ParseIf(HttpContext c, string requestPath)
    {
        var text = c.Request.Headers["If"].ToString();
        var lists = new List<IfList>();
        int i = 0;
        string current = requestPath;
        bool? tagged = null;
        void White() { while (i < text.Length && char.IsWhiteSpace(text[i])) i++; }
        string Between(char open, char close)
        {
            if (i >= text.Length || text[i++] != open) throw new DavException(400);
            int start = i;
            while (i < text.Length && text[i] != close) i++;
            if (i == text.Length || start == i) throw new DavException(400);
            var result = text[start..i]; i++; return result;
        }
        White();
        while (i < text.Length)
        {
            if (text[i] == '<')
            {
                if (tagged == false) throw new DavException(400);
                tagged = true;
                var resource = Between('<', '>');
                current = AbsoluteResourcePath(c, resource, 400);
                White();
            }
            else tagged ??= false;
            if (i >= text.Length || text[i++] != '(') throw new DavException(400);
            var terms = new List<IfTerm>(); White();
            while (i < text.Length && text[i] != ')')
            {
                bool negated = false;
                if (text.AsSpan(i).StartsWith("Not ", StringComparison.Ordinal)) { negated = true; i += 4; White(); }
                if (i >= text.Length) throw new DavException(400);
                bool etag = text[i] == '[';
                var value = etag ? Between('[', ']') : Between('<', '>');
                terms.Add(new(negated, value, etag)); White();
            }
            if (terms.Count == 0 || i >= text.Length || text[i++] != ')') throw new DavException(400);
            lists.Add(new(current, terms)); White();
        }
        return lists;
    }

    private async Task<bool> PreconditionsAsync(HttpContext c, string path, VirtualEntry? entry, bool read = false)
    {
        var match = c.Request.Headers.IfMatch.ToString();
        bool Matches(string values, bool weak) => values.Split(',').Select(v => v.Trim()).Any(v =>
            v == "*" ? entry != null : entry != null && (weak ? v.Replace("W/", "") == entry.ETag.Replace("W/", "") : !v.StartsWith("W/") && v == entry.ETag));
        if (match.Length > 0 && !Matches(match, false)) throw new DavException(412);
        if (match.Length == 0 && entry != null && DateTimeOffset.TryParse(c.Request.Headers.IfUnmodifiedSince, out var unmodified) &&
            entry.Modified.ToUnixTimeSeconds() > unmodified.ToUnixTimeSeconds()) throw new DavException(412);
        var none = c.Request.Headers.IfNoneMatch.ToString();
        if (none.Length > 0 && Matches(none, true))
        {
            if (!read) throw new DavException(412);
            c.Response.StatusCode = 304; c.Response.ContentLength = null; return false;
        }
        if (read && none.Length == 0 && entry != null && DateTimeOffset.TryParse(c.Request.Headers.IfModifiedSince, out var modified) &&
            entry.Modified.ToUnixTimeSeconds() <= modified.ToUnixTimeSeconds())
        { c.Response.StatusCode = 304; c.Response.ContentLength = null; return false; }
        var lists = ParseIf(c, path);
        var submitted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in lists.GroupBy(l => l.Path, StringComparer.OrdinalIgnoreCase))
        {
            var target = group.Key.Equals(path, StringComparison.OrdinalIgnoreCase) ? entry : await fs.GetEntryAsync(group.Key, c.RequestAborted);
            bool any = false;
            foreach (var list in group)
            {
                bool good = list.Terms.All(t =>
                {
                    bool value = t.ETag ? target?.ETag == t.Value : locks.Any(l => l.Token == t.Value && Covers(l, list.Path));
                    return t.Negated ? !value : value;
                });
                if (good)
                {
                    any = true;
                    foreach (var term in list.Terms.Where(t => !t.Negated && !t.ETag)) submitted.Add(term.Value);
                }
            }
            if (!any) throw new DavException(412);
        }
        c.Items["dav-tokens"] = submitted;
        return true;
    }

    private static bool Covers(DavLock l, string path) => l.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || l.InfiniteDepth && VirtualPath.Within(path, l.Path);
    private void CheckLocks(HttpContext c, string path, bool descendants)
    {
        var tokens = c.Items["dav-tokens"] as HashSet<string> ?? [];
        if (locks.Any(l => (Covers(l, path) || descendants && VirtualPath.Within(l.Path, path)) && !tokens.Contains(l.Token)))
            throw new DavException(423, "lock-token-submitted");
    }

    private TimeSpan Timeout(HttpContext c)
    {
        foreach (var part in c.Request.Headers["Timeout"].ToString().Split(',').Select(p => p.Trim()))
        {
            if (part == "Infinite") return options.MaxLockTimeout;
            if (part.StartsWith("Second-") && long.TryParse(part[7..], out var seconds) && seconds > 0)
                return TimeSpan.FromSeconds(Math.Min(seconds, options.MaxLockTimeout.TotalSeconds));
        }
        return options.MaxLockTimeout;
    }

    private async Task LockAsync(HttpContext c, string path)
    {
        var ct = c.RequestAborted;
        var entry = await fs.GetEntryAsync(path, ct);
        await PreconditionsAsync(c, path, entry);
        var xml = await ReadXmlAsync(c);
        if (xml == null)
        {
            var tokens = c.Items["dav-tokens"] as HashSet<string> ?? [];
            var old = locks.SingleOrDefault(l => l.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && tokens.Contains(l.Token)) ?? throw new DavException(412);
            locks.Remove(old); locks.Add(old with { Expires = DateTimeOffset.UtcNow + Timeout(c) });
            await XmlAsync(c, new XElement(Dav + "prop", LockDiscovery(path)), 200); return;
        }
        if (xml.Name != Dav + "lockinfo" || xml.Element(Dav + "lockscope")?.Element(Dav + "exclusive") == null ||
            xml.Element(Dav + "locktype")?.Element(Dav + "write") == null) throw new DavException(400);
        var depth = c.Request.Headers["Depth"].ToString();
        if (depth is not ("" or "0" or "infinity")) throw new DavException(400);
        bool recursive = depth != "0";
        if (locks.Any(l => Covers(l, path) || recursive && VirtualPath.Within(l.Path, path))) throw new DavException(423, "no-conflicting-lock");
        if (entry == null)
        {
            CheckLocks(c, VirtualPath.Parent(path), false);
            await using var write = await fs.BeginWriteAsync(path, ct);
            await write.CommitAsync(ct);
        }
        var created = new DavLock(path, "urn:uuid:" + Guid.NewGuid(), recursive, xml.Element(Dav + "owner") is { } owner ? new XElement(owner) : null, DateTimeOffset.UtcNow + Timeout(c));
        locks.Add(created);
        c.Response.Headers["Lock-Token"] = "<" + created.Token + ">";
        await XmlAsync(c, new XElement(Dav + "prop", LockDiscovery(path)), entry == null ? 201 : 200);
    }

    private async Task UnlockAsync(HttpContext c, string path)
    {
        await PreconditionsAsync(c, path, await fs.GetEntryAsync(path, c.RequestAborted));
        var token = c.Request.Headers["Lock-Token"].ToString();
        if (!token.StartsWith('<') || !token.EndsWith('>')) throw new DavException(400);
        var item = locks.SingleOrDefault(l => l.Token == token[1..^1] && l.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item == null) throw new DavException(409, "lock-token-matches-request-uri");
        locks.Remove(item); c.Response.StatusCode = 204;
    }

    private XElement LockDiscovery(string path) => new(Dav + "lockdiscovery", locks.Where(l => Covers(l, path)).Select(l =>
        new XElement(Dav + "activelock",
            new XElement(Dav + "locktype", new XElement(Dav + "write")),
            new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")),
            new XElement(Dav + "depth", l.InfiniteDepth ? "infinity" : "0"),
            l.Owner == null ? null : new XElement(l.Owner),
            new XElement(Dav + "timeout", "Second-" + Math.Max(1, (int)(l.Expires - DateTimeOffset.UtcNow).TotalSeconds)),
            new XElement(Dav + "locktoken", new XElement(Dav + "href", l.Token)),
            new XElement(Dav + "lockroot", new XElement(Dav + "href", VirtualPath.Href(l.Path, false))))));
}
