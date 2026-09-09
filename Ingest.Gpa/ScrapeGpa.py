
import collections
import csv
import json
import logging
import logging.handlers
import os
import queue
import re
import threading
import time

import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry
from tableauscraper import TableauScraper
from tableauscraper import dashboard as tableau_dashboard
from tableauscraper import utils as tableau_utils


SECONDS_BETWEEN_LOADS = 1.5

# Terms are swept in parallel, one vizql session per worker. Sessions are
# independent server-side, so a worker's filters never disturb another's.
#
# Measured server latency by concurrency: 1 worker 1.30s, 3 workers 2.02s,
# 6 workers 2.76s, 12 workers 4.41s. Throughput saturates near 1.7 commands a
# second, so six is roughly the last setting still gated by our own 1.5s
# throttle rather than by the host; twelve buys ~12% for double the load.
#
# Six is not free of risk: the host closed every connection after 39 minutes of
# six workers once, and again after 25. But a dropped connection no longer ends
# the sweep - _start_session waits the outage out and the term restarts - so the
# cost of being wrong here is a pause rather than a dead run.
WORKERS = 6

TERMS = None
SUBJECTS = None

HOST = "https://tableau.dashboard.utah.edu"
DASHBOARD_URL = (f"{HOST}/t/UAIR/views/"
                 "OfficialUUGradeSummary_17192658137620/GradeSummary")

USER_AGENT = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
              "(KHTML, like Gecko) Chrome/126.0 Safari/537.36")

# Paths are relative to the repo root - run everything from there.
# A second independent sweep. gpa2.csv finished 2026-09-04 at 74,378 rows and
# every invariant clean, but "no detectable corruption" is not "no corruption":
# the (all) >= sum-of-sections check is vacuous for single-section courses, so
# it can only see about 56% of rows. Two independent scrapes of the same source
# can be diffed, and rows that disagree are corrupt in one file or the other -
# which measures the real rate instead of bounding it. That is how the ~0.118%
# figure for the previous generation was arrived at.
#
# The progress file must move with the output file. Pointed at a done2.txt that
# already marks all 2,016 subject-terms, a fresh run would skip everything and
# write nothing.
OUTPUT_FILE = "data/gpa3.csv"
PROGRESS_FILE = "data/done3.txt"

# One JSON object per line, recording what was asked for, what came back, and
# the state of the interning dictionary at the moment each row was decoded.
# That last part is the point: a drifted dictionary writes a plausible wrong
# number and nothing downstream can tell, so the only way to catch it after the
# fact is to have kept what the dictionary looked like when the row was read.
LOG_FILE = "data/scrape.log"

LOG_MAX_BYTES = 250_000_000
LOG_BACKUPS = 2

LOAD_ATTEMPTS = 3
SECONDS_BETWEEN_RETRIES = 5

# The dashboard stops answering for minutes at a time - sometimes hours - and
# has come back on its own every time. Waiting costs a pause; giving up costs
# the rest of the sweep, and hammering it while it refuses is what prolongs the
# outage. So a session that cannot be opened waits, doubling the pause.
OUTAGE_FIRST_WAIT = 30
OUTAGE_MAX_WAIT = 600

# Values arrive as positions in a dictionary the session accumulates, and the
# server reuses its segment numbers with new contents, so a long-lived session's
# dictionary can drift out of step with the responses that index into it. When
# it does, a label decodes as convincingly as a value: one row was written as
# ECE 2245 section 004 carrying ECE 1240 section 003's numbers, Catnbr and all.
# Nothing downstream can catch that. Every read on a short session has been
# correct, so a session is retired after this many subjects to keep its
# dictionary small and close to the responses using it. A bootstrap costs a
# second or two; a subject costs minutes.
SUBJECTS_PER_SESSION = 1


# Timeouts and dropped connections leave the vizql session alive on the
# server, so they are retried on it rather than costing a new one.
TRANSPORT_ERRORS = (requests.exceptions.Timeout,
                    requests.exceptions.ConnectionError,
                    requests.exceptions.ChunkedEncodingError)


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

# Pinning a term changes nothing on screen, so its response carries no zones
# and no filters - the subject list has to be read from the response to a real
# selection instead. Any subject with data would serve; MATH is offered in all
# 18 terms the dashboard covers.
BOOTSTRAP_SUBJECT = "MATH - Mathematics"

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

# A session can stop answering altogether: the dashboard starts rendering every
# zone server-side and returns the same empty response to every command,
# whatever it asks for. Fall 2022 PSY hit it and lost 3000, 3010 and 3040 back
# to back at ~30s each, and would have walked the whole subject that way -
# nothing is corrupted, but every remaining course is skipped.
#
# Measured over 26,633 commands: healthy runs of empty responses top out at 3
# (a term pin and the clears before it), occurring 11 times. The only longer
# runs in the whole log were 6 and 14, and both were this failure. Six sits
# above anything a healthy sweep produces and below the cost of finding out
# course by course. A false trip is cheap - the worker drops the session and
# replays the term, skipping everything already marked.
WEDGED_AFTER_EMPTY = 6

# Only letter grades carry grade points. CR, NC, W and the rest are excluded
# from every GPA statistic the dashboard publishes, so counting them when
# judging a spread measures the wrong population - Fall 2023 CS 1960-002 is
# 12 CR students around 4 graded ones, and its genuine 2.31 was refused
# against a ceiling computed for 12.
GPA_BEARING = ("grade_a", "grade_b", "grade_c", "grade_d", "grade_e")

# Every statistic is published rounded to two decimals, so a section sitting
# exactly ON its ceiling prints above it: the n=4 ceiling is 2.3094 and reaches
# us as "2.31". Allow back the half-unit the rounding can add, or the most
# split section there is - half the class at 0, half at 4 - gets refused for
# being precisely what it claims to be.
DISPLAY_ROUNDING = 0.005

