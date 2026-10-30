"""The city journal (journal.md in each city's data folder): read, add entries, edit, create, archive.

The journal is plain markdown the player can read and edit too. Sections (## headings): Charter (table),
Decision log (table, newest row first), Plans, Lessons, Session notes (bullets, newest first). Old finished
decisions and old session notes move to journal-archive.md so the journal Claude reads every session stays
short. Before every write the previous version is kept as journal.previous.md (one step of undo).
"""
from __future__ import annotations

import re
import shutil
from pathlib import Path

SECTIONS = ["Charter", "Decision log", "Plans", "Lessons", "Session notes"]
TABLE_SECTIONS = ("Charter", "Decision log")
DECISION_HEADER = "| Game month | Decision | Reason | Expected effect | Check in | Outcome |"
TABLE_SEP = "|---|---|---|---|---|---|"
ARCHIVE_NOTE = "Older finished decisions and session notes: journal-archive.md (read it only when the past matters)."
CHARTER_QUESTIONS = ["Financial risk", "Growth pace", "Citizens vs. cost", "Layout style", "Tie-breaker"]
DEFAULT_TIE_BREAKER = ("Domain order: Finance, Growth, Economy, Services, Utilities, Traffic, Transport, Environment, "
                       "Citizens, Progression")
TEMPLATE = Path(__file__).resolve().parent.parent / ".claude" / "skills" / "cs2-city-assistant" / "assets" / "journal-template.md"


def safe_folder_name(name: str) -> str:
    """The folder name the mod uses for a city (ModFiles.SafeFileName: invalid file name characters -> '_')."""
    return re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name)


def _write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        shutil.copyfile(path, path.with_name(path.stem + ".previous.md"))
    path.write_text(re.sub(r"\n{3,}", "\n\n", text.rstrip("\n")) + "\n", encoding="utf-8")


def _section_span(lines: list[str], name: str) -> tuple[int, int] | None:
    """(heading line, end line exclusive) of a '## name' section, or None."""
    start = next((i for i, ln in enumerate(lines) if ln.strip().lower() == f"## {name}".lower()), None)
    if start is None:
        return None
    end = next((i for i in range(start + 1, len(lines)) if lines[i].startswith("## ")), len(lines))
    return start, end


def _cell(text: str) -> str:
    return " ".join(str(text).split()).replace("|", "/")


def decision_row(game_month: str, decision: str, reason: str = "", expected_effect: str = "", check_in: str = "",
                 outcome: str = "") -> str:
    return "| " + " | ".join(_cell(c) for c in (game_month, decision, reason, expected_effect, check_in, outcome)) + " |"


def add_entry(path: Path, section: str, text: str) -> None:
    """Put one entry at the top of a section: a table row for the decision log, a bullet elsewhere."""
    canonical = next((s for s in SECTIONS if s.lower() == section.strip().lower()), None)
    if canonical is None or canonical == "Charter":
        raise ValueError(f"section must be one of: {', '.join(s for s in SECTIONS if s != 'Charter')} "
                         "(change charter rows with edit_journal).")
    if not path.exists():
        raise ValueError("This city has no journal yet: run the charter interview and create_journal first.")
    lines = path.read_text(encoding="utf-8").splitlines()
    span = _section_span(lines, canonical)
    if span is None:  # section missing: add it at the end
        lines += ["", f"## {canonical}", ""]
        if canonical == "Decision log":
            lines += [DECISION_HEADER, TABLE_SEP]
        span = _section_span(lines, canonical)
    start, end = span
    if canonical == "Decision log":
        row = text.strip()
        if not row.startswith("|"):
            raise ValueError("A decision-log entry is a table row: | Game month | Decision | Reason | Expected effect | Check in | Outcome |")
        sep = next((i for i in range(start, end) if re.match(r"^\|\s*-{3}", lines[i])), None)
        if sep is None:
            lines[end:end] = ["", DECISION_HEADER, TABLE_SEP]
            sep = end + 2
        lines.insert(sep + 1, row)
    else:
        bullet = text.strip()
        bullet = bullet if bullet.startswith("- ") else "- " + bullet
        body = list(range(start + 1, end))
        for i in reversed(body):  # drop the template's placeholder
            if lines[i].strip() == "- (none yet)":
                del lines[i]
                end -= 1
        first = next((i for i in range(start + 1, end) if lines[i].startswith("- ")), None)
        if first is None:
            insert_at = end
            while insert_at > start + 1 and not lines[insert_at - 1].strip():
                insert_at -= 1
            lines[insert_at:insert_at] = ["", bullet, ""] if canonical == "Session notes" else ["", bullet]
        else:
            lines[first:first] = [bullet, ""] if canonical == "Session notes" else [bullet]
    _write(path, "\n".join(lines))


def edit(path: Path, old_text: str, new_text: str) -> None:
    """Replace one exact piece of the journal (a charter row, a decision's outcome, a finished plan...)."""
    if not path.exists():
        raise ValueError("This city has no journal yet.")
    text = path.read_text(encoding="utf-8")
    count = text.count(old_text)
    if count != 1:
        raise ValueError(f"old_text must appear exactly once in the journal (found {count} times); quote more of the line.")
    _write(path, text.replace(old_text, new_text))


