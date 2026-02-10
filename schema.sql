CREATE TABLE IF NOT EXISTS courses (
  id TEXT PRIMARY KEY,                 -- "CS:3505"
  subject TEXT NOT NULL,               -- "CS"
  catalog_number TEXT NOT NULL,        -- "3505"
  title TEXT,
  units REAL,
  description TEXT,
  prereq_text TEXT,                    -- raw prereq string as displayed
  UNIQUE(subject, catalog_number)
);

CREATE TABLE IF NOT EXISTS sections (
  id TEXT PRIMARY KEY,                 -- "spring-2026:CS:3505:001"
  course_id TEXT NOT NULL,             -- "CS:3505"
  term TEXT NOT NULL,                  -- "spring-2026"
  section_number TEXT NOT NULL,        -- "001"
  class_number TEXT,                   -- registration class number / CRN-like
  instructor TEXT,
  component TEXT,                      -- "LEC"/"LAB"/"DIS"
  modality TEXT,                       -- "in_person"/"online"/"hybrid"
  seats_available INTEGER,
  seats_capacity INTEGER,
  seats_enrolled INTEGER,
  prereq_text TEXT,                    -- optional copy if shown at section level
  updated_at TEXT NOT NULL,
  FOREIGN KEY(course_id) REFERENCES courses(id)
);

CREATE INDEX IF NOT EXISTS idx_sections_term_course
  ON sections(term, course_id);

CREATE TABLE IF NOT EXISTS meetings (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  section_id TEXT NOT NULL,
  days_mask INTEGER NOT NULL,          -- Mo=1 Tu=2 We=4 Th=8 Fr=16 Sa=32 Su=64
  start_min INTEGER NOT NULL,          -- minutes since midnight
  end_min INTEGER NOT NULL,
  location_raw TEXT,
  schedule_raw TEXT,                   -- e.g., "Th/09:40AM-10:30AM"
  FOREIGN KEY(section_id) REFERENCES sections(id)
);

CREATE INDEX IF NOT EXISTS idx_meetings_section
  ON meetings(section_id);