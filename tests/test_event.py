"""Tests for events."""
import pytest


class TestNeowEvent:
    def test_neow_is_first_event(self, game):
        state = game.start(seed="ne1")
        assert state["decision"] == "event_choice"
        assert "Neow" in str(state.get("event_name", ""))

    def test_neow_options(self, game):
        state = game.start(seed="ne2")
        for opt in state["options"]:
            assert "title" in opt
            assert isinstance(opt["title"], str)
            assert "is_locked" in opt

    def test_neow_option_vars(self, game):
        state = game.start(seed="ne3")
        for opt in state["options"]:
            if opt.get("vars"):
                for k, v in opt["vars"].items():
                    assert isinstance(v, (int, float))

    def test_choose_neow(self, game):
        state = game.start(seed="ne4")
        opts = [o for o in state["options"] if not o.get("is_locked")]
        state = game.act("choose_option", option_index=opts[0]["index"])
        assert state.get("decision") is not None


class TestEventDescriptions:
    def test_no_ismultiplayer_tag(self, game):
        state = game.start(seed="ed1")
        for opt in state.get("options", []):
            d = opt.get("description") or ""
            assert "IsMultiplayer" not in d

    def test_future_of_potions_exposes_option_localization_vars(self, game):
        state = game.start(seed="future-potions-vars")
        game.skip_neow(state)
        state = game.enter_room("event", event="THE_FUTURE_OF_POTIONS")

        option = state["options"][0]
        assert not option["title_vars"]["Rarity"].startswith("POTION_RARITY.")
        assert not option["description_vars"]["Potion"].endswith(".title")
        assert not option["description_vars"]["Rarity"].startswith("CARD_RARITY.")
        assert not option["description_vars"]["Type"].startswith("CARD_TYPE.")


class TestTrial:
    def test_accept_opens_the_guilty_or_innocent_decision(self, game):
        game.start(seed="trial-headless-accept")
        state = game.enter_room("event", event="TRIAL")
        accept = next(
            option for option in state["options"]
            if option["text_key"].endswith(".ACCEPT")
        )

        state = game.act("choose_option", option_index=accept["index"])

        assert state["decision"] == "event_choice"
        assert {option["text_key"].rsplit(".", 1)[-1] for option in state["options"]} == {
            "GUILTY", "INNOCENT",
        }

    def test_skipping_first_of_two_card_rewards_keeps_the_second(self, game):
        game.start(seed="trial-headless-accept")
        state = game.enter_room("event", event="TRIAL")
        accept = next(option for option in state["options"] if option["text_key"].endswith(".ACCEPT"))
        state = game.act("choose_option", option_index=accept["index"])
        guilty = next(option for option in state["options"] if option["text_key"].endswith(".GUILTY"))

        state = game.act("choose_option", option_index=guilty["index"])
        first_cards = [card["id"] for card in state["cards"]]

        assert state["decision"] == "card_reward"
        assert state["alternatives"] == []

        state = game.act("skip_card_reward")

        assert state["decision"] == "card_reward"
        assert [card["id"] for card in state["cards"]] != first_cards


class TestCrystalSphere:
    def test_revealed_card_rewards_include_rarity(self, game):
        state = game.skip_neow(game.start(seed="crystal-rarity-1"))
        state = game.enter_room("event", event="CRYSTAL_SPHERE")
        payment = next(
            option for option in state["options"]
            if option["text_key"].endswith(".PAYMENT_PLAN")
        )
        state = game.act("choose_option", option_index=payment["index"])

        assert state["decision"] == "crystal_sphere"
        assert all(
            cell.get("rarity") is None
            for row in state["rows"] for cell in row
            if cell["hidden"]
        )

        cards = []
        for x, y in [(2, 2), (5, 2), (8, 2), (2, 5), (5, 5), (8, 5)]:
            state = game.act("crystal_sphere_set_tool", tool="big")
            if not state["rows"][y][x]["hidden"]:
                hidden = [
                    cell for row in state["rows"] for cell in row if cell["hidden"]
                ]
                x, y = hidden[0]["x"], hidden[0]["y"]
            state = game.act("crystal_sphere_reveal", x=x, y=y)
            if state.get("decision") != "crystal_sphere":
                break
            cards = [
                cell for row in state["rows"] for cell in row
                if not cell["hidden"] and cell.get("item") == "CardReward"
            ]
            if cards:
                break

        assert cards
        assert all(card["rarity"] in {"Common", "Uncommon", "Rare"} for card in cards)


class TestDenseVegetation:
    @pytest.mark.parametrize(("starting_hp", "expected_hp"), [(50, 74), (80, 80)])
    def test_rest_advances_to_fight(self, game, starting_hp, expected_hp):
        game.start(seed="dense-vegetation-rest")
        game.set_player(hp=starting_hp)
        state = game.enter_room("event", event="DENSE_VEGETATION")

        rest = next(
            option for option in state["options"]
            if option["text_key"].endswith(".REST")
        )
        state = game.act("choose_option", option_index=rest["index"])

        assert state["decision"] == "event_choice"
        assert state["player"]["hp"] == expected_hp
        assert len(state["options"]) == 1
        assert state["options"][0]["text_key"].endswith(".FIGHT")

        state = game.act("choose_option", option_index=state["options"][0]["index"])
        assert state["decision"] == "combat_play"


class TestPotionCourier:
    def test_ransack_full_potion_slots_can_replace(self, game):
        game.start(seed="potion-courier-full")
        game.set_player(potions=["COLORLESS_POTION", "POWER_POTION", "BLOCK_POTION"])
        state = game.enter_room("event", event="POTION_COURIER")
        ransack = next(
            option for option in state["options"]
            if option["text_key"].endswith(".RANSACK")
        )

        state = game.act("choose_option", option_index=ransack["index"])

        assert state["decision"] == "potion_replace"
        assert state["incoming_potion"]["name"]
        assert len(state["potions"]) == 3

        state = game.act("replace_potion", potion_index=0)

        assert state["decision"] == "map_select"
        assert len(state["player"]["potions"]) == 3
        assert all(potion["id"] != "POTION.COLORLESS_POTION"
                   for potion in state["player"]["potions"])

    def test_ransack_full_potion_slots_can_be_skipped(self, game):
        game.start(seed="potion-courier-skip")
        original_ids = ["COLORLESS_POTION", "POWER_POTION", "BLOCK_POTION"]
        game.set_player(potions=original_ids)
        state = game.enter_room("event", event="POTION_COURIER")
        ransack = next(
            option for option in state["options"]
            if option["text_key"].endswith(".RANSACK")
        )
        state = game.act("choose_option", option_index=ransack["index"])

        state = game.act("skip_potion_reward")

        assert state["decision"] == "map_select"
        assert [potion["id"] for potion in state["player"]["potions"]] == [
            f"POTION.{potion_id}" for potion_id in original_ids
        ]
