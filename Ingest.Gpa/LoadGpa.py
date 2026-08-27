"""Load gpa.csv into the sections table.

Grade rows match sections on (term, subject, course_number, section_number).
This only ever UPDATEs - it cannot create a section row, having no schedule
information - so grade rows with no matching section are reported rather than
silently dropped. gpa.csv stays the source of truth: rerun this once the
schedule crawler covers the missing terms and the rows land.

Safe to rerun; every write is an idempotent overwrite of the same columns.
"""

import csv
import sqlite3
import sys
from collections import Counter
from pathlib import Path

# Paths are relative to the repo root - run everything from there.
CSV_PATH = Path("data/gpa.csv")
DB_PATH = Path("data/courseplanner.db")

KEY_COLUMNS = ["term", "subject", "course_number", "section_number"]

# The scraper writes the whole-course row under this section, from the
# dashboard's own no-section-pinned figures. It goes to course_grades, not
# sections - it is not a section and must not look like one.
COURSE_SECTION = "(all)"

# gpa.csv column -> sections column. Stats are REAL, headcounts are INTEGER.
STAT_COLUMNS = {
    "avg_gpa": "gpa_avg",
    "p25": "gpa_p25",
    "p50": "gpa_p50",
    "p75": "gpa_p75",
    "std_dev": "gpa_std_dev",
}
COUNT_COLUMNS = {
    "grade_a": "grade_a",
    "grade_b": "grade_b",
    "grade_c": "grade_c",
    "grade_d": "grade_d",
    "grade_e": "grade_e",
    "grade_cr": "grade_cr",
    "grade_nc": "grade_nc",
    "grade_w": "grade_w",
    "grade_other": "grade_other",
}
ALL_COLUMNS = list(STAT_COLUMNS.values()) + list(COUNT_COLUMNS.values())


def ensure_columns(database):
    """Add the grade columns to sections if they aren't there yet."""
    existing = {row[1] for row in database.execute("PRAGMA table_info(sections)")}
    added = []
    for column in STAT_COLUMNS.values():
        if column not in existing:
            database.execute(f"ALTER TABLE sections ADD COLUMN {column} REAL")
            added.append(column)
    for column in COUNT_COLUMNS.values():
        if column not in existing:
            database.execute(f"ALTER TABLE sections ADD COLUMN {column} INTEGER")
            added.append(column)
    return added


def ensure_course_grades(database):
    """Create course_grades if this database predates it. Mirrors schema.sql."""
    database.execute("""
        CREATE TABLE IF NOT EXISTS course_grades (
          term           TEXT NOT NULL,
          subject        TEXT NOT NULL,
          course_number  TEXT NOT NULL,
          gpa_avg REAL, gpa_p25 REAL, gpa_p50 REAL, gpa_p75 REAL, gpa_std_dev REAL,
          grade_a INTEGER, grade_b INTEGER, grade_c INTEGER, grade_d INTEGER,
          grade_e INTEGER, grade_cr INTEGER, grade_nc INTEGER, grade_w INTEGER,
          grade_other INTEGER,
          PRIMARY KEY (term, subject, course_number),
          FOREIGN KEY (subject, course_number)
            REFERENCES courses(subject, course_number)
        )""")
    database.execute("CREATE INDEX IF NOT EXISTS idx_course_grades_course "
                     "ON course_grades(subject, course_number)")


def normalize_term(term):
    """The dashboard writes 'Fall 2020'; the schedule crawler writes 'Fall2020'."""
    return "".join(term.split())


def to_gpa(value):
    """A grade point average, or None.

    Blank means the dashboard published nothing. Anything outside 0-4 is a
    stray cell the scraper picked up - a course number or a headcount - and is
    dropped rather than written into the database."""
    value = (value or "").strip()
    if not value:
        return None
    try:
        number = float(value)
    except ValueError:
        return None
    return number if 0.0 <= number <= 4.0 else None


def to_count(value):
    """A headcount, or None. Older rows have no grade columns at all."""
    value = (value or "").strip()
    if not value:
        return None
    try:
        number = int(float(value))
    except ValueError:
        return None
    return number if number >= 0 else None


