"""Replace each course's short title with the full one from its description page.

The class listing carries the registrar's 30-character short title - "Manufact
for Eng Sys" - and that is what the crawler stored for every course. The
description page it fetches for each course says the full name in its heading,
"ME EN 2650 - Manufacturing for Engineering Systems", and the crawler now keeps
that for anything it reads from here on. This is the one-off pass over the
courses already stored, which the crawler never re-reads.

One request per course, two seconds apart, close to the crawler's own pace.
Resumable: courses already visited are listed in data/titles_done.txt and
skipped, so a stopped run picks up where it left off.

    python Ingest.Schedule/Database/FillTitles.py [database] [--limit N]
"""
import html
import os
import re
import sqlite3
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = "https://class-schedule.app.utah.edu/main/"
DEFAULT_DB = "data/courseplanner.db"
DONE = "data/titles_done.txt"
PAUSE = 2.0
SEASON = {"Spring": 4, "Summer": 6, "Fall": 8}
HEADING = re.compile(r"<h1[^>]*>(.*?)</h1>", re.S)
TITLE = re.compile(r"^\s*[A-Z][A-Z ]*\s+\d+[A-Z]?\s*-\s*(?P<title>.+?)\s*$")


def term_code(term):
    """'Fall2026' -> '1268': the registrar's 1 + two-digit year + season digit."""
    season, year = term[:-4], term[-4:]
    return f"1{year[2:]}{SEASON[season]}"


def clean(text):
    return re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", text))).strip()


def full_title(page):
    heading = HEADING.search(page)
    if not heading:
        return ""
    match = TITLE.match(clean(heading.group(1)))
    return match.group("title") if match else ""


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
    for attempt in (1, 2):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                return response.read().decode("utf-8", errors="replace")
        except (urllib.error.URLError, TimeoutError) as error:
            if attempt == 2:
                print(f"  giving up on {url}: {error}")
                return None
            time.sleep(30)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    database = args[0] if args else DEFAULT_DB
    limit = int(sys.argv[sys.argv.index("--limit") + 1]) if "--limit" in sys.argv else None

    done = set()
    if os.path.exists(DONE):
        done = {line.rstrip("\n") for line in open(DONE, encoding="utf-8")}

    connection = sqlite3.connect(database)
    # One section per course to address the page with - the newest term's,
    # since the page needs a term and a section number in its URL.
    courses = connection.execute("""
        SELECT c.subject, c.course_number, c.title, s.term, s.section_number, MAX(s.rowid)
        FROM courses c
        JOIN sections s ON s.subject = c.subject AND s.course_number = c.course_number
        GROUP BY c.subject, c.course_number
        ORDER BY c.subject, c.course_number""").fetchall()
    todo = [row for row in courses if f"{row[0]}|{row[1]}" not in done]
    if limit is not None:
        todo = todo[:limit]
    print(f"{len(courses):,} courses, {len(done):,} already visited, {len(todo):,} to do "
          f"(about {len(todo) * PAUSE / 3600:.1f} hours)")

    changed = kept = missing = 0
    with open(DONE, "a", encoding="utf-8") as log:
        for i, (subject, number, title, term, section, _) in enumerate(todo, 1):
            url = (BASE + term_code(term) + "/description.html?"
                   + urllib.parse.urlencode({"subj": subject, "catno": number, "section": section}))
            page = fetch(url)
            if page is None:
                time.sleep(PAUSE)
                continue          # not marked done: the next run tries again
            found = full_title(page)
            if not found:
                missing += 1
            elif found != title:
                connection.execute("UPDATE courses SET title = ? WHERE subject = ? AND course_number = ?",
                                   (found, subject, number))
                connection.commit()
                changed += 1
            else:
                kept += 1
            log.write(f"{subject}|{number}\n")
            log.flush()
            if i % 100 == 0 or i == len(todo):
                print(f"  {i:,}/{len(todo):,}  changed {changed:,}  same {kept:,}  no heading {missing:,}"
                      f"  latest: {subject} {number}: {title!r} -> {found!r}", flush=True)
            time.sleep(PAUSE)


if __name__ == "__main__":
    main()
