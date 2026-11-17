// Paikallinen Xtream Codes -testipalvelin Kanava-soittimen kehitykseen.
// Käynnistys: dotnet run --project MockXtream   (kuuntelee http://localhost:8085/)
// Tunnukset: test / test
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using System.Text.Json;

const string User = "test", Pass = "test";
var port = args.Length > 0 ? int.Parse(args[0]) : 8085;
var rnd = new Random(42);

string[] catNames = ["Suomi", "Uutiset", "Urheilu", "Elokuvat", "Lapset", "Dokumentit", "Musiikki", "Ruotsi", "UK", "Viihde"];
string[] showWords = ["Uutiset", "Sää", "Huomenta", "Iltapäivä", "Elokuva", "Dokumentti", "Jalkapallo", "Jääkiekko", "Visailu",
    "Ruokaohjelma", "Talk show", "Sarja", "Luonto", "Matkailu", "Historia", "Tiede", "Musiikki", "Draama", "Komedia", "Rikos"];

var categories = catNames.Select((n, i) => new { category_id = (i + 1).ToString(), category_name = n, parent_id = 0 }).ToList();
var channels = Enumerable.Range(1, 320).Select(i => new
{
    num = i,
    name = $"{catNames[(i - 1) % catNames.Length]} {i}" + (i % 7 == 0 ? " HD" : ""),
    stream_type = "live",
    stream_id = 1000 + i,
    stream_icon = i % 9 == 0 ? "" : $"http://localhost:{port}/img/logo/{i}.png",
    epg_channel_id = i % 13 == 0 ? null : $"ch{i}.fi",
    added = "1700000000",
    category_id = (((i - 1) % catNames.Length) + 1).ToString(),
    tv_archive = i % 4 == 0 ? 1 : 0,
    tv_archive_duration = i % 4 == 0 ? 3 : 0,
}).ToList();

// EPG: ohjelmia nyt -8 h ... +30 h
var epgStart = DateTime.Now.Date.AddHours(DateTime.Now.Hour - 8);
var programs = new Dictionary<string, List<(DateTime s, DateTime e, string t, string d)>>();
foreach (var ch in channels.Where(c => c.epg_channel_id != null))
{
    var list = new List<(DateTime, DateTime, string, string)>();
    var t = epgStart.AddMinutes(-rnd.Next(0, 60));
    while (t < DateTime.Now.AddHours(30))
    {
        var len = new[] { 15, 30, 30, 45, 60, 60, 90, 120 }[rnd.Next(8)];
        var title = showWords[rnd.Next(showWords.Length)] + (rnd.Next(3) == 0 ? $" ({rnd.Next(1, 20)}/20)" : "");
        list.Add((t, t.AddMinutes(len), title, $"{title}: kuvaus ohjelmasta kanavalla {ch.name}. Jakso sisältää paljon kiinnostavaa testisisältöä."));
        t = t.AddMinutes(len);
    }
    programs[ch.epg_channel_id!] = list;
}

var vodCats = new[] { "Toiminta", "Komedia", "Draama", "Kauhu", "Dokumentit", "Lasten" }
    .Select((n, i) => new { category_id = (100 + i).ToString(), category_name = n, parent_id = 0 }).ToList();
