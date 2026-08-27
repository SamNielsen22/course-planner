
import csv
import json
import logging
import os
import queue
import re
import threading
import time

import requests
from tableauscraper import TableauScraper
from tableauscraper import dashboard as tableau_dashboard
from tableauscraper import utils as tableau_utils


SECONDS_BETWEEN_LOADS = 1.5

# Terms are swept in parallel, one vizql session per worker. Sessions are
# independent server-side, so a worker's filters never disturb another's.
# Each worker holds a session almost continuously, so keep this modest.
WORKERS = 6

TERMS = None
SUBJECTS = None

HOST = "https://tableau.dashboard.utah.edu"
DASHBOARD_URL = (f"{HOST}/t/UAIR/views/"
                 "OfficialUUGradeSummary_17192658137620/GradeSummary")

USER_AGENT = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
              "(KHTML, like Gecko) Chrome/126.0 Safari/537.36")

# Paths are relative to the repo root - run everything from there.
OUTPUT_FILE = "data/gpa.csv"
PROGRESS_FILE = "data/done.txt"

LOAD_ATTEMPTS = 3
SECONDS_BETWEEN_RETRIES = 5

# Timeouts and dropped connections leave the vizql session alive on the
# server, so they are retried on it rather than costing a new one.
TRANSPORT_ERRORS = (requests.exceptions.Timeout,
                    requests.exceptions.ConnectionError,
                    requests.exceptions.ChunkedEncodingError)
# Consecutive failed selects before giving up on the session itself.
SESSION_REFRESH_AFTER = 3
MAX_BACKOFF = 300


DASHBOARD_NAME = "Grade Summary"
HEADCOUNT_SHEET = "Grade Tabs"
GPA_SHEET = "Avg GPA"
FILTER_LABELS = {
    "term": "Term/Snap",
    "subject": "Subject",
    "course": "Catnbr",
    "section": "Section",
}
# Widest to narrowest. Selecting one clears everything to its right.
FILTER_ORDER = ["term", "subject", "course", "section"]
STAT_COLUMNS = {
    "Average GPA": "avg_gpa",
    "25th Percentile": "p25",
    "50th Percentile": "p50",
    "75th Percentile": "p75",
    "Standard Deviation": "std_dev",
}
# Grade Tabs reports one row per grade group, headcount in CNT(Emplid Proxy).
GRADE_COLUMNS = {
    "A": "grade_a",
    "B": "grade_b",
    "C": "grade_c",
    "D": "grade_d",
    "E": "grade_e",
    "CR": "grade_cr",
    "NC": "grade_nc",
    "W": "grade_w",
    "OTHER": "grade_other",
}

# Section value for the whole-course row, which the dashboard reports when no
# section is pinned. Parenthesised so it cannot collide with a real section.
COURSE_SECTION = "(all)"

# The grade columns come last so rows written before they existed still line up.
CSV_COLUMNS = ["term", "subject", "catnbr", "section",
               "avg_gpa", "p25", "p50", "p75", "std_dev",
               "grade_a", "grade_b", "grade_c", "grade_d", "grade_e",
               "grade_cr", "grade_nc", "grade_w", "grade_other"]


def vizql_root(url):
    """https://host/t/SITE/views/WORKBOOK/SHEET -> /vizql/t/SITE/w/WORKBOOK/v/SHEET"""
    site, workbook, sheet = re.match(
        r"https://[^/]+/t/([^/]+)/views/([^/]+)/([^/?]+)", url).groups()
    return f"/vizql/t/{site}/w/{workbook}/v/{sheet}"


VIZQL_ROOT = vizql_root(DASHBOARD_URL)


def split_tableau_frames(text):
    """A bootstrap response is <charcount>;<json> repeated. Return the jsons."""
    frames = []
    position = 0
    while position < len(text):
        semicolon = text.find(";", position)
        header = text[position:semicolon].strip() if semicolon != -1 else ""
        if not header.isdigit():
            break
        length = int(header)
        frames.append(text[semicolon + 1: semicolon + 1 + length])
        position = semicolon + 1 + length
    return frames