# Grades run 0 to 4, so the spread has a ceiling: half the class at 0 and half
# at 4. For the POPULATION deviation that ceiling is 2.0 - but the dashboard
# reports the SAMPLE deviation, whose ceiling is 2*sqrt(n/(n-1)) and therefore
# exceeds 2.0 for a small section: 2.83 for two students, 2.24 for five.
# Judging every row against a flat 2.0 would discard real data - CS 1960-001
# reports 2.03 across six students, which is genuine. So reject only a spread
# that no class of that size could produce, and when the headcount is
# suppressed fall back to the most forgiving ceiling of all. Count only the
# GPA-bearing grades: a section can be mostly CR, and any group under five is
# suppressed out of the sheet entirely, so the graded count is a floor. That
# undercount is the safe direction - a smaller n only ever raises the ceiling.
GRADE_SPAN = 4.0
MOST_FORGIVING_STD_DEV = GRADE_SPAN / 2 * (2 ** 0.5)   # the n=2 ceiling, 2.83

# Section value for the whole-course row, which the dashboard reports when no
# section is pinned. Parenthesised so it cannot collide with a real section.
COURSE_SECTION = "(all)"

# Term value for the all-terms pass, swept as if it were one more term. A row
# carrying it in BOTH term and section is the third grain: one course with every
# term pooled, which is NOT the sum of the per-term rows - suppression is applied
# to whatever is on screen, so a group that is four-per-term is hidden in all
# eighteen reads and survives only when the terms are pooled first. Measured on
# CS 2420 the pooled read is 17 students larger, every one of them a D, an E or
# a W: the grades that make a course look harder are the ones per-term
# collection loses.
ALL_TERMS = "(all)"

# The grade columns come last so rows written before they existed still line up.
CSV_COLUMNS = ["term", "subject", "catnbr", "section",
               "avg_gpa", "p25", "p50", "p75", "std_dev",
               "grade_a", "grade_b", "grade_c", "grade_d", "grade_e",
               "grade_cr", "grade_nc", "grade_w", "grade_other"]


_log = logging.getLogger("scrape")


