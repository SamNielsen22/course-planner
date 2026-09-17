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
/// The real site, hosted in-process against the real catalogue with accounts
/// off, driven over HTTP the way a browser would. The catalogue is read-only,
/// and fixtures are discovered from it so the suite survives a re-crawl.
/// </summary>
public sealed class Site : WebApplicationFactory<Program>
{
    /// <summary>Set before the first request to host the site as it ships, hidden from search engines.</summary>
    public bool Unlisted { get; init; }

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
        // Listed by default here, so the sitemap and robots tests see the
        // public behaviour; <see cref="Unlisted"/> flips it for the tests that
        // check the shipped default.
        builder.UseSetting("Site:Unlisted", Unlisted ? "true" : "false");
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

    /// <summary>Add a lecture and a chosen companion together, as the popup does.</summary>
    public async Task AddPair(Catalogue.Section lecture, string companionSection)
    {
        var status = await Post("AddPair", ("term", lecture.Term), ("subject", lecture.Subject),
            ("number", lecture.Number), ("lecture", lecture.Number2), ("companion", companionSection));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>The embedded companion-choices JSON for a builder search page.</summary>
    public async Task<System.Text.Json.JsonElement> CompanionData(string query, string term)
    {
        var doc = await Page($"/builder?term={term}&q={Uri.EscapeDataString(query)}&open=false&noClash=false");
        var json = doc.DocumentNode.SelectSingleNode("//script[@id='companion-data']")!.InnerText;
        return System.Text.Json.JsonDocument.Parse(json).RootElement;
    }

    public Task<HttpStatusCode> AddBreak(string name, string days, string from, string until) =>
        Post("AddBreak", ("name", name), ("days", days), ("from", from), ("until", until));

    /// <summary>The course codes in the cart, in order.</summary>
    public async Task<List<string>> Cart()
    {
        var doc = await Page("/builder?handler=Cart");
        return doc.DocumentNode.SelectNodes("//a[@class='cart-code']")?.Select(n => n.InnerText.Trim()).ToList() ?? [];
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

    /// <summary>
    /// A course with a published pairing that also has a self-contained lecture
    /// (one with no companion paired to it, typically online). Returns the course
    /// and both a lecture that needs a companion and one that does not.
    /// </summary>
    public (string Subject, string Number, string NeedsCompanion, string SelfContained) CourseWithAStandaloneLecture()
    {
        using var db = Open();
        var row = db.QueryFirstOrDefault<(string Sub, string Num, string Need, string Solo)>(@"
            SELECT c.subject AS Sub, c.course_number AS Num,
                   (SELECT k.pairs_with FROM sections k WHERE k.term=c.term AND k.campus=c.campus
                    AND k.subject=c.subject AND k.course_number=c.course_number AND k.pairs_with IS NOT NULL LIMIT 1) AS Need,
                   MIN(CASE WHEN NOT EXISTS (SELECT 1 FROM sections p WHERE p.term=c.term AND p.campus=c.campus
                        AND p.subject=c.subject AND p.course_number=c.course_number AND p.pairs_with=c.section_number)
                        THEN c.section_number END) AS Solo
            FROM sections c
            WHERE c.term='Fall2026' AND c.campus='main' AND c.component='Lecture'
              AND EXISTS (SELECT 1 FROM sections k WHERE k.term=c.term AND k.campus=c.campus
                   AND k.subject=c.subject AND k.course_number=c.course_number AND k.pairs_with IS NOT NULL)
            GROUP BY c.subject, c.course_number
            HAVING Need IS NOT NULL AND Solo IS NOT NULL
            ORDER BY c.subject, c.course_number LIMIT 1");
        if (row.Sub is null) throw new InvalidOperationException("no published course with a standalone lecture in Fall2026");
        return (row.Sub, row.Num, row.Need, row.Solo);
    }

    /// <summary>A lecture that requires a companion, and one companion section stored as paired to it.</summary>
    public (Section Lecture, string Companion) LectureWithCompanion(string term) => LectureWithCompanion(term, spaceSubject: false);

    /// <summary>
    /// As above, optionally restricted to a subject whose code contains a space
    /// ("ME EN"), the case where the lecture-companion link marker once mis-parsed.
    /// </summary>
    public (Section Lecture, string Companion) LectureWithCompanion(string term, bool spaceSubject)
    {
        using var db = Open();
        var like = spaceSubject ? "AND c.subject LIKE '% %'" : "";
        var row = db.QueryFirstOrDefault<(string Sub, string Num, string Lec, string Comp)>($@"
            SELECT c.subject AS Sub, c.course_number AS Num, c.section_number AS Lec, k.section_number AS Comp
            FROM sections c
            JOIN sections k ON k.term = c.term AND k.campus = c.campus AND k.subject = c.subject
                 AND k.course_number = c.course_number AND k.pairs_with = c.section_number
            WHERE c.term = @term AND c.campus = 'main' AND c.component = 'Lecture' {like}
            ORDER BY c.subject, c.course_number, c.section_number LIMIT 1", new { term });
        if (row.Sub is null) throw new InvalidOperationException($"no paired lecture in {term}");
        var lec = One("term=@t AND subject=@s AND course_number=@n AND section_number=@sec",
            new { t = term, s = row.Sub, n = row.Num, sec = row.Lec });
        return (lec, row.Comp);
    }

    /// <summary>The term the builder browses: the newest one with sections.</summary>
    /// <summary>Terms the site keeps off its pickers (Terms:Hidden in the site's appsettings.json) - a schedule crawled ahead of registration.</summary>
    private static readonly HashSet<string> Hidden =
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Site.RepoRoot, "Web", "appsettings.json"))).RootElement
            .TryGetProperty("Terms", out var terms) && terms.TryGetProperty("Hidden", out var hidden)
            ? hidden.EnumerateArray().Select(e => e.GetString()!).ToHashSet()
            : [];

    /// <summary>The newest term the site offers: the newest with sections, hidden ones aside.</summary>
    public string NewestTerm()
    {
        using var db = Open();
        return Pages.Terms.NewestFirst(db.Query<string>("SELECT DISTINCT term FROM sections").Where(t => !Hidden.Contains(t))).First();
    }

    /// <summary>The newest offered term with sections on a campus: the registrar publishes the Asia Campus list for a new term later than the main one.</summary>
    public string NewestTermOn(string campus)
    {
        using var db = Open();
        return Pages.Terms.NewestFirst(db.Query<string>("SELECT DISTINCT term FROM sections WHERE campus = @campus", new { campus }).Where(t => !Hidden.Contains(t))).First();
    }

    public Section Timed(string term) => One("term = @term AND times LIKE '%/%'", new { term });
    public Section OnAsiaCampus(string term) => One("term = @term AND campus = 'uac' AND times LIKE '%/%'", new { term });
    public Section OnUOnline(string term) => One("term = @term AND campus = 'online'", new { term });
    public Section OnSaturday(string term) => One("term = @term AND times LIKE 'Sa/%'", new { term });
    public Section OnWeekdaysOnly(string term) => One("term = @term AND times LIKE 'TuTh/%'", new { term });
    public Section WithDayRange(string term) => One("term = @term AND times LIKE 'Mo-Th/%'", new { term });
    public Section WithTwoRooms(string term) => One("term = @term AND times LIKE '%; %' AND location LIKE '%, %' AND location NOT LIKE 'CANVAS%'", new { term });
    public Section Online(string term) => One("term = @term AND location = 'CANVAS .' AND times LIKE '%/%'", new { term });
    public Section InBuilding(string term, string code) => One("term = @term AND location LIKE @like AND times LIKE '%/%'", new { term, like = code + " %" });

    /// <summary>A full section with people waiting, from the newest term that has one.</summary>
    public (Section Section, int Waiting) FullWithWaitlist()
    {
        using var db = Open();
        var rows = db.Query($"SELECT {Columns}, waitlist AS Waiting FROM sections WHERE campus = 'main' AND seats_available <= 0 AND has_waitlist = 1 AND waitlist > 0 ORDER BY subject, course_number, section_number").ToList();
        var term = Pages.Terms.NewestFirst(rows.Select(r => (string)r.Term).Distinct()).FirstOrDefault()
            ?? throw new InvalidOperationException("no full section has anyone waiting");
        var row = rows.First(r => (string)r.Term == term);
        return (new Section((string)row.Term, (string)row.Subject, (string)row.Number, (string)row.Number2, (string?)row.Times, (string?)row.Location), (int)(long)row.Waiting);
    }

    /// <summary>A full section the class list says cannot be waited on.</summary>
    public Section FullWithoutWaitlist(string term) => One("term = @term AND seats_available <= 0 AND has_waitlist = 0", new { term });

    /// <summary>A lab whose course has no lecture at all: the lab is the course, so it is not hidden from search.</summary>
    public Section StandaloneLab(string term)
    {
        using var db = Open();
        var row = db.QueryFirstOrDefault<Section>($"""
            SELECT {Columns} FROM sections lab
            WHERE lab.term = @term AND lab.campus = 'main' AND lab.component = 'Laboratory'
              AND NOT EXISTS (SELECT 1 FROM sections x
                   WHERE x.term = lab.term AND x.campus = lab.campus AND x.subject = lab.subject
                     AND x.course_number = lab.course_number AND x.component = 'Lecture')
            ORDER BY lab.subject, lab.course_number, lab.section_number LIMIT 1
            """, new { term });
        return row ?? throw new InvalidOperationException($"no standalone lab in {term}");
    }

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

    /// <summary>A professor whose latest graded term is a summer but who mostly teaches fall and spring.</summary>
    public (string Unid, string SummerTerm, string LatestRegularTerm) ProfessorWhoseLatestTermIsSummer()
    {
        using var db = Open();
        var rows = db.Query(@"
            SELECT si.instructor_unid AS Unid, sg.term AS Term, COUNT(*) AS N
            FROM section_grades sg JOIN section_instructors si
              ON si.term = sg.term AND si.subject = sg.subject AND si.course_number = sg.course_number AND si.section_number = sg.section_number
            WHERE sg.gpa_avg IS NOT NULL GROUP BY 1, 2").ToList();
        foreach (var person in rows.GroupBy(r => (string)r.Unid))
        {
            var terms = Pages.Terms.NewestFirst(person.Select(r => (string)r.Term));
            var summer = person.Where(r => Pages.Terms.IsSummer((string)r.Term)).Sum(r => (int)(long)r.N);
            var regular = person.Where(r => !Pages.Terms.IsSummer((string)r.Term)).Sum(r => (int)(long)r.N);
            if (Pages.Terms.IsSummer(terms[0]) && regular > summer)
                return (person.Key, terms[0], terms.First(t => !Pages.Terms.IsSummer(t)));
        }
        throw new InvalidOperationException("no professor whose latest graded term is a summer");
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

    /// <summary>Someone who teaches but has no published grades on any section.</summary>
    public string ProfessorWithoutGrades()
    {
        using var db = Open();
        return db.QueryFirst<string>(@"SELECT i.unid FROM instructors i
            WHERE NOT EXISTS (SELECT 1 FROM section_instructors si JOIN section_grades g
                              ON g.term = si.term AND g.subject = si.subject AND g.course_number = si.course_number AND g.section_number = si.section_number
                              WHERE si.instructor_unid = i.unid AND g.gpa_avg IS NOT NULL) LIMIT 1");
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
