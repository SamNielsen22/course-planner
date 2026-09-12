using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Web.Tests;

/// <summary>
/// The real site, hosted in-process against the real catalogue, with accounts
/// off. Every test drives it over HTTP the way a browser would - pages, the
/// builder's handlers, the calendar file - so a change that breaks what a
/// student sees fails here rather than on the site.
///
/// The catalogue is opened read-only: nothing here may write to it. Fixtures
/// are discovered from it rather than hard-coded, so the suite survives a
/// re-crawl - a test asks for "a Saturday section in the newest term", not
/// for ESSF 1130-002.
/// </summary>
public sealed class Site : WebApplicationFactory<Program>
{
    public static readonly string RepoRoot = FindRepoRoot();
    public static readonly string DatabasePath = Path.Combine(RepoRoot, "data", "courseplanner.db");

    public Catalogue Catalogue { get; } = new(DatabasePath);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: user-secrets stay out, so accounts are off and no
        // test can reach Postgres; and the production pipeline (output cache,
        // forwarded headers) is the one under test.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:CoursePlanner", $"Data Source={DatabasePath};Mode=ReadOnly");
        builder.UseSetting("ConnectionStrings:UserData", "");
        // The test server has no remote address, so the loopback-only trust
        // the site applies to X-Forwarded-* would drop the headers unread.
        builder.ConfigureTestServices(services => services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        }));
    }

    /// <summary>A fresh visitor: its own cookie jar, so its own schedule.</summary>
    public Browser Visitor() => new(CreateClient(new WebApplicationFactoryClientOptions
    {
        HandleCookies = true,
        AllowAutoRedirect = false,
    }));

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data", "courseplanner.db"))) return dir.FullName;
        throw new InvalidOperationException("data/courseplanner.db was not found above the test directory");
    }
}

/// <summary>One visitor's browser: cookies, and the antiforgery token the builder's handlers want.</summary>
public sealed class Browser(HttpClient client)
{
    private string? _token;

    public HttpClient Client => client;

    public async Task<HttpResponseMessage> Send(string url, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return await client.SendAsync(request);
    }

    public async Task<string> Get(string url)
    {
        var response = await client.GetAsync(url);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} -> {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync();
    }

    public async Task<HtmlDocument> Page(string url) => Html(await Get(url));

    public static HtmlDocument Html(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    /// <summary>A builder handler, posted the way the page's script posts it.</summary>
    public async Task<HttpStatusCode> Post(string handler, params (string Name, string Value)[] form)
    {
        _token ??= Regex.Match(await Get("/builder"), @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        var request = new HttpRequestMessage(HttpMethod.Post, "/builder?handler=" + handler)
        {
            Content = new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))),
        };
        request.Headers.Add("RequestVerificationToken", _token);
        return (await client.SendAsync(request)).StatusCode;
    }

    public async Task Add(Catalogue.Section s)
    {
        var status = await Post("Add", ("term", s.Term), ("subject", s.Subject), ("number", s.Number), ("section", s.Number2));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    public Task<HttpStatusCode> AddBreak(string name, string days, string from, string until) =>
        Post("AddBreak", ("name", name), ("days", days), ("from", from), ("until", until));

    /// <summary>The course codes in the cart, in order.</summary>
    public async Task<List<string>> Cart()
    {
        var doc = await Page("/builder?handler=Cart");
        return doc.DocumentNode.SelectNodes("//span[@class='cart-code']")?.Select(n => n.InnerText.Trim()).ToList() ?? [];
    }
}

/// <summary>Read-only questions to the catalogue, for choosing fixtures.</summary>
public sealed class Catalogue(string path)
{
    /// <summary>A section as the builder keys it. Number2 is the section number ("001").</summary>
    public sealed record Section(string Term, string Subject, string Number, string Number2, string? Times, string? Location)
    {
        public string Code => $"{Subject} {Number}";
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        db.Open();
        return db;
    }

    private const string Columns = "term AS Term, subject AS Subject, course_number AS Number, section_number AS Number2, times AS Times, location AS Location";

    /// <summary>The first matching section; on the main campus unless the condition says which campus.</summary>
    public Section One(string where, object? args = null)
    {
        using var db = Open();
        var scope = where.Contains("campus") ? "" : "campus = 'main' AND ";
        var row = db.QueryFirstOrDefault<Section>($"SELECT {Columns} FROM sections WHERE {scope}{where} ORDER BY subject, course_number, section_number LIMIT 1", args);
        return row ?? throw new InvalidOperationException($"no section matches: {where}");
    }

    /// <summary>The term the builder browses: the newest one with sections.</summary>
    public string NewestTerm()
    {
        using var db = Open();
        return Pages.Terms.NewestFirst(db.Query<string>("SELECT DISTINCT term FROM sections")).First();
    }

