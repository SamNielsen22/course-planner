using Ingest.Schedule.Database;
using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

static class DbStore
{
    const string ConnectionString = "Data Source=data/courseplanner.db";

    // Instructor anchors with no uNID, which cannot be stored. Expected to stay
    // at zero; above zero means the schedule site changed.
    static int skippedWithoutUnid;
    public static int SkippedWithoutUnid => skippedWithoutUnid;

    public static void StoreSection(SectionRecord section, DetailsRecord details, string campus)
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
            INSERT INTO sections (term, subject, course_number, section_number, campus, component, type, units, location, times, seats_available, seats_updated, has_waitlist, pairs_with)
            VALUES (@Term, @Subject, @CourseNumber, @SectionNumber, @Campus, @Component, @Type, @Units, @Location, @Times, @SeatsAvailable, @SeatsUpdated, @HasWaitlist, @PairsWith)
            ON CONFLICT(term, subject, course_number, section_number) DO UPDATE SET
              campus = excluded.campus,
              component = excluded.component,
              type = excluded.type,
              units = excluded.units,
              location = excluded.location,
              times = excluded.times,
              seats_available = excluded.seats_available,
              seats_updated = excluded.seats_updated,
              -- A list that lost its wait-list line keeps what was known.
              has_waitlist = COALESCE(excluded.has_waitlist, sections.has_waitlist),
              -- A published note wins; without one, a hand-entered pairing survives the crawl.
              pairs_with = COALESCE(excluded.pairs_with, sections.pairs_with);
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
            // The full name from the description page when it was read; the
            // listing's 30-character short title only until then.
            database.Execute(upsertCourseSql, new
            {
                section.Subject,
                section.CourseNumber,
                Title = details.Title.Length > 0 ? details.Title : section.Title,
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
            Campus = campus,
            section.Component,
            section.Type,
            section.Units,
            section.Location,
            section.Times,
            section.SeatsAvailable,
            SeatsUpdated = section.SeatsAvailable is null ? null : DateTime.UtcNow.ToString("o"),
            HasWaitlist = section.HasWaitlist is { } waitable ? (waitable ? 1 : 0) : (int?)null,
            section.PairsWith
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
            // No uNID, no row: a name is not a person, and two "Nguyen, Khoi" teach MATH.
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
          campus    TEXT NOT NULL DEFAULT 'main',
          subject   TEXT NOT NULL,

          PRIMARY KEY (term_code, campus, subject)
        );
    """;

    public static HashSet<string> CompletedSubjects(string termCode, string campus)
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        database.Execute(createProgressSql);

        return database.Query<string>(
            "SELECT subject FROM crawl_progress WHERE term_code = @TermCode AND campus = @Campus",
            new { TermCode = termCode, Campus = campus }).ToHashSet();
    }

    public static void MarkSubjectDone(string termCode, string campus, string subject)
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        database.Execute(createProgressSql);

        database.Execute(
            "INSERT OR IGNORE INTO crawl_progress (term_code, campus, subject) VALUES (@TermCode, @Campus, @Subject)",
            new { TermCode = termCode, Campus = campus, Subject = subject });
    }

    /// <summary>
    /// Forget that a term's subjects were crawled, so the next crawl walks them
    /// again. Scoped to one term: the marks for finished terms stay, because a
    /// term that has ended cannot change and re-reading it is wasted traffic.
    /// </summary>
    public static int ClearProgress(string termCode)
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        database.Execute(createProgressSql);

        return database.Execute(
            "DELETE FROM crawl_progress WHERE term_code = @TermCode", new { TermCode = termCode });
    }

    // A courses row only exists once its description page has been fetched, so its
    // presence means the fetch can be skipped - even across restarts.
    public static DetailsRecord? StoredDetails(string subject, string courseNumber)
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();

        var row = database.QueryFirstOrDefault(
            "SELECT title, description, prerequisites, requirement_designation FROM courses WHERE subject = @Subject AND course_number = @CourseNumber",
            new { Subject = subject, CourseNumber = courseNumber });

        if (row is null) return null;
        // The stored title stands whatever it is: the full name once the page
        // has been read (or backfilled), the short one until then.
        return new DetailsRecord((string?)row.title ?? "", (string?)row.description ?? "", (string?)row.prerequisites ?? "",
                                 (string?)row.requirement_designation ?? "");
    }

    /// <summary>Columns added after the first crawl, so an existing database keeps working.</summary>
    /// <summary>
    /// The subjects worth re-reading for a pairing: those with a course that has
    /// both a lecture and a companion section. Roughly a sixth of the schedule,
    /// so asking the database first saves fetching the rest.
    /// </summary>
    public static HashSet<string> SubjectsWithCompanions(string termCode, string campus)
    {
        var term = DisplayTerm(termCode);
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        return database.Query<string>("""
            SELECT DISTINCT s.subject
            FROM sections s
            WHERE s.term = @term AND s.campus = @campus
              AND s.component IN ('Laboratory', 'Lab/ Discussion', 'Discussion', 'Field Work')
              AND EXISTS (SELECT 1 FROM sections l
                          WHERE l.term = s.term AND l.campus = s.campus
                            AND l.subject = s.subject AND l.course_number = s.course_number
                            AND l.component = 'Lecture')
            """, new { term, campus }).ToHashSet();
    }

    /// <summary>The registrar's code ("1268") as the term the crawl stores ("Fall2026").</summary>
    public static string DisplayTerm(string termCode) => TermCodes.Display(termCode);

    /// <summary>How many sections of a term are stored, to tell "not crawled" from "nothing to pair".</summary>
    public static int SectionCount(string termCode, string campus)
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        return database.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM sections WHERE term = @term AND campus = @campus",
            new { term = DisplayTerm(termCode), campus });
    }

    /// <summary>
    /// Write only the companion pairings from a freshly read class list, leaving every
    /// other column alone. Labs the page names are stamped; labs it does not
    /// name keep whatever they had, so a hand-entered pairing survives.
    ///
    /// Known limit: a pairing is never cleared, only overwritten. If a note
    /// stops naming a lab and no other note picks it up, the old pairing stays.
    /// Clearing would also erase hand-entered rows, and nothing stored says
    /// which is which. The enrollment checksum is the way to catch it.
    /// </summary>
    public static int UpdatePairings(string campus, IEnumerable<SectionRecord> sections)
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        using var tx = database.BeginTransaction();

        var updated = 0;
        foreach (var s in sections)
        {
            if (s.PairsWith is null) continue;
            updated += database.Execute(
                "UPDATE sections SET pairs_with = @PairsWith WHERE term = @Term AND campus = @Campus AND subject = @Subject AND course_number = @CourseNumber AND section_number = @SectionNumber AND pairs_with IS NOT @PairsWith",
                new { s.PairsWith, s.Term, Campus = campus, s.Subject, s.CourseNumber, s.SectionNumber }, tx);
        }
        tx.Commit();
        return updated;
    }

    public static void EnsureColumns()
    {
        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();

        var sectionColumns = database.Query<string>("SELECT name FROM pragma_table_info('sections')").ToHashSet();
        if (!sectionColumns.Contains("seats_available"))
            database.Execute("ALTER TABLE sections ADD COLUMN seats_available INTEGER");
        if (!sectionColumns.Contains("seats_updated"))
            database.Execute("ALTER TABLE sections ADD COLUMN seats_updated TEXT");

        // The enrollment side, from the registrar's sections table (refreshed
        // with the seats) and the class list's yes/no on the wait list.
        foreach (var (column, type) in new[] { ("class_number", "TEXT"), ("enrollment_cap", "INTEGER"), ("enrolled", "INTEGER"), ("waitlist", "INTEGER"), ("has_waitlist", "INTEGER") })
            if (!sectionColumns.Contains(column))
                database.Execute($"ALTER TABLE sections ADD COLUMN {column} {type}");

        // On a lab, discussion or field-work section, the lecture it registers you
        // into. From the registrar's note, or data/companion-pairs.tsv where none was published.
        if (!sectionColumns.Contains("pairs_with"))
            database.Execute("ALTER TABLE sections ADD COLUMN pairs_with TEXT");

        // Which schedule listed the section. Rows from before the column are main's.
        if (!sectionColumns.Contains("campus"))
            database.Execute("ALTER TABLE sections ADD COLUMN campus TEXT NOT NULL DEFAULT 'main'");

        // Progress is per schedule too, so the table is rebuilt around the wider key.
        var progressColumns = database.Query<string>("SELECT name FROM pragma_table_info('crawl_progress')").ToHashSet();
        if (progressColumns.Count > 0 && !progressColumns.Contains("campus"))
        {
            database.Execute("ALTER TABLE crawl_progress RENAME TO crawl_progress_old");
            database.Execute(createProgressSql);
            database.Execute("INSERT INTO crawl_progress (term_code, campus, subject) SELECT term_code, 'main', subject FROM crawl_progress_old");
            database.Execute("DROP TABLE crawl_progress_old");
        }

        var courseColumns = database.Query<string>("SELECT name FROM pragma_table_info('courses')").ToHashSet();
        if (!courseColumns.Contains("requirement_designation"))
            database.Execute("ALTER TABLE courses ADD COLUMN requirement_designation TEXT");
        // When this course's description page was last read. A course carries one
        // description, prerequisite list and designation for every term, and the
        // registrar edits them between terms, so they have to be re-read rather
        // than fetched once and trusted forever. Null means never re-read since
        // the column arrived, which sorts oldest-first and so gets picked up.
        if (!courseColumns.Contains("details_updated"))
            database.Execute("ALTER TABLE courses ADD COLUMN details_updated TEXT");

        // Instructor identity, keyed on the registrar's uNID rather than the name.
        database.Execute("""
            CREATE TABLE IF NOT EXISTS instructors (
              unid          TEXT PRIMARY KEY,
              display_name  TEXT NOT NULL
            );
        """);

        // Rebuild section_instructors around the uNID, discarding rows keyed by
        // name - they cannot be migrated, only re-crawled, so progress is cleared too.
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

        // Grades now live in their own tables, one per grain. Older shapes are
        // dropped rather than migrated: the csv is the source of truth and the
        // `grades` command reloads every row.
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

    /// <summary>What a refresh did: rows updated, rows only the registrar has, rows removed.</summary>
    public sealed record CountsResult(int Updated, int Unknown, int Removed);

    /// <summary>
    /// Refresh one subject's enrollment figures from the registrar's sections
    /// table. Sections the table no longer lists have been cancelled and are
    /// removed - but only from a whole, non-empty page.
    /// </summary>
    public static CountsResult UpdateCounts(string term, string campus, string subject, IReadOnlyList<SectionCounts> counts, bool complete)
    {
        const string updateSql = """
            UPDATE sections
               SET class_number    = @ClassNumber,
                   enrollment_cap  = @EnrollmentCap,
                   enrolled        = @Enrolled,
                   waitlist        = @Waitlist,
                   seats_available = @SeatsAvailable,
                   seats_updated   = @Now
             WHERE term = @Term AND subject = @Subject
               AND course_number = @CourseNumber AND section_number = @SectionNumber;
        """;
        const string listedSql = """
            SELECT course_number || '|' || section_number
              FROM sections
             WHERE term = @Term AND campus = @Campus AND subject = @Subject;
        """;
        const string deleteInstructorsSql = """
            DELETE FROM section_instructors
             WHERE term = @Term AND subject = @Subject AND course_number = @CourseNumber AND section_number = @SectionNumber;
        """;
        const string deleteSectionSql = """
            DELETE FROM sections
             WHERE term = @Term AND subject = @Subject AND course_number = @CourseNumber AND section_number = @SectionNumber;
        """;

        using IDbConnection database = new SqliteConnection(ConnectionString);
        database.Open();
        database.Execute("PRAGMA foreign_keys = ON;");
        using var tx = database.BeginTransaction();

        var now = DateTime.UtcNow.ToString("o");
        var updated = 0;
        var unknown = 0;
        foreach (var row in counts)
        {
            var changed = database.Execute(updateSql, new
            {
                row.ClassNumber, row.EnrollmentCap, row.Enrolled, row.Waitlist, row.SeatsAvailable, Now = now,
                Term = term, row.Subject, row.CourseNumber, row.SectionNumber
            }, tx);
            if (changed > 0) updated += changed; else unknown++;
        }

        var removed = 0;
        if (complete && counts.Count > 0)
        {
            var onTable = counts.Select(c => c.CourseNumber + "|" + c.SectionNumber).ToHashSet();
            var gone = database.Query<string>(listedSql, new { Term = term, Campus = campus, Subject = subject }, tx)
                               .Where(key => !onTable.Contains(key)).ToList();
            foreach (var key in gone)
            {
                var parts = key.Split('|');
                var args = new { Term = term, Subject = subject, CourseNumber = parts[0], SectionNumber = parts[1] };
                database.Execute(deleteInstructorsSql, args, tx);
                removed += database.Execute(deleteSectionSql, args, tx);
            }
        }

        tx.Commit();
        return new CountsResult(updated, unknown, removed);
    }
}
