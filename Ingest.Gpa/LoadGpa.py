"""Load the GPA csv into the grade tables, routed by grain.

The csv carries two of the three grains, told apart by the section column:

    section != '(all)'  ->  section_grades      one section, one term
    section == '(all)'  ->  course_term_grades  one course, one term

Separate tables because they have different keys and neither can be derived
from the other: any grade group under five students is suppressed, so summing
the sections undercounts the course. The third grain - course_grades, one
course across all terms - comes from its own all-terms pass and is not in
this csv.

Nothing is matched against the schedule. Grades come from the Tableau dashboard
and sections from the class schedule; the dashboard covers terms the crawler
does not, and the previous version could only UPDATE an existing section row,
so those rows were counted as "unmatched" and thrown away every run.

Safe to rerun: every write is an idempotent overwrite of the same primary key.
"""

import csv
import sqlite3
import sys
from collections import Counter
from pathlib import Path

# Paths are relative to the repo root - run everything from there.
CSV_PATH = Path("data/gpa2.csv")
DB_PATH = Path("data/courseplanner.db")

# The scraper writes the whole-course row under this section name, and the
# all-terms pass writes its rows under this term. A row carrying both is the
# third grain: one course, every term pooled.
COURSE_SECTION = "(all)"
ALL_TERMS = "(all)"

# csv column -> grades column. Stats are REAL, headcounts are INTEGER.
STAT_COLUMNS = {
    "avg_gpa": "gpa_avg",
    "p25": "gpa_p25",
    "p50": "gpa_p50",
    "p75": "gpa_p75",
    "std_dev": "gpa_std_dev",
}
COUNT_COLUMNS = {name: name for name in (
    "grade_a", "grade_b", "grade_c", "grade_d", "grade_e",
    "grade_cr", "grade_nc", "grade_w", "grade_other")}
VALUE_COLUMNS = list(STAT_COLUMNS.values()) + list(COUNT_COLUMNS.values())
KEY_COLUMNS = ["term", "subject", "course_number", "section_number"]


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
    """Rows keyed by (term, subject, course, section), last row winning."""
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
    """Route each row to the table for its grain. Returns (sections, courses).

    The section column is what tells them apart: a real section number is a
    section-grain row, '(all)' is the whole-course row for that term."""
    sections, courses, totals = {}, {}, {}
    for key, stats in graded.items():
        term, subject, catnbr, section = key
        if term == ALL_TERMS and section == COURSE_SECTION:
            totals[(subject, catnbr)] = stats       # all terms, whole course
        elif section == COURSE_SECTION:
            courses[key[:3]] = stats                # one term, whole course
        else:
            sections[key] = stats                   # one term, one section

    write(database, "section_grades",
          ["term", "subject", "course_number", "section_number"], sections)
    write(database, "course_term_grades",
          ["term", "subject", "course_number"], courses)
    write(database, "course_grades", ["subject", "course_number"], totals)
    return len(sections), len(courses), len(totals)


def write(database, table, key_columns, rows):
    """One idempotent overwrite per primary key."""
    columns = ", ".join(key_columns + VALUE_COLUMNS)
    holes = ", ".join("?" * (len(key_columns) + len(VALUE_COLUMNS)))
    database.executemany(
        f"INSERT OR REPLACE INTO {table} ({columns}) VALUES ({holes})",
        [key + tuple(stats[column] for column in VALUE_COLUMNS)
         for key, stats in rows.items()])


def report(csv_paths, graded, blank, duplicates, sections, courses, totals):
    names = ", ".join(p.name for p in csv_paths)
    print(f"read {len(graded) + blank} rows from {names}")
    print(f"  {len(graded):>6} with grades or headcounts")
    print(f"  {blank:>6} with none published (labs, discussions) - skipped")
    if duplicates:
        print(f"  {duplicates:>6} repeated keys - last row won")
    print()
    print(f"section_grades     {sections:>7,} rows")
    print(f"course_term_grades {courses:>7,} rows")
    print(f"course_grades      {totals:>7,} rows")
    print("\n  by term:")
    for term, count in sorted(Counter(key[0] for key in graded).items()):
        print(f"    {term:<12} {count}")


def main():
    """LoadGpa.py [database] [csv ...]

    Several csvs can be given, so the per-term sweep and the all-terms pass
    load together. They key differently and never collide."""
    database_path = Path(sys.argv[1]) if len(sys.argv) > 1 else DB_PATH
    csv_paths = [Path(a) for a in sys.argv[2:]] or [CSV_PATH]
    if not database_path.exists():
        sys.exit(f"no database at {database_path}")
    for path in csv_paths:
        if not path.exists():
            sys.exit(f"no csv at {path}")

    graded, blank, duplicates = {}, 0, 0
    for path in csv_paths:
        rows, empty, repeats = read_csv(path)
        overlap = set(rows) & set(graded)
        if overlap:
            print(f"warning: {len(overlap)} keys in {path.name} were already "
                  f"read from an earlier file - the later one wins")
        graded.update(rows)
        blank += empty
        duplicates += repeats

    database = sqlite3.connect(database_path)
    try:
        sections, courses, totals = load(database, graded)
        database.commit()
    finally:
        database.close()

    report(csv_paths, graded, blank, duplicates, sections, courses, totals)


if __name__ == "__main__":
    main()
