PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS courses (
  subject        TEXT NOT NULL,
  course_number  TEXT NOT NULL,
  title          TEXT,
  description    TEXT,
  prerequisites  TEXT,
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
