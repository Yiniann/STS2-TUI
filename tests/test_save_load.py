"""Regression tests for native save/load behavior."""

import json

from conftest import Game


def test_load_map_save_does_not_retrigger_neow(tmp_path):
    save_path = tmp_path / "map_select.save"

    game = Game()
    try:
        state = game.start(seed="sl1")
        state = game.skip_neow(state)
        assert state["decision"] == "map_select"

        save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert save_result["type"] == "save_result"
        assert save_result["success"] is True
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["decision"] == "map_select"
    finally:
        game.close()


def test_load_pre_neow_save_preserves_neow_choice(tmp_path):
    save_path = tmp_path / "pre_neow.save"

    game = Game()
    try:
        state = game.start(seed="sl2")
        assert state["decision"] == "event_choice"

        save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert save_result["type"] == "save_result"
        assert save_result["success"] is True
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["decision"] == "event_choice"
    finally:
        game.close()


def test_combat_save_restarts_same_room_with_precombat_hp(tmp_path):
    save_path = tmp_path / "combat_restart.save"

    game = Game()
    try:
        state = game.skip_neow(game.start(seed="sl1"))
        game.set_player(hp=40, max_hp=80)
        pick = state["choices"][0]
        state = game.act("select_map_node", col=pick["col"], row=pick["row"])
        assert state["decision"] == "combat_play"
        expected_enemies = [enemy["name"] for enemy in state["enemies"]]

        for _ in range(5):
            state = game.act("end_turn")
            if state.get("player", {}).get("hp", 40) < 40:
                break
        assert state["player"]["hp"] < 40

        save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert save_result["success"] is True
        saved = json.loads(save_path.read_text())
        assert saved["players"][0]["current_hp"] == 40
        act = saved["current_act_index"]
        assert len(saved["visited_map_coords"]) == len(saved["map_point_history"][act]) + 1
    finally:
        game.close()

    for _ in range(2):
        game = Game()
        try:
            state = game.send({"cmd": "load_save", "path": str(save_path)})
            assert state["decision"] == "combat_play"
            assert state["player"]["hp"] == 40
            assert [enemy["name"] for enemy in state["enemies"]] == expected_enemies
        finally:
            game.close()