var vods = Enumerable.Range(1, 240).Select(i => new
{
    num = i, name = $"Testielokuva {i}", stream_type = "movie", stream_id = 5000 + i,
    stream_icon = $"http://localhost:{port}/img/poster/{i}.png",
    rating = (rnd.Next(30, 95) / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
    added = (1700000000 + i * 3600).ToString(), category_id = (100 + i % 6).ToString(), container_extension = "mjpg",
}).ToList();

var seriesCats = new[] { "Draamasarjat", "Komediasarjat", "Rikossarjat", "Dokumenttisarjat" }
    .Select((n, i) => new { category_id = (200 + i).ToString(), category_name = n, parent_id = 0 }).ToList();
var series = Enumerable.Range(1, 60).Select(i => new
{
    num = i, name = $"Testisarja {i}", series_id = 7000 + i,
    cover = $"http://localhost:{port}/img/poster/{i + 500}.png",
    plot = $"Testisarjan {i} juoni: jännittäviä tapahtumia kausi toisensa jälkeen.",
    genre = "Draama", releaseDate = $"{2010 + i % 15}-01-01", rating = "7.5", category_id = (200 + i % 4).ToString(),
}).ToList();

var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{port}/");
listener.Start();
Console.WriteLine($"MockXtream: http://localhost:{port}/  (tunnukset {User}/{Pass})");

while (true)
{
    var ctx = await listener.GetContextAsync();
    _ = Task.Run(() => Handle(ctx));
}

async Task Handle(HttpListenerContext ctx)
{
    var req = ctx.Request;
    var res = ctx.Response;
    var path = req.Url!.AbsolutePath;
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} {path}{req.Url.Query}");
    try
    {
        if (path == "/player_api.php") await Api(req, res);
        else if (path == "/xmltv.php") await Xmltv(req, res);
        else if (path.StartsWith("/img/")) await Img(path, res);
        else if ((path.StartsWith("/movie/") || path.StartsWith("/series/")) && SampleFile() is { } f) await ServeFile(f, req, res);
        else if (path.StartsWith("/live/") || path.StartsWith("/movie/") || path.StartsWith("/series/")) await Mjpeg(path, res);
        else if (path.StartsWith("/timeshift/")) await Mjpeg(path, res);
        else { res.StatusCode = 404; res.Close(); }
    }
    catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
    catch (Exception ex) { Console.WriteLine(ex); try { res.StatusCode = 500; res.Close(); } catch { } }
}

async Task Json(HttpListenerResponse res, object o)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(o);
    res.ContentType = "application/json";
    res.ContentLength64 = bytes.Length;
    await res.OutputStream.WriteAsync(bytes);
    res.Close();
}

async Task Api(HttpListenerRequest req, HttpListenerResponse res)
{
    var q = req.QueryString;
    if (q["username"] != User || q["password"] != Pass)
    {
        await Json(res, new { user_info = new { auth = 0 } });
        return;
    }
    switch (q["action"])
    {
        case null or "":
            await Json(res, new
            {
                user_info = new
                {
                    username = User, password = Pass, auth = 1, status = "Active",
                    exp_date = DateTimeOffset.Now.AddDays(90).ToUnixTimeSeconds().ToString(),
                    is_trial = "0", active_cons = "0", max_connections = "2", allowed_output_formats = new[] { "m3u8", "ts" },
                },
                server_info = new { url = "localhost", port = port.ToString(), timezone = "Europe/Helsinki" },
            });
            break;
        case "get_live_categories": await Json(res, categories); break;
        case "get_live_streams": await Json(res, channels); break;
        case "get_vod_categories": await Json(res, vodCats); break;
        case "get_vod_streams": await Json(res, vods); break;
        case "get_series_categories": await Json(res, seriesCats); break;
        case "get_series": await Json(res, series); break;
        case "get_vod_info":
            var vid = int.Parse(q["vod_id"] ?? "0");
            await Json(res, new
            {
                info = new
                {
                    plot = $"Elokuvan {vid - 5000} juoni. Sankari lähtee seikkailuun ja kohtaa monenlaisia haasteita matkallaan.",
                    cast = "Näyttelijä Yksi, Näyttelijä Kaksi", director = "Ohjaaja Testinen", genre = "Toiminta",
                    releasedate = "2024-05-01", duration = "01:52:00",
                    backdrop_path = new[] { $"http://localhost:{port}/img/backdrop/{vid}.png" },
                },
                movie_data = new { stream_id = vid, container_extension = "mjpg" },
            });
            break;
        case "get_series_info":
            var sid = int.Parse(q["series_id"] ?? "0");
            var eps = new Dictionary<string, object>();
            for (var s = 1; s <= 3; s++)
                eps[s.ToString()] = Enumerable.Range(1, 8).Select(e => new
                {
                    id = $"{sid}{s:00}{e:00}", episode_num = e, title = $"Jakso {e}", container_extension = "mjpg", season = s,
                    info = new { plot = $"Kauden {s} jakson {e} kuvaus.", duration = "00:45:00" },
                }).ToArray();
            await Json(res, new
            {
                seasons = Enumerable.Range(1, 3).Select(s => new { season_number = s, name = $"Season {s}" }),
                info = new { name = $"Testisarja {sid - 7000}", plot = "Sarjan juoni.", cast = "Näyttelijä A, Näyttelijä B", genre = "Draama" },
                episodes = eps,
            });
            break;
        case "get_short_epg":
            var stream = int.Parse(q["stream_id"] ?? "0");
            var limit = int.Parse(q["limit"] ?? "4");
            var ch = channels.FirstOrDefault(c => c.stream_id == stream);
            var listings = ch?.epg_channel_id is { } eid && programs.TryGetValue(eid, out var pl)
                ? pl.Where(p => p.e > DateTime.Now).Take(limit).Select(p => new
                {
                    title = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.t)),
                    description = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.d)),
                    start_timestamp = new DateTimeOffset(p.s).ToUnixTimeSeconds().ToString(),
                    stop_timestamp = new DateTimeOffset(p.e).ToUnixTimeSeconds().ToString(),
                }).ToArray()
                : [];
            await Json(res, new { epg_listings = listings });
            break;
        default: await Json(res, Array.Empty<object>()); break;
    }
}