class DashboardSession:
    """One live vizql session, driven over plain HTTP.

    The server stopped shipping data in bootstrap responses (it now defers
    them and renders the dashboard server-side as image tiles), so instead
    of navigating with filters in the URL, filters are applied through the
    session's dashboard-categorical-filter command - those responses still
    carry the data and quick-filter option lists.

    A browser used to do the bootstrap, but the two calls it makes -
    startSession then bootstrapSession - work perfectly well from requests,
    and the session is identified purely by the id in the URL path (the only
    cookie in play is tableau_locale). Dropping the browser takes a bootstrap
    from ~8s to ~1s and a worker from ~300MB to nothing, which is what makes
    running a pool of them practical."""

    def __init__(self):
        self.http = requests.Session()
        self.http.headers.update({
            "User-Agent": USER_AGENT,
            "Referer": DASHBOARD_URL + "?:embed=y",
            "X-Requested-With": "XMLHttpRequest",
        })
        self.http.cookies.set("tableau_locale", "en")
        self.session_id = None
        self.selected = {}     # level -> value currently pinned
        self.failures = 0      # consecutive failed selects, for the backoff

    def connect(self):
        if self.session_id is None:
            self._start_session()

    def _start_session(self):
        self.scraper = TableauScraper(logLevel=logging.ERROR)
        self.scraper.dashboard = DASHBOARD_NAME
        self.scraper.filters = {}
        self.scraper.zones = {}
        self.scraper.dataSegments = {}

        started = self.http.post(
            HOST + VIZQL_ROOT + "/startSession/viewing?%3Aembed=y&%3Aredirect=auth",
            data={}, timeout=60)
        if started.status_code != 200:
            raise RuntimeError(f"startSession -> HTTP {started.status_code}")
        info = json.loads(started.text)

        size = json.dumps({"w": 1200, "h": 800})
        booted = self.http.post(
            HOST + VIZQL_ROOT + f"/bootstrapSession/sessions/{info['sessionid']}",
            data={"worksheetPortSize": size, "dashboardPortSize": size,
                  "clientDimension": size, "renderMapsClientSide": "true",
                  "isBrowserRendering": "true", "browserRenderingThreshold": "100",
                  "formatDataValueLocally": "false", "clientNum": "",
                  "navType": "Reload", "navSrc": "Top", "devicePixelRatio": "1",
                  "clientRenderPixelLimit": "25000000",
                  "allowAutogenWorksheetPhoneLayouts": "true",
                  "sheet_id": info.get("sheetId", ""),
                  "showParams": info.get("showParams", ""),
                  "stickySessionKey": json.dumps(info.get("stickySessionKey", {})),
                  "filterTileSize": "200", "locale": "en_US", "language": "en",
                  "verboseMode": "false", ":session_feature_flags": "{}",
                  "keychain_version": "1"}, timeout=120)
        if booted.status_code != 200:
            raise RuntimeError(f"bootstrapSession -> HTTP {booted.status_code}")
        frames = split_tableau_frames(booted.text)
        if not frames:
            raise RuntimeError("dashboard never loaded")
        self.session_id = json.loads(frames[0])["newSessionId"]

        # An empty subject selection is a cheap way to make the server send
        # every quick filter's option list (filter-all responses carry none);
        # afterwards the term/subject dropdowns are readable.
        self._command(FILTER_LABELS["subject"], [])
        for level in FILTER_ORDER:
            if level in self.selected:
                self._command(FILTER_LABELS[level], [self.selected[level]])

    def _command(self, label, values, update_type="filter-replace"):
        time.sleep(SECONDS_BETWEEN_LOADS)
        reply = self.http.post(
            HOST + VIZQL_ROOT +
            f"/sessions/{self.session_id}/commands/tabdoc/dashboard-categorical-filter",
            data={"dashboard": DASHBOARD_NAME,
                  "qualifiedFieldCaption": label,
                  "exclude": "false",
                  "filterUpdateType": update_type,
                  "filterValues": json.dumps(values)}, timeout=180)
        if reply.status_code != 200:
            raise RuntimeError(
                f"filter {label}={values} -> HTTP {reply.status_code}: "
                f"{reply.text[:120]}")
        response = json.loads(reply.text)
        self._absorb(response)
        return tableau_dashboard.getCmdResponse(
            self.scraper, response, self.scraper.logger)

    def _absorb(self, response):
        """Fold a command response's data, filters and zones into the scraper.
        (TableauWorkbook.updateFullData does the same but crashes on the
        filter-all responses, whose presModel has no workbookPresModel.)"""
        pres_model = (response["vqlCmdResponse"]["layoutStatus"]
                      .get("applicationPresModel", {}))

        segments = pres_model.get("dataDictionary", {}).get("dataSegments", {})
        for key, segment in segments.items():
            if segment is not None:
                self.scraper.dataSegments[key] = segment

        new_filters = tableau_utils.getFiltersForAllWorksheet(
            self.scraper.logger, data=response, info=None,
            rootDashboard=DASHBOARD_NAME, cmdResponse=True)
        for worksheet, filters in new_filters.items():
            kept = [f for f in self.scraper.filters.get(worksheet, [])
                    if not any(f["globalFieldName"] == n["globalFieldName"]
                               for n in filters)]
            self.scraper.filters[worksheet] = kept + filters

        new_zones = tableau_utils.getZones(pres_model)
        for zone_id, zone in new_zones.items():
            if zone is None:
                continue
            if tableau_utils.hasVizData(zone) or zone_id not in self.scraper.zones:
                self.scraper.zones[zone_id] = zone

    def select(self, level, value):
        """Pin one filter and return the updated dashboard.

        A timed-out or dropped request does not kill the session server-side,
        so those are retried on the session we already have. Abandoning it and
        bootstrapping a new one - which is what this used to do on any failure
        - stranded the old session on the server until it aged out, so a bad
        patch spent sessions faster than they expired and starved the
        dashboard. Only a command the server actually refuses earns a fresh
        session, and only after the trouble looks persistent.

        Retrying a filter command is safe: filter-replace and filter-all both
        set an absolute state, so re-sending one the server already applied
        lands on the same result."""
        if self.failures:
            self._back_off()
        for attempt in range(1, LOAD_ATTEMPTS + 1):
            try:
                if self.session_id is None:
                    self._start_session()
                return self._select_once(level, value)
            except TRANSPORT_ERRORS as error:
                failure = error            # keep the session; it outlives these
            except Exception as error:
                self.session_id = None     # refused - the session may be gone
                failure = error
            if attempt == LOAD_ATTEMPTS:
                raise failure
            time.sleep(SECONDS_BETWEEN_RETRIES * attempt)

    def note_result(self, succeeded):
        """Record how a course turned out - this is what drives the backoff.

        A command can answer perfectly well and still be useless: an empty
        section list, a filter that did not apply. Those never reach select(),
        so counting HTTP outcomes alone let a whole subject cascade without
        ever pausing."""
        self.failures = 0 if succeeded else self.failures + 1

    def _back_off(self):
        """Wait out a bad patch instead of grinding through it.

        Every course used to fail on its own for the full retry budget, so an
        outage burned hours making it worse. The pause doubles per consecutive
        failed course and resets as soon as one comes through cleanly."""
        wait = min(SECONDS_BETWEEN_RETRIES * 2 ** (self.failures - 1), MAX_BACKOFF)
        print(f"  server failing ({self.failures} in a row) - waiting {wait}s")
        time.sleep(wait)
        if self.failures >= SESSION_REFRESH_AFTER:
            self.session_id = None         # persistent: this one may be dead

    def _select_once(self, level, value):
        """The session holds filters until they are changed, so a course left
        pinned from the previous subject silently narrows the next subject's
        dropdowns - clear the narrower filters before pinning this one.

        Narrowest first: 'all' means all values currently in the filter's
        domain, so clearing a wide filter while a narrow one still applies
        just re-pins it to the narrowed set."""
        for narrower in reversed(FILTER_ORDER[FILTER_ORDER.index(level) + 1:]):
            if narrower in self.selected:
                self._command(FILTER_LABELS[narrower], [], "filter-all")
                del self.selected[narrower]
        result = self._command(FILTER_LABELS[level], [value])
        self.selected[level] = value
        return result

    def options(self, label):
        """Values currently offered by one dropdown filter."""
        for filters in self.scraper.filters.values():
            for f in filters:
                if f.get("column") == label:
                    return list(f.get("values", []))
        return []

    def close(self):
        try:
            self.http.close()
        except Exception:
            pass




