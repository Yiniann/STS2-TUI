"""Focused tests for combat TUI layout and navigation."""

from __future__ import annotations

import curses
import importlib.util
import pathlib


ROOT = pathlib.Path(__file__).resolve().parents[1]
TUI_PATH = ROOT / "python" / "tui.py"

spec = importlib.util.spec_from_file_location("tui_module_for_tests", TUI_PATH)
tui = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(tui)


def _combat_tui():
    app = tui.Tui.__new__(tui.Tui)
    app.state = {
        "hand": [{"index": 0}],
        "enemies": [
            {"index": 0, "powers": [{"name": "Strength"}, {"name": "Weak"}]},
            {"index": 1, "powers": [{"name": "Vulnerable"}]},
        ],
    }
    app.cursor = 0
    app.combat_focus = "hand"
    app.enemy_cursor = 0
    app.enemy_power_cursor = 0
    app.target_cursor = None
    app.pending = None
    return app


def test_inline_rows_wraps_complete_power_labels():
    rows = tui._inline_rows(["Strength 2", "Weak 1", "Vulnerable 3"], width=20)

    assert [[text for _, text, _ in row] for row in rows] == [
        ["Strength 2", "Weak 1"],
        ["Vulnerable 3"],
    ]
    assert rows[0][1][2] == len("Strength 2") + 2


def test_upgrade_selection_title_says_upgrade_one_card():
    class Screen:
        def __init__(self):
            self.calls = []

        def getmaxyx(self):
            return 24, 100

        def addstr(self, y, x, text, attr=0):
            self.calls.append((y, x, text, attr))

    app = tui.Tui.__new__(tui.Tui)
    app.screen = Screen()
    app.state = {
        "decision": "card_select",
        "selection_kind": "upgrade",
        "min_select": 1,
        "max_select": 1,
        "cards": [{
            "index": 0,
            "id": "CARD.BASH",
            "name": "Bash",
            "cost": 2,
            "type": "Attack",
            "rarity": "Basic",
        }],
    }
    app.lang = "zh"
    app.t = lambda en, zh: zh
    app.cursor = 0
    app.selected = set()
    app.message = ""

    original_color_pair = tui.curses.color_pair
    tui.curses.color_pair = lambda number: number
    try:
        app._render_decision()
    finally:
        tui.curses.color_pair = original_color_pair

    rendered = "\n".join(call[2] for call in app.screen.calls)
    assert "升级一张牌" in rendered


def test_crystal_sphere_card_tokens_include_rarity():
    assert tui._crystal_sphere_token({"hidden": True, "item": "CardReward", "rarity": "Rare"}) == "?"
    assert tui._crystal_sphere_token({"hidden": False, "item": "CardReward", "rarity": "Common"}) == "C"
    assert tui._crystal_sphere_token({"hidden": False, "item": "CardReward", "rarity": "Uncommon"}) == "U"
    assert tui._crystal_sphere_token({"hidden": False, "item": "CardReward", "rarity": "Rare"}) == "★"


def test_crystal_sphere_big_preview_is_clipped_three_by_three_area():
    assert tui._crystal_sphere_preview_cells(5, 5, 11, 11, "Big") == {
        (x, y) for y in range(4, 7) for x in range(4, 7)
    }
    assert tui._crystal_sphere_preview_cells(0, 0, 11, 11, "Big") == {
        (0, 0), (1, 0), (0, 1), (1, 1),
    }
    assert tui._crystal_sphere_preview_cells(5, 5, 11, 11, "Small") == {(5, 5)}


