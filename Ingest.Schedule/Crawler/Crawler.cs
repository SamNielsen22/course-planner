using System.Data;
using Dapper;
using HtmlAgilityPack;
using Microsoft.Data.Sqlite;

public class Crawler
{
    public void Run()
    {
        var baseUrl = "https://class-schedule.app.utah.edu/main/1264/";
        var indexUrl = baseUrl + "index.html";

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

        Console.WriteLine($"Fetching subjects from {indexUrl}");
        var subjects = SubjectScraper.Scrape(ScraperUtils.LoadFromUrl(indexUrl));
        Console.WriteLine($"Found {subjects.Count} subjects");

        foreach (var subject in subjects)
        {
            var classListUrl = baseUrl + "class_list.html?subject=" + subject;
            Console.WriteLine($"Scraping subject {subject}: {classListUrl}");

            var sectionInfo = MainSearchScraper.Scrape(ScraperUtils.LoadFromUrl(classListUrl));
            Console.WriteLine($" sections: {sectionInfo.Count}");

            var seenCourses = new HashSet<string>();

            using var transaction = database.BeginTransaction();

            foreach (var section in sectionInfo)
            {
                var classKey = $"{section.Subject}:{section.CourseNumber}";
                if (seenCourses.Add(classKey))
                {
                    var detailsUrl =
                        baseUrl + "description.html?subj=" + Uri.EscapeDataString(section.Subject) +
                        "&catno=" + Uri.EscapeDataString(section.CourseNumber) +
                        "&section=" + Uri.EscapeDataString(section.SectionNumber);

                    var details = DescriptionScraper.Scrape(ScraperUtils.LoadFromUrl(detailsUrl));

                    database.Execute(upsertCourseSql, new
                    {
                        section.Subject,
                        section.CourseNumber,
                        section.Title,
                        details.Description,
                        details.Prerequisites
                    }, transaction);
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
                }, transaction);

                database.Execute(deleteSectionInstructorsSql, new
                {
                    section.Term,
                    section.Subject,
                    section.CourseNumber,
                    section.SectionNumber
                }, transaction);

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
                    }, transaction);
                }
            }

            transaction.Commit();
        }
    }
}