async Task Xmltv(HttpListenerRequest req, HttpListenerResponse res)
{
    var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<tv generator-info-name=\"MockXtream\">\n");
    foreach (var ch in channels.Where(c => c.epg_channel_id != null))
        sb.Append($"  <channel id=\"{ch.epg_channel_id}\"><display-name>{ch.name}</display-name></channel>\n");
    foreach (var (id, list) in programs)
        foreach (var p in list)
            sb.Append($"  <programme start=\"{p.s:yyyyMMddHHmmss} {Off(p.s)}\" stop=\"{p.e:yyyyMMddHHmmss} {Off(p.e)}\" channel=\"{id}\">" +
                      $"<title lang=\"fi\">{System.Security.SecurityElement.Escape(p.t)}</title><desc lang=\"fi\">{System.Security.SecurityElement.Escape(p.d)}</desc></programme>\n");
    sb.Append("</tv>\n");
    var bytes = Encoding.UTF8.GetBytes(sb.ToString());
    res.ContentType = "application/xml";
    res.ContentLength64 = bytes.Length;
    await res.OutputStream.WriteAsync(bytes);
    res.Close();
}

static string Off(DateTime t)
{
    var o = TimeZoneInfo.Local.GetUtcOffset(t);
    return $"{(o < TimeSpan.Zero ? "-" : "+")}{Math.Abs(o.Hours):00}{Math.Abs(o.Minutes):00}";
}

async Task Img(string path, HttpListenerResponse res)
{
    // /img/logo/N.png, /img/poster/N.png, /img/backdrop/N.png
    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var kind = parts[1];
    var n = int.Parse(Path.GetFileNameWithoutExtension(parts[2]));
    var (w, h) = kind switch { "logo" => (160, 100), "poster" => (240, 360), _ => (640, 360) };
    using var bmp = new Bitmap(w, h);
    using (var g = Graphics.FromImage(bmp))
    {
        var hue = n * 47 % 360;
        g.Clear(FromHsv(hue, 0.6, kind == "logo" ? 0.85 : 0.45));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", kind == "logo" ? 36 : 28, FontStyle.Bold);
        var text = kind == "logo" ? n.ToString() : kind == "poster" ? $"#{n}" : "Kanava";
        var sz = g.MeasureString(text, font);
        g.DrawString(text, font, Brushes.White, (w - sz.Width) / 2, (h - sz.Height) / 2);
    }
    using var ms = new MemoryStream();
    bmp.Save(ms, ImageFormat.Png);
    res.ContentType = "image/png";
    res.ContentLength64 = ms.Length;
    await res.OutputStream.WriteAsync(ms.ToArray());
    res.Close();
}

