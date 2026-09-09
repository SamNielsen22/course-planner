"""Give every instructor a department, inferred from what they teach.

The registrar publishes no department for a person - only the subject code on
each section. So the department is worked out from teaching load: map each
subject to a department, then give the instructor whichever department they
teach the most sections in.

The result is written to instructors.department rather than computed on read.
Deriving it means grouping every section a person has ever taught, and the
professor search shows twenty-four people at a time; that is twenty-four such
groupings per keystroke. It changes only when the schedule is re-crawled.

Ties are broken toward the department with more sections overall, which keeps a
person who taught one class in each of two units with the one they belong to
rather than with whichever sorted first.

    python Ingest.Schedule/Database/AssignDepartments.py [database]
"""
import collections
import csv
import os
import sqlite3
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
MAPPING = os.path.join(HERE, "departments.csv")
DEFAULT_DB = "data/courseplanner.db"


def load_mapping():
    with open(MAPPING, newline="", encoding="utf-8") as handle:
        rows = list(csv.DictReader(handle))
    return {row["subject"]: row["department"] for row in rows}


def main():
    database_path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DB
    if not os.path.exists(database_path):
        sys.exit(f"no database at {database_path}")

    subject_to_department = load_mapping()
    database = sqlite3.connect(database_path)

    # Every subject a person has taught, and how many sections in each.
    taught = collections.defaultdict(collections.Counter)
    for unid, subject in database.execute(
            "SELECT instructor_unid, subject FROM section_instructors"):
        taught[unid][subject] += 1

    # How big each department is overall, for tie-breaking.
    size = collections.Counter()
    for unid, subjects in taught.items():
        for subject, count in subjects.items():
            department = subject_to_department.get(subject)
            if department:
                size[department] += count

    assignments = []
    unmapped = collections.Counter()
    for unid, subjects in taught.items():
        totals = collections.Counter()
        for subject, count in subjects.items():
            department = subject_to_department.get(subject)
            if department:
                totals[department] += count
            else:
                unmapped[subject] += count
        if not totals:
            continue
        best = max(totals.items(), key=lambda pair: (pair[1], size[pair[0]]))
        assignments.append((best[0], unid))

    database.executemany(
        "UPDATE instructors SET department = ? WHERE unid = ?", assignments)
    database.commit()

    total = database.execute("SELECT COUNT(*) FROM instructors").fetchone()[0]
    named = database.execute(
        "SELECT COUNT(*) FROM instructors WHERE department IS NOT NULL").fetchone()[0]
    print(f"instructors            : {total:,}")
    print(f"  given a department   : {named:,}  ({named / total * 100:.1f}%)")
    print(f"  left without one     : {total - named:,}  (no sections, or only unmapped subjects)")
    if unmapped:
        print(f"  subjects with no mapping: {dict(unmapped.most_common(8))}")

    print("\nlargest departments by instructor count:")
    for department, count in database.execute("""
            SELECT department, COUNT(*) FROM instructors
            WHERE department IS NOT NULL
            GROUP BY department ORDER BY COUNT(*) DESC LIMIT 10"""):
        print(f"   {department:<44} {count:>5,}")
    database.close()


if __name__ == "__main__":
    main()
