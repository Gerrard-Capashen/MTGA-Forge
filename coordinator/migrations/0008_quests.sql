-- Daily quests (src/quests.ts): three slots per player. A slot keeps its quest until it is finished; a finished quest is replaced on the
-- next UTC day, an empty slot is filled at once. Each quest can be swapped once a day (swap_day = the day it was swapped).
CREATE TABLE quests (
  player_id    TEXT NOT NULL,
  slot         INTEGER NOT NULL,      -- 0..2
  quest_id     TEXT NOT NULL,         -- this assignment (a swap or a new day makes a new one)
  kind         TEXT NOT NULL,         -- the quest definition (quests.ts POOL)
  goal         INTEGER NOT NULL,
  progress     INTEGER NOT NULL DEFAULT 0,
  xp           INTEGER NOT NULL,
  assigned_day INTEGER NOT NULL,
  swap_day     INTEGER NOT NULL DEFAULT -1,
  done_day     INTEGER,               -- set when finished (the XP is paid then, mastery xp_events key "quest:<quest_id>")
  PRIMARY KEY (player_id, slot)
);
CREATE UNIQUE INDEX quests_id ON quests (quest_id);

-- One progress post per (match, player): a match's counters count once.
CREATE TABLE quest_events (
  award     TEXT NOT NULL,
  player_id TEXT NOT NULL,
  created   INTEGER NOT NULL,
  PRIMARY KEY (award, player_id)
);
