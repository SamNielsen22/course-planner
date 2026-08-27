using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

static class DbStore
{
    const string ConnectionString = "Data Source=data/courseplanner.db";

    public static void StoreSection(SectionRecord section, DetailsRecord details)
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        database.Execute("PRAGMA foreign_keys = ON;");

        const string upsertCourseSql = """
            INSERT INTO courses (subject, course_number, title, description, prerequisites, requirement_designation)
            VALUES (@Subject, @CourseNumber, @Title, @Description, @Prerequisites, @RequirementDesignation)
            ON CONFLICT(subject, course_number) DO UPDATE SET
              title = excluded.title,
              description = excluded.description,
              prerequisites = excluded.prerequisites,
              requirement_designation = excluded.requirement_designation;
        """;

        const string upsertSectionSql = """
            INSERT INTO sections (term, subject, course_number, section_number, component, type, units, location, times, seats_available, seats_updated)
            VALUES (@Term, @Subject, @CourseNumber, @SectionNumber, @Component, @Type, @Units, @Location, @Times, @SeatsAvailable, @SeatsUpdated)
            ON CONFLICT(term, subject, course_number, section_number) DO UPDATE SET
              component = excluded.component,
              type = excluded.type,
              units = excluded.units,
              location = excluded.location,
              times = excluded.times,
              seats_available = excluded.seats_available,
              seats_updated = excluded.seats_updated;
        """;

        const string deleteSectionInstructorsSql = """
            DELETE FROM section_instructors
            WHERE term = @Term AND subject = @Subject AND course_number = @CourseNumber AND section_number = @SectionNumber;
        """;

        const string insertSectionInstructorSql = """
            INSERT OR IGNORE INTO section_instructors
              (term, subject, course_number, section_number, instructor)
            VALUES
              (@Term, @Subject, @CourseNumber, @SectionNumber, @Instructor);
        """;

        var seenCourses = new HashSet<string>();

        using var tx = database.BeginTransaction();
        var classKey = $"{section.Subject}:{section.CourseNumber}";
        if (seenCourses.Add(classKey))
        {
            database.Execute(upsertCourseSql, new
            {
                section.Subject,
                section.CourseNumber,
                section.Title,
                details.Description,
                details.Prerequisites,
                details.RequirementDesignation
            }, tx);
        }

        database.Execute(upsertSectionSql, new
        {
            section.Term,
            section.Subject,
            section.CourseNumber,
            section.SectionNumber,
            section.Component,
            section.Type,
            section.Units,
            section.Location,
            section.Times,
            section.SeatsAvailable,
            SeatsUpdated = section.SeatsAvailable is null ? null : DateTime.UtcNow.ToString("o")
        }, tx);

        database.Execute(deleteSectionInstructorsSql, new
        {
            section.Term,
            section.Subject,
            section.CourseNumber,
            section.SectionNumber
        }, tx);

        foreach (var instructor in section.Instructors ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(instructor)) continue;

            database.Execute(insertSectionInstructorSql, new
            {
                section.Term,
                section.Subject,
                section.CourseNumber,
                section.SectionNumber,
                Instructor = instructor
            }, tx);
        }
        

        tx.Commit();
    }

    // Resume support. A subject is recorded only after its sections are stored, so
    // a run that dies mid-subject redoes that subject rather than skipping it.

    const string createProgressSql = """
        CREATE TABLE IF NOT EXISTS crawl_progress (
          term_code TEXT NOT NULL,
          subject   TEXT NOT NULL,

          PRIMARY KEY (term_code, subject)
        );
    """;

    public static HashSet<string> CompletedSubjects(string termCode)
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        database.Execute(createProgressSql);

        return database.Query<string>(
            "SELECT subject FROM crawl_progress WHERE term_code = @TermCode",
            new { TermCode = termCode }).ToHashSet();
    }

    public static void MarkSubjectDone(string termCode, string subject)
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        database.Execute(createProgressSql);

        database.Execute(
            "INSERT OR IGNORE INTO crawl_progress (term_code, subject) VALUES (@TermCode, @Subject)",
            new { TermCode = termCode, Subject = subject });
    }

    // A courses row only exists once its description page has been fetched, so its
    // presence means the fetch can be skipped - even across restarts.
    public static DetailsRecord? StoredDetails(string subject, string courseNumber)
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();

        var row = database.QueryFirstOrDefault(
            "SELECT description, prerequisites, requirement_designation FROM courses WHERE subject = @Subject AND course_number = @CourseNumber",
            new { Subject = subject, CourseNumber = courseNumber });

        if (row is null) return null;
        return new DetailsRecord((string?)row.description ?? "", (string?)row.prerequisites ?? "",
                                 (string?)row.requirement_designation ?? "");
    }

    /// <summary>Columns added after the first crawl, so an existing database keeps working.</summary>
    public static void EnsureColumns()
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();

        var sectionColumns = database.Query<string>("SELECT name FROM pragma_table_info('sections')").ToHashSet();
        if (!sectionColumns.Contains("seats_available"))
            database.Execute("ALTER TABLE sections ADD COLUMN seats_available INTEGER");
        if (!sectionColumns.Contains("seats_updated"))
            database.Execute("ALTER TABLE sections ADD COLUMN seats_updated TEXT");

        var courseColumns = database.Query<string>("SELECT name FROM pragma_table_info('courses')").ToHashSet();
        if (!courseColumns.Contains("requirement_designation"))
            database.Execute("ALTER TABLE courses ADD COLUMN requirement_designation TEXT");
    }

    /// <summary>
    /// Refresh seat counts only. Everything else about a section is left alone, so
    /// this can run often without re-crawling description pages.
    /// </summary>
    public static int UpdateSeats(IEnumerable<SectionRecord> sections)
    {
        const string sql = """
            UPDATE sections
               SET seats_available = @SeatsAvailable,
                   seats_updated   = @SeatsUpdated
             WHERE term = @Term AND subject = @Subject
               AND course_number = @CourseNumber AND section_number = @SectionNumber;
        """;

        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        using var tx = database.BeginTransaction();

        var now = DateTime.UtcNow.ToString("o");
        var updated = 0;
        foreach (var section in sections)
        {
            if (section.SeatsAvailable is null) continue;
            updated += database.Execute(sql, new
            {
                section.SeatsAvailable,
                SeatsUpdated = now,
                section.Term,
                section.Subject,
                section.CourseNumber,
                section.SectionNumber
            }, tx);
        }

        tx.Commit();
        return updated;
    }
}