def test_crystal_sphere_big_preview_highlights_hidden_neighbors_only():
    class Screen:
        def __init__(self):
            self.calls = []

        def getmaxyx(self):
            return 24, 80

        def addstr(self, y, x, text, attr=0):
            self.calls.append((y, x, text, attr))

    app = tui.Tui.__new__(tui.Tui)
    app.screen = Screen()
    app.state = {
        "decision": "crystal_sphere",
        "event_name": "Crystal Sphere",
        "remaining": 2,
        "tool": "Big",
        "width": 3,
        "height": 3,
        "rows": [
            [{"hidden": True} for _ in range(3)],
            [{"hidden": True}, {"hidden": True}, {"hidden": False, "item": "Gold", "is_good": True}],
            [{"hidden": True} for _ in range(3)],
        ],
    }
    app.lang = "en"
    app.t = lambda en, zh: en
    app.cursor = 4
    app.message = ""

    original_color_pair = tui.curses.color_pair
    tui.curses.color_pair = lambda number: number
    try:
        app._render_crystal_sphere()
    finally:
        tui.curses.color_pair = original_color_pair

    grid_calls = [call for call in app.screen.calls if 7 <= call[0] <= 9 and call[2] in {" ? ", " G "}]
    selected = next(call for call in grid_calls if call[0] == 8 and call[2] == " ? " and call[1] == 38)
    revealed_neighbor = next(call for call in grid_calls if call[0] == 8 and call[2] == " G ")
    previewed_neighbors = [call for call in grid_calls if call != selected and call != revealed_neighbor]

    assert selected[3] == curses.A_BOLD | 7
    assert len(previewed_neighbors) == 7
    assert all(call[3] == curses.A_BOLD | 6 for call in previewed_neighbors)
    assert revealed_neighbor[3] == 2


def test_relic_trade_details_show_both_relic_effects():
    class Screen:
        def __init__(self):
            self.calls = []

        def getmaxyx(self):
            return 30, 120

        def addstr(self, y, x, text, attr=0):
            self.calls.append((y, x, text, attr))

    app = tui.Tui.__new__(tui.Tui)
    app.screen = Screen()
    app.state = {
        "decision": "event_choice",
        "event_name": "Relic Trader",
        "description": "Choose a trade.",
        "options": [{
            "index": 0,
            "title": "Take the Middle One",
            "description": "Trade Joss Paper for Strawberry.",
            "effects": [
                {
                    "kind": "relic",
                    "role": "give",
                    "name": "Joss Paper",
                    "description": "Every {ExhaustAmount} cards, draw {Cards} card.",
                    "vars": {"ExhaustAmount": 5, "Cards": 1},
                },
                {
                    "kind": "relic",
                    "role": "receive",
                    "name": "Strawberry",
                    "description": "Raise Max HP by {MaxHp}.",
                    "vars": {"MaxHp": 7},
                },
            ],
        }],
    }
    app.lang = "en"
    app.t = lambda en, zh: en
    app.cursor = 0
    app.message = ""

    original_color_pair = tui.curses.color_pair
    tui.curses.color_pair = lambda number: number
    try:
        app._render_decision()
    finally:
        tui.curses.color_pair = original_color_pair

    rendered = "\n".join(call[2] for call in app.screen.calls)
    assert "Give - Joss Paper: Every 5 cards, draw 1 card." in rendered
    assert "Receive - Strawberry: Raise Max HP by 7." in rendered


def test_tab_enters_enemy_focus_and_arrows_select_enemy_status():
    app = _combat_tui()

    app._combat_key(9)
    assert app.combat_focus == "enemy"

    app._combat_key(curses.KEY_RIGHT)
    assert app.enemy_power_cursor == 1

    app._combat_key(curses.KEY_DOWN)
    assert app.enemy_cursor == 1
    assert app.enemy_power_cursor == 0

    app._combat_key(9)
    assert app.combat_focus == "hand"


def test_escape_returns_from_enemy_focus_to_hand():
    app = _combat_tui()
    app.combat_focus = "enemy"

    app._combat_key(27)

    assert app.combat_focus == "hand"