// Testivideo elokuville ja jaksoille (pituus + haku → jatka katsomista / seuraava jakso testattavissa)
string? SampleFile()
{
    var f = Path.Combine(AppContext.BaseDirectory, "sample.ts");
    return File.Exists(f) ? f : null;
}

async Task ServeFile(string file, HttpListenerRequest req, HttpListenerResponse res)
{
    var len = new FileInfo(file).Length;
    long from = 0, to = len - 1;
    var range = req.Headers["Range"];
    if (range is not null && range.StartsWith("bytes="))
    {
        var parts = range[6..].Split('-');
        if (long.TryParse(parts[0], out var a)) from = a;
        if (parts.Length > 1 && long.TryParse(parts[1], out var b)) to = Math.Min(b, len - 1);
        res.StatusCode = 206;
        res.AddHeader("Content-Range", $"bytes {from}-{to}/{len}");
    }
    res.AddHeader("Accept-Ranges", "bytes");
    res.ContentType = "video/mp2t";
    res.ContentLength64 = to - from + 1;
    await using var fs = File.OpenRead(file);
    fs.Position = from;
    var buf = new byte[64 * 1024];
    var left = to - from + 1;
    while (left > 0)
    {
        var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)));
        if (n <= 0) break;
        await res.OutputStream.WriteAsync(buf.AsMemory(0, n));
        left -= n;
    }
    res.Close();
}

// Liikkuva testikuva MJPEG-virtana (multipart/x-mixed-replace), jota VLC osaa toistaa
async Task Mjpeg(string path, HttpListenerResponse res)
{
    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 4 || parts[1] != User || parts[2] != Pass) { res.StatusCode = 401; res.Close(); return; }
    var id = Path.GetFileNameWithoutExtension(parts[^1]);
    var label = parts[0] switch
    {
        "live" => channels.FirstOrDefault(c => c.stream_id.ToString() == id)?.name ?? id,
        "movie" => $"Elokuva {id}",
        "timeshift" => $"Arkisto {parts[4]}",
        _ => $"Jakso {id}",
    };
    // Kanava 1050 katkeaa 8 s välein (uudelleenyhdistyksen testi)
    var dropAt = parts[0] == "live" && id == "1050" ? DateTime.Now.AddSeconds(8) : DateTime.MaxValue;
    res.ContentType = "multipart/x-mixed-replace; boundary=frame";
    res.SendChunked = true;
    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
    var ep = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L) } };
    var hue = id.GetHashCode() & 0xFFFF % 360;
    var frame = 0;
    using var big = new Font("Segoe UI", 48, FontStyle.Bold);
    using var small = new Font("Segoe UI", 24);
    while (DateTime.Now < dropAt)
    {
        using var bmp = new Bitmap(960, 540);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(FromHsv(Math.Abs(hue) % 360, 0.5, 0.35));
            var x = (int)(480 + 380 * Math.Sin(frame / 15.0));
            g.FillEllipse(Brushes.White, x - 20, 440, 40, 40);
            g.DrawString(label, big, Brushes.White, 40, 160);
            g.DrawString(DateTime.Now.ToString("HH:mm:ss"), small, Brushes.LightGray, 44, 260);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, ep);
        var header = Encoding.ASCII.GetBytes($"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {ms.Length}\r\n\r\n");
        await res.OutputStream.WriteAsync(header);
        await res.OutputStream.WriteAsync(ms.ToArray());
        await res.OutputStream.WriteAsync("\r\n"u8.ToArray());
        await res.OutputStream.FlushAsync();
        frame++;
        await Task.Delay(80);
    }
    res.Abort(); // katkaistaan yhteys (testikanava)
}

static Color FromHsv(double h, double s, double v)
{
    var c = v * s; var x = c * (1 - Math.Abs(h / 60 % 2 - 1)); var m = v - c;
    var (r, g, b) = h switch { < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x), < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x) };
    return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
}