# Workers append to the same two files. The appends are tiny next to a
# ~10s command, so one lock costs nothing and keeps gpa.csv resumable
# exactly as before - rows interleave across terms, but a row is never
# torn and the set of rows is unchanged.
write_lock = threading.Lock()


def load_progress():
    if not os.path.exists(PROGRESS_FILE):
        return set()
    with open(PROGRESS_FILE) as f:
        return set(line.strip() for line in f)


def mark_finished(key):
    with write_lock:
        with open(PROGRESS_FILE, "a") as f:
            f.write(key + "\n")


def subject_key(term, subject):
    """Marks a whole subject done so reruns skip re-walking its courses."""
    return f"SUBJECT|{term}|{subject}"


def write_csv_row(row):
    with write_lock:
        is_new_file = not os.path.exists(OUTPUT_FILE)
        with open(OUTPUT_FILE, "a", newline="") as f:
            writer = csv.DictWriter(f, fieldnames=CSV_COLUMNS)
            if is_new_file:
                writer.writeheader()
            writer.writerow(row)




def shorten(term, subject):
    return term.replace(" End of Term", "").strip(), subject.split(" - ")[0].strip()


def is_gpa(value):
    """A real grade point average and nothing else.

    The dashboard occasionally hands back a stray cell - a course number, a
    headcount - which would otherwise be copied into every statistic. Anything
    outside 0-4 is not a GPA, whatever the worksheet claims."""
    try:
        number = float(value)
    except (TypeError, ValueError):
        return False
    return 0.0 <= number <= 4.0


