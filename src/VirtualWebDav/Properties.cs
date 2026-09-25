using System.Xml.Linq;
using Microsoft.AspNetCore.Http;

namespace VirtualWebDav;

public sealed partial class WebDavServer
{
    private async Task PropFindAsync(HttpContext c, string path)
    {
        var depth = c.Request.Headers["Depth"].ToString();
        if (depth is "" or "infinity") throw new DavException(403, "propfind-finite-depth");
        if (depth is not ("0" or "1")) throw new DavException(400);
        var request = await ReadXmlAsync(c);
        if (request != null && request.Name != Dav + "propfind") throw new DavException(400);
        var selectors = request?.Elements().Where(e => e.Name == Dav + "prop" || e.Name == Dav + "propname" || e.Name == Dav + "allprop").ToArray();
        if (request != null && selectors?.Length != 1) throw new DavException(400);
        var selector = selectors?.SingleOrDefault();
        var entry = await RequiredAsync(path, c.RequestAborted);
        await PreconditionsAsync(c, path, entry);
        var result = new XElement(Dav + "multistatus");
        result.Add(await PropertyResponseAsync(path, entry, selector, request?.Element(Dav + "include"), c.RequestAborted));
        if (depth == "1" && entry.IsDirectory)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await foreach (var child in fs.EnumerateAsync(path, c.RequestAborted))
            {
                if (child.Name.Contains('/') || !names.Add(child.Name)) throw new InvalidOperationException("Provider returned ambiguous names.");
                var childPath = VirtualPath.Normalize((path == "/" ? "" : path) + "/" + child.Name);
                result.Add(await PropertyResponseAsync(childPath, child, selector, request?.Element(Dav + "include"), c.RequestAborted));
            }
        }
        await XmlAsync(c, result);
    }

    private async Task<XElement> PropertyResponseAsync(string path, VirtualEntry entry, XElement? selector, XElement? include, CancellationToken ct)
    {
        var properties = new List<XElement>
        {
            new(Dav + "displayname", entry.Name),
            new(Dav + "resourcetype", entry.IsDirectory ? new XElement(Dav + "collection") : null),
            new(Dav + "creationdate", entry.Created.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")),
            new(Dav + "getlastmodified", entry.Modified.ToString("R")),
            new(Dav + "getetag", entry.ETag),
            new(Dav + "supportedlock", new XElement(Dav + "lockentry",
                new XElement(Dav + "lockscope", new XElement(Dav + "exclusive")),
                new XElement(Dav + "locktype", new XElement(Dav + "write")))),
            LockDiscovery(path)
        };
        if (!entry.IsDirectory)
        {
            properties.Add(new XElement(Dav + "getcontentlength", entry.Length));
            properties.Add(new XElement(Dav + "getcontenttype", "application/octet-stream"));
        }
        var liveNames = properties.Select(p => p.Name).ToHashSet();
        properties.AddRange((await fs.GetPropertiesAsync(path, ct)).Where(p => !liveNames.Contains(p.Name)).Select(p => new XElement(p)));
        var response = new XElement(Dav + "response", new XElement(Dav + "href", VirtualPath.Href(path, entry.IsDirectory)));
        if (selector?.Name == Dav + "propname")
            response.Add(PropStat(properties.Select(p => new XElement(p.Name)), 200));
        else if (selector?.Name == Dav + "prop")
        {
            var requested = selector.Elements().Select(p => p.Name).Distinct().ToArray();
            var found = requested.Select(n => properties.FirstOrDefault(p => p.Name == n)).OfType<XElement>().ToArray();
            var missing = requested.Where(n => properties.All(p => p.Name != n)).Select(n => new XElement(n)).ToArray();
            if (found.Length > 0) response.Add(PropStat(found, 200));
            if (missing.Length > 0) response.Add(PropStat(missing, 404));
            if (requested.Length == 0) response.Add(PropStat([], 200));
        }
        else
        {
            response.Add(PropStat(properties, 200));
            var missing = include?.Elements().Where(p => properties.All(q => q.Name != p.Name)).Select(p => new XElement(p.Name)).ToArray();
            if (missing?.Length > 0) response.Add(PropStat(missing, 404));
        }
        return response;
    }

    private static XElement PropStat(IEnumerable<XElement> properties, int status) => new(Dav + "propstat",
        new XElement(Dav + "prop", properties), new XElement(Dav + "status", $"HTTP/1.1 {status} {Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status)}"));

    private async Task PropPatchAsync(HttpContext c, string path)
    {
        var entry = await RequiredAsync(path, c.RequestAborted);
        var request = await ReadXmlAsync(c);
        if (request?.Name != Dav + "propertyupdate" || !request.HasElements) throw new DavException(400);
        var changes = new Dictionary<XName, XElement?>();
        foreach (var instruction in request.Elements())
        {
            if (instruction.Name != Dav + "set" && instruction.Name != Dav + "remove") throw new DavException(400);
            var prop = instruction.Element(Dav + "prop") ?? throw new DavException(400);
            foreach (var p in prop.Elements()) changes[p.Name] = instruction.Name == Dav + "set" ? new XElement(p) : null;
        }
        // DAV live properties are protected. Custom namespaces remain provider-owned.
        bool forbidden = changes.Keys.Any(n => n.Namespace == Dav);
        var response = new XElement(Dav + "response", new XElement(Dav + "href", VirtualPath.Href(path, entry.IsDirectory)));
        if (forbidden)
        {
            foreach (var group in changes.Keys.GroupBy(n => n.Namespace == Dav ? 403 : 424))
                response.Add(PropStat(group.Select(n => new XElement(n)), group.Key));
        }
        else
        {
            await fs.PatchPropertiesAsync(path, changes.Values.OfType<XElement>().ToArray(), changes.Where(p => p.Value == null).Select(p => p.Key).ToArray(), c.RequestAborted);
            response.Add(PropStat(changes.Keys.Select(n => new XElement(n)), 200));
        }
        await XmlAsync(c, new XElement(Dav + "multistatus", response));
    }
}
