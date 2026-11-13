## Task search — Russian search words so the owner finds pictures by any natural word

The owner types a word into Map's asset search; it matches names, details and tags. Inputs: `in/sets.json` (all 9 101 sets:
name, one, detail, tags, collection), `in/vocab_counts.json` (tag vocabulary with counts), `in/vocab_merge.json`.
Do (offline):
1. Build the list of distinct searchable concepts (names + key detail nouns). For each, list the Russian words a person
   would type that do NOT already occur in that set's name/detail/tags: synonyms («сундук» ↔ «ларь»), common short forms,
   plural/singular, ё/е, frequent misspellings, and the common English word the owner might type («barrel»). Only words
   that mean the same thing — no loose associations.
2. Deliver `aliases.json` ({"concept": [words...]}, concept = an existing name/tag value), `aliases_report.md` (counts, 40
   examples, what you rejected and why). Keep it conservative: a wrong alias makes wrong results appear.
3. Validate: every concept exists in sets.json names or tags; no alias maps to two different concepts unless both are
   genuinely right (list those separately).