def start_log():
    """A rotating JSON-lines log. The handler locks, so workers can share it."""
    handler = logging.handlers.RotatingFileHandler(
        LOG_FILE, maxBytes=LOG_MAX_BYTES, backupCount=LOG_BACKUPS, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(message)s"))
    _log.addHandler(handler)
    _log.setLevel(logging.INFO)
    _log.propagate = False
    note("start", output=OUTPUT_FILE, workers=WORKERS,
         subjects_per_session=SUBJECTS_PER_SESSION)


def note(kind, **fields):
    """Record one event. Logging must never be the thing that breaks a sweep."""
    try:
        fields["at"] = time.strftime("%H:%M:%S")
        fields["kind"] = kind
        fields["worker"] = threading.current_thread().name
        _log.info(json.dumps(fields, default=str))
    except Exception:
        pass


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


def dictionary_size(segments):
    """Values held per data type - the interning table's fingerprint.

    Logged beside every row. A drifted dictionary writes a plausible wrong
    number and nothing downstream can tell, so what the table looked like at
    the moment of the read is the only trace left to diagnose it by."""
    totals = {}
    for segment in segments.values():
        for column in segment.get("dataColumns", []):
            kind = column.get("dataType")
            totals[kind] = totals.get(kind, 0) + len(column.get("dataValues", []))
    return totals


class ImpossibleValue(RuntimeError):
    """A statistic that cannot be true of a grade point average.

    When the interning dictionary shifts, the value that lands in a field came
    from a neighbouring one and usually looks perfectly ordinary - an average
    of 1.27 is a real-looking number. Sometimes it does not: a standard
    deviation of 3.73, or percentiles that run backwards. Those are the cases
    arithmetic can catch on its own, without a second read to compare against.

    It refused 13 rows in the 2026-09-01 run - 11 on the deviation, 2 on the
    percentiles - and raised no false positive anywhere in 35,052 written rows.

    Measuring it against the finished file understates it by construction:
    anything it catches is never written, so the corruption that survives is
    exactly the corruption it cannot see. Of the 80 rows the (all) >= sum
    invariant later flagged, these checks would have refused none. It is a
    cheap net with a wide mesh, not a substitute for that invariant. Refusing
    is still right: the number came from somewhere else, and a skipped row is
    re-read later while a written one is believed."""


class StaleDictionary(RuntimeError):
    """The interning dictionary we hold does not match this response.

    Values are not sent inline. Each response carries numbered dictionary
    segments, the client concatenates every segment it has seen into one list
    per data type, and the worksheet addresses that list BY POSITION - a
    negative index meaning the string table instead. The server reuses those
    segment numbers with entirely different contents, so the accumulated
    dictionary can stop lining up with what a response expects.

    The decoder does not notice. An index past the end of the list is silently
    dropped, which shortens a column, and the rows either side of the gap pair
    up with the wrong values - a course's percentiles came out right while its
    average and standard deviation came from somewhere else entirely. Only when
    a negative index overruns does it raise, as IndexError.

    So every index is checked against the dictionary before the numbers are
    believed. Writing a row from a response that fails this check is the one
    outcome worse than skipping it: a plausible number that is quietly wrong,
    which no invariant downstream can catch."""


class NotRefreshed(RuntimeError):
    """A response did not carry what we were about to read.

    The dashboard ships a worksheet only when that worksheet's rendering
    changed, and a quick filter's option list only alongside it, so a response
    can legitimately arrive without the sheet or dropdown we want. Reading one
    anyway is what wrote the previous subject's numbers down as this subject's
    - Fall 2023 QUEST was published with QAMO's course list and QAMO's totals
    that way. So this is always a failure to retry, never a reason to fall back
    on whatever was read last."""


class Reply:
    """One command response, read only through itself.

    Nothing is carried between responses except dataSegments, which is an
    append-only interning table the server expects the client to accumulate:
    later responses refer to keys defined in earlier ones, so it is shared
    state by design rather than by accident."""

    def __init__(self, segments, response):
        pres_model = (response["vqlCmdResponse"]["layoutStatus"]
                      .get("applicationPresModel", {}))
        new_segments = (pres_model.get("dataDictionary", {})
                        .get("dataSegments", {}))
        self.absorbed = {}
        for key, segment in new_segments.items():
            if segment is not None:
                columns = segment.get("dataColumns", [])
                self.absorbed[key] = sum(len(c.get("dataValues", []))
                                         for c in columns)
                segments[key] = segment

        self.response = response
        self.segments = segments
        self.zones = {zone_id: zone
                      for zone_id, zone in tableau_utils.getZones(pres_model).items()
                      if zone is not None and tableau_utils.hasVizData(zone)}
        self.filters = tableau_utils.getFiltersForAllWorksheet(
            logging.getLogger(), data=response, info=None,
            rootDashboard=DASHBOARD_NAME, cmdResponse=True)
        self.refreshed = {zone.get("worksheet") for zone in self.zones.values()}
        self._tables = None

        # Zones the server redrew but sent as an image instead of data, kept
        # apart from zones it did not redraw at all - the two look identical
        # from outside and mean completely different things.
        all_zones = tableau_utils.getZones(pres_model)
        self.tiled = sorted({z.get("worksheet") for z in all_zones.values()
                             if z is not None and not tableau_utils.hasVizData(z)
                             and z.get("worksheet")} - self.refreshed)

    def ordered(self):
        """The interning table in the order the server indexes it.

        The decoder concatenates every segment's dataColumns in **dict
        insertion order**:

            for d in list(originSegmentscp):
                dataColumns.extend(originSegmentscp[d]["dataColumns"])

        The server numbers its segments and expects them concatenated by that
        number. A session that happens to receive slot 3 before slot 0 builds
        its dictionary back to front, and every index in every response then
        lands len(segment 3) places away from the value it was meant to name.

        The integers usually survive, because their columns often align by
        luck; the strings do not. So a row keeps plausible headcounts while
        `Catnbr-alias` decodes to something else entirely - sometimes garbage
        like "1.02", and sometimes another course's number, which is how one
        course's students end up written under another course's label with
        nothing downstream able to tell.

        Fall 2025 PSY 2710 is the worked example: slots arrived 3, 0, 1, 2 and
        the section read decoded as `catnbr=1.02, grade=E`; ordered numerically
        the same bytes give `catnbr=2710, grade=A`.

        Rare but real: 7 of 758 multi-segment sessions in the log (0.9%)
        received slots out of order, accounting for ~140 written rows. It is
        heavily over-represented among failures - 32 of 55 captured at the
        time - and that 60x enrichment is what marks it as the cause rather
        than a bystander.

        Sorting is numeric, not lexical: sessions reach a dozen slots, and
        "10" sorts before "2" as a string."""
        def slot(key):
            try:
                return (0, int(key))
            except (TypeError, ValueError):
                return (1, str(key))
        return {k: self.segments[k] for k in sorted(self.segments, key=slot)}

    def dictionary_size(self):
        return dictionary_size(self.segments)

    def _check_dictionary(self, name):
        """Refuse a worksheet whose values we cannot all look up.

        Mirrors exactly what the decoder is about to do, and fails where the
        decoder would silently shrug."""
        pres_model = (self.response["vqlCmdResponse"]["layoutStatus"]
                      .get("applicationPresModel", {}))
        data_full = tableau_utils.getDataFullCmdResponse(
            pres_model, self.ordered())
        strings = data_full.get("cstring", [])
        # Every zone, not just the one asked for. getCmdResponse decodes ALL
        # zones carrying vizData before handing back the sheet you wanted, so
        # a bad index anywhere in the response reaches the decoder. Checking
        # only the named sheet let Fall 2023 PSY 3460 through: Grade Tabs had
        # nothing out of range while Avg GPA wanted cstring[127] of 127 and
        # Grade Dist wanted entry 76 of 69. The decoder raised a bare
        # IndexError, which is not StaleDictionary and so never aborted the
        # term - and worse, when the requested sheet's own indices happened to
        # land in range, the row was written from a dictionary that plainly
        # did not match the response.
        for zone in self.zones.values():
            columns = (zone["presModelHolder"]["visual"]
                       .get("vizData", {}).get("paneColumnsData"))
            if not columns:
                continue
            for column in columns["vizDataColumns"]:
                if not column.get("fieldCaption"):
                    continue
                pane = columns["paneColumnsList"][column["paneIndices"][0]]
                cell = pane["vizPaneColumns"][column["columnIndices"][0]]
                values = data_full.get(column.get("dataType"), strings)
                for index in list(cell["valueIndices"]) + list(cell["aliasIndices"]):
                    room = len(strings) if index < 0 else len(values)
                    wanted = abs(index) - 1 if index < 0 else index
                    if wanted >= room:
                        where = zone.get("worksheet") or "?"
                        note("stale_dictionary", worksheet=name, zone=where,
                             field=column["fieldCaption"], index=index,
                             room=room, dictionary=self.dictionary_size(),
                             segments=len(self.segments))
                        raise StaleDictionary(
                            f"{name}: {where}.{column['fieldCaption']} wants "
                            f"entry {index} of {room} - the dictionary we hold "
                            f"does not match this response")

    def worksheet(self, name):
        """This response's own copy of a worksheet."""
        if name not in self.refreshed:
            raise NotRefreshed(f"{name} not in this response "
                               f"(refreshed: {sorted(self.refreshed)})")
        self._check_dictionary(name)
        if self._tables is None:
            scraper = TableauScraper(logLevel=logging.ERROR)
            scraper.dashboard = DASHBOARD_NAME
            scraper.filters = {}
            scraper.zones = self.zones
            scraper.dataSegments = self.ordered()
            self._tables = tableau_dashboard.getCmdResponse(
                scraper, self.response, scraper.logger)
        return self._tables.getWorksheet(name).data

    def _filter(self, level):
        """This response's copy of one quick filter.

        Prefers a populated copy: an empty list is a real answer, but only once
        no worksheet in the response offers a fuller one."""
        label = FILTER_LABELS[level]
        empty = None
        for filters in self.filters.values():
            for one in filters:
                if one.get("column") == label:
                    if one.get("values"):
                        return one
                    empty = one
        if empty is None:
            raise NotRefreshed(f"{label} filter not in this response")
        return empty

    def options(self, level):
        """The values a dropdown offers. Empty is an answer; absent is not."""
        return list(self._filter(level).get("values", []))

    def confirm(self, level, value, absent_ok=False):
        """Check the server's own account of what it applied.

        A filter the response does not carry cannot disconfirm anything. The
        server sends a filter only when its DOMAIN changes, so pinning a
        section in a subject with one course changes no domain and comes back
        with no filters at all - while the worksheets refresh perfectly well.
        Fall 2020 MID E 2910 is that shape, and demanding the echo threw away
        three good sections and lost the course, four times over.
        `absent_ok` is for the callers that have another way to tell the read
        landed: a refreshed worksheet is itself evidence, because a selection
        that failed to apply would have changed nothing and been sent nothing.
        Where there is no such evidence - confirming the term from the next
        response - absence still has to be an error.

        A filter that did not narrow reports every value it offers plus 'all',
        which is exactly how one that silently failed to apply looks - and how
        Spring 2023 BUS came to walk nine courses belonging to another
        subject."""
        try:
            reported = self._filter(level)
        except NotRefreshed:
            if absent_ok:
                return
            raise
        chosen = [v for v in reported.get("selection", []) if v != "all"]
        if set(chosen) != {value}:
            shown = chosen if len(chosen) <= 5 else f"{len(chosen)} values"
            raise NotRefreshed(f"{FILTER_LABELS[level]} reads {shown} after "
                               f"selecting {value!r}")


class SessionWedged(RuntimeError):
    """The session has stopped returning data and will not recover.

    Every command comes back with no worksheet at all, so nothing can be read
    and nothing can be checked. It is not corruption - no row is written - but
    carrying on costs the rest of the subject for nothing. Aborting the term
    puts it back on the queue, where a fresh session resumes from the subjects
    already marked."""


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

        # The host closes idle keep-alive connections, and a pooled socket that
        # has already been closed fails the next request instantly with
        # RemoteDisconnected("Remote end closed connection without response").
        # requests retries nothing by default, and urllib3 excludes POST from
        # its default retry set because POST is not idempotent in general - but
        # every call this scraper makes is. startSession is a fresh session
        # either way, and filter-replace and filter-all both set an ABSOLUTE
        # state, so re-sending one the server already applied lands on exactly
        # the same result (see select()).
        #
        # Without this, one dropped socket aborts a whole term and it goes back
        # on the queue to be walked again: 41 requeues and 37 outages in the
        # 2026-09-01 run, nearly all of them RemoteDisconnected. Retrying at the
        # transport layer turns those into a pause of a few seconds.
        retry = Retry(
            total=4, connect=4, read=4,
            backoff_factor=0.6,          # 0.6s, 1.2s, 2.4s, 4.8s
            backoff_max=10,
            status_forcelist=(502, 503, 504),
            allowed_methods=frozenset(["GET", "POST"]),
            raise_on_status=False,
        )
        self.http.mount("https://", HTTPAdapter(max_retries=retry))

        self.http.headers.update({
            "User-Agent": USER_AGENT,
            "Referer": DASHBOARD_URL + "?:embed=y",
            "X-Requested-With": "XMLHttpRequest",
        })
        self.http.cookies.set("tableau_locale", "en")
        self.session_id = None
        self.selected = {}     # level -> value currently pinned
        # Select every term rather than one. Set by the all-terms pass and
        # re-applied on every session rebuild - see _open_session.
        self.all_terms = False
        self.segments = {}     # the interning table, per session
        self.empty_streak = 0  # consecutive responses carrying no worksheet

    def connect(self):
        """Start a session if there is none, returning its priming response.

        That response is the only one carrying every dropdown's option list, so
        it is the only place the term list can be read - and it cannot be asked
        for twice. Returns None when a session was already up, because then the
        priming response is long gone."""
        if self.session_id is None:
            return self._start_session()
        return None

    def _start_session(self):
        """Open a session, waiting out an outage rather than failing the sweep.

        Every route to the server passes through here - the scout reading the
        term list, a worker starting a term, and select() rebuilding a session
        it had to drop - so waiting here is the only place it has to be done.

        Retrying the real thing IS the health check. An earlier version polled
        the dashboard page instead, which disagrees with it: through the outage
        of 2026-08-30 the page answered 200 in 0.4s while startSession returned
        HTTP 500 for half an hour. That probe called the server healthy on all
        88 wake-ups, not one of which could open a session, so the backoff reset
        every 30 seconds and the sweep spent the outage hammering a host that
        could not serve it. Waiting on the capability we actually need also
        leaves no probe session stranded on the server to age out."""
        delay = OUTAGE_FIRST_WAIT
        while True:
            try:
                return self._open_session()
            except TRANSPORT_ERRORS as error:
                reason = type(error).__name__
            except RuntimeError as error:
                reason = str(error)
            self.session_id = None
            note("outage", reason=reason, retry_in=delay)
            print(f"cannot open a session ({reason}); retrying in {delay}s")
            time.sleep(delay)
            delay = min(delay * 2, OUTAGE_MAX_WAIT)

    def _open_session(self):
        self.segments = {}     # segment keys belong to the session that sent them
        self.empty_streak = 0  # a fresh session starts the count over

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
        note("session_open", session=self.session_id,
             replaying=dict(self.selected))

        primed = self.prime()

        # Selecting every term is a filter-all, not a value, so it leaves
        # nothing in self.selected for the loop below to replay. Without this
        # a rebuilt session silently falls back to the dashboard's default
        # term - and the rows would still be written, labelled as all-terms.
        # Sessions are rebuilt constantly (726 times in the last sweep), so
        # this is the difference between an all-terms pass and one term's data
        # wearing the wrong name.
        if self.all_terms:
            self._command(FILTER_LABELS["term"], [], "filter-all")

        for level in FILTER_ORDER:
            # The term is re-applied above as a filter-all; replaying it as a
            # value would pin the literal string "(all)".
            if level == "term" and self.all_terms:
                continue
            if level in self.selected:
                self._command(FILTER_LABELS[level], [self.selected[level]])
        return primed

    def prime(self):
        """Select no subject at all, on a session that has just booted.

        This is the one response that carries every dropdown's full option
        list, and so the only place the term list can be read. It works exactly
        once, and only before a term is pinned: a second empty selection - or
        one issued after a term is pinned - leaves the view exactly as it was,
        and a response that changed nothing carries nothing at all."""
        return self._command(FILTER_LABELS["subject"], [])

    def _command(self, label, values, update_type="filter-replace"):
        time.sleep(SECONDS_BETWEEN_LOADS)
        started = time.time()
        reply = self.http.post(
            HOST + VIZQL_ROOT +
            f"/sessions/{self.session_id}/commands/tabdoc/dashboard-categorical-filter",
            data={"dashboard": DASHBOARD_NAME,
                  "qualifiedFieldCaption": label,
                  "exclude": "false",
                  "filterUpdateType": update_type,
                  "filterValues": json.dumps(values)}, timeout=180)
        seconds = round(time.time() - started, 1)
        if reply.status_code != 200:
            note("command", filter=label, values=values, how=update_type,
                 seconds=seconds, status=reply.status_code)
            raise RuntimeError(
                f"filter {label}={values} -> HTTP {reply.status_code}: "
                f"{reply.text[:120]}")
        body = json.loads(reply.text)
        answer = Reply(self.segments, body)
        note("command", filter=label, values=values, how=update_type,
             seconds=seconds, refreshed=sorted(answer.refreshed),
             tiled=answer.tiled, segments_in=answer.absorbed,
             dictionary=answer.dictionary_size(), selected=dict(self.selected))

        if answer.refreshed:
            self.empty_streak = 0
        else:
            self.empty_streak += 1
            if self.empty_streak >= WEDGED_AFTER_EMPTY:
                note("session_wedged", streak=self.empty_streak,
                     filter=label, values=values,
                     selected=dict(self.selected))
                raise SessionWedged(
                    f"{self.empty_streak} commands in a row came back with no "
                    f"worksheet - the session has stopped answering, and every "
                    f"course after this one would be skipped for nothing")
        return answer

    def select(self, level, value):
        """Pin one filter and return the response that proves it applied.

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
        for attempt in range(1, LOAD_ATTEMPTS + 1):
            try:
                if self.session_id is None:
                    self._start_session()
                reply = self._select_once(level, value)
                if level != "term" and (GPA_SHEET in reply.refreshed
                                        or HEADCOUNT_SHEET in reply.refreshed):
                    # The sheets we read came back fresh, so the selection
                    # landed; an echo would only confirm what the data shows.
                    reply.confirm(level, value, absent_ok=True)
                elif level != "term":
                    # A term pin re-renders nothing, so its own response has
                    # nothing to check against; sweep_term confirms the term
                    # from the next response instead.
                    reply.confirm(level, value)
                return reply
            except TRANSPORT_ERRORS as error:
                failure = error            # keep the session; it outlives these
            except NotRefreshed as error:
                failure = error            # the session is fine, the view is not
            except Exception as error:
                self.session_id = None     # refused - the session may be gone
                failure = error
            if attempt == LOAD_ATTEMPTS:
                raise failure
            time.sleep(SECONDS_BETWEEN_RETRIES * attempt)

    def _select_once(self, level, value):
        """The session holds filters until they are changed, so a course left
        pinned from the previous subject silently narrows the next subject's
        dropdowns - clear the narrower filters before pinning this one.

        Narrowest first: 'all' means all values currently in the filter's
        domain, so clearing a wide filter while a narrow one still applies
        just re-pins it to the narrowed set.

        Clearing matters for a second reason. A worksheet is sent only when
        its rendering changed, so narrowing from everything on screen down to
        one value is always a visible change, while moving between two views
        that are both blank is not - and a response that carries nothing is
        one we cannot read."""
        narrower_levels = FILTER_ORDER[FILTER_ORDER.index(level) + 1:]
        for narrower in reversed(narrower_levels):
            if narrower in self.selected:
                self._command(FILTER_LABELS[narrower], [], "filter-all")
                del self.selected[narrower]

        if self.selected.get(level) == value:
            # Re-pinning what is already pinned moves nothing on screen, so the
            # response would come back empty and the read would fail. Widen
            # first so that pinning it again is a change the server will send.
            # This is what makes a retry worth attempting at all, and it is the
            # path taken whenever a sweep reaches the subject it bootstrapped
            # the term's list from.
            self._command(FILTER_LABELS[level], [], "filter-all")
            del self.selected[level]

        # Selecting every term rather than one. This is the third grain: the
        # dashboard applies its under-five-student suppression to whatever is on
        # screen, so a group that is four-per-term is hidden in all eighteen
        # term reads and survives only when the terms are pooled first. Measured
        # on CS 2420 the pooled read is 17 students larger, and every one of
        # them is a D, an E or a W - the grades that make a course look harder
        # are exactly the ones per-term collection loses.
        #
        # It is a filter-all, not a value, so all_terms records it: nothing goes
        # into self.selected that _open_session could replay, and a rebuilt
        # session would otherwise fall back to the default term and keep writing
        # under the all-terms label.
        if level == "term" and value == ALL_TERMS:
            self.all_terms = True
            self.selected[level] = value
            return self._command(FILTER_LABELS["term"], [], "filter-all")

        # Record the pin BEFORE issuing it. A command that fails can still have
        # been applied - the server answered Section=['013'] with HTTP 400 on
        # 2026-09-03 and pinned it anyway - and recording afterwards means a
        # failure leaves us believing nothing is pinned. The clear above is
        # conditional on this bookkeeping, so the stale filter is then never
        # cleared: Section=013 narrowed the Catnbr domain to the one course
        # that has a section 013, no other course could be selected, and eight
        # courses were lost before the subject ended and retired the session.
        #
        # Being wrong in this direction is cheap. If the pin really did not
        # land, the next widening sends one filter-all against an already wide
        # filter, which changes nothing. Being wrong the other way wedges the
        # session. Clears keep the opposite bias for the same reason - they
        # delete from self.selected only after the command returns, so a failed
        # clear leaves the filter marked pinned and gets cleared again.
        self.selected[level] = value
        return self._command(FILTER_LABELS[level], [value])

    def reread_section(self, catnbr, section):
        """Read one section again, pinning it BEFORE the course.

        Pinning a section of a course whose sections all share the same spread
        of grades leaves a chart identical, so the dashboard does not send it
        and those numbers cannot be read at all - Fall 2025 ART 1080 has three
        sections every one of which averages 4.00. Selecting the section first
        and the course second makes the last command a large change, from every
        course in the subject down to one, and that is always redrawn with data.

        This reads the numbers rather than assuming them. Carrying the previous
        section's values forward would be the stale read that fabricated rows
        in the first place, and it cannot be told apart from a real change: the
        dashboard also withholds data when it renders server-side as an image,
        which it does for a broad selection whose numbers certainly did change."""
        for level in ("section", "course"):
            if level in self.selected:
                self._command(FILTER_LABELS[level], [], "filter-all")
                del self.selected[level]

        # Recorded before the command, for the reason in _select_once: a pin
        # that fails may still have applied, and this is the path the wedge of
        # 2026-09-03 came through.
        self.selected["section"] = section
        widened = self._command(FILTER_LABELS["section"], [section])
        # Same rule as select(): a refreshed worksheet is itself the evidence
        # the pin landed. This path exists for exactly the courses whose
        # filters do not move - Fall 2020 MID E 2910 is one course, so pinning
        # its section changes no domain and echoes nothing, while the GPA chart
        # refreshes perfectly well. Demanding the echo here skipped the course
        # even after select() stopped demanding it.
        widened.confirm("section", section,
                        absent_ok=bool({GPA_SHEET, HEADCOUNT_SHEET}
                                       & widened.refreshed))

        # Pinning the course afterwards often changes nothing: in a small
        # subject the section already picks out the only course that has it, so
        # the response carries nothing at all - Fall 2020 ATSM 5404 is that
        # shape. The section response is then the answer already. It breaks its
        # data down by course and every read filters on Catnbr, so the right
        # numbers come out of it either way; the course only has to be pinned
        # to leave the session where the next section expects it.
        self.selected["course"] = catnbr
        narrowed = self._command(FILTER_LABELS["course"], [catnbr])
        if {GPA_SHEET, HEADCOUNT_SHEET} <= narrowed.refreshed:
            return narrowed
        if {GPA_SHEET, HEADCOUNT_SHEET} <= widened.refreshed:
            return widened
        return narrowed if narrowed.refreshed else widened

    def close_session(self):
        """Drop the vizql session, keeping the HTTP connection and selections.

        Only the term is kept pinned, so the replay costs one command rather
        than four; the next subject is selected from a cleared view anyway,
        which is also the state a subject read wants."""
        note("session_retire", session=self.session_id,
             segments=len(self.segments),
             dictionary=dictionary_size(self.segments))
        self.session_id = None
        self.segments = {}
        self.selected = {level: value for level, value in self.selected.items()
                         if level == "term"}

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


def gpa_stats(reply, catnbr, students=0):
    """The GPA table's stats as a dict - empty when nothing was published.

    Only rows naming this course are read. The worksheet carries one block of
    measures per course, and a response can hold more courses than the Catnbr
    dropdown admits to - ARAB Fall 2022 offers only 1010, yet its subject-level
    GPA table also carries 2010, whose headcounts are all suppressed. Reading
    every row and letting the last one win handed 2010's average to 1010."""
    stats = {}
    table = reply.worksheet(GPA_SHEET)
    if table is not None and len(table):
        for _, row in table.iterrows():
            if str(row.get("Catnbr-alias")) != catnbr:
                continue
            column = STAT_COLUMNS.get(row.get("Measure Names-alias"))
            value = row.get("Measure Values-alias")
            if column and value not in (None, "", "%null%") and is_gpa(value):
                stats[column] = value

    deviation = stats.get("std_dev")
    if deviation is not None:
        ceiling = (GRADE_SPAN / 2 * (students / (students - 1)) ** 0.5
                   if students > 1 else MOST_FORGIVING_STD_DEV)
        if float(deviation) - DISPLAY_ROUNDING > ceiling:
            note("impossible", catnbr=catnbr, reason="std_dev", value=deviation,
                 students=students, ceiling=round(ceiling, 2))
            raise ImpossibleValue(
                f"{catnbr}: standard deviation {deviation} is above "
                f"{ceiling:.2f}, the most that "
                f"{students if students else 'any number of'} students scoring "
                f"between 0 and 4 could produce")
    low, mid, high = stats.get("p25"), stats.get("p50"), stats.get("p75")
    if None not in (low, mid, high) and not (float(low) <= float(mid) <= float(high)):
        note("impossible", catnbr=catnbr, reason="percentiles",
             value=f"{low}/{mid}/{high}")
        raise ImpossibleValue(f"{catnbr}: percentiles {low}/{mid}/{high} "
                              f"do not increase")

    # Every statistic zero means no letter grades were awarded - a credit/no
    # credit section - not a class where everyone scored zero.
    if stats and all(float(value) == 0.0 for value in stats.values()):
        return {}
    return stats


def grade_counts(reply, catnbr):
    """How many students earned each grade in the selected section.

    The worksheet is long form - one row per grade group - and repeats itself
    as a %all% totals block, which has to be skipped or every count doubles.
    Keeping only rows that name this course drops that block and any sibling
    course the response happens to carry. Grade groups the dashboard adds later
    fall into grade_other rather than being silently dropped."""
    counts = {}
    table = reply.worksheet(HEADCOUNT_SHEET)
    if table is None or not len(table):
        return counts

    for _, row in table.iterrows():
        if str(row.get("Catnbr-alias")) != catnbr:
            continue
        try:
            headcount = int(float(row.get("CNT(Emplid Proxy)-alias")))
        except (TypeError, ValueError):
            continue
        grade = str(row.get("Grade Group-alias", "")).strip().upper()
        column = GRADE_COLUMNS.get(grade, "grade_other")
        counts[column] = counts.get(column, 0) + headcount

    return counts


def record(finished, term, subject, catnbr, section, reply, tag=""):
    """Write one row, then mark it done.

    Both readings come from the response passed in, and worksheet() refuses a
    response that did not carry them, so nothing is ever marked on the strength
    of a reply that said nothing. mark_finished used to fire on that path too,
    which is what made a silent error permanent: the key was recorded, every
    later run skipped it, and no rerun could ever repair the row."""
    key = f"{term}|{subject}|{catnbr}|{section}"
    if key in finished:
        return

    # Take each sheet only if this response carried it, and never mind up a
    # value it did not. The two are sent independently: pinning a section of a
    # course where every section has the same spread of grades redraws the
    # headcount chart but leaves the GPA chart identical, so the GPA chart is
    # not resent - Fall 2025 ART 1080 has three sections all averaging 4.00.
    # Demanding both sheets threw the whole course away; carrying the last one
    # seen forward would be the stale read that fabricated rows in the first
    # place. Writing the half that arrived is the only honest option, and a
    # response carrying neither sheet is still a failure to retry.
    if not {GPA_SHEET, HEADCOUNT_SHEET} & reply.refreshed:
        raise NotRefreshed(f"neither {GPA_SHEET} nor {HEADCOUNT_SHEET} in this "
                           f"response (refreshed: {sorted(reply.refreshed)})")
    counts = grade_counts(reply, catnbr) if HEADCOUNT_SHEET in reply.refreshed else {}
    graded = sum(counts.get(column, 0) for column in GPA_BEARING)
    stats = (gpa_stats(reply, catnbr, graded)
             if GPA_SHEET in reply.refreshed else {})
    short_term, short_subject = shorten(term, subject)
    if stats or counts:      # nothing published stays unpublished, not invented
        write_csv_row({"term": short_term, "subject": short_subject,
                       "catnbr": catnbr, "section": section, **stats, **counts})
    mark_finished(key)
    finished.add(key)
    written = dict(counts)
    note("row", key=key, wrote=bool(stats or counts), stats=stats,
         counts=counts, refreshed=sorted(reply.refreshed),
         dictionary=reply.dictionary_size(), segments=len(reply.segments))
    print(f"{tag}  {short_subject} {catnbr}-{section}: "
          f"avg={stats.get('avg_gpa', '-')}")
    return written


def scrape_subject(session, finished, term, subject, tag=""):
    """Scrape one subject. True if every course came through cleanly."""
    subject_reply = session.select("subject", subject)
    courses = subject_reply.options("course")
    short_term, short_subject = shorten(term, subject)
    print(f"{tag}{short_term} / {short_subject}: {len(courses)} courses")
    if not courses:
        # A fresh response offering no courses is an answer, not a failure -
        # this subject simply ran nothing this term. Calling it a failure is
        # what left hundreds of subject-terms to be re-walked on every run,
        # so that a complete sweep could never actually finish.
        return True

    complete = True
    for catnbr in courses:
        try:
            if len(courses) == 1:
                # The subject offers one course, so the subject view is already
                # showing it and pinning it moves nothing - the dashboard sends
                # no headcount sheet back and the read fails, the same reason a
                # single-section course needs no section command.
                #
                # Reading it from the subject response instead is exact, not an
                # approximation: checked on eight courses across three subjects,
                # a subject-level read restricted to one course matches the
                # course-pinned read on every grade group and every statistic.
                # The restriction is what makes it safe - the response can carry
                # a sibling course the dropdown never mentions, because a course
                # under five students in every grade group is suppressed out of
                # both the headcount sheet and the dropdown while still
                # appearing in the GPA sheet, and its sections are listed too.
                # Fall 2022 ARAB is that shape: it offers only 1010 but carries
                # 2010, whose section 001 is in the list. Attributing by Catnbr
                # keeps 1010's numbers 1010's, and leaves 2010's section
                # yielding nothing rather than a row invented for 1010.
                reply = subject_reply
            else:
                reply = session.select("course", catnbr)
            sections = reply.options("section")
            if not sections:
                raise RuntimeError(f"no sections listed for {catnbr}")

            # The course-level response is already in hand, and its numbers
            # beat summing the sections: headcounts under 5 are suppressed per
            # section, so small grade groups vanish there but survive here.
            whole = record(finished, term, subject, catnbr,
                           COURSE_SECTION, reply, tag)
            parts = {}

            # The all-terms pass collects courses only. A section number is not
            # stable across terms - Fall's 001 and Spring's 001 are different
            # classes taught by different people - so "section 001 across all
            # terms" is not one thing and must not be written as if it were.
            if term == ALL_TERMS:
                continue

            if len(sections) == 1:
                # Every student in the course is in that one section, so the
                # course-level view already IS the section view. Pinning it
                # changes nothing on screen and the dashboard sends no
                # worksheet back at all - there is nothing to wait for and
                # nothing new to read, so write the row we already hold.
                record(finished, term, subject, catnbr, sections[0], reply, tag)
                continue

            for section in sections:
                key = f"{term}|{subject}|{catnbr}|{section}"
                if key in finished:
                    continue
                try:
                    reply = session.select("section", section)
                    note("section", key=key, path="direct",
                         refreshed=sorted(reply.refreshed), tiled=reply.tiled)
                    if not {GPA_SHEET, HEADCOUNT_SHEET} <= reply.refreshed:
                        # A chart the dashboard did not redraw is a chart it
                        # did not send. Ask again in the order that forces a
                        # redraw.
                        reply = session.reread_section(catnbr, section)
                        note("section", key=key, path="reread",
                             refreshed=sorted(reply.refreshed),
                             tiled=reply.tiled)
                    got = record(finished, term, subject, catnbr, section,
                                 reply, tag)
                except NotRefreshed as error:
                    # A section we cannot read is one section lost, not the
                    # whole course. Every step above can fail with the section
                    # still unread, and all three did in the log:
                    #
                    #   select()          22  the Section filter is missing
                    #                         from the response, or comes back
                    #                         reading [] for what was asked
                    #   record()           3  neither sheet was sent - the
                    #                         reread can return LESS than the
                    #                         direct read, which is how Spring
                    #                         2024 CERM 3270 died
                    #
                    # That is 25 of the 56 course losses in the log, and each
                    # one discarded every remaining section of its course too.
                    # The sections list comes from a subject-level response
                    # that spans courses, so some of these sections do not
                    # belong to this course at all and never will read.
                    #
                    # Retrying is already done: select() makes LOAD_ATTEMPTS of
                    # its own and the reread exists to force a redraw the
                    # direct read did not get. Once both have failed, leave the
                    # section unclaimed - record() marks nothing on this path -
                    # and let a later sweep have it on a fresh session.
                    #
                    # Falling back on an earlier reply is NOT the fix. Segments
                    # are shared and the server re-sends slot numbers it has
                    # already used, so decoding a held-back response after
                    # later commands have landed is the stale-dictionary read
                    # that fabricated rows before.
                    #
                    # Carrying on is safe even if the failure left the course
                    # unpinned: every read attributes by Catnbr rather than by
                    # what was last selected, which is the same rule that keeps
                    # Fall 2022 ARAB 1010's numbers off the 2010 the response
                    # also carries.
                    complete = False
                    note("skip", scope="section", term=term, subject=subject,
                         catnbr=catnbr, section=section, error=repr(error))
                    print(f"{tag}  ! {short_subject} {catnbr}-{section} "
                          f"skipped: {error!r}")
                    continue
                for grade, n in (got or {}).items():
                    parts[grade] = parts.get(grade, 0) + n

            # Sections cannot hold more students than the whole course. When
            # they do, something decoded wrong and said nothing about it -
            # this is the only trigger that fires on SILENT corruption.
            #
            # Only when the course actually reported counts. record() returns
            # None for a section already marked done and {} when the response
            # carried no headcount sheet, and an empty course bounds nothing:
            # every section then "exceeds" it and the dump is noise. Three of
            # the twelve in the log are that shape.
            if whole:
                over = [g for g, n in parts.items() if n > whole.get(g, 0)]
                if over:
                    note("sections_exceed_course", term=term,
                         subject=subject, catnbr=catnbr,
                         course=whole, sections=parts, over=over)
        except StaleDictionary as error:
            note("stale_dictionary", term=term, subject=subject,
                 catnbr=catnbr, error=str(error))
            raise
        except (*TRANSPORT_ERRORS, SessionWedged):
            raise            # connection gone, or the session has stopped
                             # answering - neither is about this course
        except ImpossibleValue as error:
            # A number that cannot be true of a GPA did not come from the field
            # it was read out of, which means this session's dictionary no
            # longer lines up with the responses indexing into it. That does
            # not heal, and skipping only the course did not stop it: across
            # the 16 drift events in the log the same sessions went on to write
            # 279 more rows - up to 59 from one - and those carry plausible
            # numbers that no invariant downstream can catch. So end the
            # subject here and retire the session.
            #
            # Retiring rather than raising keeps the sweep moving. Letting it
            # out would abort and requeue the term, and three of these courses
            # fail the same way every time they are read, so the term would
            # cycle for ever. close_session leaves exactly the state the next
            # subject expects - term pinned, nothing narrower - which is the
            # same handover sweep_term already makes between subjects, and the
            # subject stays unmarked so a rerun collects it whole.
            note("skip", scope="subject", term=term, subject=subject,
                 catnbr=catnbr, error=repr(error))
            print(f"{tag}! {short_subject} abandoned at {catnbr}: {error!r}")
            session.close_session()
            return False
        except Exception as error:
            complete = False
            # A skipped course is rare - five in the first day - and it never
            # heals itself: Fall 2020 MID E 2910 has failed the same way four
            # times and will keep failing, so the course is simply lost. Keep
            # the responses that led to it, since the same shape recurring is
            # the best chance of understanding it.
            note("skip", scope="course", term=term, subject=subject,
                 catnbr=catnbr, error=repr(error))
            print(f"{tag}  ! {short_subject} {catnbr} skipped: {error!r}")
    return complete


def sweep_term(session, finished, term, tag=""):
    """Every subject in one term, on one session."""
    session.select("term", term)

    # Pinning a term re-renders nothing, so that response carries no zones and
    # no filters - there is nothing in it to read and nothing to check it
    # against. The first real selection is where both the subject list and the
    # proof that the term applied have to come from.
    #
    # Reading the list here also scopes it to the term. The list offered before
    # any term is pinned belongs to the default term, and using it everywhere
    # both walked subjects that ran nothing in the term being swept and missed
    # subjects that ran only in older ones.
    reply = session.select("subject", BOOTSTRAP_SUBJECT)
    # With every term selected the filter echoes all eighteen values rather than
    # one, so there is nothing for confirm() to match against.
    if term != ALL_TERMS:
        reply.confirm("term", term)
    subjects = SUBJECTS or reply.options("subject")
    if not subjects:
        raise RuntimeError(f"no subjects offered for {term} - refusing to "
                           f"report it swept")

    for position, subject in enumerate(subjects):
        key = subject_key(term, subject)
        if key in finished:
            continue
        if position and position % SUBJECTS_PER_SESSION == 0:
            # Retire the session before its dictionary has time to drift. The
            # next select() rebuilds one and replays the term, so this costs a
            # bootstrap and nothing else.
            session.close_session()
        try:
            complete = scrape_subject(session, finished, term, subject, tag)
        except (*TRANSPORT_ERRORS, StaleDictionary, SessionWedged):
            # A dropped connection is not this subject's problem, and treating
            # it as one walked the rest of the alphabet failing every subject
            # in turn - a hundred-odd futile attempts, each with its own
            # retries and backoff, aimed at a host that had already stopped
            # answering. Let it out: the worker drops the session, waits, and
            # starts the term again on a fresh one.
            raise
        except Exception as error:
            note("skip", scope="subject", term=term, subject=subject,
                 error=repr(error))
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
                        # Put it back rather than drop it. Only a transport
                        # failure or a stale dictionary aborts a term, and both
                        # are transient, so another pass will carry on from the
                        # subjects already marked. Losing a whole term's
                        # coverage to a ten-minute outage is the worse outcome.
                        note("requeue", term=term, error=repr(error))
                        print(f"{tag}putting {term} back on the queue.")
                        time.sleep(SECONDS_BETWEEN_RETRIES * attempt)
                        pending.put(term)
                    else:
                        time.sleep(SECONDS_BETWEEN_RETRIES * attempt)
    finally:
        session.close()


def main():
    start_log()
    finished = load_progress()
    print(f"{len(finished)} sections already done; resuming.")

    terms = TERMS
    if not terms:                  # one throwaway session just to read the list
        scout = DashboardSession()
        try:
            terms = scout.connect().options("term")
        finally:
            scout.close()
    if not terms:
        raise RuntimeError("no terms offered - refusing to sweep nothing and "
                           "call it complete")

    # The all-terms pass rides the same queue as one more term. It costs about
    # a term's worth of work - one read per course, no sections - and a worker
    # picks it up like any other, so it needs no scheduling of its own. Queued
    # last because the per-term rows are the ones the site needs first.
    terms = list(terms) + [ALL_TERMS]

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
    note("complete", rows_marked=len(finished))
    print("SWEEP COMPLETE")


if __name__ == "__main__":
    main()
