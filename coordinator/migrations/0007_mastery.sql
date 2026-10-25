-- Mastery (src/mastery.ts): one endless XP track; every level gives one cosmetic of src/mastery-track.json (the order is the same
-- for every player). The track never resets (no season key).

-- xp = total XP ever; level = levels finished (= track items granted). day / day_xp: the UTC day of the last award and XP awarded that day (daily cap).
CREATE TABLE progress (
  player_id  TEXT PRIMARY KEY,
  xp         INTEGER NOT NULL DEFAULT 0,
  level      INTEGER NOT NULL DEFAULT 0,
  day        INTEGER NOT NULL DEFAULT 0,
  day_xp     INTEGER NOT NULL DEFAULT 0,
  version    INTEGER NOT NULL DEFAULT 0,
  last_award TEXT,                    -- the award key of the last change (with version: proof that THIS batch's update ran)
  updated    INTEGER NOT NULL
);

-- One row per (award, player): a match pays once. award = a match id (online, from the verdict) or "bot:<id>" (a local bot match).
-- xp = what was paid after the daily cap (0 when capped); asked = before the cap.
CREATE TABLE xp_events (
  award     TEXT NOT NULL,
  player_id TEXT NOT NULL,
  source    TEXT NOT NULL,            -- 'match' | 'bot'
  asked     INTEGER NOT NULL,
  xp        INTEGER NOT NULL,
  seq       INTEGER NOT NULL,         -- progress.version after this event (NOT NULL makes a stale write fail the batch)
  created   INTEGER NOT NULL,
  PRIMARY KEY (award, player_id)
);

-- The ownership ledger: what a player was granted (the always-owned set is not stored). source: 'mastery:<level>' (later 'rank:...', 'event:...').
CREATE TABLE cosmetic_grants (
  player_id   TEXT NOT NULL,
  cosmetic_id TEXT NOT NULL,
  kind        TEXT NOT NULL,          -- avatar | sleeve | pet | emote | title
  source      TEXT NOT NULL,
  granted_at  INTEGER NOT NULL,
  PRIMARY KEY (player_id, cosmetic_id)
);
