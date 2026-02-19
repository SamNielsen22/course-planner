using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

static class DbStore
{
    public static void StoreSection(SectionRecord section, DetailsRecord details)
    {
        using IDbConnection database = new SqliteConnection("Data Source=data/courseplanner.db");
        database.Open();
        database.Execute("PRAGMA foreign_keys = ON;");

        const string upsertCourseSql = """
            INSERT INTO courses (subject, course_number, title, description, prerequisites)
            VALUES (@Subject, @CourseNumber, @Title, @Description, @Prerequisites)
            ON CONFLICT(subject, course_number) DO UPDATE SET
              title = excluded.title,
              description = excluded.description,
              prerequisites = excluded.prerequisites;
        """;

        const string upsertSectionSql = """
            INSERT INTO sections (term, subject, course_number, section_number, component, type, units, location, times)
            VALUES (@Term, @Subject, @CourseNumber, @SectionNumber, @Component, @Type, @Units, @Location, @Times)
            ON CONFLICT(term, subject, course_number, section_number) DO UPDATE SET
              component = excluded.component,
              type = excluded.type,
              units = excluded.units,
              location = excluded.location,
              times = excluded.times;
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
                details.Prerequisites
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
            section.Times
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
}
