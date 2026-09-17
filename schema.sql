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

  -- On a lab, discussion or field-work section, the lecture it registers you
  -- into: from the note on the lecture's class-list card, or data/companion-pairs.tsv.
  -- '*' means any lecture.
  pairs_with      TEXT,

  PRIMARY KEY (term, subject, course_number, section_number),
  FOREIGN KEY (subject, course_number)
    REFERENCES courses(subject, course_number)
);

-- The registrar's person id, from the instructor link on the class schedule.
-- The only reliable identity: names collide, and spellings vary by term.
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

-- Which people taught which section. Keyed on the uNID, never the name: two
-- people sharing a name would collide, and a name is a function of the id anyway.
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
-- The same measures at three grains, none derivable from another, because
-- groups under five students are suppressed at whatever grain is on screen:
--
--   section_grades      one section, one term
--   course_term_grades  one course, one term
--   course_grades       one course, all terms
--
-- Three tables rather than one, since they have three different keys and a SUM
-- over a mixed-grain table would count every student three times. None is
-- foreign-keyed to sections: the dashboard covers terms the crawl does not.

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
-- numeric code ('1268'), not the display term used elsewhere.
CREATE TABLE IF NOT EXISTS crawl_progress (
  term_code TEXT NOT NULL,
  campus    TEXT NOT NULL DEFAULT 'main',
  subject   TEXT NOT NULL,

  PRIMARY KEY (term_code, campus, subject)
);
