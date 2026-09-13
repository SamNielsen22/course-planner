PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS courses (
  subject        TEXT NOT NULL,
  course_number  TEXT NOT NULL,
  title          TEXT,
  description    TEXT,
  prerequisites  TEXT,
  requirement_designation TEXT,
  PRIMARY KEY (subject, course_number)
);

CREATE TABLE IF NOT EXISTS sections (
  term           TEXT NOT NULL,
  subject        TEXT NOT NULL,
  course_number  TEXT NOT NULL,
  section_number TEXT NOT NULL,

  -- Which of the registrar's three schedules listed it: main (Salt Lake, with
  -- the Sandy, St. George and Herriman classes), uac (Asia Campus) or online
  -- (UOnline Programs). A section is listed in exactly one.
  campus         TEXT NOT NULL DEFAULT 'main',

  component      TEXT,
  type           TEXT,
  units          INTEGER,
  location       TEXT,
  times          TEXT,

  -- Refreshed on its own pass; negative when a section is over-enrolled.
  seats_available INTEGER,
  seats_updated   TEXT,

  -- The enrollment side, from the registrar's sections table on the same
  -- pass: the class number a student registers with, how many may enrol,
  -- how many have, how many are waiting. has_waitlist is the class list's
  -- yes/no - a full section with no wait list cannot be waited on.
  class_number    TEXT,
  enrollment_cap  INTEGER,
  enrolled        INTEGER,
  waitlist        INTEGER,
  has_waitlist    INTEGER,

  PRIMARY KEY (term, subject, course_number, section_number),
  FOREIGN KEY (subject, course_number)
    REFERENCES courses(subject, course_number)
);

-- The registrar's own person id, lifted from the instructor link on the class
-- schedule (profiles.faculty.utah.edu/u0171400). It is the only reliable way to
-- tell people apart: names collide - two different "Nguyen, Khoi" both teach
-- MATH - and the same person's name is spelled differently across terms. Never
-- infer identity from the name.
CREATE TABLE IF NOT EXISTS instructors (
  unid          TEXT PRIMARY KEY,
  display_name  TEXT NOT NULL,

  -- Inferred from what they teach, and stored rather than derived on read.
  -- Working it out means grouping every section a person has ever taught, which
  -- is far too much to repeat for each of 24 cards on a search page. It only
  -- changes when the schedule is re-crawled.
  department    TEXT
);

CREATE INDEX IF NOT EXISTS idx_instructors_department
  ON instructors(department);

-- A pure relationship: which people taught which section. The name is NOT here.
-- It is a function of the uNID, so keeping it per-row stored the same 7,908
-- names 143,026 times, and let the registrar's spelling of one person vary
-- between their own sections.
--
-- The key is the uNID, not the name. Keyed on the name, two different people
-- who happen to share one silently collide and the second is dropped - and
-- these sections run large enough for that to be reachable: NURS 7701-001 has
-- thirty instructors.
CREATE TABLE IF NOT EXISTS section_instructors (
  term            TEXT NOT NULL,
  subject         TEXT NOT NULL,
  course_number   TEXT NOT NULL,
  section_number  TEXT NOT NULL,
  instructor_unid TEXT NOT NULL REFERENCES instructors(unid),

  PRIMARY KEY (term, subject, course_number, section_number, instructor_unid),
  FOREIGN KEY (term, subject, course_number, section_number)
    REFERENCES sections(term, subject, course_number, section_number)
);

CREATE INDEX IF NOT EXISTS idx_section_instructors_unid
  ON section_instructors(instructor_unid);

CREATE INDEX IF NOT EXISTS idx_sections_term
  ON sections(term);

CREATE INDEX IF NOT EXISTS idx_sections_course
  ON sections(subject, course_number);

-- ---------------------------------------------------------------- grades
--
-- The dashboard publishes the same fourteen measures at three grains, and none
-- of them can be derived from another. Any grade group under five students is
-- suppressed, and the rule is applied to whatever is on screen - so summing a
-- finer grain always undercounts, and always in the same direction.
--
--   section_grades      one section, one term
--   course_term_grades  one course, one term    (sum of its sections + what
--                                                suppression hid from them:
--                                                MATH 1220 Fall 2020 loses
--                                                ~10% of students that way)
--   course_grades       one course, all terms   (sum of its terms + what
--                                                suppression hid from those:
--                                                CS 2420 gains 17 students,
--                                                every one a D, E or W)
--
-- Three tables rather than one with a grain column, because the three have
-- three different natural keys and mixing them makes the obvious aggregate
-- silently wrong - SUM over a mixed-grain table counts every student three
-- times. The repeated measure columns are the ordinary cost of a base fact
-- plus its rollups.
--
-- None is foreign-keyed to sections. Grades come from the Tableau dashboard and
-- sections from the class schedule; the dashboard covers terms the schedule
-- crawl does not, and keying to sections meant those rows had nowhere to land.

CREATE TABLE IF NOT EXISTS section_grades (
  term           TEXT NOT NULL,
  subject        TEXT NOT NULL,
  course_number  TEXT NOT NULL,
  section_number TEXT NOT NULL,

  gpa_avg        REAL,
  gpa_p25        REAL,
  gpa_p50        REAL,
  gpa_p75        REAL,
  gpa_std_dev    REAL,

  grade_a        INTEGER,
  grade_b        INTEGER,
  grade_c        INTEGER,
  grade_d        INTEGER,
  grade_e        INTEGER,
  grade_cr       INTEGER,
  grade_nc       INTEGER,
  grade_w        INTEGER,
  grade_other    INTEGER,

  PRIMARY KEY (term, subject, course_number, section_number)
);

CREATE TABLE IF NOT EXISTS course_term_grades (
  term           TEXT NOT NULL,
  subject        TEXT NOT NULL,
  course_number  TEXT NOT NULL,

  gpa_avg        REAL,
  gpa_p25        REAL,
  gpa_p50        REAL,
  gpa_p75        REAL,
  gpa_std_dev    REAL,

  grade_a        INTEGER,
  grade_b        INTEGER,
  grade_c        INTEGER,
  grade_d        INTEGER,
  grade_e        INTEGER,
  grade_cr       INTEGER,
  grade_nc       INTEGER,
  grade_w        INTEGER,
  grade_other    INTEGER,

  PRIMARY KEY (term, subject, course_number)
);

CREATE TABLE IF NOT EXISTS course_grades (
  subject        TEXT NOT NULL,
  course_number  TEXT NOT NULL,

  gpa_avg        REAL,
  gpa_p25        REAL,
  gpa_p50        REAL,
  gpa_p75        REAL,
  gpa_std_dev    REAL,

  grade_a        INTEGER,
  grade_b        INTEGER,
  grade_c        INTEGER,
  grade_d        INTEGER,
  grade_e        INTEGER,
  grade_cr       INTEGER,
  grade_nc       INTEGER,
  grade_w        INTEGER,
  grade_other    INTEGER,

  PRIMARY KEY (subject, course_number)
);

CREATE INDEX IF NOT EXISTS idx_section_grades_course
  ON section_grades(subject, course_number);

CREATE INDEX IF NOT EXISTS idx_course_term_grades_course
  ON course_term_grades(subject, course_number);

-- Which subjects have been crawled, per term. term_code is the registrar's
-- numeric code ('1268'), not the display term ('Fall2026') used everywhere
-- else - the schedule site is driven by the code.
CREATE TABLE IF NOT EXISTS crawl_progress (
  term_code TEXT NOT NULL,
  campus    TEXT NOT NULL DEFAULT 'main',
  subject   TEXT NOT NULL,

  PRIMARY KEY (term_code, campus, subject)
);
