// ExtractJolLinks.cs
// Reads the thread pages saved by CrawlJolForum.cs and pulls out every link, inline image and
// bare URL posted in a message, with who posted it, when, and a little surrounding text.
//
//   dotnet run ExtractJolLinks.cs -- FOLDER
//
// Writes FOLDER/links.tsv  (thread, title, post, date, author, kind, url, domain, context)
// and     FOLDER/posts.tsv  (thread, title, post, date, author, plain text) for keyword searches.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length != 1 || !Directory.Exists(Path.Combine(args[0], "threads")))
{
    Console.Error.WriteLine("Usage: dotnet run ExtractJolLinks.cs -- FOLDER   (the --out folder of CrawlJolForum.cs)");
    return 2;
}
string dir = args[0];

var titles = File.ReadLines(Path.Combine(dir, "threads.tsv")).Skip(1)
    .Select(l => l.Split('\t')).ToDictionary(f => f[0], f => f[1]);

var message = new Regex(
    @"<p class=""auteur"">\s*Par\s*(?:<a [^>]*>)?(?<author>.*?)(?:</a>)?\s*le <span class=""date"">(?<date>[^<]*)</span>\s*\(#(?<post>\d+)\)\s*</p>\s*" +
    @"<p class=""texte""><texte>(?<body>.*?)</texte></p>",
    RegexOptions.Singleline);
var anchor = new Regex(@"<a\s[^>]*href=""(?<url>[^""]+)""[^>]*>(?<text>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
var image = new Regex(@"<img\s[^>]*src=""(?<url>[^""]+)""", RegexOptions.IgnoreCase);
var bareUrl = new Regex(@"(?<![""=/\w])(?:(?:https?|ftp)://|www\.)[^\s<>""']+", RegexOptions.IgnoreCase);
var smiley = new Regex(@"/(?:images/)?smilies/|/img/smilies?/", RegexOptions.IgnoreCase);

var links = new List<string> { "thread\ttitle\tpost\tdate\tauthor\tkind\turl\tdomain\tcontext" };
var posts = new List<string> { "thread\ttitle\tpost\tdate\tauthor\ttext" };
int files = 0, messages = 0;

foreach (string file in Directory.EnumerateFiles(Path.Combine(dir, "threads"), "*.html").Order())
{
    files++;
    string name = Path.GetFileNameWithoutExtension(file);
    string thread = name.Split('-')[0];
    string title = titles.GetValueOrDefault(thread, "");
    string html = File.ReadAllText(file, Encoding.UTF8);
    foreach (Match m in message.Matches(html))
    {
        messages++;
        string author = Plain(m.Groups["author"].Value), date = m.Groups["date"].Value.Replace(" à ", " ");
        string post = m.Groups["post"].Value, body = m.Groups["body"].Value;
        string text = Plain(body);
        posts.Add(Row(thread, title, post, date, author, text));

        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in anchor.Matches(body))
            Add("link", a.Groups["url"].Value, Plain(a.Groups["text"].Value));
        foreach (Match i in image.Matches(body))
            if (!smiley.IsMatch(i.Groups["url"].Value)) Add("image", i.Groups["url"].Value, "");
        // URLs typed as plain text (not already caught as a link)
        foreach (Match u in bareUrl.Matches(text))
            Add("text", u.Value.TrimEnd('.', ',', ')', ';', '!', '?', ':'), "");

        void Add(string kind, string url, string linkText)
        {
            url = WebUtility.HtmlDecode(url).Trim();
            if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "http://" + url;
            if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || !found.Add(url)) return;
            string domain = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "(relative)";
            if (domain.StartsWith("www.")) domain = domain[4..];
            string context = linkText.Length > 0 && !linkText.Equals(url, StringComparison.OrdinalIgnoreCase)
                ? $"[{linkText}] {Snippet(text, 200)}" : Snippet(text, 200);
            links.Add(Row(thread, title, post, date, author, kind, url, domain, context));
        }
    }
}

File.WriteAllLines(Path.Combine(dir, "links.tsv"), links);
File.WriteAllLines(Path.Combine(dir, "posts.tsv"), posts);
Console.WriteLine($"{files} thread pages, {messages} messages, {links.Count - 1} links/images");
return 0;

static string Plain(string html)
{
    html = Regex.Replace(html, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
    html = Regex.Replace(html, "<[^>]+>", " ");
    return Regex.Replace(WebUtility.HtmlDecode(html), @"\s+", " ").Trim();
}

static string Snippet(string s, int max) => s.Length <= max ? s : s[..max] + "...";

static string Row(params string[] fields) =>
    string.Join('\t', fields.Select(f => f.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')));