def read_csv(path):
    """CSV rows keyed by section, newest row winning. Empty rows are dropped."""
    graded, blank, duplicates = {}, 0, 0
    with open(path, newline="") as handle:
        for row in csv.DictReader(handle):
            stats = {column: to_gpa(row.get(source))
                     for source, column in STAT_COLUMNS.items()}
            counts = {column: to_count(row.get(source))
                      for source, column in COUNT_COLUMNS.items()}

            published = [value for value in stats.values() if value is not None]
            # Every statistic zero means no letter grades were awarded - not a
            # section where everybody failed - so the stats are discarded. The
            # headcounts stay: a credit/no credit section still reports them.
            if published and all(value == 0.0 for value in published):
                stats = {column: None for column in stats}
                published = []

            if not published and not any(v is not None for v in counts.values()):
                blank += 1
                continue

            stats.update(counts)
            key = (normalize_term(row["term"]), row["subject"],
                   row["catnbr"], row["section"])
            if key in graded:
                duplicates += 1
            graded[key] = stats
    return graded, blank, duplicates


def load(database, graded):
    """Update matching sections. Returns the keys that had nowhere to go."""
    known = set(database.execute(
        f"SELECT {', '.join(KEY_COLUMNS)} FROM sections"))
    matched = {key: stats for key, stats in graded.items() if key in known}

    assignments = ", ".join(f"{column} = ?" for column in ALL_COLUMNS)
    conditions = " AND ".join(f"{column} = ?" for column in KEY_COLUMNS)
    database.executemany(
        f"UPDATE sections SET {assignments} WHERE {conditions}",
        [tuple(stats[column] for column in ALL_COLUMNS) + key
         for key, stats in matched.items()])

    return len(matched), [key for key in graded if key not in known]


def load_courses(database, graded):
    """Write the whole-course rows. Keyed (term, subject, course_number), so
    unlike the section rows these only need the course to exist - which is why
    they land even for courses whose individual sections the crawler missed.

    Rewritable: rerunning overwrites the same row rather than duplicating it."""
    known = set(database.execute("SELECT subject, course_number FROM courses"))
    matched = {key: stats for key, stats in graded.items() if key[1:] in known}

    columns = ", ".join(ALL_COLUMNS)
    holes = ", ".join("?" * (3 + len(ALL_COLUMNS)))
    database.executemany(
        f"INSERT OR REPLACE INTO course_grades "
        f"(term, subject, course_number, {columns}) VALUES ({holes})",
        [key + tuple(stats[column] for column in ALL_COLUMNS)
         for key, stats in matched.items()])

    return len(matched), [key for key in graded if key[1:] not in known]


def report(csv_path, graded, blank, duplicates, updated, unmatched):
    print(f"read {len(graded) + blank} rows from {csv_path.name}")
    print(f"  {len(graded):>6} with grades or headcounts")
    print(f"  {blank:>6} with none published (labs, discussions) - skipped")
    if duplicates:
        print(f"  {duplicates:>6} repeated section keys - last row won")
    print()
    print(f"updated   {updated} sections")
    print(f"unmatched {len(unmatched)} rows had no section row to attach to")

    if not unmatched:
        return
    print("\n  unmatched by term:")
    for term, count in sorted(Counter(key[0] for key in unmatched).items()):
        print(f"    {term:<12} {count}")
    print("\n  first few:")
    for term, subject, course, section in unmatched[:5]:
        print(f"    {term} {subject} {course}-{section}")
    print("\n  These stay in gpa.csv - rerun once the crawler covers them.")


def main():
    database_path = Path(sys.argv[1]) if len(sys.argv) > 1 else DB_PATH
    csv_path = Path(sys.argv[2]) if len(sys.argv) > 2 else CSV_PATH
    if not database_path.exists():
        sys.exit(f"no database at {database_path}")
    if not csv_path.exists():
        sys.exit(f"no csv at {csv_path}")

    graded, blank, duplicates = read_csv(csv_path)
    courses = {key[:3]: stats for key, stats in graded.items()
               if key[3] == COURSE_SECTION}
    sections = {key: stats for key, stats in graded.items()
                if key[3] != COURSE_SECTION}

    database = sqlite3.connect(database_path)
    try:
        added = ensure_columns(database)
        ensure_course_grades(database)
        if added:
            print(f"added columns to sections: {', '.join(added)}\n")
        updated, unmatched = load(database, sections)
        written, no_course = load_courses(database, courses)
        database.commit()
    finally:
        database.close()

    report(csv_path, sections, blank, duplicates, updated, unmatched)
    print(f"\ncourse_grades {written} whole-course rows written")
    if no_course:
        print(f"              {len(no_course)} had no course row to attach to")


if __name__ == "__main__":
    main()
