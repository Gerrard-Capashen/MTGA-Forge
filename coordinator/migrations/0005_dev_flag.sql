-- Developer flag: the client draws a flagged player's name in the orange "Mythic" colour (profile, VS screen, duel, challenge lobby).
-- Never set through the API; the project owner sets it by hand: UPDATE players SET dev = 1 WHERE name_lc = '...' AND tag = '...';
ALTER TABLE players ADD COLUMN dev INTEGER NOT NULL DEFAULT 0;