def create(path: Path, city: str, game_month: str, answers: dict[str, str]) -> None:
    """A new journal from the skill's template with the player's charter answers ('not asked yet' if skipped)."""
    if path.exists():
        raise ValueError("This city already has a journal; change charter rows with edit_journal.")
    template = TEMPLATE.read_text(encoding="utf-8") if TEMPLATE.exists() else (
        "# <City name> - City journal\n\nRead at the start of every session; updated at the end. Newest entries at "
        "the top of each log section.\n\n## Charter\n\n| Question | Current choice | Since |\n|---|---|---|\n"
        + "".join(f"| {q} | <player's answer> | <game month> |\n" for q in CHARTER_QUESTIONS)
        + f"\n## Decision log\n\n{DECISION_HEADER}\n{TABLE_SEP}\n\n## Plans\n\n- (none yet)\n\n## Lessons\n\n- (none yet)\n\n"
          "## Session notes\n")
    lines = template.replace("<City name>", city).splitlines()
    for i, ln in enumerate(lines):
        m = re.match(r"^\|\s*([^|]+?)\s*\|\s*<player's answer[^|]*\|\s*<game month>\s*\|$", ln)
        if m and m.group(1) in CHARTER_QUESTIONS:
            q = m.group(1)
            answer = (answers.get(q) or "").strip()
            if q == "Tie-breaker" and answer.lower() in ("default", "keep default", "keep the default domain order"):
                answer = DEFAULT_TIE_BREAKER
            lines[i] = f"| {q} | {_cell(answer) if answer else 'not asked yet'} | {game_month if answer else '-'} |"
    _write(path, "\n".join(lines))


def _month_key(text: str) -> int | None:
    m = re.match(r"\s*(\d{4})-(\d{2})", text)
    return int(m.group(1)) * 12 + int(m.group(2)) - 1 if m else None


def archive(path: Path, current_month: str, older_than_months: int = 12, keep_session_notes: int = 10) -> dict[str, int]:
    """Move finished decisions (Outcome filled, not 'Pending...') older than `older_than_months` and all but the newest
    `keep_session_notes` session notes to journal-archive.md (newest first there too). Charter, open decisions,
    plans and lessons stay."""
    if not path.exists():
        raise ValueError("This city has no journal yet.")
    now = _month_key(current_month)
    if now is None:
        raise ValueError("current_month must look like 2031-04.")
    lines = path.read_text(encoding="utf-8").splitlines()
    moved_rows: list[str] = []
    span = _section_span(lines, "Decision log")
    if span:
        start, end = span
        keep = []
        for i in range(start, end):
            ln = lines[i]
            cells = [c.strip() for c in ln.strip().strip("|").split("|")] if ln.startswith("|") else []
            month = _month_key(cells[0]) if len(cells) >= 6 else None
            finished = len(cells) >= 6 and bool(cells[5]) and not cells[5].lower().startswith(("pending", "open", "not yet", "-"))
            if month is not None and finished and now - month >= older_than_months:
                moved_rows.append(ln)
            else:
                keep.append(ln)
        lines[start:end] = keep
    moved_notes: list[str] = []
    span = _section_span(lines, "Session notes")
    if span:
        start, end = span
        bullets = [i for i in range(start + 1, end) if lines[i].startswith("- ")]
        if len(bullets) > keep_session_notes:
            cut = bullets[keep_session_notes]
            moved_notes = [ln for ln in lines[cut:end] if ln.strip()]
            lines[cut:end] = []
    if not moved_rows and not moved_notes:
        return {"decisionsMoved": 0, "sessionNotesMoved": 0}
    if ARCHIVE_NOTE not in lines:
        heading = next((i for i, ln in enumerate(lines) if ln.startswith("# ")), -1)
        lines[heading + 1:heading + 1] = ["", ARCHIVE_NOTE]
    _write(path, "\n".join(lines))

    archive_path = path.with_name("journal-archive.md")
    city = path.parent.name
    if archive_path.exists():
        alines = archive_path.read_text(encoding="utf-8").splitlines()
    else:
        alines = [f"# {city} - Journal archive", "", "Moved out of journal.md to keep it short. Newest first.", "",
                  "## Decision log", "", DECISION_HEADER, TABLE_SEP, "", "## Session notes", ""]
    if moved_rows:
        start, end = _section_span(alines, "Decision log")
        sep = next(i for i in range(start, end) if re.match(r"^\|\s*-{3}", alines[i]))
        alines[sep + 1:sep + 1] = moved_rows
    if moved_notes:
        start, end = _section_span(alines, "Session notes")
        first = next((i for i in range(start + 1, end) if alines[i].startswith("- ")), end)
        block = []
        for ln in moved_notes:  # a blank line between bullets, none inside a multi-line bullet
            if ln.startswith("- ") and block:
                block.append("")
            block.append(ln)
        alines[first:first] = block + [""]
    _write(archive_path, "\n".join(alines))
    return {"decisionsMoved": len(moved_rows), "sessionNotesMoved": len([n for n in moved_notes if n.startswith("- ")])}
