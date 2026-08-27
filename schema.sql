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

  component      TEXT,
  type           TEXT,
  units          INTEGER,
  location       TEXT,
  times          TEXT,

  -- Refreshed on its own pass; negative when a section is over-enrolled.
  seats_available INTEGER,
  seats_updated   TEXT,


  gpa_avg        REAL,
  gpa_p25        REAL,
  gpa_p50        REAL,
  gpa_p75        REAL,
  gpa_std_dev    REAL,

  -- Headcount per grade, same source, filled by the same loader.
  grade_a        INTEGER,
  grade_b        INTEGER,
  grade_c        INTEGER,
  grade_d        INTEGER,
  grade_e        INTEGER,
  grade_cr       INTEGER,
  grade_nc       INTEGER,
  grade_w        INTEGER,
  grade_other    INTEGER,

  PRIMARY KEY (term, subject, course_number, section_number),
  FOREIGN KEY (subject, course_number)
    REFERENCES courses(subject, course_number)
);

CREATE TABLE IF NOT EXISTS section_instructors (
  term           TEXT NOT NULL,
  subject        TEXT NOT NULL,
  course_number  TEXT NOT NULL,
  section_number TEXT NOT NULL,
  instructor     TEXT NOT NULL,

  PRIMARY KEY (term, subject, course_number, section_number, instructor),
  FOREIGN KEY (term, subject, course_number, section_number)
    REFERENCES sections(term, subject, course_number, section_number)
);

CREATE INDEX IF NOT EXISTS idx_section_instructors_instructor
  ON section_instructors(instructor);

CREATE INDEX IF NOT EXISTS idx_sections_term
  ON sections(term);

CREATE INDEX IF NOT EXISTS idx_sections_course
  ON sections(subject, course_number);

-- Whole-course grade data, one row per course per term - what the dashboard
-- reports with no section pinned. This is not the sum of the sections: the
-- under-5 headcount suppression is applied per section, so small grade groups
-- vanish there but survive here (MATH 1220 Fall 2020 loses ~10% of its
-- students, mostly D/E/W, when you add the sections up).
CREATE TABLE IF NOT EXISTS course_grades (
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

  PRIMARY KEY (term, subject, course_number),
  FOREIGN KEY (subject, course_number)
    REFERENCES courses(subject, course_number)
);

CREATE INDEX IF NOT EXISTS idx_course_grades_course
  ON course_grades(subject, course_number);

CREATE TABLE IF NOT EXISTS crawl_progress (
  term_code TEXT NOT NULL,
  subject   TEXT NOT NULL,

  PRIMARY KEY (term_code, subject)
);
