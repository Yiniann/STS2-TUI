"""Tests for CLI play helpers."""

from __future__ import annotations

import importlib.util
import pathlib
import sys


ROOT = pathlib.Path(__file__).resolve().parents[1]
PLAY_PATH = ROOT / "python" / "play.py"

sys.path.insert(0, str(ROOT / "python"))
spec = importlib.util.spec_from_file_location("play_module_for_tests", PLAY_PATH)
play = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(play)


def test_quit_always_uses_active_save():
    path = play._quit_with_save(None, "Ironclad", "seed123")

    assert path == play.ACTIVE_SAVE_PATH


def test_quit_does_not_save_finished_run():
    path = play._quit_with_save(None, "Ironclad", "seed123", run_finished=True)

    assert path is None


def test_autosave_checkpoint_commands():
    ok = {"decision": "combat_play"}
    assert play._should_autosave({"cmd": "start_run"}, ok)
    assert play._should_autosave({"cmd": "load_save"}, ok)
    assert play._should_autosave(
        {"cmd": "action", "action": "select_map_node"}, ok
    )
    assert play._should_autosave(
        {"cmd": "action", "action": "proceed"}, {"decision": "map_select"}
    )
    assert not play._should_autosave(
        {"cmd": "action", "action": "play_card"}, ok
    )
    assert not play._should_autosave(
        {"cmd": "start_run"}, {"type": "error"}
    )


def test_delete_active_save(monkeypatch, tmp_path):
    active = tmp_path / "current_run.save"
    active.write_text("save")
    monkeypatch.setattr(play, "ACTIVE_SAVE_PATH", str(active))

    assert play._delete_active_save()
    assert not active.exists()
    assert not play._delete_active_save()
