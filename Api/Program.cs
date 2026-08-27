using CoursePlanner.Data;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("CoursePlanner")
                       ?? "Data Source=../data/courseplanner.db";
builder.Services.AddSingleton(new CourseQueries(connectionString));

var app = builder.Build();

// Serves the built frontend from wwwroot alongside the API.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/terms", (CourseQueries db) => db.GetTerms());

// q matches subject, number or title; req matches a gen ed designation.
app.MapGet("/courses", (CourseQueries db, string term, string? q, string? req) =>
    db.SearchCourses(term, q, req));

app.MapGet("/courses/{subject}/{courseNumber}/sections",
    (CourseQueries db, string subject, string courseNumber, string term) =>
        db.GetSections(term, subject, courseNumber));

// The workhorse for the schedule builder: any combination of text, requirement,
// open seats and start time. Times are "HH:MM" or "H:MMPM".
app.MapGet("/sections", (CourseQueries db, string term, string? subject, string? q, string? req,
                         bool? open, string? startAfter, string? startBefore) =>
    db.FindSections(term, subject: subject, query: q, requirement: req, openOnly: open ?? false,
                    startAfter: startAfter, startBefore: startBefore));

app.MapGet("/instructors", (CourseQueries db, string? q, string? term) =>
    db.SearchInstructors(q, term));

app.MapGet("/instructors/{instructor}/sections",
    (CourseQueries db, string instructor, string term) =>
        db.GetInstructorSections(term, instructor));

app.Run();
