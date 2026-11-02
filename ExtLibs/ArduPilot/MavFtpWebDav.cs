using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MissionPlanner.ArduPilot.Mavlink
{
    /// <summary>
    /// Mounts the vehicle's MAVFTP file system as a folder on macOS and Linux,
    /// like <see cref="MavFtpDokan"/> does on Windows. A small WebDAV server on
    /// localhost serves the vehicle's files, and the operating system's own WebDAV
    /// client mounts it (mount_webdav on macOS, gio on Linux), so no kernel
    /// extension or driver is needed.
    /// </summary>
    public class MavFtpWebDav
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly TimeSpan DirCacheTtl = TimeSpan.FromSeconds(10);

        private static MavFtpWebDav _instance;
        private static string _mountPoint;

        private readonly MAVFtp _mavftp;
        private readonly HttpListener _listener = new HttpListener();
        private readonly Dictionary<string, Tuple<List<MAVFtp.FtpFileInfo>, DateTime>> _dirCache =
            new Dictionary<string, Tuple<List<MAVFtp.FtpFileInfo>, DateTime>>();
        // Finder metadata (._*, .DS_Store) is kept in memory, not written to the vehicle
        private readonly Dictionary<string, byte[]> _shadow = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public string Url { get; private set; }

        public static bool IsMounted
        {
            get { return _instance != null; }
        }

        public static string MountPoint
        {
            get { return _mountPoint; }
        }

        public static string DefaultMountPoint
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "MAVFTP"); }
        }

        private MavFtpWebDav(MAVFtp mavftp)
        {
            _mavftp = mavftp;
        }

        /// <summary>Serve the vehicle files over WebDAV on localhost without mounting them.</summary>
        public static MavFtpWebDav Serve(MAVFtp mavftp)
        {
            var server = new MavFtpWebDav(mavftp);
            server.Start();
            return server;
        }

        public void Close()
        {
            Stop();
        }

        public static void Mount(MAVFtp mavftp, string mountPoint)
        {
            if (_instance != null)
                throw new InvalidOperationException("Already mounted at " + _mountPoint);

            var server = Serve(mavftp);
            try
            {
                Directory.CreateDirectory(mountPoint);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    Exec("/sbin/mount_webdav", "-s -v MAVFTP \"" + server.Url + "\" \"" + mountPoint + "\"");
                else if (File.Exists("/usr/bin/gio"))
                    Exec("/usr/bin/gio", "mount \"" + server.Url.Replace("http://", "dav://") + "\"");
                else
                    throw new PlatformNotSupportedException("No WebDAV client found; the files are served at " + server.Url);
            }
            catch
            {
                server.Stop();
                throw;
            }

            _instance = server;
            _mountPoint = mountPoint;
            log.Info("MAVFTP mounted at " + mountPoint + " from " + server.Url);
        }

        public static void Unmount(string mountPoint = null)
        {
            var server = _instance;
            if (server == null)
                return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    try
                    {
                        Exec("/sbin/umount", "\"" + (mountPoint ?? _mountPoint) + "\"", 15000);
                    }
                    catch
                    {
                        // busy (e.g. a Finder window): force it, the server goes away next
                        Exec("/sbin/umount", "-f \"" + (mountPoint ?? _mountPoint) + "\"", 15000);
                    }
                }
                else if (File.Exists("/usr/bin/gio"))
                    Exec("/usr/bin/gio", "mount -u \"" + server.Url.Replace("http://", "dav://") + "\"");
            }
            catch (Exception ex)
            {
                log.Warn("unmount", ex);
            }
            finally
            {
                server.Stop();
                _instance = null;
                _mountPoint = null;
            }
        }

        private static void Exec(string file, string args, int timeoutMs = 30000)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using (var p = Process.Start(psi))
            {
                // read asynchronously: a mount helper may leave a daemon holding the pipes open
                var err = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException(Path.GetFileName(file) + " did not finish within " + timeoutMs / 1000 + " s");
                }
                if (p.ExitCode != 0)
                {
                    var text = (err.Wait(1000) ? err.Result : "") + (output.Wait(1000) ? output.Result : "");
                    throw new Exception(Path.GetFileName(file) + " failed (" + p.ExitCode + "): " + text.Trim());
                }
            }
        }

        // ------------------------------------------------------------------ server

        private void Start()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint) probe.LocalEndpoint).Port;
            probe.Stop();

            Url = "http://127.0.0.1:" + port + "/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            Task.Run(() => Loop());
        }

        private void Stop()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
            }
        }

        private void Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext();
                }
                catch
                {
                    break;
                }

                // the vehicle link is serial: one request at a time
                try
                {
                    Handle(ctx);
                }
                catch (Exception ex)
                {
                    log.Warn("webdav " + ctx.Request.HttpMethod + " " + ctx.Request.Url, ex);
                    try
                    {
                        ctx.Response.StatusCode = 500;
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    try
                    {
                        ctx.Response.Close();
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static CancellationTokenSource NewCancel()
        {
            return new CancellationTokenSource(30000);
        }

        private static string FtpPath(Uri url)
        {
            var path = Uri.UnescapeDataString(url.AbsolutePath);
            if (path.Length > 1)
                path = path.TrimEnd('/');
            return path == "" ? "/" : path;
        }

        private static string Parent(string path)
        {
            var i = path.LastIndexOf('/');
            return i <= 0 ? "/" : path.Substring(0, i);
        }

        private static bool IsShadow(string path)
        {
            var name = path.Substring(path.LastIndexOf('/') + 1);
            return name.StartsWith("._") || name == ".DS_Store" || name == ".hidden" ||
                   name == ".metadata_never_index" || name == ".Spotlight-V100" || name == ".Trashes" ||
                   name == ".fseventsd" || name == ".localized";
        }

        private List<MAVFtp.FtpFileInfo> ListDir(string path, bool refresh = false)
        {
            lock (_dirCache)
            {
                Tuple<List<MAVFtp.FtpFileInfo>, DateTime> cached;
                if (!refresh && _dirCache.TryGetValue(path, out cached) && DateTime.UtcNow < cached.Item2)
                    return cached.Item1;
            }

            List<MAVFtp.FtpFileInfo> entries;
            lock (_mavftp)
                entries = _mavftp.kCmdListDirectory(path, NewCancel());
            if (entries == null)
                return null;
            entries = entries.Where(e => e.Name != "" && e.Name != "." && e.Name != "..").ToList();
            lock (_dirCache)
                _dirCache[path] = Tuple.Create(entries, DateTime.UtcNow.Add(DirCacheTtl));
            return entries;
        }

        private void Invalidate(string path)
        {
            lock (_dirCache)
            {
                _dirCache.Remove(Parent(path));
                _dirCache.Remove(path);
            }
        }

        private MAVFtp.FtpFileInfo Find(string path)
        {
            if (path == "/")
                return new MAVFtp.FtpFileInfo("", "/", true);
            var name = path.Substring(path.LastIndexOf('/') + 1);
            var entries = ListDir(Parent(path));
            return entries?.FirstOrDefault(e => e.Name == name);
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            var path = FtpPath(req.Url);
            res.AddHeader("DAV", "1, 2");
            res.AddHeader("MS-Author-Via", "DAV");

            switch (req.HttpMethod)
            {
                case "OPTIONS":
                    res.AddHeader("Allow", "OPTIONS, GET, HEAD, PUT, DELETE, MKCOL, MOVE, COPY, PROPFIND, PROPPATCH, LOCK, UNLOCK");
                    res.StatusCode = 200;
                    break;
                case "PROPFIND":
                    PropFind(ctx, path);
                    break;
                case "GET":
                case "HEAD":
                    Get(ctx, path, req.HttpMethod == "HEAD");
                    break;
                case "PUT":
                    Put(ctx, path);
                    break;
                case "DELETE":
                    Delete(ctx, path);
                    break;
                case "MKCOL":
                    lock (_mavftp)
                        res.StatusCode = _mavftp.kCmdCreateDirectory(path, NewCancel()) ? 201 : 409;
                    Invalidate(path);
                    break;
                case "MOVE":
                case "COPY":
                    Move(ctx, path, req.HttpMethod == "COPY");
                    break;
                case "PROPPATCH":
                    PropPatch(ctx, path);
                    break;
                case "LOCK":
                    Lock(ctx, path);
                    break;
                case "UNLOCK":
                    res.StatusCode = 204;
                    break;
                default:
                    res.StatusCode = 405;
                    break;
            }
        }

        private static string Href(string path, bool dir)
        {
            var parts = path.Split('/').Select(Uri.EscapeDataString);
            var href = string.Join("/", parts);
            if (dir && !href.EndsWith("/"))
                href += "/";
            return href == "" ? "/" : href;
        }

        private static void AppendResponse(StringBuilder sb, string path, bool dir, ulong size, DateTime? modified)
        {
            var name = path == "/" ? "MAVFTP" : path.Substring(path.LastIndexOf('/') + 1);
            var when = (modified ?? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUniversalTime();
            sb.Append("<D:response><D:href>").Append(SecurityElement.Escape(Href(path, dir))).Append("</D:href>");
            sb.Append("<D:propstat><D:prop>");
            sb.Append("<D:displayname>").Append(SecurityElement.Escape(name)).Append("</D:displayname>");
            sb.Append("<D:getlastmodified>").Append(when.ToString("r", CultureInfo.InvariantCulture)).Append("</D:getlastmodified>");
            sb.Append("<D:creationdate>").Append(when.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("</D:creationdate>");
            if (dir)
            {
                sb.Append("<D:resourcetype><D:collection/></D:resourcetype>");
            }
            else
            {
                sb.Append("<D:resourcetype/>");
                sb.Append("<D:getcontentlength>").Append(size).Append("</D:getcontentlength>");
                sb.Append("<D:getcontenttype>application/octet-stream</D:getcontenttype>");
            }
            sb.Append("<D:supportedlock><D:lockentry><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype></D:lockentry></D:supportedlock>");
            sb.Append("</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
        }

        private static void WriteXml(HttpListenerResponse res, int status, string xml)
        {
            var bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + xml);
            res.StatusCode = status;
            res.ContentType = "application/xml; charset=utf-8";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
        }

        private void PropFind(HttpListenerContext ctx, string path)
        {
            var res = ctx.Response;
            var depth = ctx.Request.Headers["Depth"] ?? "1";

            byte[] shadow;
            lock (_shadow)
                _shadow.TryGetValue(path, out shadow);
            if (shadow != null)
            {
                var one = new StringBuilder("<D:multistatus xmlns:D=\"DAV:\">");
                AppendResponse(one, path, false, (ulong) shadow.Length, DateTime.UtcNow);
                WriteXml(res, 207, one.Append("</D:multistatus>").ToString());
                return;
            }
            if (IsShadow(path))
            {
                res.StatusCode = 404;
                return;
            }

            var entry = Find(path);
            if (entry == null)
            {
                res.StatusCode = 404;
                return;
            }

            var sb = new StringBuilder("<D:multistatus xmlns:D=\"DAV:\">");
            AppendResponse(sb, path, entry.isDirectory, entry.Size, entry.ModifiedUtc);
            if (entry.isDirectory && depth != "0")
            {
                var children = ListDir(path) ?? new List<MAVFtp.FtpFileInfo>();
                foreach (var child in children)
                    AppendResponse(sb, (path == "/" ? "" : path) + "/" + child.Name, child.isDirectory, child.Size, child.ModifiedUtc);
                lock (_shadow)
                    foreach (var kv in _shadow.Where(k => Parent(k.Key) == path))
                        AppendResponse(sb, kv.Key, false, (ulong) kv.Value.Length, DateTime.UtcNow);
            }
            WriteXml(res, 207, sb.Append("</D:multistatus>").ToString());
        }

        private void Get(HttpListenerContext ctx, string path, bool head)
        {
            var res = ctx.Response;
            byte[] data;
            lock (_shadow)
                _shadow.TryGetValue(path, out data);
            if (data == null)
            {
                var entry = IsShadow(path) ? null : Find(path);
                if (entry == null || entry.isDirectory)
                {
                    res.StatusCode = entry == null ? 404 : 405;
                    return;
                }
                if (head)
                {
                    res.StatusCode = 200;
                    res.ContentLength64 = (long) entry.Size;
                    return;
                }
                MemoryStream ms;
                lock (_mavftp)
                    ms = _mavftp.GetFile(path, NewCancel());
                if (ms == null)
                {
                    res.StatusCode = 404;
                    return;
                }
                data = ms.ToArray();
            }

            res.StatusCode = 200;
            res.ContentType = "application/octet-stream";
            res.ContentLength64 = data.Length;
            if (!head)
                res.OutputStream.Write(data, 0, data.Length);
        }

        private void Put(HttpListenerContext ctx, string path)
        {
            var ms = new MemoryStream();
            ctx.Request.InputStream.CopyTo(ms);
            if (IsShadow(path))
            {
                lock (_shadow)
                    _shadow[path] = ms.ToArray();
                ctx.Response.StatusCode = 201;
                return;
            }

            ms.Position = 0;
            lock (_mavftp)
                _mavftp.UploadFile(path, ms, NewCancel());
            Invalidate(path);
            ctx.Response.StatusCode = 201;
        }

        private void Delete(HttpListenerContext ctx, string path)
        {
            lock (_shadow)
                if (_shadow.Remove(path))
                {
                    ctx.Response.StatusCode = 204;
                    return;
                }

            var entry = Find(path);
            if (entry == null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            bool ok;
            lock (_mavftp)
                ok = entry.isDirectory
                    ? _mavftp.kCmdRemoveDirectory(path, NewCancel())
                    : _mavftp.kCmdRemoveFile(path, NewCancel());
            Invalidate(path);
            ctx.Response.StatusCode = ok ? 204 : 409;
        }

        private void Move(HttpListenerContext ctx, string path, bool copy)
        {
            var dest = ctx.Request.Headers["Destination"];
            if (dest == null)
            {
                ctx.Response.StatusCode = 400;
                return;
            }
            var destPath = FtpPath(new Uri(dest));

            byte[] shadow;
            lock (_shadow)
                _shadow.TryGetValue(path, out shadow);
            if (shadow != null)
            {
                lock (_shadow)
                {
                    _shadow[destPath] = shadow;
                    if (!copy)
                        _shadow.Remove(path);
                }
                ctx.Response.StatusCode = 201;
                return;
            }

            bool ok;
            if (copy)
            {
                MemoryStream ms;
                lock (_mavftp)
                {
                    ms = _mavftp.GetFile(path, NewCancel());
                    ms.Position = 0;
                    _mavftp.UploadFile(destPath, ms, NewCancel());
                }
                ok = true;
            }
            else
            {
                lock (_mavftp)
                    ok = _mavftp.kCmdRename(path, destPath, NewCancel());
            }
            Invalidate(path);
            Invalidate(destPath);
            ctx.Response.StatusCode = ok ? 201 : 409;
        }

        private void PropPatch(HttpListenerContext ctx, string path)
        {
            // accept and ignore property changes (timestamps, Finder info)
            new StreamReader(ctx.Request.InputStream).ReadToEnd();
            var sb = new StringBuilder("<D:multistatus xmlns:D=\"DAV:\"><D:response><D:href>");
            sb.Append(SecurityElement.Escape(Href(path, false))).Append("</D:href>");
            sb.Append("<D:propstat><D:prop/><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>");
            WriteXml(ctx.Response, 207, sb.ToString());
        }

        private void Lock(HttpListenerContext ctx, string path)
        {
            // Finder only mounts writable when locking works; the vehicle has a
            // single client, so every lock is granted.
            new StreamReader(ctx.Request.InputStream).ReadToEnd();
            var token = "opaquelocktoken:" + Guid.NewGuid();
            ctx.Response.AddHeader("Lock-Token", "<" + token + ">");
            var xml = "<D:prop xmlns:D=\"DAV:\"><D:lockdiscovery><D:activelock>" +
                      "<D:locktype><D:write/></D:locktype><D:lockscope><D:exclusive/></D:lockscope>" +
                      "<D:depth>0</D:depth><D:timeout>Second-3600</D:timeout>" +
                      "<D:locktoken><D:href>" + token + "</D:href></D:locktoken>" +
                      "<D:lockroot><D:href>" + SecurityElement.Escape(Href(path, false)) + "</D:href></D:lockroot>" +
                      "</D:activelock></D:lockdiscovery></D:prop>";
            WriteXml(ctx.Response, 200, xml);
        }
    }
}