    public Section Timed(string term) => One("term = @term AND times LIKE '%/%'", new { term });
    public Section OnAsiaCampus(string term) => One("term = @term AND campus = 'uac' AND times LIKE '%/%'", new { term });
    public Section OnSaturday(string term) => One("term = @term AND times LIKE 'Sa/%'", new { term });
    public Section OnWeekdaysOnly(string term) => One("term = @term AND times LIKE 'TuTh/%'", new { term });
    public Section WithDayRange(string term) => One("term = @term AND times LIKE 'Mo-Th/%'", new { term });
    public Section WithTwoRooms(string term) => One("term = @term AND times LIKE '%; %' AND location LIKE '%, %' AND location NOT LIKE 'CANVAS%'", new { term });
    public Section Online(string term) => One("term = @term AND location = 'CANVAS .' AND times LIKE '%/%'", new { term });
    public Section InBuilding(string term, string code) => One("term = @term AND location LIKE @like AND times LIKE '%/%'", new { term, like = code + " %" });

    /// <summary>Two sections of one course that meet at exactly the same time.</summary>
    public (Section A, Section B) SameTimePair(string term)
    {
        using var db = Open();
        var row = db.QueryFirstOrDefault($@"
            SELECT a.subject AS Subject, a.course_number AS Number, a.section_number AS A, b.section_number AS B, a.times AS Times, a.location AS Location
            FROM sections a JOIN sections b ON a.term = b.term AND a.subject = b.subject AND a.course_number = b.course_number
                 AND a.section_number < b.section_number AND a.times = b.times
            WHERE a.term = @term AND a.times LIKE '%/%' LIMIT 1", new { term })
            ?? throw new InvalidOperationException("no two sections of one course share a time");
        return (new Section(term, row.Subject, row.Number, row.A, row.Times, row.Location),
                new Section(term, row.Subject, row.Number, row.B, row.Times, row.Location));
    }

    /// <summary>A professor with two classes in one term that each have several graded sections, plus a class of theirs with one.</summary>
    public (string Unid, string Term, string ClassA, int SectionsA, string ClassB, int SectionsB) ProfessorWithMultiSectionClasses()
    {
        using var db = Open();
        var rows = db.Query(@"
            SELECT si.instructor_unid AS Unid, sg.term AS Term, sg.subject || ' ' || sg.course_number AS Class, COUNT(DISTINCT sg.section_number) AS N
            FROM section_grades sg JOIN section_instructors si
              ON si.term = sg.term AND si.subject = sg.subject AND si.course_number = sg.course_number AND si.section_number = sg.section_number
            GROUP BY 1, 2, 3 HAVING N >= 2").ToList();
        var pair = rows.GroupBy(r => (string)r.Unid + "|" + (string)r.Term).First(g => g.Count() >= 2).Take(2).ToList();
        return ((string)pair[0].Unid, (string)pair[0].Term, (string)pair[0].Class, (int)(long)pair[0].N, (string)pair[1].Class, (int)(long)pair[1].N);
    }

    /// <summary>A term, class and professor where that class has exactly one graded section.</summary>
    public (string Unid, string Term, string Class) ProfessorWithSingleSectionClass()
    {
        using var db = Open();
        var row = db.QueryFirst(@"
            SELECT si.instructor_unid AS Unid, sg.term AS Term, sg.subject || ' ' || sg.course_number AS Class
            FROM section_grades sg JOIN section_instructors si
              ON si.term = sg.term AND si.subject = sg.subject AND si.course_number = sg.course_number AND si.section_number = sg.section_number
            GROUP BY 1, 2, 3 HAVING COUNT(DISTINCT sg.section_number) = 1 LIMIT 1");
        return ((string)row.Unid, (string)row.Term, (string)row.Class);
    }

    /// <summary>A course with a published all-terms row, and one of its graded terms.</summary>
    public (string Subject, string Number, string Term) GradedCourse()
    {
        using var db = Open();
        var row = db.QueryFirst(@"
            SELECT g.subject AS Subject, g.course_number AS Number, s.term AS Term
            FROM course_grades g JOIN section_grades s ON s.subject = g.subject AND s.course_number = g.course_number
            WHERE g.gpa_p50 IS NOT NULL LIMIT 1");
        return ((string)row.Subject, (string)row.Number, (string)row.Term);
    }

    public int CourseCount()
    {
        using var db = Open();
        return db.ExecuteScalar<int>("SELECT COUNT(*) FROM courses");
    }

    public int GradedClassCount()
    {
        using var db = Open();
        return db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM (SELECT DISTINCT si.instructor_unid, sg.subject, sg.course_number
            FROM section_grades sg JOIN section_instructors si ON si.term = sg.term AND si.subject = sg.subject
              AND si.course_number = sg.course_number AND si.section_number = sg.section_number
            WHERE sg.gpa_avg IS NOT NULL)");
    }

    public int InstructorCount()
    {
        using var db = Open();
        return db.ExecuteScalar<int>("SELECT COUNT(*) FROM instructors");
    }

    public (string Unid, string Name) AnInstructor()
    {
        using var db = Open();
        var row = db.QueryFirst("SELECT unid, display_name FROM instructors WHERE display_name LIKE '%, %' LIMIT 1");
        return ((string)row.unid, (string)row.display_name);
    }

    public string AnInstructorName()
    {
        using var db = Open();
        return db.QueryFirst<string>("SELECT display_name FROM instructors WHERE display_name LIKE '%, %' LIMIT 1");
    }
}