def test_enemy_intent_and_selected_power_description_render_in_place():
    class Screen:
        def __init__(self):
            self.calls = []

        def getmaxyx(self):
            return 32, 120

        def addstr(self, y, x, text, attr=0):
            self.calls.append((y, x, text, attr))

    app = tui.Tui.__new__(tui.Tui)
    app.screen = Screen()
    app.state = {
        "decision": "combat_play",
        "player": {"name": "Ironclad", "hp": 70, "max_hp": 80, "block": 0},
        "player_powers": [],
        "hand": [],
        "enemies": [{
            "index": 0,
            "name": "Cultist",
            "hp": 48,
            "max_hp": 48,
            "block": 0,
            "intents": [{"type": "Attack", "damage": 6, "hits": 2}],
            "powers": [
                {"name": "Ritual", "amount": 3, "description": "Gain {Amount} Strength."},
                {"name": "Weak", "amount": 1, "description": "Deal less damage."},
            ],
        }],
        "round": 1,
        "energy": 3,
        "max_energy": 3,
        "draw_pile_count": 5,
        "discard_pile_count": 0,
        "exhaust_pile_count": 0,
    }
    app.lang = "en"
    app.t = lambda en, zh: en
    app.cursor = 0
    app.combat_focus = "enemy"
    app.enemy_cursor = 0
    app.enemy_power_cursor = 0
    app.target_cursor = None
    app.pending = None
    app.message = ""

    original_color_pair = tui.curses.color_pair
    tui.curses.color_pair = lambda number: number
    try:
        app._render_combat()
    finally:
        tui.curses.color_pair = original_color_pair

    cultist = next(call for call in app.screen.calls if "Cultist" in call[2])
    intent = next(call for call in app.screen.calls if "Intent: ATK 6x2" in call[2])
    attack_badge = next(call for call in app.screen.calls if "ATK 6x2" in call[2] and call[0] > intent[0])
    ritual = next(call for call in app.screen.calls if call[2] == "Ritual 3")
    weak = next(call for call in app.screen.calls if call[2] == "Weak 1")
    description = next(call for call in app.screen.calls if "Gain 3 Strength." in call[2])

    assert intent[0] == cultist[0]
    assert intent[1] > cultist[1]
    assert attack_badge[0] == cultist[0] + 1
    assert ritual[0] == weak[0]
    assert description[0] > ritual[0]


def test_killing_card_thief_shows_returned_card_before_combat_rewards():
    stolen = {
        "id": "CARD.BLUDGEON",
        "name": "Bludgeon",
        "cost": 3,
        "type": "Attack",
        "rarity": "Uncommon",
        "upgraded": False,
    }
    defend = {
        "id": "CARD.DEFEND_IRONCLAD",
        "name": "Defend",
        "cost": 1,
        "type": "Skill",
        "rarity": "Basic",
        "upgraded": False,
    }
    old_state = {
        "decision": "combat_play",
        "player": {"deck": [defend], "gold": 0, "relics": [], "potions": []},
        "enemies": [{
            "index": 0,
            "name": "Vine Shambler",
            "powers": [{"name": "Swipe", "stolen_card": stolen}],
        }],
    }
    new_state = {
        "decision": "card_reward",
        "gold_earned": 10,
        "player": {"deck": [defend, stolen], "gold": 10, "relics": [], "potions": []},
        "enemies": [],
    }

    app = tui.Tui.__new__(tui.Tui)
    app.state = old_state
    app.send = lambda _cmd: new_state
    app.lang = "zh"
    app.t = lambda en, zh: zh
    app.cursor = 0
    app.combat_focus = "hand"
    app.enemy_cursor = 0
    app.enemy_power_cursor = 0
    app.target_cursor = None
    app.pending = None
    app.selected = set()
    app.overlay = None
    app.overlay_cursor = 0
    app.message = ""
    app.pending_reward = False
    app.pending_reward_gold = 0
    app.pending_reward_relics = []
    app.pending_reward_potions = []
    app.pending_reward_cards = []
    app.pending_reward_title = ""

    app._act("play_card", {"card_index": 0})

    assert app.overlay == "acquired"
    assert app.acquired_title == "卡牌已归还"
    assert app.acquired_cards == [stolen]
    assert app.pending_reward is True
    assert app.pending_reward_cards == []


def test_grabbed_card_returned_to_hand_is_detected():
    stolen = {
        "id": "CARD.BLUDGEON",
        "name": "Bludgeon",
        "upgraded": False,
    }
    old_state = {
        "decision": "combat_play",
        "player": {"deck": [stolen]},
        "hand": [],
        "draw_pile": [],
        "discard_pile": [],
        "exhaust_pile": [],
        "play_pile": [],
        "enemies": [{
            "index": 0,
            "powers": [{"name": "Grabbed", "stolen_card": stolen}],
        }],
    }
    new_state = {
        "decision": "combat_play",
        "player": {"deck": [stolen]},
        "hand": [stolen],
        "draw_pile": [],
        "discard_pile": [],
        "exhaust_pile": [],
        "play_pile": [],
        "enemies": [{"index": 0, "powers": []}],
    }

    returned = tui._returned_stolen_cards(old_state, new_state)

    assert len(returned) == 1
    assert returned[0]["name"] == "Bludgeon"
    assert returned[0]["_pile"] == "hand"