def gpa_stats(dashboard):
    """The GPA table's stats as a dict - empty when nothing was published."""
    stats = {}
    table = dashboard.getWorksheet(GPA_SHEET).data
    if table is not None and len(table):
        for _, row in table.iterrows():
            column = STAT_COLUMNS.get(row.get("Measure Names-alias"))
            value = row.get("Measure Values-alias")
            if column and value not in (None, "", "%null%") and is_gpa(value):
                stats[column] = value

    # Every statistic zero means no letter grades were awarded - a credit/no
    # credit section - not a class where everyone scored zero.
    if stats and all(float(value) == 0.0 for value in stats.values()):
        return {}
    return stats


def grade_counts(dashboard):
    """How many students earned each grade in the selected section.

    The worksheet is long form - one row per grade group - and repeats itself
    as a %all% totals block, which has to be skipped or every count doubles.
    Grade groups the dashboard adds later fall into grade_other rather than
    being silently dropped."""
    counts = {}
    table = dashboard.getWorksheet(HEADCOUNT_SHEET).data
    if table is None or not len(table):
        return counts

    for _, row in table.iterrows():
        if str(row.get("Catnbr-alias")) == "%all%":
            continue
        try:
            headcount = int(float(row.get("CNT(Emplid Proxy)-alias")))
        except (TypeError, ValueError):
            continue
        grade = str(row.get("Grade Group-alias", "")).strip().upper()
        column = GRADE_COLUMNS.get(grade, "grade_other")
        counts[column] = counts.get(column, 0) + headcount

    return counts


def confirm_course_applied(dashboard, catnbr):
    """Guard against a filter silently not applying (burned us before)."""
    table = dashboard.getWorksheet(HEADCOUNT_SHEET).data
    if table is None or not len(table):
        return
    if not (table.astype(str) == catnbr).any().any():
        raise RuntimeError(f"course filter {catnbr} did not apply")


