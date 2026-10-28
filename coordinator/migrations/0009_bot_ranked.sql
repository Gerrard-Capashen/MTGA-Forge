-- Ranked-queue bot matches (src/rank.ts POST /me/rank/bot): the start is recorded so a result can only count after the minimum length.
CREATE TABLE bot_ranked (
  match_id  TEXT NOT NULL,
  player_id TEXT NOT NULL,
  queue     TEXT NOT NULL,
  pool      TEXT NOT NULL,
  started   INTEGER NOT NULL,
  PRIMARY KEY (match_id, player_id)
);
