import csv
import json
import logging
import os
import re
import time
from urllib.parse import urlencode, quote

from playwright.sync_api import sync_playwright
from tableauscraper import TableauScraper
from tableauscraper import utils as tableau_utils



TEST_MODE = True
SECONDS_BETWEEN_LOADS = 1.5

TERMS = None
SUBJECTS = ["CS - Computer Science"]

DASHBOARD_URL = ("https://tableau.dashboard.utah.edu/t/UAIR/views/"
                 "OfficialUUGradeSummary_17192658137620/GradeSummary")

OUTPUT_FILE = "gpa.csv"
PROGRESS_FILE = "done.txt"


HEADCOUNT_SHEET = "Grade Tabs"
GPA_SHEET = "Avg GPA"
FILTER_LABELS = {
    "term": "Term/Snap",
    "subject": "Subject",
    "course": "Catnbr",
    "section": "Section",
}
STAT_COLUMNS = {
    "Average GPA": "avg_gpa",
    "25th Percentile": "p25",
    "50th Percentile": "p50",
    "75th Percentile": "p75",
    "Standard Deviation": "std_dev",
}
CSV_COLUMNS = ["term", "subject", "catnbr", "section",
               "avg_gpa", "p25", "p50", "p75", "std_dev"]


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


def parse_dashboard(response_bytes):
    """Raw bootstrap bytes -> a TableauScraper holding all worksheets."""
    frames = split_tableau_frames(response_bytes.decode("utf-8"))
    if len(frames) < 2:
        raise RuntimeError("response is not a tableau bootstrap")

    dashboard = TableauScraper(logLevel=logging.ERROR)
    dashboard.info = json.loads(frames[0])
    dashboard.data = json.loads(frames[1])
    secondary = dashboard.data.get("secondaryInfo", {})
    if "presModelMap" in secondary:
        dashboard.dataSegments = (
            secondary["presModelMap"]["dataDictionary"]
            ["presModelHolder"]["genDataDictionaryPresModel"]["dataSegments"])
    dashboard.dashboard = dashboard.info["sheetName"]
    dashboard.filters = tableau_utils.getFiltersForAllWorksheet(
        dashboard.logger, dashboard.data, dashboard.info,
        rootDashboard=dashboard.dashboard)
    return dashboard


def filter_options(dashboard, label):
    """Values currently available in one dropdown filter."""
    for f in dashboard.getWorksheet(HEADCOUNT_SHEET).getFilters():
        if f["column"] == label:
            return list(f["values"])
    return []


def url_field_names(dashboard):
    """Map each filter label to the field name usable as a URL parameter.
    (e.g. 'Section' -> 'SECTION', 'Term/Snap' -> 'Calculation_17186...')"""
    names = {}
    for f in dashboard.getWorksheet(HEADCOUNT_SHEET).getFilters():
        match = re.search(r"\[none:(.+?):nk\]", f.get("globalFieldName", ""))
        if match:
            names[f["column"]] = match.group(1)
    return names




class DashboardBrowser:
    """One headless Chromium; each load() navigates with filters in the URL
    and returns the parsed dashboard."""

    def __init__(self, playwright):
        self.browser = playwright.chromium.launch(headless=not TEST_MODE)
        context = self.browser.new_context()
        context.route(re.compile(r"\.(png|jpg|gif|woff2?|css|svg)(\?|$)"),
                      lambda route: route.abort())   # skip images/fonts
        self.page = context.new_page()
        self.captured = []
        self.page.on("response", self._capture_bootstrap)

    def _capture_bootstrap(self, response):
        if "bootstrapSession" in response.url and response.status == 200:
            try:
                self.captured.append(response.body())
            except Exception:
                pass

    def load(self, url_params):
        time.sleep(SECONDS_BETWEEN_LOADS)
        self.captured = []
        url = DASHBOARD_URL + "?:embed=y"
        if url_params:
            url += "&" + urlencode(url_params, quote_via=quote)
        self.page.goto(url, timeout=60_000)

        for _ in range(40): # wait up to ~20s
            if self.captured:
                self.page.wait_for_timeout(1500)
                break
            self.page.wait_for_timeout(500)
        if not self.captured:
            raise RuntimeError(f"dashboard never loaded for {url_params}")
        return parse_dashboard(max(self.captured, key=len))

    def close(self):
        try:
            self.browser.close()
        except Exception:
            pass