def scrape_subject(session, finished, term, subject, tag=""):
    """Scrape one subject. True if every course came through cleanly."""
    session.select("subject", subject)
    courses = session.options(FILTER_LABELS["course"])
    short_term, short_subject = shorten(term, subject)
    print(f"{tag}{short_term} / {short_subject}: {len(courses)} courses")
    if not courses:
        return False       # nothing listed - don't record it as swept

    complete = True
    for catnbr in courses:
        try:
            dashboard = session.select("course", catnbr)
            sections = session.options(FILTER_LABELS["section"])

            # The course-level response is already in hand, and its numbers
            # beat summing the sections: headcounts under 5 are suppressed per
            # section, so small grade groups vanish there but survive here.
            key = f"{term}|{subject}|{catnbr}|{COURSE_SECTION}"
            if key not in finished:
                confirm_course_applied(dashboard, catnbr)
                stats = gpa_stats(dashboard)
                counts = grade_counts(dashboard)
                if stats or counts:
                    write_csv_row({"term": short_term, "subject": short_subject,
                                   "catnbr": catnbr, "section": COURSE_SECTION,
                                   **stats, **counts})
                mark_finished(key)
                finished.add(key)
                print(f"{tag}  {short_subject} {catnbr}-{COURSE_SECTION}: "
                      f"avg={stats.get('avg_gpa', '-')}")

            if not sections:
                raise RuntimeError(f"no sections listed for {catnbr}")

            for section in sections:
                key = f"{term}|{subject}|{catnbr}|{section}"
                if key in finished:
                    continue
                dashboard = session.select("section", section)
                confirm_course_applied(dashboard, catnbr)

                stats = gpa_stats(dashboard)
                counts = grade_counts(dashboard)
                if stats or counts:   # sections with nothing published are skipped
                    write_csv_row({"term": short_term, "subject": short_subject,
                                   "catnbr": catnbr, "section": section,
                                   **stats, **counts})
                mark_finished(key)
                finished.add(key)
                print(f"{tag}  {short_subject} {catnbr}-{section}: "
                      f"avg={stats.get('avg_gpa', '-')}")
            session.note_result(True)
        except Exception as error:
            complete = False
            session.note_result(False)
            print(f"{tag}  ! {short_subject} {catnbr} skipped: {error!r}")
    return complete


def sweep_term(session, finished, term, tag=""):
    """Every subject in one term, on one session."""
    session.select("term", term)
    subjects = SUBJECTS or session.options(FILTER_LABELS["subject"])

    for subject in subjects:
        key = subject_key(term, subject)
        if key in finished:
            continue
        try:
            complete = scrape_subject(session, finished, term, subject, tag)
        except Exception as error:
            print(f"{tag}! {shorten(term, subject)[1]} skipped: {error!r}")
            continue
        if complete:
            mark_finished(key)
            finished.add(key)


def worker(number, pending, finished):
    """Pull terms off the queue until it runs dry.

    Terms are handed out one at a time rather than sliced up front because
    they differ a lot in size - a worker that draws a light term comes back
    for another instead of idling while the rest finish."""
    tag = f"[{number}] "
    session = DashboardSession()
    try:
        while True:
            try:
                term = pending.get_nowait()
            except queue.Empty:
                return
            for attempt in range(1, LOAD_ATTEMPTS + 1):
                try:
                    session.connect()
                    sweep_term(session, finished, term, tag)
                    print(f"{tag}finished {term}")
                    break
                except Exception as error:
                    print(f"{tag}error on {term}: {error!r}")
                    session.session_id = None
                    session.selected = {}
                    if attempt == LOAD_ATTEMPTS:
                        print(f"{tag}giving up on {term} - rerun to resume.")
                    else:
                        time.sleep(SECONDS_BETWEEN_RETRIES * attempt)
    finally:
        session.close()


def main():
    finished = load_progress()
    print(f"{len(finished)} sections already done; resuming.")

    terms = TERMS
    if not terms:                  # one throwaway session just to read the list
        scout = DashboardSession()
        try:
            scout.connect()
            terms = scout.options(FILTER_LABELS["term"])
        finally:
            scout.close()

    pending = queue.Queue()
    for term in terms:
        pending.put(term)

    count = max(1, min(WORKERS, len(terms)))
    print(f"{len(terms)} terms across {count} workers.\n")

    threads = [threading.Thread(target=worker, daemon=True,
                                args=(i + 1, pending, finished))
               for i in range(count)]
    try:
        for thread in threads:
            thread.start()
        # Joining with a timeout keeps the main thread interruptible - a bare
        # join() swallows Ctrl+C on Windows. The workers are daemons and every
        # row is already on disk, so exiting here loses nothing.
        while any(thread.is_alive() for thread in threads):
            for thread in threads:
                thread.join(0.5)
    except KeyboardInterrupt:
        print("\ninterrupted - progress saved, rerun to resume.")
        return
    print("SWEEP COMPLETE")


if __name__ == "__main__":
    main()
