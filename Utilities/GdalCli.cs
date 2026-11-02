using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.Projections;
using GMap.NET.WindowsForms;
using log4net;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace MissionPlanner.Utilities
{
    /// <summary>
    /// Georeferenced imagery overlay ("GDAL Custom" map) using the GDAL command
    /// line tools, for platforms without the GDAL .NET bindings (macOS, Linux).
    /// Each image is reprojected once to WGS84 and cached as a PNG, then drawn
    /// into map tiles like the native implementation does.
    /// </summary>
    public class GdalCli : IGDAL
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly string[] searchDirs =
            {"/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/Library/Frameworks/GDAL.framework/Programs"};

        // largest side of the cached reprojected image
        private const int MaxCachedSize = 8192;

        private static readonly object locker = new object();
        private static readonly List<Image> images = new List<Image>();

        private class Image
        {
            public string File;
            public RectLatLng Rect;
            public int Width, Height;
            public double Resolution;
            public Bitmap Bitmap;
        }

        public static string Tool(string name)
        {
            foreach (var dir in searchDirs)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                    return path;
            }

            return null;
        }

        public static bool Available
        {
            get { return Tool("gdalinfo") != null && Tool("gdalwarp") != null && Tool("gdal_translate") != null; }
        }

        private static string Run(string tool, params string[] args)
        {
            var psi = new ProcessStartInfo(Tool(tool),
                string.Join(" ", args.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using (var p = Process.Start(psi))
            {
                var stderr = p.StandardError.ReadToEndAsync();
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                    throw new Exception(tool + " failed: " + stderr.Result.Trim());
                return output;
            }
        }

        private static string CacheDir
        {
            get
            {
                var dir = Path.Combine(Settings.GetDataDirectory(), "gdalcache");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string CacheName(string file)
        {
            var fi = new FileInfo(file);
            using (var sha = SHA1.Create())
            {
                var key = Encoding.UTF8.GetBytes(fi.FullName + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks);
                return BitConverter.ToString(sha.ComputeHash(key)).Replace("-", "").Substring(0, 20);
            }
        }

        public void ScanDirectory(string path)
        {
            if (!Directory.Exists(path) || !Available)
                return;

            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
            int i = 0;
            foreach (var file in files)
            {
                i++;
                try
                {
                    // same filter as the native implementation: skip tiny files
                    if (new FileInfo(file).Length < 1024)
                        continue;
                    GDAL.ReportProgress((i - 1) / (double) files.Length, file);
                    var image = Load(file);
                    if (image != null)
                        lock (locker)
                            images.Add(image);
                }
                catch (Exception ex)
                {
                    log.Error(file + ": " + ex.Message);
                }
            }

            // lowest resolution first, so detailed images are drawn on top
            lock (locker)
                images.Sort((a, b) => b.Resolution.CompareTo(a.Resolution));
        }

        private static Image Load(string file)
        {
            // only georeferenced rasters
            var info = JObject.Parse(Run("gdalinfo", "-json", file));
            if (info["coordinateSystem"] == null && info["gcps"] == null)
                return null;
            if (info["size"] == null)
                return null;

            var name = CacheName(file);
            var png = Path.Combine(CacheDir, name + ".png");
            var meta = Path.Combine(CacheDir, name + ".json");

            if (!File.Exists(png) || !File.Exists(meta))
            {
                var vrt = Path.Combine(CacheDir, name + ".vrt");
                // reproject to WGS84 with an alpha band for the area outside the image
                Run("gdalwarp", "-overwrite", "-of", "VRT", "-t_srs", "EPSG:4326", "-dstalpha", "-r", "bilinear", file, vrt);
                var warped = JObject.Parse(Run("gdalinfo", "-json", vrt));
                int w = (int) warped["size"][0], h = (int) warped["size"][1];
                var scale = Math.Min(1.0, MaxCachedSize / (double) Math.Max(w, h));
                int ow = Math.Max(1, (int) (w * scale)), oh = Math.Max(1, (int) (h * scale));
                Run("gdal_translate", "-of", "PNG", "-outsize", ow.ToString(CultureInfo.InvariantCulture),
                    oh.ToString(CultureInfo.InvariantCulture), "-r", "bilinear", vrt, png);
                var corners = warped["cornerCoordinates"];
                var bounds = new JObject
                {
                    ["left"] = (double) corners["upperLeft"][0],
                    ["top"] = (double) corners["upperLeft"][1],
                    ["right"] = (double) corners["lowerRight"][0],
                    ["bottom"] = (double) corners["lowerRight"][1],
                    ["width"] = ow,
                    ["height"] = oh,
                    ["source"] = file
                };
                File.WriteAllText(meta, bounds.ToString());
                try { File.Delete(vrt); } catch { }
                try { File.Delete(png + ".aux.xml"); } catch { }
            }

            var m = JObject.Parse(File.ReadAllText(meta));
            double left = (double) m["left"], top = (double) m["top"], right = (double) m["right"], bottom = (double) m["bottom"];
            var image = new Image
            {
                File = file,
                Rect = new RectLatLng(top, left, right - left, top - bottom),
                Width = (int) m["width"],
                Height = (int) m["height"],
                Bitmap = new Bitmap(png)
            };
            image.Resolution = (right - left) / image.Width;
            log.InfoFormat("GDAL image {0} {1} {2}x{3}", file, image.Rect, image.Width, image.Height);
            return image;
        }

        public static Bitmap GetBitmap(double lng1, double lat1, double lng2, double lat2, int width, int height)
        {
            Image[] list;
            lock (locker)
                list = images.ToArray();
            if (list.Length == 0)
                return null;

            var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            int drawn = 0;
            using (var g = Graphics.FromImage(output))
            {
                g.Clear(Color.Transparent);
                var request = new RectLatLng(lat1, lng1, lng2 - lng1, lat1 - lat2);
                foreach (var image in list)
                {
                    if (!image.Rect.IntersectsWith(request))
                        continue;
                    float x1 = (float) Map(request.Left, image.Rect.Left, image.Rect.Right, 0, image.Width);
                    float x2 = (float) Map(request.Right, image.Rect.Left, image.Rect.Right, 0, image.Width);
                    float y1 = (float) Map(request.Top, image.Rect.Top, image.Rect.Bottom, 0, image.Height);
                    float y2 = (float) Map(request.Bottom, image.Rect.Top, image.Rect.Bottom, 0, image.Height);
                    lock (image)
                        g.DrawImage(image.Bitmap, new RectangleF(0, 0, width, height), new RectangleF(x1, y1, x2 - x1, y2 - y1), GraphicsUnit.Pixel);
                    if (++drawn >= 50)
                        break;
                }
            }

            if (drawn == 0)
            {
                output.Dispose();
                return null;
            }

            return output;
        }

        private static double Map(double x, double inMin, double inMax, double outMin, double outMax)
        {
            return (x - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;
        }

        public GMapProvider GetProvider()
        {
            return Provider.Instance;
        }

        /// <summary>The "GDAL Custom" map type: satellite base with the images on top.</summary>
        public class Provider : GMapProvider
        {
            public static readonly Provider Instance = new Provider();

            private readonly Guid id = new Guid("8E1F7C34-2B7A-4D66-9C4B-0B6D1A3C55E1");
            private GMapProvider[] overlays;

            private Provider()
            {
                MaxZoom = 24;
                BypassCache = true;
                // register like GDAL.GDALProvider, so the map type can be restored from settings
                try
                {
                    var field = typeof(GMapProviders).GetField("DbHash", BindingFlags.Static | BindingFlags.NonPublic);
                    var list = (Dictionary<int, GMapProvider>) field.GetValue(null);
                    if (!list.ContainsKey(DbId))
                        list.Add(DbId, this);
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }
            }

            public override Guid Id
            {
                get { return id; }
            }

            public override string Name
            {
                get { return "GDAL Custom"; }
            }

            public override PureProjection Projection
            {
                get { return MercatorProjection.Instance; }
            }

            public override GMapProvider[] Overlays
            {
                get { return overlays ?? (overlays = new GMapProvider[] {GoogleSatelliteMapProvider.Instance, this}); }
            }

            public override PureImage GetTileImage(GPoint pos, int zoom)
            {
                var px1 = Projection.FromTileXYToPixel(pos);
                var px2 = px1;
                var p1 = Projection.FromPixelToLatLng(px1, zoom);
                px2.Offset(Projection.TileSize.Width, Projection.TileSize.Height);
                var p2 = Projection.FromPixelToLatLng(px2, zoom);

                var bmp = GetBitmap(p1.Lng, p1.Lat, p2.Lng, p2.Lat, (int) Projection.TileSize.Width, (int) Projection.TileSize.Height);
                if (bmp == null)
                    return null;

                var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                return new GMapImage {Img = bmp, Data = ms};
            }
        }
    }
}