def load_progress():
    if not os.path.exists(PROGRESS_FILE):
        return set()
    with open(PROGRESS_FILE) as f:
        return set(line.strip() for line in f)


def mark_finished(key):
    with open(PROGRESS_FILE, "a") as f:
        f.write(key + "\n")


def write_csv_row(row):
    is_new_file = not os.path.exists(OUTPUT_FILE)
    with open(OUTPUT_FILE, "a", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=CSV_COLUMNS)
        if is_new_file:
            writer.writeheader()
        writer.writerow(row)



def shorten(term, subject):
    return term.replace(" End of Term", "").strip(), subject.split(" - ")[0].strip()


def gpa_stats(dashboard):
    """The GPA table's stats as a dict - empty for lab/discussion sections."""
    stats = {}
    table = dashboard.getWorksheet(GPA_SHEET).data
    if table is not None and len(table):
        for _, row in table.iterrows():
            column = STAT_COLUMNS.get(row.get("Measure Names-alias"))
            value = row.get("Measure Values-alias")
            if column and value not in (None, "", "%null%"):
                stats[column] = value
    return stats


def confirm_course_applied(dashboard, catnbr):
    """Guard against a filter silently not applying (burned us before)."""
    table = dashboard.getWorksheet(HEADCOUNT_SHEET).data
    if table is None or not len(table):
        return
    if not (table.astype(str) == catnbr).any().any():
        raise RuntimeError(f"course filter {catnbr} did not apply")


def sweep(browser, finished):
    dashboard = browser.load({})
    fields = url_field_names(dashboard)
    term_field = fields[FILTER_LABELS["term"]]
    subject_field = fields[FILTER_LABELS["subject"]]
    course_field = fields[FILTER_LABELS["course"]]
    section_field = fields[FILTER_LABELS["section"]]

    terms = TERMS or filter_options(dashboard, FILTER_LABELS["term"])
    if TEST_MODE:
        terms = terms[:1]

    for term in terms:
        if SUBJECTS:
            subjects = SUBJECTS
        else:
            dashboard = browser.load({term_field: term})
            subjects = filter_options(dashboard, FILTER_LABELS["subject"])
        if TEST_MODE:
            subjects = subjects[:1]

        for subject in subjects:
            dashboard = browser.load({term_field: term, subject_field: subject})
            courses = filter_options(dashboard, FILTER_LABELS["course"])
            short_term, short_subject = shorten(term, subject)
            print(f"{short_term} / {short_subject}: {len(courses)} courses")

            for catnbr in courses:
                pinned = {term_field: term, subject_field: subject,
                          course_field: catnbr}
                dashboard = browser.load(pinned)
                confirm_course_applied(dashboard, catnbr)
                sections = filter_options(dashboard, FILTER_LABELS["section"])

                for section in sections:
                    key = f"{term}|{subject}|{catnbr}|{section}"
                    if key in finished:
                        continue
                    dashboard = browser.load({**pinned, section_field: section})
                    confirm_course_applied(dashboard, catnbr)

                    row = {"term": short_term, "subject": short_subject,
                           "catnbr": catnbr, "section": section,
                           **gpa_stats(dashboard)}
                    write_csv_row(row)
                    mark_finished(key)
                    finished.add(key)
                    print(f"  {short_subject} {catnbr}-{section}: "
                          f"avg={row.get('avg_gpa', '-')}")



def main():
    finished = load_progress()
    print(f"{len(finished)} sections already done; resuming.")

    for attempt in range(1, 11):
        browser = None
        try:
            with sync_playwright() as playwright:
                browser = DashboardBrowser(playwright)
                sweep(browser, finished)
            print("SWEEP COMPLETE")
            return
        except KeyboardInterrupt:
            print("\ninterrupted - progress saved, rerun to resume.")
            return
        except Exception as error:
            print(f"error: {error!r}")
            wait = min(60 * attempt, 300)
            print(f"restarting browser in {wait}s (attempt {attempt}/10)")
            time.sleep(wait)
        finally:
            if browser:
                browser.close()
    print("too many failures - stopping. Rerun to resume.")


if __name__ == "__main__":
    main()