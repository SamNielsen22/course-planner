using Ingest.Schedule.Database;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

static class DbStore
{
    const string ConnectionString = "Data Source=data/courseplanner.db";

    // Instructor anchors that carried no uNID, and so could not be stored.
    // Measured coverage is 100%, so this is expected to stay at zero; a run that
    // ends with it above zero has hit a change on the schedule site, and the
    // count is the only evidence of what was silently dropped.
    static int skippedWithoutUnid;
    public static int SkippedWithoutUnid => skippedWithoutUnid;

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

        // The person, keyed on the registrar's id. The display name is refreshed
        // each crawl so it tracks the most recent spelling.
        const string upsertInstructorSql = """
            INSERT INTO instructors (unid, display_name)
            VALUES (@Unid, @DisplayName)
            ON CONFLICT(unid) DO UPDATE SET display_name = excluded.display_name;
        """;

        const string insertSectionInstructorSql = """
            INSERT OR IGNORE INTO section_instructors
              (term, subject, course_number, section_number, instructor_unid)
            VALUES
              (@Term, @Subject, @CourseNumber, @SectionNumber, @InstructorUnid);
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

        foreach (var instructor in section.Instructors ?? new List<InstructorRef>())
        {
            // No uNID, no row. Identity comes from the registrar's id and nothing
            // else - a name is not a person, and writing one without an id is how
            // two different "Nguyen, Khoi" in MATH become one professor page.
            // Coverage was measured at 100% (1,421 anchors, zero without a link),
            // so this should drop nothing; if it starts dropping, that is a real
            // change on the schedule site and worth knowing about rather than
            // papering over.
            if (instructor.Unid is null || string.IsNullOrWhiteSpace(instructor.Name))
            {
                skippedWithoutUnid++;
                continue;
            }

            // The person row has to exist before the section can point at it.
            database.Execute(upsertInstructorSql,
                new { Unid = instructor.Unid,
                      DisplayName = InstructorNames.Clean(instructor.Name) }, tx);

            database.Execute(insertSectionInstructorSql, new
            {
                section.Term,
                section.Subject,
                section.CourseNumber,
                section.SectionNumber,
                InstructorUnid = instructor.Unid
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

        // Instructor identity, keyed on the registrar's uNID rather than the name.
        database.Execute("""
            CREATE TABLE IF NOT EXISTS instructors (
              unid          TEXT PRIMARY KEY,
              display_name  TEXT NOT NULL
            );
        """);

        // Rebuild section_instructors around the uNID. This DISCARDS the old
        // rows, and it has to: they were crawled before the uNID was captured,
        // the href was never stored, and a name cannot be turned back into a
        // person - two "Nguyen, Khoi" both teach MATH. There is nothing to
        // migrate, only to re-crawl. crawl_progress is cleared with it so the
        // next run actually re-walks the subjects rather than skipping them.
        //
        // Detected by the absence of instructor_unid OR the presence of the old
        // instructor column, so this is idempotent and a no-op once done.
        var instructorColumns = database.Query<string>(
            "SELECT name FROM pragma_table_info('section_instructors')").ToHashSet();
        var needsRebuild = instructorColumns.Count > 0
                           && (instructorColumns.Contains("instructor")
                               || !instructorColumns.Contains("instructor_unid"));
        if (needsRebuild)
        {
            database.Execute("DROP TABLE IF EXISTS section_instructors");
            database.Execute("DELETE FROM crawl_progress");
        }

        database.Execute("""
            CREATE TABLE IF NOT EXISTS section_instructors (
              term            TEXT NOT NULL,
              subject         TEXT NOT NULL,
              course_number   TEXT NOT NULL,
              section_number  TEXT NOT NULL,
              instructor_unid TEXT NOT NULL REFERENCES instructors(unid),

              PRIMARY KEY (term, subject, course_number, section_number, instructor_unid),
              FOREIGN KEY (term, subject, course_number, section_number)
                REFERENCES sections(term, subject, course_number, section_number)
            );
        """);

        database.Execute("""
            CREATE INDEX IF NOT EXISTS idx_section_instructors_unid
              ON section_instructors(instructor_unid);
        """);

        // Grades moved out of sections and split by grain. Three shapes have to
        // be cleared out of older databases, and CREATE TABLE IF NOT EXISTS will
        // not do it - a stale table keeps its old columns and silently answers
        // with the wrong grain.
        //
        //   sections.gpa_*/grade_*  grades used to live on the schedule row,
        //                           which gave that table two writers and meant
        //                           a grade could not be stored unless the
        //                           crawler had already seen the section.
        //   grades                  a single mixed-grain table; SUM over it
        //                           counted every student three times.
        //   course_grades WITH a
        //   term column             the old per-term rollup. The name is reused
        //                           for the all-terms rollup, so an old one left
        //                           in place would answer the wrong question.
        //
        // Nothing is migrated across: every row is reloaded from the csv by
        // Ingest.Gpa/LoadGpa.py, which is the source of truth.
        database.Execute("DROP TABLE IF EXISTS grades");

        var oldCourseGrades = database.Query<string>(
            "SELECT name FROM pragma_table_info('course_grades')").ToHashSet();
        if (oldCourseGrades.Contains("term"))
            database.Execute("DROP TABLE IF EXISTS course_grades");

        var gradeColumns = database.Query<string>(
            "SELECT name FROM pragma_table_info('sections')")
            .Where(c => c.StartsWith("gpa_") || c.StartsWith("grade_"))
            .ToList();
        foreach (var column in gradeColumns)
            database.Execute($"ALTER TABLE sections DROP COLUMN {column}");
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
