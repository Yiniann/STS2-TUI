#!/usr/bin/env python3
"""Curses frontend for the sts2 headless JSON protocol."""

import curses
import unicodedata


def _wlen(text):
    return sum(2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1 for ch in str(text))


def _clip(text, width):
    if width <= 0:
        return ""
    out = []
    used = 0
    for ch in str(text).replace("\n", " "):
        cw = 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
        if used + cw > width:
            break
        out.append(ch)
        used += cw
    return "".join(out)


def _wrap(text, width):
    if not text or width <= 1:
        return []

    def split_wide(line):
        wrapped = []
        remaining = line
        while remaining:
            used = 0
            last_space = -1
            end = 0
            for index, ch in enumerate(remaining):
                char_width = 2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1
                if used + char_width > width:
                    break
                used += char_width
                end = index + 1
                if ch.isspace():
                    last_space = end
            if end == len(remaining):
                wrapped.append(remaining.rstrip())
                break
            if last_space > 0:
                wrapped.append(remaining[:last_space].rstrip())
                remaining = remaining[last_space:].lstrip()
            else:
                wrapped.append(remaining[:end])
                remaining = remaining[end:]
        return wrapped or [""]

    lines = []
    for paragraph in str(text).splitlines() or [""]:
        lines.extend(split_wide(paragraph))
    return lines


def _name(value):
    return str(value) if value is not None else "?"


def _error_message(value, translate):
    message = _name(value)
    translations = {
        "Not enough gold": ("Not enough gold", "金币不足"),
        "Not enough gold for Crystal Sphere divination": (
            "Not enough gold for Crystal Sphere divination",
            "金币不足，无法进行水晶球占卜",
        ),
    }
    localized = translations.get(message)
    return translate(*localized) if localized else message


def _card_cost(card, field="cost"):
    flag = "cost_is_x" if field == "cost" else f"{field}_is_x"
    return "X" if card.get(flag) else card.get(field, "?")


def _modifier_badge(name, amount):
    if not name:
        return ""
    suffix = ""
    if isinstance(amount, (int, float)) and amount:
        suffix = f" {amount:+g}"
    return f" [{_name(name)}{suffix}]"


def _card_title(card):
    if not isinstance(card, dict):
        return _name(card)
    title = _name(card.get("name")) + ("+" if card.get("upgraded") else "")
    title += _modifier_badge(card.get("enchantment"), card.get("enchantment_amount"))
    title += _modifier_badge(card.get("affliction"), card.get("affliction_amount"))
    return title


def _card_playable(card, energy):
    return bool(card.get("can_play")) and (
        card.get("cost_is_x") or card.get("cost", 99) <= energy
    )


def _description(item, *, in_combat=False, lang="zh"):
    text = item.get("description", "") if isinstance(item, dict) else ""
    if not isinstance(text, str):
        return ""
    import re
    text = re.sub(r"\[/?[^\]]+\]", "", text)
    stats = {
        str(k).lower(): v
        for k, v in (
            item.get("stats")
            or item.get("description_vars")
            or item.get("vars")
            or {}
        ).items()
    }
    stats.setdefault("cardtype", item.get("type"))
    stats.setdefault("targettype", item.get("target_type"))
    upgraded = bool(item.get("upgraded"))
    energy_unit = "E" if lang == "en" else ("E/能量" if lang == "both" else "能量")

    def value_for(name):
        return stats.get(name.strip().lower())

    def icon_value(name, formatter, unit):
        value = value_for(name)
        if value is not None:
            return f"{value}{unit}"
        match = re.search(r"\((\d+)\)", formatter)
        count = int(match.group(1)) if match else None
        # energyPrefix is the unit/icon itself. Keeping the singular form without
        # a leading 1 also makes localized text such as "0{energyPrefix...}" read correctly.
        if name.lower() == "energyprefix" and count == 1:
            return unit
        return f"{count}{unit}" if count is not None else unit

    def replace_expression(match):
        expression = match.group(1)
        if ":" not in expression:
            if expression.lower() == "singlestaricon":
                return "★"
            value = value_for(expression)
            return str(value) if value is not None else ""

        name, formatter = expression.split(":", 1)
        lowered = name.lower()
        if lowered == "incombat":
            branches = formatter.split("|")
            return branches[0] if in_combat else (branches[1] if len(branches) > 1 else "")
        if lowered == "ifupgraded":
            if formatter.startswith("show:"):
                formatter = formatter[len("show:"):]
            branches = formatter.split("|")
            if upgraded:
                return branches[0]
            return branches[1] if len(branches) > 1 else ""

        if formatter.startswith("energyIcons"):
            return icon_value(name, formatter, energy_unit)
        if formatter.startswith("starIcons"):
            return icon_value(name, formatter, "★")

        value = value_for(name)
        if formatter.startswith("plural:"):
            forms = formatter[len("plural:"):].split("|")
            if not forms:
                return ""
            try:
                singular = int(value) == 1
            except (TypeError, ValueError):
                singular = False
            return forms[0] if singular or len(forms) == 1 else forms[1]

        choose_match = re.match(r"choose\(([^)]*)\):(.*)", formatter, re.DOTALL)
        if choose_match:
            choices = choose_match.group(1).split("|")
            branches = choose_match.group(2).split("|")
            selected = next((i for i, choice in enumerate(choices) if str(value).lower() == choice.lower()), None)
            if selected is None:
                try:
                    selected = int(value)
                except (TypeError, ValueError):
                    selected = len(branches) - 1
            return branches[selected] if 0 <= selected < len(branches) else branches[-1]

        if formatter.startswith(("diff", "inverseDiff")):
            return str(value) if value is not None else ""
        if "|" in formatter:
            branches = formatter.split("|")
            return branches[0] if value else branches[-1]
        return str(value) if value is not None else ""

    # SmartFormat expressions can be nested. Resolve the innermost pair first,
    # then let the enclosing conditional consume the resulting plain text.
    for _ in range(12):
        resolved, count = re.subn(r"\{([^{}]*)\}", replace_expression, text)
        text = resolved
        if count == 0:
            break
    return text.strip()


def _card_selection_title(state, translate, *, lang="zh"):
    raw_title = state.get("selection_title")
    if raw_title:
        title = _description({
            "description": raw_title,
            "description_vars": state.get("selection_title_vars") or {},
        }, lang=lang)
        if title:
            return title

    kind = state.get("selection_kind")
    minimum = state.get("min_select", 1)
    maximum = state.get("max_select", 1)
    if minimum == maximum == 1:
        en_count, zh_count = "a card", "一张牌"
    elif minimum == maximum:
        en_count, zh_count = f"{minimum} cards", f"{minimum} 张牌"
    elif minimum == 0:
        en_count, zh_count = f"up to {maximum} cards", f"至多 {maximum} 张牌"
    else:
        en_count, zh_count = f"{minimum}-{maximum} cards", f"{minimum}-{maximum} 张牌"
    fallback = {
        "upgrade": (f"Choose {en_count} to upgrade", f"选择{zh_count}升级"),
        "remove": (f"Choose {en_count} to remove", f"选择{zh_count}移除"),
        "transform": (f"Choose {en_count} to transform", f"选择{zh_count}变化"),
        "enchant": (f"Choose {en_count} to enchant", f"选择{zh_count}附魔"),
        "discard": ("Choose cards to discard", "选择要丢弃的牌"),
        "exhaust": ("Choose cards to exhaust", "选择要消耗的牌"),
    }
    if kind in fallback:
        return translate(*fallback[kind])
    return translate("Choose cards", "选择卡牌")


def _intent(enemy, translate=None):
    tr = translate or (lambda en, zh: en)
    parts = []
    labels = {
        "Defend": tr("DEF", "守势"),
        "Buff": tr("BUFF", "强化"),
        "Debuff": tr("DEBUFF", "策略"),
        "DebuffStrong": tr("DEBUFF", "策略"),
        "CardDebuff": tr("MALICE", "恶意"),
        "Heal": tr("HEAL", "回复"),
        "Escape": tr("ESCAPE", "懦弱"),
        "Sleep": tr("SLEEP", "沉睡"),
        "StatusCard": tr("STATUS", "策略"),
        "Stun": tr("STUN", "击晕"),
        "Summon": tr("SUMMON", "召唤"),
        "DeathBlow": tr("DEATH BLOW", "濒死一击"),
        "Unknown": tr("UNKNOWN", "未知"),
    }
    for intent in enemy.get("intents") or []:
        kind = intent.get("type", "Unknown")
        if kind == "Attack":
            damage = intent.get("damage")
            hits = intent.get("hits", 1)
            parts.append(f"{tr('ATK', '攻势')} {damage if damage is not None else '?'}" + (f"x{hits}" if hits and hits > 1 else ""))
        else:
            parts.append(labels.get(kind, kind.upper()))
    return " + ".join(parts) or "?"


def _effect_text(effect, *, in_combat=False, lang="zh"):
    if not isinstance(effect, dict):
        return ""
    name = _name(effect.get("name"))
    description = _description(effect, in_combat=in_combat, lang=lang)
    if description:
        return f"{name}: {description}"
    return name


def _card_modifier_details(card, *, in_combat=False, lang="zh"):
    if not isinstance(card, dict):
        return []
    details = []
    fields = (
        ("enchantment", "enchantment_amount", "enchantment_info", ("Enchant", "附魔")),
        ("affliction", "affliction_amount", "affliction_info", ("Affliction", "负面附魔")),
    )
    for name_field, amount_field, info_field, labels in fields:
        name = card.get(name_field)
        info = card.get(info_field)
        if not isinstance(info, dict) and name:
            info = {
                "name": name,
                "amount": card.get(amount_field),
                "vars": {"Amount": card.get(amount_field)},
            }
        text = _effect_text(info, in_combat=in_combat, lang=lang)
        if text:
            label = labels[0] if lang == "en" else labels[1] if lang == "zh" else "/".join(labels)
            details.append(f"{label} - {text}")
    return details


def _card_keyword_details(card, translate=None):
    if not isinstance(card, dict):
        return []
    tr = translate or (lambda en, zh: en)
    keyword_text = {
        "Eternal": (
            "Eternal: Cannot be removed or transformed from your Deck.",
            "永恒：无法从你的牌组中移除或变化。",
        ),
        "Unplayable": (
            "Unplayable: This card cannot be played.",
            "不能被打出：这张牌无法被打出。",
        ),
        "Ethereal": (
            "Ethereal: If this card is in your Hand at the end of the turn, Exhaust it.",
            "虚无：如果这张牌在回合结束时仍在手牌中，将其消耗。",
        ),
        "Exhaust": (
            "Exhaust: Removed until the end of combat.",
            "消耗：在战斗结束前移除。",
        ),
        "Innate": (
            "Innate: Start each combat with this card in your Hand.",
            "固有：每场战斗开始时，这张牌会出现在你的手牌中。",
        ),
        "Retain": (
            "Retain: This card is not discarded at the end of the turn.",
            "保留：这张牌不会在回合结束时被弃掉。",
        ),
        "Sly": (
            "Sly: If discarded from your Hand before the end of your turn, play it for free.",
            "奇巧：如果这张牌在回合结束前从手牌中被丢弃，则免费将其打出。",
        ),
    }
    details = []
    for keyword in card.get("keywords") or []:
        text = keyword_text.get(keyword)
        if text:
            details.append(tr(*text))
        else:
            details.append(f"{tr('Keyword', '关键词')}：{keyword}")
    return details


def _card_has_keyword(card, keyword):
    if not isinstance(card, dict):
        return False
    expected = keyword.casefold()
    return any(str(value).casefold() == expected for value in card.get("keywords") or [])


def _power_line(power):
    amount = power.get("amount", 0)
    suffix = f" {amount}" if amount else ""
    return f"{_name(power.get('name'))}{suffix}"


def _power_description(power, *, lang="zh"):
    if not isinstance(power, dict):
        return ""
    item = dict(power)
    variables = dict(power.get("vars") or {})
    amount = power.get("amount")
    if amount is not None:
        variables.setdefault("Amount", abs(amount) if isinstance(amount, (int, float)) else amount)
    item["vars"] = variables
    description = _description(item, in_combat=True, lang=lang)
    stolen_card = power.get("stolen_card")
    if isinstance(stolen_card, dict):
        stolen_text = (
            f"Stolen card: {_card_title(stolen_card)}"
            if lang == "en"
            else f"已偷走：{_card_title(stolen_card)}"
        )
        description = f"{description}  {stolen_text}" if description else stolen_text
    return description


def _inline_rows(items, width, gap=2):
    """Lay out indexed labels from left to right, wrapping at display width."""
    if width <= 0:
        return []
    rows = []
    row = []
    used = 0
    for index, value in enumerate(items):
        text = _clip(value, width)
        text_width = _wlen(text)
        if row and used + gap + text_width > width:
            rows.append(row)
            row = []
            used = 0
        x = used + (gap if row else 0)
        row.append((index, text, x))
        used = x + text_width
    if row:
        rows.append(row)
    return rows


def _crystal_sphere_token(cell):
    if cell.get("hidden", True):
        return "?"
    if cell.get("item") == "CardReward":
        return {
            "Common": "C",
            "Uncommon": "U",
            "Rare": "★",
        }.get(cell.get("rarity"), "C")
    return {
        "Curse": "X",
        "Gold": "G",
        "Potion": "P",
        "Relic": "R",
    }.get(cell.get("item"), ".")


def _crystal_sphere_preview_cells(x, y, width, height, tool):
    radius = 1 if tool == "Big" else 0
    return {
        (cell_x, cell_y)
        for cell_y in range(max(0, y - radius), min(height, y + radius + 1))
        for cell_x in range(max(0, x - radius), min(width, x + radius + 1))
    }


def _newly_stolen_cards(old_state, new_state):
    """Return cards that appeared on enemy steal powers after an action."""
    old_cards = _stolen_cards_by_enemy(old_state)
    return [
        card for key, card in _stolen_cards_by_enemy(new_state).items()
        if key not in old_cards
    ]


def _stolen_cards_by_enemy(state):
    result = {}
    for enemy in state.get("enemies") or []:
        enemy_key = enemy.get("index", enemy.get("name"))
        for power in enemy.get("powers") or []:
            card = power.get("stolen_card")
            if not isinstance(card, dict):
                continue
            key = (enemy_key, power.get("name"), _deck_card_key(card))
            result[key] = card
    return result


def _returned_stolen_cards(
    old_state, new_state, added_deck_cards=None, added_combat_cards=None,
):
    """Match vanished enemy steal powers with cards restored to deck or combat piles."""
    old_stolen = list(_stolen_cards_by_enemy(old_state).values())
    new_stolen_counts = {}
    for card in _stolen_cards_by_enemy(new_state).values():
        key = _deck_card_key(card)
        new_stolen_counts[key] = new_stolen_counts.get(key, 0) + 1

    no_longer_stolen = []
    for card in old_stolen:
        key = _deck_card_key(card)
        if new_stolen_counts.get(key, 0) > 0:
            new_stolen_counts[key] -= 1
        else:
            no_longer_stolen.append(card)

    deck_candidates = added_deck_cards
    if deck_candidates is None:
        deck_candidates = _deck_card_changes(old_state, new_state)[1]
    combat_candidates = added_combat_cards
    if combat_candidates is None:
        combat_candidates = _added_combat_cards(old_state, new_state)
    candidates = {}
    # Prefer combat copies because they include the destination pile for the prompt.
    for card in list(combat_candidates) + list(deck_candidates):
        key = _deck_card_key(card)
        candidates.setdefault(key, []).append(card)

    returned = []
    for card in no_longer_stolen:
        key = _deck_card_key(card)
        if candidates.get(key):
            returned.append(candidates[key].pop(0))
    return returned


def _without_matching_cards(cards, excluded):
    excluded_counts = {}
    for card in excluded:
        key = _deck_card_key(card)
        excluded_counts[key] = excluded_counts.get(key, 0) + 1
    remaining = []
    for card in cards:
        key = _deck_card_key(card)
        if excluded_counts.get(key, 0) > 0:
            excluded_counts[key] -= 1
        else:
            remaining.append(card)
    return remaining


def _deck_card_key(card):
    return (
        card.get("id") or ("name", card.get("name")),
        bool(card.get("upgraded")),
        card.get("enchantment"),
        card.get("enchantment_amount"),
        card.get("affliction"),
        card.get("affliction_amount"),
    )


def _deck_card_changes(old_state, new_state):
    """Return removed and added deck cards, including upgrades/enchantments."""
    old_deck = (old_state.get("player") or {}).get("deck") or []
    new_deck = (new_state.get("player") or {}).get("deck") or []
    old_counts = {}
    for card in old_deck:
        key = _deck_card_key(card)
        old_counts[key] = old_counts.get(key, 0) + 1

    added = []
    for card in new_deck:
        key = _deck_card_key(card)
        if old_counts.get(key, 0) > 0:
            old_counts[key] -= 1
        else:
            added.append(card)

    new_counts = {}
    for card in new_deck:
        key = _deck_card_key(card)
        new_counts[key] = new_counts.get(key, 0) + 1
    removed = []
    for card in old_deck:
        key = _deck_card_key(card)
        if new_counts.get(key, 0) > 0:
            new_counts[key] -= 1
        else:
            removed.append(card)
    return removed, added


def _added_deck_cards(old_state, new_state):
    return _deck_card_changes(old_state, new_state)[1]


def _added_player_items(old_state, new_state, field):
    """Return newly present relics or potions, preserving display order and duplicates."""
    old_items = (old_state.get("player") or {}).get(field) or []
    new_items = (new_state.get("player") or {}).get(field) or []
    old_counts = {}
    for item in old_items:
        key = item.get("id") or (item.get("name"), item.get("description"))
        old_counts[key] = old_counts.get(key, 0) + 1
    added = []
    for item in new_items:
        key = item.get("id") or (item.get("name"), item.get("description"))
        if old_counts.get(key, 0) > 0:
            old_counts[key] -= 1
        else:
            added.append(item)
    return added


def _added_combat_cards(old_state, new_state):
    """Find cards created during combat, ignoring ordinary moves between piles."""
    zones = ("hand", "draw_pile", "discard_pile", "exhaust_pile", "play_pile")

    def key(card):
        return (
            card.get("id") or ("name", card.get("name")),
            bool(card.get("upgraded")),
            card.get("enchantment"),
            card.get("affliction"),
        )

    old_counts = {}
    for zone in zones:
        for card in old_state.get(zone) or []:
            card_key = key(card)
            old_counts[card_key] = old_counts.get(card_key, 0) + 1

    added = []
    for zone in zones:
        for card in new_state.get(zone) or []:
            card_key = key(card)
            if old_counts.get(card_key, 0) > 0:
                old_counts[card_key] -= 1
            else:
                found = dict(card)
                found["_pile"] = zone
                added.append(found)
    return added


def _card_name_counts(cards):
    names = []
    counts = {}
    for card in cards:
        name = _name(card.get("name"))
        if name not in counts:
            names.append(name)
            counts[name] = 0
        counts[name] += 1
    return ", ".join(name + (f" x{counts[name]}" if counts[name] > 1 else "") for name in names)


def _upgraded_card_preview(card):
    """Merge after_upgrade data into a card-shaped object for normal rendering."""
    after = card.get("after_upgrade") or {}
    if not after:
        return card
    preview = dict(card)
    for field in ("cost", "cost_is_x", "stats", "description"):
        if after.get(field) is not None:
            preview[field] = after[field]
    preview["upgraded"] = True
    keywords = list(card.get("keywords") or [])
    removed = set(after.get("removed_keywords") or [])
    keywords = [keyword for keyword in keywords if keyword not in removed]
    for keyword in after.get("added_keywords") or []:
        if keyword not in keywords:
            keywords.append(keyword)
    preview["keywords"] = keywords
    return preview


class SetupTui:
    """Pre-run settings screen for interactive games."""

    CHARACTERS = [
        ("Ironclad", "The Ironclad", "铁甲战士"),
        ("Silent", "The Silent", "静默猎手"),
        ("Defect", "The Defect", "故障机器人"),
        ("Regent", "The Regent", "储君"),
        ("Necrobinder", "The Necrobinder", "亡灵契约师"),
    ]
    LANGUAGES = [
        ("zh", "Chinese", "中文"),
        ("en", "English", "英文"),
        ("both", "Bilingual", "双语"),
    ]

    def __init__(self, screen, character, ascension, lang, seed, log_enabled,
                 saves=None, active_save=None):
        self.screen = screen
        self.character = character
        self.ascension = ascension
        self.lang = lang
        self.seed = seed
        self.log_enabled = log_enabled
        self.saves = list(saves or [])
        self.active_save = active_save
        self.row = 0
        self.view = "active" if active_save else "setup"
        self.save_cursor = 0
        self._init_screen()

    def t(self, en, zh):
        if self.lang == "en":
            return en
        if self.lang == "both":
            return f"{en} / {zh}"
        return zh

    def _init_screen(self):
        curses.curs_set(0)
        self.screen.keypad(True)
        try:
            curses.use_default_colors()
        except curses.error:
            pass
        curses.start_color()
        for pair, fg, bg in (
            (1, curses.COLOR_CYAN, -1),
            (2, curses.COLOR_WHITE, curses.COLOR_BLUE),
            (3, curses.COLOR_BLACK, curses.COLOR_WHITE),
            (4, curses.COLOR_YELLOW, -1),
            (5, curses.COLOR_GREEN, -1),
        ):
            try:
                curses.init_pair(pair, fg, bg)
            except curses.error:
                pass

    def add(self, y, x, text, attr=0, width=None):
        h, w = self.screen.getmaxyx()
        if y < 0 or y >= h or x < 0 or x >= w - 1:
            return
        available = w - x - 1
        if width is not None:
            available = min(available, max(0, width))
        try:
            self.screen.addstr(y, x, _clip(text, available), attr)
        except curses.error:
            pass

    def box(self, y, x, h, w, title=""):
        max_h, max_w = self.screen.getmaxyx()
        h, w = min(h, max_h - y), min(w, max_w - x)
        if h < 2 or w < 2:
            return
        self.add(y, x, "+" + "-" * (w - 2) + "+", width=w)
        for line in range(y + 1, y + h - 1):
            self.add(line, x, "|" + " " * (w - 2) + "|", width=w)
        self.add(y + h - 1, x, "+" + "-" * (w - 2) + "+", width=w)
        if title:
            self.add(y, x + 2, f" {title} ", curses.A_BOLD, w - 4)

    def _character_label(self, entry):
        _, en, zh = entry
        return self.t(en, zh)

    def _language_label(self, entry):
        _, en, zh = entry
        return f"{en} / {zh}" if self.lang == "both" else (en if self.lang == "en" else zh)

    def _selected(self, row, text, width):
        attr = curses.color_pair(3) | curses.A_BOLD if self.row == row else curses.A_BOLD
        marker = "> " if self.row == row else "  "
        return marker + _clip(text, max(0, width - 2)), attr

    def render(self):
        if self.view in ("active", "abandon-active"):
            self._render_active_save()
            return
        if self.view == "saves":
            self._render_saves()
            return

        self.screen.erase()
        h, w = self.screen.getmaxyx()
        if h < 24 or w < 76:
            self.add(2, 2, self.t("Terminal too small (minimum 76x24)", "终端太小（至少 76x24）"), curses.A_BOLD)
            self.add(h - 2, 2, self.t("Resize terminal or press Q to quit", "请放大终端，或按 Q 退出"), curses.A_REVERSE)
            self.screen.refresh()
            return

        panel_w = min(96, w - 6)
        panel_x = (w - panel_w) // 2
        panel_h = min(27, h - 4)
        panel_y = max(1, (h - panel_h) // 2)
        self.box(panel_y, panel_x, panel_h, panel_w, self.t("Game Setup", "游戏设置"))

        title = self.t("SLAY THE SPIRE 2", "杀戮尖塔 2")
        self.add(panel_y + 2, panel_x + max(2, (panel_w - _wlen(title)) // 2), title,
                 curses.A_BOLD | curses.color_pair(1), panel_w - 4)
        author = "By Y1niann"
        self.add(panel_y + 3, panel_x + max(2, (panel_w - _wlen(author)) // 2), author,
                 curses.A_DIM, panel_w - 4)

        inner_x = panel_x + 4
        inner_w = panel_w - 8
        y = panel_y + 4
        label, attr = self._selected(0, self.t("Character", "英雄"), 18)
        self.add(y, inner_x, label, attr, 18)
        choices_x = inner_x + 18
        current_idx = next((i for i, c in enumerate(self.CHARACTERS) if c[0] == self.character), 0)
        available = inner_w - 18
        char_name = self._character_label(self.CHARACTERS[current_idx])
        self.add(y, choices_x, f"<  [{char_name}]  >    {current_idx + 1}/{len(self.CHARACTERS)}",
                 curses.color_pair(4) | curses.A_BOLD, available)

        y += 2
        label, attr = self._selected(1, self.t("Ascension", "渐进难度"), 18)
        self.add(y, inner_x, label, attr, 18)
        asc_text = f"<  A{self.ascension}  >"
        self.add(y, choices_x, asc_text, curses.color_pair(4) | curses.A_BOLD, available)
        self.add(y + 1, choices_x, self.t("Range 0-10; higher levels add cumulative challenges.",
                                         "范围 0-10；等级越高，挑战会逐级叠加。"), curses.A_DIM, available)

        y += 3
        label, attr = self._selected(2, self.t("Language", "语言"), 18)
        self.add(y, inner_x, label, attr, 18)
        lang_idx = next((i for i, item in enumerate(self.LANGUAGES) if item[0] == self.lang), 0)
        lang_name = self._language_label(self.LANGUAGES[lang_idx])
        self.add(y, choices_x, f"<  [{lang_name}]  >", curses.color_pair(4) | curses.A_BOLD, available)

        y += 2
        label, attr = self._selected(3, self.t("Seed", "种子"), 18)
        self.add(y, inner_x, label, attr, 18)
        seed_text = self.seed if self.seed else self.t("Random", "随机")
        self.add(y, choices_x, f"{seed_text}", curses.color_pair(4) | curses.A_BOLD, available)
        self.add(y + 1, choices_x, self.t("Press Enter to edit; empty input restores random.",
                                         "按回车编辑；留空恢复随机种子。"), curses.A_DIM, available)

        y += 3
        label, attr = self._selected(4, self.t("Game log", "游戏日志"), 18)
        self.add(y, inner_x, label, attr, 18)
        log_text = self.t("On", "开启") if self.log_enabled else self.t("Off", "关闭")
        self.add(y, choices_x, f"<  {log_text}  >", curses.color_pair(5 if self.log_enabled else 4) | curses.A_BOLD, available)

        button_y = panel_y + panel_h - 3
        start_text = self.t("Start Run", "开始游戏")
        load_text = self.t("Load Save", "读取存档")
        quit_text = self.t("Quit", "退出")
        start_attr = curses.color_pair(2) | curses.A_BOLD if self.row == 5 else curses.A_BOLD
        load_attr = curses.color_pair(2) | curses.A_BOLD if self.row == 6 else curses.A_BOLD
        quit_attr = curses.color_pair(3) | curses.A_BOLD if self.row == 7 else curses.A_DIM
        button_w = inner_w // 3
        self.add(button_y, inner_x, ("> " if self.row == 5 else "  ") + f"[ {start_text} ]", start_attr, button_w)
        self.add(button_y, inner_x + button_w, ("> " if self.row == 6 else "  ") + f"[ {load_text} ]", load_attr, button_w)
        self.add(button_y, inner_x + button_w * 2, ("> " if self.row == 7 else "  ") + f"[ {quit_text} ]", quit_attr, inner_w - button_w * 2)

        hint = self.t("Up/Down select | Left/Right adjust | Enter confirm | Q quit",
                      "上下选择 | 左右调节 | 回车确认 | Q 退出")
        self.add(h - 1, 0, " " * (w - 1), curses.A_REVERSE)
        self.add(h - 1, 1, hint, curses.A_REVERSE, w - 3)
        self.screen.refresh()

    def _render_active_save(self):
        self.screen.erase()
        h, w = self.screen.getmaxyx()
        if h < 24 or w < 76:
            self.add(2, 2, self.t("Terminal too small (minimum 76x24)", "终端太小（至少 76x24）"), curses.A_BOLD)
            self.add(h - 2, 2, self.t("Resize terminal or press Q to quit", "请放大终端，或按 Q 退出"), curses.A_REVERSE)
            self.screen.refresh()
            return

        save = self.active_save or {}
        panel_w = min(86, w - 6)
        panel_h = min(22, h - 4)
        panel_x = (w - panel_w) // 2
        panel_y = max(1, (h - panel_h) // 2)
        title = self.t("Active Run", "进行中的游戏")
        self.box(panel_y, panel_x, panel_h, panel_w, title)

        inner_x = panel_x + 4
        inner_w = panel_w - 8
        heading = self.t("SLAY THE SPIRE 2", "杀戮尖塔 2")
        self.add(panel_y + 2, panel_x + max(2, (panel_w - _wlen(heading)) // 2),
                 heading, curses.A_BOLD | curses.color_pair(1), panel_w - 4)
        self.add(panel_y + 3, panel_x + max(2, (panel_w - 11) // 2),
                 "By Y1niann", curses.A_DIM, panel_w - 4)

        if save.get("corrupt"):
            lines = [
                self.t("The active save could not be read.", "活动存档无法读取。"),
                self.t("Abandon it to delete the damaged save and start again.",
                       "请放弃本局并删除损坏的存档，然后重新开始。"),
            ]
        else:
            run_time = int(save.get("run_time") or 0)
            lines = [
                f"{self.t('Character', '英雄')}  {save.get('character', '?')}    "
                f"{self.t('Ascension', '渐进难度')} A{save.get('ascension', 0)}",
                f"{self.t('Act', '幕')} {save.get('act', '?')}    "
                f"{self.t('Floor', '层')} {save.get('floor', '?')}    "
                f"HP {save.get('hp', '?')}/{save.get('max_hp', '?')}    "
                f"{save.get('gold', '?')}g",
                f"{self.t('Seed', '种子')}  {save.get('seed', '?')}    "
                f"{self.t('Play time', '游戏时间')} {run_time // 60}m{run_time % 60:02d}s",
                self.t("A new run can be started only after abandoning this one.",
                       "只有放弃当前游戏后，才能开始新的一局。"),
            ]
        for offset, line in enumerate(lines):
            attr = curses.A_BOLD if offset < 3 else curses.A_DIM
            self.add(panel_y + 6 + offset * 2, inner_x, line, attr, inner_w)

        if self.view == "abandon-active":
            warning_y = panel_y + panel_h - 7
            self.add(warning_y, inner_x,
                     self.t("Abandon this run and permanently delete its autosave?",
                            "确定放弃本局并永久删除自动存档吗？"),
                     curses.color_pair(4) | curses.A_BOLD, inner_w)
            yes_attr = curses.color_pair(2) | curses.A_BOLD if self.row == 0 else curses.A_BOLD
            no_attr = curses.color_pair(3) | curses.A_BOLD if self.row == 1 else curses.A_BOLD
            self.add(warning_y + 2, inner_x, ("> " if self.row == 0 else "  ") +
                     f"[ {self.t('Yes, abandon', '确认放弃')} ]", yes_attr, inner_w // 2)
            self.add(warning_y + 2, inner_x + inner_w // 2, ("> " if self.row == 1 else "  ") +
                     f"[ {self.t('Cancel', '取消')} ]", no_attr, inner_w - inner_w // 2)
            hint = self.t("Left/Right select | Enter confirm | Esc cancel",
                          "左右选择 | 回车确认 | Esc 取消")
        else:
            button_y = panel_y + panel_h - 4
            labels = [
                self.t("Continue", "继续游戏"),
                self.t("Abandon", "放弃本局"),
                self.t("Quit", "退出"),
            ]
            button_w = inner_w // 3
            for index, label in enumerate(labels):
                x = inner_x + button_w * index
                width = button_w if index < 2 else inner_w - button_w * 2
                attr = curses.color_pair(2) | curses.A_BOLD if self.row == index else curses.A_BOLD
                self.add(button_y, x, ("> " if self.row == index else "  ") + f"[ {label} ]", attr, width)
            hint = self.t("Left/Right select | Enter confirm | Q quit",
                          "左右选择 | 回车确认 | Q 退出")

        self.add(h - 1, 0, " " * (w - 1), curses.A_REVERSE)
        self.add(h - 1, 1, hint, curses.A_REVERSE, w - 3)
        self.screen.refresh()

    def _render_saves(self):
        self.screen.erase()
        h, w = self.screen.getmaxyx()
        if h < 24 or w < 76:
            self.add(2, 2, self.t("Terminal too small (minimum 76x24)", "终端太小（至少 76x24）"), curses.A_BOLD)
            self.add(h - 2, 2, self.t("Resize terminal or press Esc to return", "请放大终端，或按 Esc 返回"), curses.A_REVERSE)
            self.screen.refresh()
            return

        panel_w = min(96, w - 6)
        panel_x = (w - panel_w) // 2
        panel_h = min(27, h - 4)
        panel_y = max(1, (h - panel_h) // 2)
        self.box(panel_y, panel_x, panel_h, panel_w, self.t("Load Save", "读取存档"))

        inner_x = panel_x + 4
        inner_w = panel_w - 8
        list_y = panel_y + 2
        detail_y = panel_y + panel_h - 8
        max_rows = max(1, detail_y - list_y - 2)

        if not self.saves:
            empty = self.t("No saves found", "没有找到可读取的存档")
            self.add(panel_y + panel_h // 2, inner_x, empty, curses.A_DIM | curses.A_BOLD, inner_w)
        else:
            self.save_cursor %= len(self.saves)
            start = min(
                max(0, self.save_cursor - max_rows // 2),
                max(0, len(self.saves) - max_rows),
            )
            for index, save in enumerate(self.saves[start:start + max_rows], start):
                native = save.get("type") == "native"
                kind = self.t("Native", "原生") if native else self.t("Replay", "回放")
                source = self.t("Game", "游戏") if save.get("source") == "game" else self.t("Local", "本地")
                if native:
                    progress = self.t(
                        f"Act {save.get('act', '?')} Floor {save.get('floor', '?')}",
                        f"第 {save.get('act', '?')} 幕  第 {save.get('floor', '?')} 层",
                    )
                    stats = f"HP {save.get('hp', '?')}/{save.get('max_hp', '?')}  {save.get('gold', '?')}g"
                else:
                    progress = self.t(
                        f"{save.get('actions', 0)} actions",
                        f"{save.get('actions', 0)} 步操作",
                    )
                    stats = ""
                label = (
                    f"{save.get('modified', '?')}  [{kind}/{source}]  "
                    f"{save.get('character', '?')}  {progress}  {stats}"
                ).rstrip()
                selected = index == self.save_cursor
                attr = curses.color_pair(3) | curses.A_BOLD if selected else 0
                self.add(list_y + index - start, inner_x, ("> " if selected else "  ") + label, attr, inner_w)

            selected = self.saves[self.save_cursor]
            self.add(detail_y, inner_x, "-" * inner_w, curses.A_DIM, inner_w)
            self.add(detail_y, inner_x + 2, f" {self.t('Details', '详情')} ", curses.color_pair(1) | curses.A_BOLD, inner_w - 4)
            run_time = int(selected.get("run_time") or 0)
            detail_lines = [
                f"{self.t('File', '文件')}  {selected.get('file', '?')}",
                f"{self.t('Character', '英雄')}  {selected.get('character', '?')}    "
                f"{self.t('Seed', '种子')}  {selected.get('seed', '?')}",
            ]
            if selected.get("type") == "native":
                detail_lines.append(
                    f"{self.t('Ascension', '渐进难度')} A{selected.get('ascension', 0)}    "
                    f"{self.t('Play time', '游戏时间')} {run_time // 60}m{run_time % 60:02d}s"
                )
            else:
                detail_lines.append(self.t(
                    "This save will replay its recorded actions.",
                    "该存档将通过重放已记录的操作恢复进度。",
                ))
            for offset, line in enumerate(detail_lines[:3], 2):
                self.add(detail_y + offset, inner_x + 2, line, curses.A_DIM if offset > 2 else curses.A_BOLD, inner_w - 4)

        hint = self.t(
            "Up/Down select | Enter load | Esc back",
            "上下选择 | 回车读取 | Esc 返回",
        )
        self.add(h - 1, 0, " " * (w - 1), curses.A_REVERSE)
        self.add(h - 1, 1, hint, curses.A_REVERSE, w - 3)
        self.screen.refresh()

    def _cycle(self, values, current, delta):
        index = values.index(current) if current in values else 0
        return values[(index + delta) % len(values)]

    def _adjust(self, delta):
        if self.row == 0:
            values = [c[0] for c in self.CHARACTERS]
            self.character = self._cycle(values, self.character, delta)
        elif self.row == 1:
            self.ascension = max(0, min(10, self.ascension + delta))
        elif self.row == 2:
            values = [item[0] for item in self.LANGUAGES]
            self.lang = self._cycle(values, self.lang, delta)
        elif self.row == 4:
            self.log_enabled = not self.log_enabled

    def _edit_seed(self):
        h, w = self.screen.getmaxyx()
        popup_w = min(70, w - 8)
        popup_x = (w - popup_w) // 2
        popup_y = h // 2 - 3
        self.box(popup_y, popup_x, 7, popup_w, self.t("Custom seed", "自定义种子"))
        prompt = self.t("Seed: ", "种子：")
        self.add(popup_y + 3, popup_x + 3, prompt, curses.A_BOLD, popup_w - 6)
        input_x = popup_x + 3 + _wlen(prompt)
        self.add(popup_y + 3, input_x, self.seed or "", curses.color_pair(4), popup_w - (input_x - popup_x) - 3)
        self.screen.refresh()
        try:
            curses.curs_set(1)
            curses.echo()
            self.screen.move(popup_y + 3, input_x)
            raw = self.screen.getstr(popup_y + 3, input_x, max(1, popup_w - (input_x - popup_x) - 4))
            self.seed = raw.decode("utf-8", errors="replace").strip() or None
        except curses.error:
            pass
        finally:
            curses.noecho()
            curses.curs_set(0)

    def run(self):
        while True:
            self.render()
            key = self.screen.getch()
            if self.view == "active":
                if key in (ord("q"), ord("Q"), 27):
                    return None
                if key in (curses.KEY_LEFT, curses.KEY_UP, ord("h"), ord("k")):
                    self.row = (self.row - 1) % 3
                elif key in (curses.KEY_RIGHT, curses.KEY_DOWN, ord("l"), ord("j"), 9):
                    self.row = (self.row + 1) % 3
                elif key in (10, 13, curses.KEY_ENTER):
                    if self.row == 0 and not (self.active_save or {}).get("corrupt"):
                        return {
                            "load_path": self.active_save["path"],
                            "load_type": "native",
                            "character": self.active_save.get("character", self.character),
                            "lang": self.lang,
                            "log": self.log_enabled,
                        }
                    if self.row in (0, 1):
                        self.view = "abandon-active"
                        self.row = 1
                    else:
                        return None
                continue
            if self.view == "abandon-active":
                if key in (ord("q"), ord("Q"), 27, ord("n"), ord("N")):
                    self.view = "active"
                    self.row = 1
                elif key in (curses.KEY_LEFT, curses.KEY_UP, ord("h"), ord("k")):
                    self.row = (self.row - 1) % 2
                elif key in (curses.KEY_RIGHT, curses.KEY_DOWN, ord("l"), ord("j"), 9):
                    self.row = (self.row + 1) % 2
                elif key in (ord("y"), ord("Y")):
                    return {"abandon_active": True, "lang": self.lang, "log": self.log_enabled}
                elif key in (10, 13, curses.KEY_ENTER):
                    if self.row == 0:
                        return {"abandon_active": True, "lang": self.lang, "log": self.log_enabled}
                    self.view = "active"
                    self.row = 1
                continue
            if self.view == "saves":
                if key in (ord("q"), ord("Q"), 27):
                    self.view = "setup"
                elif key in (curses.KEY_UP, ord("k")) and self.saves:
                    self.save_cursor = (self.save_cursor - 1) % len(self.saves)
                elif key in (curses.KEY_DOWN, ord("j"), 9) and self.saves:
                    self.save_cursor = (self.save_cursor + 1) % len(self.saves)
                elif key == curses.KEY_PPAGE and self.saves:
                    self.save_cursor = max(0, self.save_cursor - 8)
                elif key == curses.KEY_NPAGE and self.saves:
                    self.save_cursor = min(len(self.saves) - 1, self.save_cursor + 8)
                elif key in (10, 13, curses.KEY_ENTER) and self.saves:
                    selected = self.saves[self.save_cursor]
                    return {
                        "load_path": selected["path"],
                        "load_type": selected["type"],
                        "character": selected.get("character", self.character),
                        "lang": self.lang,
                        "log": self.log_enabled,
                    }
                continue
            if key in (ord("q"), ord("Q"), 27):
                return None
            if key in (curses.KEY_UP, ord("k")):
                self.row = (self.row - 1) % 8
            elif key in (curses.KEY_DOWN, ord("j"), 9):
                self.row = (self.row + 1) % 8
            elif key in (curses.KEY_LEFT, ord("h")):
                self._adjust(-1)
            elif key in (curses.KEY_RIGHT, ord("l")):
                self._adjust(1)
            elif key in (10, 13, curses.KEY_ENTER):
                if self.row == 3:
                    self._edit_seed()
                elif self.row == 5:
                    return {
                        "character": self.character,
                        "ascension": self.ascension,
                        "lang": self.lang,
                        "seed": self.seed,
                        "log": self.log_enabled,
                    }
                elif self.row == 6:
                    self.view = "saves"
                    self.save_cursor = 0
                elif self.row == 7:
                    return None
                else:
                    self._adjust(1)


class Tui:
    def __init__(self, screen, initial_state, send, get_map=None, lang="zh", translate=None):
        self.screen = screen
        self.state = initial_state
        self.send = send
        self.get_map = get_map
        self.lang = lang
        self.t = translate or (lambda en, zh: en if lang == "en" else zh)
        self.cursor = 0
        self.combat_focus = "hand"
        self.enemy_cursor = 0
        self.enemy_power_cursor = 0
        self.target_cursor = None
        self.pending = None
        self.selected = set()
        self.overlay = None
        self.overlay_cursor = 0
        self.overlay_line_count = 0
        self.acquired_cards = []
        self.acquired_previous_cards = []
        self.acquired_title = self.t("Cards obtained", "获得卡牌")
        self.treasure_gold = 0
        self.treasure_relics = []
        self.treasure_potions = []
        self.treasure_cards = []
        self.treasure_title = self.t("Treasure obtained", "宝箱奖励")
        self.pending_reward = (
            self.state.get("decision") == "card_reward"
            and self.state.get("gold_earned") is not None
        )
        self.pending_reward_gold = self.state.get("gold_earned", 0) if self.pending_reward else 0
        self.pending_reward_relics = []
        self.pending_reward_potions = []
        self.pending_reward_cards = []
        self.pending_reward_title = self.t("Combat rewards", "战斗奖励")
        self._combat_snapshot = (
            self.state if self.state.get("decision") == "combat_play" else None
        )
        self.message = _name(self.state.get("autosave_error")) if self.state.get("autosave_error") else ""
        self.map_cache = None
        self.map_scroll = 0
        self.map_line_count = 0
        self.map_visible_lines = 0
        self.map_follow_current = True
        self._init_screen()
        if self.state.get("decision") == "map_select":
            self._refresh_map()

    def _init_screen(self):
        curses.curs_set(0)
        self.screen.keypad(True)
        self.screen.timeout(-1)
        try:
            curses.use_default_colors()
        except curses.error:
            pass
        curses.start_color()
        color_pair_limit = getattr(curses, "COLOR_PAIRS", 64)
        self._dedicated_map_colors = color_pair_limit > 12
        pairs = [
            (1, curses.COLOR_RED, -1),
            (2, curses.COLOR_GREEN, -1),
            (3, curses.COLOR_YELLOW, -1),
            (4, curses.COLOR_CYAN, -1),
            (5, curses.COLOR_MAGENTA, -1),
            (6, curses.COLOR_WHITE, curses.COLOR_BLUE),
            (7, curses.COLOR_BLACK, curses.COLOR_WHITE),
            (8, curses.COLOR_MAGENTA, -1),
            (9, curses.COLOR_GREEN, -1),
            (10, curses.COLOR_YELLOW, -1),
            (11, curses.COLOR_YELLOW, -1),
            (12, curses.COLOR_MAGENTA, -1),
        ]
        if getattr(curses, "COLORS", 0) >= 256:
            pairs[-5:] = [
                (8, 135, -1),   # purple: elite
                (9, 78, -1),    # green: rest site
                (10, 179, -1),  # muted gold: event, readable on light backgrounds
                (11, 220, -1),  # yellow: shop
                (12, 213, -1),  # pink: treasure
            ]
        for pair, fg, bg in pairs:
            if pair >= color_pair_limit:
                continue
            try:
                curses.init_pair(pair, fg, bg)
            except curses.error:
                pass

    def add(self, y, x, text, attr=0, width=None):
        h, w = self.screen.getmaxyx()
        if y < 0 or y >= h or x < 0 or x >= w - 1:
            return
        available = max(0, w - x - 1)
        if width is not None:
            available = min(available, max(0, width))
        try:
            self.screen.addstr(y, x, _clip(text, available), attr)
        except curses.error:
            pass

    @staticmethod
    def _card_type_attr(card):
        card_type = _name(card.get("type") if isinstance(card, dict) else "")
        return {
            "Attack": curses.color_pair(1),
            "Skill": curses.color_pair(4),
            "Power": curses.color_pair(3),
            "Status": curses.A_DIM,
            "Curse": curses.color_pair(5),
        }.get(card_type, 0)

    def box(self, y, x, h, w, title="", attr=0):
        max_h, max_w = self.screen.getmaxyx()
        h = min(h, max_h - y)
        w = min(w, max_w - x)
        if h < 2 or w < 2:
            return
        self.add(y, x, "+" + "-" * (w - 2) + "+", attr, w)
        for row in range(y + 1, y + h - 1):
            self.add(row, x, "|" + " " * (w - 2) + "|", attr, w)
        self.add(y + h - 1, x, "+" + "-" * (w - 2) + "+", attr, w)
        if title:
            self.add(y, x + 2, f" {title} ", attr | curses.A_BOLD, w - 4)

    def _top_bar(self):
        _, w = self.screen.getmaxyx()
        player = self.state.get("player", {})
        ctx = self.state.get("context", {})
        hp = f"HP {player.get('hp', 0)}/{player.get('max_hp', 0)}"
        potions = player.get("potions") or []
        potion_names = "/".join(_name(p.get("name")) for p in potions if p)
        pot = f"{self.t('Potions', '药水')} {potion_names or '-'}"
        gold = f"{self.t('Gold', '金币')} {player.get('gold', 0)}"
        deck = f"[D] {self.t('Deck', '牌组')} {player.get('deck_size', 0)}"
        route = f"[M] {self.t('Map', '路线')} {ctx.get('act', '?')}-{ctx.get('floor', '?')}"
        relic = f"[R] {self.t('Relics', '遗物')} {len(player.get('relics') or [])}"
        line = f" {hp}   [P] {pot}   {gold}   {deck}   {route}   {relic} "
        self.add(0, 0, " " * (w - 1))
        self.add(0, 0, line, curses.A_BOLD, w - 1)

    def _footer(self, hint):
        h, w = self.screen.getmaxyx()
        text = self.message or hint
        self.add(h - 1, 0, " " * (w - 1), curses.A_REVERSE)
        self.add(h - 1, 1, text, curses.A_REVERSE, w - 3)

    def _hp_bar(self, current, maximum, width):
        maximum = max(int(maximum or 0), 1)
        filled = max(0, min(width, round(int(current or 0) / maximum * width)))
        return "#" * filled + "." * (width - filled)

    def _damage_row(self, card, target_index=None):
        rows = card.get("damage_by_target") or []
        if target_index is not None:
            return next((row for row in rows if row.get("target_index") == target_index), None)
        if not rows:
            return None
        signatures = {
            (row.get("damage"), row.get("repeat", 1), row.get("total_damage"))
            for row in rows
        }
        return rows[0] if len(signatures) == 1 else None

    def _damage_text(self, row):
        if not row or row.get("damage") is None:
            return ""
        damage = row["damage"]
        repeat = row.get("repeat", 1) or 1
        label = self.t("dmg", "伤")
        if repeat > 1:
            total = row.get("total_damage", damage * repeat)
            return f"{damage}x{repeat}={total}{label}"
        return f"{damage}{label}"

    def _selected_combat_card(self, hand):
        if self.pending and self.pending[0] == "play_card":
            card_index = self.pending[1].get("card_index")
            return next((card for card in hand if card.get("index") == card_index), None)
        if hand:
            return hand[min(max(self.cursor, 0), len(hand) - 1)]
        return None

    def _clamp_combat_cursors(self):
        hand = self.state.get("hand") or []
        self.cursor = min(max(self.cursor, 0), max(0, len(hand) - 1))
        enemies = self.state.get("enemies") or []
        if enemies:
            self.enemy_cursor = min(max(self.enemy_cursor, 0), len(enemies) - 1)
            powers = enemies[self.enemy_cursor].get("powers") or []
            self.enemy_power_cursor = min(
                max(self.enemy_power_cursor, 0), max(0, len(powers) - 1)
            )
        else:
            self.enemy_cursor = 0
            self.enemy_power_cursor = 0
            self.combat_focus = "hand"
        if self.target_cursor is not None:
            if enemies:
                self.target_cursor = min(max(self.target_cursor, 0), len(enemies) - 1)
            else:
                self.target_cursor = None
                self.pending = None

    def _render_combat(self):
        h, w = self.screen.getmaxyx()
        if h < 24 or w < 76:
            self.add(3, 2, self.t("Terminal too small (minimum 76x24)", "终端太小（至少 76x24）"), curses.color_pair(1) | curses.A_BOLD)
            self._footer(self.t("Resize terminal or press Q to quit", "请放大终端，或按 Q 退出"))
            return

        hand = self.state.get("hand") or []
        enemies = self.state.get("enemies") or []
        self._clamp_combat_cursors()
        selected_card = self._selected_combat_card(hand)
        selected_enemy_cursor = None
        selected_target_index = None
        if self.target_cursor is not None and enemies:
            selected_enemy_cursor = self.target_cursor
        elif self.combat_focus == "enemy" and enemies:
            selected_enemy_cursor = self.enemy_cursor
        if selected_enemy_cursor is not None:
            selected_target_index = enemies[selected_enemy_cursor].get("index")
        selected_damage = self._damage_row(selected_card or {}, selected_target_index)
        combat_bottom = max(11, h - 10)
        left_w = max(30, int(w * 0.38))
        self.box(2, 1, combat_bottom - 2, left_w, self.t("Player", "角色"))
        self.box(2, left_w + 1, combat_bottom - 2, w - left_w - 2, self.t("Enemies", "敌人"))

        player = self.state.get("player", {})
        y = 3
        self.add(y, 3, _name(player.get("name")), curses.A_BOLD | curses.color_pair(4), left_w - 4)
        block_badge = f" {self.t('Block', '格挡')} {player.get('block', 0)} "
        block_x = max(4, left_w - _wlen(block_badge) - 1)
        self.add(y, block_x, block_badge, curses.color_pair(4) | curses.A_REVERSE | curses.A_BOLD)
        y += 1
        hp, mhp = player.get("hp", 0), player.get("max_hp", 1)
        self.add(y, 3, f"HP [{self._hp_bar(hp, mhp, max(8, left_w - 18))}] {hp}/{mhp}", curses.color_pair(1), left_w - 4)
        y += 1
        self.add(y, 3, f"{self.t('Round', '回合')} {self.state.get('round', 0)}", 0, left_w - 4)
        y += 2
        power_name_w = max(10, min(18, (left_w - 6) // 3))
        power_desc_x = 3 + power_name_w + 1
        power_desc_w = max(1, left_w - power_desc_x - 1)
        for power in self.state.get("player_powers") or []:
            if y >= combat_bottom - 2:
                break
            description_lines = _wrap(
                _power_description(power, lang=self.lang),
                power_desc_w,
            ) or [""]
            visible_lines = description_lines[:max(1, combat_bottom - 2 - y)]
            self.add(
                y,
                3,
                _power_line(power),
                curses.color_pair(2) | curses.A_BOLD,
                power_name_w,
            )
            for line_index, line in enumerate(visible_lines):
                self.add(
                    y + line_index,
                    power_desc_x,
                    line,
                    curses.A_DIM,
                    power_desc_w,
                )
            y += max(1, len(visible_lines))
        osty = self.state.get("osty")
        if osty and y < combat_bottom - 2:
            label = f"Osty {osty.get('hp', 0)}/{osty.get('max_hp', 0)}" if osty.get("alive") else "Osty (dead)"
            self.add(y, 3, label, curses.color_pair(5), left_w - 4)
            y += 1
        if self.state.get("orbs") and y < combat_bottom - 2:
            orbs = " | ".join(f"{_name(o.get('name'))} {o.get('passive', 0)}/{o.get('evoke', 0)}" for o in self.state["orbs"])
            self.add(y, 3, orbs, curses.color_pair(4), left_w - 4)
        if self.state.get("stars") is not None and y < combat_bottom - 2:
            self.add(y, 3, f"{self.t('Stars', '星辰')} {self.state['stars']}", curses.color_pair(3), left_w - 4)

        enemy_w = w - left_w - 6
        enemy_h = max(5, (combat_bottom - 4) // max(1, len(enemies)))
        for i, enemy in enumerate(enemies):
            ey = 3 + i * enemy_h
            if ey >= combat_bottom - 2:
                break
            enemy_limit = min(3 + (i + 1) * enemy_h, combat_bottom - 2)
            selected = selected_enemy_cursor == i
            inspecting = selected and self.target_cursor is None and self.combat_focus == "enemy"
            attr = curses.color_pair(7) if selected else 0
            marker = "> " if selected else "  "
            line_x = left_w + 5
            line_w = enemy_w - 2
            name_text = marker + _name(enemy.get("name"))
            intent_text = f"{self.t('Intent', '意图')}: {_intent(enemy, self.t)}"
            display_name = _clip(name_text, max(1, line_w // 2))
            self.add(ey, left_w + 3, display_name, attr | curses.A_BOLD, line_w)
            intent_x = left_w + 3 + _wlen(display_name) + 2
            self.add(
                ey,
                intent_x,
                intent_text,
                curses.color_pair(3) | curses.A_BOLD,
                max(1, left_w + 3 + line_w - intent_x),
            )
            ehp, emhp = enemy.get("hp", 0), enemy.get("max_hp", 1)
            attacks = []
            for intent in enemy.get("intents") or []:
                if intent.get("type") != "Attack":
                    continue
                damage = intent.get("damage")
                hits = intent.get("hits", 1)
                attack = str(damage if damage is not None else "?")
                if hits and hits > 1:
                    attack += f"x{hits}"
                attacks.append(attack)
            block_badge = f" {self.t('Block', '格挡')} {enemy.get('block', 0)} "
            attack_badge = f" {self.t('ATK', '攻击')} {'+'.join(attacks)} " if attacks else ""
            badges = [(block_badge, curses.color_pair(4) | curses.A_REVERSE | curses.A_BOLD)]
            if attack_badge:
                badges.append((attack_badge, curses.color_pair(1) | curses.A_REVERSE | curses.A_BOLD))
            badges_w = sum(_wlen(text) for text, _ in badges) + max(0, len(badges) - 1)
            hp_fixed_w = _wlen(f"HP [] {ehp}/{emhp}")
            bar_w = max(3, line_w - badges_w - hp_fixed_w - 2)
            hp_text = f"HP [{self._hp_bar(ehp, emhp, bar_w)}] {ehp}/{emhp}"
            badge_x = line_x + line_w - badges_w
            self.add(ey + 1, line_x, hp_text, curses.color_pair(1), max(1, badge_x - line_x - 1))
            for badge_index, (badge, badge_attr) in enumerate(badges):
                self.add(ey + 1, badge_x, badge, badge_attr, _wlen(badge))
                badge_x += _wlen(badge) + (1 if badge_index + 1 < len(badges) else 0)
            enemy_powers = enemy.get("powers") or []
            power_rows = _inline_rows([_power_line(power) for power in enemy_powers], line_w)
            selected_power = None
            selected_power_row = 0
            if inspecting and enemy_powers:
                self.enemy_power_cursor = min(self.enemy_power_cursor, len(enemy_powers) - 1)
                selected_power = enemy_powers[self.enemy_power_cursor]
                selected_power_row = next(
                    (row_index for row_index, row in enumerate(power_rows)
                     if any(index == self.enemy_power_cursor for index, _, _ in row)),
                    0,
                )
            description_lines = _wrap(
                _power_description(selected_power, lang=self.lang) if selected_power else "",
                line_w,
            )
            reserved_damage_rows = 1 if selected and selected_damage else 0
            available_rows = max(0, enemy_limit - (ey + 2))
            reserved_description_rows = min(
                len(description_lines),
                max(0, available_rows - reserved_damage_rows - (1 if power_rows else 0)),
            )
            visible_power_count = max(
                0, available_rows - reserved_damage_rows - reserved_description_rows
            )
            power_start = min(
                max(0, selected_power_row - visible_power_count + 1),
                max(0, len(power_rows) - visible_power_count),
            ) if inspecting else 0
            visible_power_rows = power_rows[power_start:power_start + visible_power_count]
            power_y = ey + 2
            for row_offset, row in enumerate(visible_power_rows):
                for power_index, power_text, power_x in row:
                    power_attr = curses.color_pair(5) | curses.A_BOLD
                    if inspecting and power_index == self.enemy_power_cursor:
                        power_attr = curses.color_pair(7) | curses.A_BOLD
                    self.add(power_y + row_offset, line_x + power_x, power_text, power_attr, line_w - power_x)
            detail_y = power_y + len(visible_power_rows)
            for row_offset, row in enumerate(description_lines[:reserved_description_rows]):
                self.add(detail_y + row_offset, line_x, row, curses.A_DIM, line_w)
            if selected and selected_damage:
                damage_text = self._damage_text(selected_damage)
                if damage_text:
                    damage_y = detail_y + reserved_description_rows
                    if damage_y < enemy_limit:
                        self.add(damage_y, line_x, f"{self.t('Damage', '伤害')}: {damage_text}", curses.color_pair(1) | curses.A_BOLD, line_w)

        hand_y = combat_bottom
        hand_h = h - hand_y - 1
        self.box(hand_y, 1, hand_h, w - 2, self.t("Hand", "手牌"))
        energy_badge = (
            f" {self.t('Energy', '能量')} "
            f"{self.state.get('energy', 0)}/{self.state.get('max_energy', 0)} "
        )
        self.add(
            hand_y + 1,
            3,
            energy_badge,
            curses.color_pair(4) | curses.A_REVERSE | curses.A_BOLD,
        )
        pile_counts = (
            f"{self.t('Draw pile', '抽牌堆')} {self.state.get('draw_pile_count', 0)}  "
            f"{self.t('Discard pile', '弃牌堆')} {self.state.get('discard_pile_count', 0)}  "
            f"{self.t('Exhaust pile', '消耗堆')} "
            f"{self.state.get('exhaust_pile_count', len(self.state.get('exhaust_pile') or []))}"
        )
        min_pile_x = 3 + _wlen(energy_badge) + 2
        if _wlen(pile_counts) > w - min_pile_x - 3:
            pile_counts = (
                f"Draw/抽 {self.state.get('draw_pile_count', 0)}  "
                f"Discard/弃 {self.state.get('discard_pile_count', 0)}  "
                f"Exhaust/耗 "
                f"{self.state.get('exhaust_pile_count', len(self.state.get('exhaust_pile') or []))}"
            )
        pile_x = max(min_pile_x, w - _wlen(pile_counts) - 4)
        self.add(
            hand_y + 1,
            pile_x,
            pile_counts,
            curses.color_pair(2) | curses.A_BOLD,
            max(1, w - pile_x - 3),
        )
        self.add(hand_y + 2, 2, "-" * (w - 4), curses.A_DIM, w - 4)
        card_y = hand_y + 3
        if not hand:
            self.add(card_y, 3, self.t("No cards", "没有手牌"), curses.A_DIM)
        else:
            self.cursor = min(max(self.cursor, 0), len(hand) - 1)
            card_gap = 1
            hand_width = w - 5  # Inner width after the left padding; excludes the right border.
            target_columns = min(len(hand), 7)
            card_w = max(13, min(
                25,
                (hand_width - (target_columns - 1) * card_gap) // target_columns,
            ))
            # The final card has no trailing gap, so include one gap before division.
            columns = min(len(hand), max(1, (hand_width + card_gap) // (card_w + card_gap)))
            page_size = columns * 2
            page_count = (len(hand) + page_size - 1) // page_size
            page_index = self.cursor // page_size
            start = page_index * page_size
            visible_hand = hand[start:start + page_size]
            rows_used = (len(visible_hand) + columns - 1) // columns

            position = self.t(
                f"Card {self.cursor + 1}/{len(hand)}",
                f"第 {self.cursor + 1}/{len(hand)} 张",
            )
            if page_count > 1:
                position += self.t(
                    f"  Page {page_index + 1}/{page_count}",
                    f"  第 {page_index + 1}/{page_count} 页",
                )
            position_x = w - 3 - _wlen(position)
            if position_x > 4 + _wlen(energy_badge):
                self.add(hand_y + 1, position_x, position, curses.color_pair(3) | curses.A_BOLD)

            for pos, card in enumerate(visible_hand):
                idx = start + pos
                row, col = divmod(pos, columns)
                x = 3 + col * (card_w + card_gap)
                y = card_y + row * 2
                chosen = idx == self.cursor
                card_focused = self.combat_focus == "hand" or self.target_cursor is not None
                playable = _card_playable(card, self.state.get("energy", 0))
                attr = curses.color_pair(7) if chosen and card_focused else self._card_type_attr(card)
                if not chosen and not playable:
                    attr |= curses.A_DIM
                marker = "> " if chosen and card_focused else "  "
                self.add(y, x, marker + f"[{_card_cost(card)}] {_card_title(card)}", attr | curses.A_BOLD, card_w)
                stats = card.get("stats") or {}
                stat = []
                target_index = selected_target_index if chosen else None
                damage_text = self._damage_text(self._damage_row(card, target_index))
                if damage_text:
                    stat.append(damage_text)
                else:
                    damage = stats.get("calculateddamage", stats.get("damage"))
                    if damage is not None:
                        stat.append(f"{damage}{self.t('dmg', '伤')}")
                if "block" in stats:
                    stat.append(f"{stats['block']}{self.t('blk', '挡')}")
                if stat and y + 1 < h - 1:
                    self.add(y + 1, x + 2, " ".join(stat), attr, card_w - 2)
                if chosen:
                    details = _description(card, in_combat=True, lang=self.lang)
                    modifier_text = "  |  ".join(_card_modifier_details(card, in_combat=True, lang=self.lang))
                    if modifier_text:
                        details = f"{details}  |  {modifier_text}" if details else modifier_text
                    if target_index is not None and damage_text:
                        target_name = _name(enemies[selected_enemy_cursor].get("name"))
                        details = f"{details}  |  {self.t('Target', '目标')} {target_name}: {damage_text}"
                    detail_y = card_y + rows_used * 2
                    if detail_y < h - 2:
                        self.add(detail_y, 3, details, curses.A_DIM, w - 6)

        if self.target_cursor is not None:
            hint = self.t("Select enemy: arrows, Enter confirm, Esc cancel", "选择敌人：方向键，回车确认，Esc 取消")
        elif self.combat_focus == "enemy":
            hint = self.t(
                "Tab hand | Up/Down enemy | Left/Right status | Esc hand",
                "Tab 返回手牌 | 上下选择怪物 | 左右选择状态 | Esc 返回手牌",
            )
        else:
            hint = self.t("Arrows select | Tab enemies | Enter play | E/Space end turn | P potion | D deck | M map | R relics | A abandon | Q save & quit", "方向键选择 | Tab 查看怪物 | 回车出牌 | E/空格 结束回合 | P 药水 | D 牌组 | M 路线 | R 遗物 | A 放弃 | Q 保存退出")
        self._footer(hint)

    def _decision_items(self):
        dec = self.state.get("decision")
        if dec == "map_select":
            return [(f"{self.t('Floor', '层')} {c.get('row', '?') + 1 if isinstance(c.get('row'), int) else '?'}  {self._node_type(c.get('type'))}", c) for c in self.state.get("choices") or []]
        if dec in ("card_reward", "card_select"):
            return [(
                (f"[{self.t('SLY', '奇巧')}] " if (
                    dec == "card_select"
                    and self.state.get("selection_kind") == "discard"
                    and _card_has_keyword(c, "Sly")
                ) else "")
                + f"{_card_title(c)}  "
                f"{self.t('Cost', '费用')} {_card_cost(c)}  {_name(c.get('type'))}",
                c,
            ) for c in self.state.get("cards") or []]
        if dec == "bundle_select":
            return [(f"{self.t('Pack', '卡牌包')} {b.get('index', '?')}: " + ", ".join(_card_title(c) for c in b.get("cards") or []), b) for b in self.state.get("bundles") or []]
        if dec == "potion_replace":
            incoming = _name((self.state.get("incoming_potion") or {}).get("name"))
            return [(
                self.t(
                    f"Discard {_name(p.get('name'))} and take {incoming}",
                    f"丢弃 {_name(p.get('name'))}，获得 {incoming}",
                ),
                p,
            ) for p in self.state.get("potions") or []]
        if dec in ("rest_site", "event_choice"):
            options = self.state.get("options") or []
            allowed = [o for o in options if (o.get("is_enabled", True) if dec == "rest_site" else not o.get("is_locked"))]
            rest_zh = {"HEAL": "休息", "SMITH": "升级", "LIFT": "锻炼", "DIG": "挖掘", "RECALL": "回忆", "TOKE": "吸食"}
            result = []
            for option in allowed:
                label = option.get("title") or option.get("description") or option.get("option_id") or option.get("name")
                if dec == "rest_site" and option.get("option_id") in rest_zh:
                    label = option["option_id"] if self.lang == "en" else rest_zh[option["option_id"]]
                elif dec == "event_choice" and isinstance(label, str):
                    label = _description({
                        "description": label,
                        "description_vars": option.get("title_vars") or option.get("vars") or {},
                    }, lang=self.lang)
                result.append((_name(label), option))
            return result
        if dec == "shop":
            items = []
            items.extend((
                f"{_card_title(c)}  "
                f"{self.t('Cost', '费用')} {_card_cost(c, 'card_cost')}  "
                f"{self.t('Price', '价格')} {c.get('cost', '?')}g",
                ("card", c),
            ) for c in self.state.get("cards") or [] if c.get("is_stocked"))
            items.extend((f"{_name(r.get('name'))}  {self.t('Price', '价格')} {r.get('cost', '?')}g", ("relic", r)) for r in self.state.get("relics") or [] if r.get("is_stocked"))
            items.extend((f"{_name(p.get('name'))}  {self.t('Price', '价格')} {p.get('cost', '?')}g", ("potion", p)) for p in self.state.get("potions") or [] if p.get("is_stocked"))
            removal = self.state.get("card_removal_cost")
            if removal is not None:
                items.append((f"{self.t('Remove a card', '移除卡牌')}  {self.t('Price', '价格')} {removal}g", ("remove", None)))
            return items
        return []

    def _node_type(self, node_type):
        labels = {
            "Monster": ("Monster", "怪物"),
            "Elite": ("Elite", "精英"),
            "Boss": ("Boss", "Boss"),
            "RestSite": ("Rest", "休息处"),
            "Shop": ("Shop", "商店"),
            "Treasure": ("Treasure", "宝箱"),
            "Event": ("Event", "事件"),
            "Unknown": ("Unknown", "未知"),
            "Ancient": ("Ancient", "远古"),
        }
        en, zh = labels.get(node_type, (_name(node_type), _name(node_type)))
        return self.t(en, zh)

    def _map_node_attr(self, node_type):
        if self._dedicated_map_colors:
            pair = {
                "Boss": 1, "RestSite": 9, "Elite": 8, "Event": 10,
                "Unknown": 10, "Shop": 11, "Treasure": 12, "Ancient": 4,
            }.get(node_type, 0)
        else:
            pair = {
                "Boss": 1, "RestSite": 2, "Elite": 5, "Event": 3,
                "Unknown": 3, "Shop": 3, "Treasure": 5, "Ancient": 4,
            }.get(node_type, 0)
        attr = curses.color_pair(pair) if pair else 0
        if node_type in ("Boss", "Elite", "Shop", "Treasure"):
            attr |= curses.A_BOLD
        return attr

    def _map_connector_lines(self, lower_nodes, upper_row, grid_w, cell_w):
        """Draw one independent connector marker per edge, like the legacy CLI map."""
        cells = [" "] * grid_w

        for lower in lower_nodes:
            source = lower.get("col", 0) * cell_w + cell_w // 2
            for child in lower.get("children") or []:
                if child.get("row") != upper_row:
                    continue
                target = child.get("col", 0) * cell_w + cell_w // 2
                if not (0 <= source < grid_w and 0 <= target < grid_w):
                    continue

                if source == target:
                    position, glyph = source, "│"
                else:
                    position = (source + target) // 2
                    glyph = "╱" if source < target else "╲"

                existing = cells[position]
                cells[position] = glyph if existing in (" ", glyph) else "╳"

        return ["".join(cells)]

    def _refresh_map(self):
        if not self.get_map:
            self.map_cache = None
            return
        try:
            map_data = self.get_map()
            self.map_cache = map_data if map_data and map_data.get("type") == "map" else None
            self.map_follow_current = True
        except Exception:
            self.map_cache = None

    def _map_legend_lines(self, width):
        entries = [
            (f"M={self._node_type('Monster')}", self._map_node_attr("Monster")),
            (f"E={self._node_type('Elite')}", self._map_node_attr("Elite")),
            (f"R={self._node_type('RestSite')}", self._map_node_attr("RestSite")),
            (f"$={self._node_type('Shop')}", self._map_node_attr("Shop")),
            (f"T={self._node_type('Treasure')}", self._map_node_attr("Treasure")),
            (f"?={self._node_type('Event')}", self._map_node_attr("Event")),
            (self.t("[x]=Current", "[x]=当前"), curses.color_pair(2) | curses.A_BOLD),
            (self.t("reverse=Choice", "反色=可选"), curses.A_REVERSE),
        ]
        prefix = self.t("Legend  ", "图例  ")
        lines = [[(prefix, curses.color_pair(4) | curses.A_BOLD)]]
        line_width = _wlen(prefix)
        for text, attr in entries:
            segment = ("  " if line_width else "") + text
            segment_width = _wlen(segment)
            if line_width and line_width + segment_width > max(12, width):
                lines.append([])
                line_width = 0
                segment = text
                segment_width = _wlen(segment)
            lines[-1].append((segment, attr))
            line_width += segment_width
        return lines

    def _render_map_legend(self, top, lines):
        _, w = self.screen.getmaxyx()
        for row_index, line in enumerate(lines):
            x = 2
            for text, attr in line:
                self.add(top + row_index, x, text, attr, w - x - 1)
                x += _wlen(text)

    def _render_map_graph(self, top, bottom, choices=None, selected_choice=None):
        """Render the same node/connection graph as the legacy CLI map."""
        _, w = self.screen.getmaxyx()
        data = self.map_cache or {}
        rows = data.get("rows") or []
        choices = choices or []
        if not rows:
            self.add(top, 2, self.t("Map unavailable", "路线不可用"), curses.A_DIM, w - 4)
            return

        icons = {
            "Monster": "M", "Elite": "E", "Boss": "B", "RestSite": "R",
            "Shop": "$", "Treasure": "T", "Event": "?", "Unknown": "?",
            "Ancient": "A",
        }
        choice_by_coord = {(item.get("col"), item.get("row")): i for i, item in enumerate(choices)}
        current = data.get("current_coord") or {}
        max_col = max((node.get("col", 0) for row in rows for node in row), default=0)
        boss = data.get("boss") or {}
        max_col = max(max_col, boss.get("col", 0))
        total_cols = max_col + 1
        cell_w = max(4, min(8, (w - 10) // max(1, total_cols)))
        grid_w = cell_w * total_cols
        grid_x = max(7, (w - grid_w) // 2)
        levels = [(boss.get("row", len(rows)), [boss], True)]
        levels.extend((row[0].get("row", 0), row, False) for row in reversed(rows) if row)
        graph_lines = []
        node_line_by_row = {}
        for level_index, (row_number, nodes, is_boss) in enumerate(levels):
            node_line_by_row[row_number] = len(graph_lines)
            graph_lines.append(("nodes", row_number, nodes, is_boss))
            if level_index + 1 < len(levels):
                lower_nodes = levels[level_index + 1][1]
                upper_row = row_number
                for connector in self._map_connector_lines(lower_nodes, upper_row, grid_w, cell_w):
                    graph_lines.append(("connector", None, connector, False))

        ctx = data.get("context") or self.state.get("context", {})
        boss_name = _name(boss.get("name") or self.t("Boss", "Boss"))
        heading = f"{_name(ctx.get('act_name', '?'))}  {self.t('Floor', '层')} {ctx.get('floor', '?')}  |  {self.t('Boss', 'Boss')}: {boss_name}"
        self.add(top, 2, heading, curses.A_BOLD | curses.color_pair(4), w - 4)
        graph_top = top + 1
        visible = max(1, bottom - graph_top)
        self.map_line_count = len(graph_lines)
        self.map_visible_lines = visible
        max_scroll = max(0, len(graph_lines) - visible)
        if self.map_follow_current:
            current_line = node_line_by_row.get(current.get("row"))
            if current_line is None:
                current_line = max(node_line_by_row.values(), default=0)
            self.map_scroll = min(max_scroll, max(0, current_line - visible + 1))
            self.map_follow_current = False
        else:
            self.map_scroll = min(max(0, self.map_scroll), max_scroll)
        start = self.map_scroll
        visible_graph = graph_lines[start:start + visible]

        for offset, (kind, row_number, content, is_boss) in enumerate(visible_graph):
            y = graph_top + offset
            if kind == "connector":
                self.add(y, max(0, grid_x - 5), "   │", curses.A_DIM, 4)
                self.add(y, grid_x, content, 0, grid_w)
                continue
            nodes = content
            label = "B" if is_boss else str(int(row_number) + 1)
            self.add(y, max(0, grid_x - 5), f"{label:>3}│", curses.A_DIM, 4)
            self.add(y, grid_x, " " * grid_w, 0, grid_w)
            for node in nodes:
                col = node.get("col", 0)
                x = grid_x + col * cell_w + cell_w // 2 - 1
                icon = icons.get(node.get("type"), ".")
                coord = (node.get("col"), node.get("row"))
                choice_index = choice_by_coord.get(coord)
                is_current = (current.get("col"), current.get("row")) == coord or node.get("current")
                attr = self._map_node_attr(node.get("type"))
                if is_boss:
                    token = "[B]"
                elif is_current:
                    token = f"[{icon}]"
                    attr |= curses.A_BOLD | curses.A_UNDERLINE
                else:
                    token = f" {icon} "
                if choice_index is not None:
                    attr |= curses.A_BOLD
                    if choice_index == selected_choice:
                        attr |= curses.A_REVERSE
                elif node.get("visited") and not is_current:
                    attr |= curses.A_DIM
                self.add(y, x, token, attr, 3)

        if self.map_scroll > 0:
            self.add(graph_top, w - 3, "^", curses.color_pair(3) | curses.A_BOLD)
        if self.map_scroll < max_scroll:
            self.add(bottom - 1, w - 3, "v", curses.color_pair(3) | curses.A_BOLD)

    def _render_map_decision(self):
        h, w = self.screen.getmaxyx()
        choices = self.state.get("choices") or []
        if not self.map_cache:
            self._render_decision()
            return

        choice_top = h - 5
        legend_lines = self._map_legend_lines(w - 4)
        legend_top = max(3, choice_top - len(legend_lines))
        self._render_map_graph(1, legend_top, choices, self.cursor)
        self._render_map_legend(legend_top, legend_lines)

        self.add(choice_top, 1, "-" * (w - 2), curses.A_DIM, w - 2)
        self.add(choice_top, 3, f" {self.t('Choose next room', '选择下一站')} ", curses.A_BOLD, w - 6)
        if choices:
            self.cursor %= len(choices)
            item_w = max(16, (w - 4) // len(choices))
            for index, choice in enumerate(choices):
                x = 2 + index * item_w
                icon = {"Monster": "M", "Elite": "E", "Boss": "B", "RestSite": "R",
                        "Shop": "$", "Treasure": "T", "Event": "?", "Unknown": "?",
                        "Ancient": "A"}.get(choice.get("type"), "?")
                label = f"[{index}] {icon} {self._node_type(choice.get('type'))}"
                attr = self._map_node_attr(choice.get("type")) | curses.A_BOLD
                if index == self.cursor:
                    attr |= curses.A_REVERSE
                self.add(choice_top + 2, x, ("> " if index == self.cursor else "  ") + label, attr, item_w - 1)
        else:
            self.add(choice_top + 2, 3, self.t("No available route", "没有可选路线"), curses.A_DIM, w - 6)
        self._footer(self.t("Left/Right select | Up/Down scroll | Enter confirm | D deck | A abandon | Q save & quit",
                            "左右选择 | 上下滚动 | 回车确认 | D 牌组 | A 放弃 | Q 保存退出"))

    def _render_decision(self):
        h, w = self.screen.getmaxyx()
        dec = self.state.get("decision", "")
        titles = {
            "map_select": self.t("Choose route", "选择路线"),
            "card_reward": self.t("Card reward", "卡牌奖励"),
            "bundle_select": self.t("Choose a card pack", "选择卡牌包"),
            "potion_replace": self.t("Potion slots full", "药水栏已满"),
            "card_select": _card_selection_title(self.state, self.t, lang=self.lang),
            "shop": self.t("Merchant", "商店"),
            "rest_site": self.t("Rest site", "休息处"),
            "event_choice": _name(self.state.get("event_name") or self.t("Event", "事件")),
        }
        title = titles.get(dec, dec)
        self.box(2, 2, h - 4, w - 4, title)
        y = 4
        if dec == "event_choice":
            event_description = _description({
                "description": self.state.get("description") or "",
                "description_vars": self.state.get("description_vars") or {},
            }, lang=self.lang)
            for line in _wrap(event_description, w - 10)[:3]:
                self.add(y, 5, line, curses.A_DIM, w - 10)
                y += 1
            y += 1
        elif dec == "potion_replace":
            incoming = self.state.get("incoming_potion") or {}
            self.add(
                y, 5,
                self.t(
                    f"New potion: {_name(incoming.get('name'))}",
                    f"新药水：{_name(incoming.get('name'))}",
                ),
                curses.color_pair(4) | curses.A_BOLD,
                w - 10,
            )
            y += 1
            for line in _wrap(_description(incoming, lang=self.lang), w - 10)[:2]:
                self.add(y, 5, line, curses.color_pair(4), w - 10)
                y += 1
            y += 1
        items = self._decision_items()
        if items:
            self.cursor %= len(items)
        detail_top = max(y + 2, h - 10)
        max_rows = max(1, detail_top - y - 1)
        if dec == "shop":
            section_info = {
                "card": (self.t("Cards", "卡牌"), curses.color_pair(4)),
                "relic": (self.t("Relics", "遗物"), curses.color_pair(5)),
                "potion": (self.t("Potions", "药水"), curses.color_pair(2)),
                "remove": (self.t("Services", "服务"), curses.color_pair(3)),
            }
            display_rows = []
            previous_kind = None
            for item_index, (label, value) in enumerate(items):
                kind = value[0]
                if kind != previous_kind:
                    display_rows.append(("header", section_info[kind], None))
                    previous_kind = kind
                display_rows.append(("item", (label, 0), item_index))
            selected_row = next(
                (row_index for row_index, row in enumerate(display_rows) if row[2] == self.cursor),
                0,
            )
            start = min(
                max(0, selected_row - max_rows // 2),
                max(0, len(display_rows) - max_rows),
            )
            for screen_row, (row_kind, content, item_index) in enumerate(
                display_rows[start:start + max_rows], y
            ):
                text, base_attr = content
                if row_kind == "header":
                    rule_width = max(0, w - 12 - _wlen(text))
                    self.add(
                        screen_row, 5,
                        f"── {text} " + "─" * rule_width,
                        base_attr | curses.A_BOLD,
                        w - 10,
                    )
                else:
                    kind, item = items[item_index][1]
                    attr = curses.color_pair(7) if item_index == self.cursor else (
                        self._card_type_attr(item) if kind == "card" else 0
                    )
                    if kind == "card":
                        attr |= curses.A_BOLD
                    self.add(screen_row, 7, f"  {text}", attr, w - 14)
        else:
            start = min(max(0, self.cursor - max_rows // 2), max(0, len(items) - max_rows))
            for idx, (label, value) in enumerate(items[start:start + max_rows], start):
                is_sly = (
                    dec == "card_select"
                    and self.state.get("selection_kind") == "discard"
                    and _card_has_keyword(value, "Sly")
                )
                if is_sly:
                    attr = curses.color_pair(3) | curses.A_BOLD
                    if idx == self.cursor:
                        attr = curses.color_pair(7) | curses.A_BOLD
                else:
                    attr = curses.color_pair(7) if idx == self.cursor else (
                        self._card_type_attr(value)
                        if dec in ("card_reward", "card_select") else 0
                    )
                    if dec in ("card_reward", "card_select"):
                        attr |= curses.A_BOLD
                mark = "*" if dec == "card_select" and value.get("index") in self.selected else " "
                self.add(y + idx - start, 5, f"{mark} {label}", attr, w - 10)

        if items and detail_top > y:
            _, current = items[self.cursor]
            item = current[1] if dec == "shop" else current
            is_upgrade = dec == "card_select" and self.state.get("selection_kind") == "upgrade"
            detail_item = _upgraded_card_preview(item) if is_upgrade and isinstance(item, dict) else item
            detail_parts = []
            if is_upgrade and isinstance(detail_item, dict):
                detail_parts.append(
                    f"{self.t('After upgrade', '升级后')}  "
                    f"{self.t('Cost', '费用')} {_card_cost(detail_item)}"
                )
            if isinstance(detail_item, dict):
                detail_parts.append(_description(detail_item, lang=self.lang))
                detail_parts.extend(_card_modifier_details(detail_item, lang=self.lang))
                detail_parts.extend(_card_keyword_details(detail_item, self.t))
            if isinstance(item, dict):
                for related_effect in item.get("effects") or []:
                    effect_text = _effect_text(related_effect, lang=self.lang)
                    if not effect_text:
                        continue
                    role = related_effect.get("role")
                    role_label = {
                        "give": self.t("Give", "交出"),
                        "receive": self.t("Receive", "获得"),
                    }.get(role, self.t("Effect", "效果"))
                    detail_parts.append(f"{role_label} - {effect_text}")
                effect = item.get("effect")
                effect_text = _effect_text(effect, lang=self.lang)
                if effect_text:
                    effect_label = self.t("Enchant", "附魔") if effect.get("kind") == "enchantment" else self.t("Effect", "效果")
                    detail_parts.append(f"{effect_label} - {effect_text}")
            selection_text = _effect_text(self.state.get("selection_info"), lang=self.lang)
            if selection_text:
                detail_parts.insert(0, f"{self.t('Enchant', '附魔')} - {selection_text}")
            detail = "\n".join(part for part in detail_parts if part)
            self.add(detail_top, 4, "-" * (w - 8), curses.A_DIM, w - 8)
            self.add(detail_top, 6, f" {self.t('Details', '详情')} ", curses.color_pair(4) | curses.A_BOLD, w - 12)
            detail_lines = _wrap(detail, w - 12)
            max_detail_lines = max(1, h - detail_top - 5)
            for line_index, line in enumerate(detail_lines[:max_detail_lines]):
                attr = curses.color_pair(4) | (curses.A_BOLD if line_index == 0 else 0)
                self.add(detail_top + 2 + line_index, 6, line, attr, w - 12)

        if dec == "card_select":
            mn, mx = self.state.get("min_select", 1), self.state.get("max_select", 1)
            hint = self.t(f"Space select ({len(self.selected)}/{mn}-{mx}) | Enter confirm", f"空格选择（{len(self.selected)}/{mn}-{mx}）| 回车确认")
        else:
            hint = self.t("Arrows select | Enter confirm", "方向键选择 | 回车确认")
        if dec == "card_reward" and self.state.get("can_reroll"):
            hint += self.t(" | F reroll (once)", " | F 重掷（1次）")
        if dec == "card_reward" and self.state.get("alternatives"):
            alternative = self.state["alternatives"][0]
            hint += f" | X {_name(alternative.get('name') or alternative.get('id'))}"
        if dec == "card_reward":
            hint += self.t(" | S skip this reward", " | S 跳过本次")
        elif dec in ("event_choice", "shop", "potion_replace") or (dec == "card_select" and self.state.get("min_select", 1) == 0):
            hint += self.t(" | S skip/leave", " | S 跳过/离开")
        self._footer(hint + self.t(" | D deck | M map | A abandon | Q save & quit", " | D 牌组 | M 路线 | A 放弃 | Q 保存退出"))

    def _overlay_items(self):
        player = self.state.get("player", {})
        if self.overlay == "deck":
            return self.t("Deck", "牌组"), player.get("deck") or []
        if self.overlay == "relics":
            return self.t("Relics", "遗物"), player.get("relics") or []
        if self.overlay in ("potions", "potions-use"):
            return self.t("Potions", "药水"), player.get("potions") or []
        return "", []

    def _render_overlay(self):
        h, w = self.screen.getmaxyx()
        if self.overlay == "abandon-confirm":
            ow = min(w - 8, 64)
            oh = 8
            oy, ox = (h - oh) // 2, (w - ow) // 2
            self.box(oy, ox, oh, ow, self.t("Abandon run", "放弃本局"), curses.color_pair(1))
            self.add(
                oy + 2, ox + 3,
                self.t("The active autosave will be deleted.", "活动存档将被删除。"),
                curses.A_BOLD | curses.color_pair(1), ow - 6,
            )
            self.add(oy + 4, ox + 3, self.t("Press Y to abandon", "按 Y 确认放弃"), curses.A_BOLD, ow - 6)
            self.add(oy + 5, ox + 3, self.t("Press N or Esc to cancel", "按 N 或 Esc 取消"), curses.A_DIM, ow - 6)
            self._footer(self.t("Y abandon | N/Esc cancel", "Y 放弃 | N/Esc 取消"))
            return
        if self.overlay == "map":
            self.screen.erase()
            self._top_bar()
            legend_lines = self._map_legend_lines(w - 4)
            legend_top = max(3, h - 1 - len(legend_lines))
            self._render_map_graph(1, legend_top)
            self._render_map_legend(legend_top, legend_lines)
            self._footer(self.t("Up/Down scroll | Esc/Enter close",
                                "上下滚动 | Esc/回车关闭"))
            return
        if self.overlay == "acquired":
            self._render_acquired_overlay()
            return
        if self.overlay == "treasure-rewards":
            self._render_treasure_rewards_overlay()
            return

        title, items = self._overlay_items()
        lines = []
        for i, item in enumerate(items):
            if not item:
                continue
            line = f"[{i}] {_card_title(item) if self.overlay == 'deck' else _name(item.get('name'))}"
            detail = _description(
                item,
                in_combat=self.state.get("decision") == "combat_play",
                lang=self.lang,
            )
            if self.overlay == "deck":
                modifier_text = " | ".join(_card_modifier_details(
                    item,
                    in_combat=self.state.get("decision") == "combat_play",
                    lang=self.lang,
                ))
                if modifier_text:
                    detail = f"{detail} | {modifier_text}" if detail else modifier_text
                keyword_text = " | ".join(_card_keyword_details(item, self.t))
                if keyword_text:
                    detail = f"{detail} | {keyword_text}" if detail else keyword_text
            lines.append((line + (f" - {detail}" if detail else ""), item))
        if not lines:
            lines = [(self.t("Empty", "空"), None)]
        oh = min(h - 4, max(8, len(lines) + 4))
        ow = min(w - 6, max(50, w * 4 // 5))
        oy, ox = (h - oh) // 2, (w - ow) // 2
        self.box(oy, ox, oh, ow, title, curses.A_BOLD)
        visible_lines = max(1, oh - 3)
        self.overlay_line_count = len(lines)
        if self.overlay == "potions-use":
            start = min(max(0, self.overlay_cursor - visible_lines // 2), max(0, len(lines) - visible_lines))
        else:
            start = min(self.overlay_cursor, max(0, len(lines) - visible_lines))
        for i, (line, item) in enumerate(lines[start:start + visible_lines]):
            selected = self.overlay == "potions-use" and start + i == self.overlay_cursor
            attr = curses.color_pair(7) if selected else (
                self._card_type_attr(item) | curses.A_BOLD
                if self.overlay == "deck" and item else 0
            )
            self.add(oy + 2 + i, ox + 2, line, attr, ow - 4)
        if self.overlay == "potions-use":
            self._footer(self.t("Arrows select | Enter use | Esc cancel", "方向键选择 | 回车使用 | Esc 取消"))
        else:
            self._footer(self.t("Esc/Enter close", "Esc/回车关闭"))

    def _render_acquired_overlay(self):
        h, w = self.screen.getmaxyx()
        ow = min(w - 6, max(56, w * 4 // 5))
        content_w = max(20, ow - 6)
        lines = []
        for index, card in enumerate(self.acquired_cards):
            if index < len(self.acquired_previous_cards):
                previous = self.acquired_previous_cards[index]
                change = (
                    f"{_card_title(previous)}"
                    f"  ->  {_card_title(card)}"
                )
                lines.append((change, curses.A_BOLD | curses.color_pair(7)))
            cost = _card_cost(card)
            meta = "  ".join(filter(None, (_name(card.get("type")), _name(card.get("rarity")))))
            lines.append((
                f"[{cost}] {_card_title(card)}" + (f"  {meta}" if meta else ""),
                curses.A_BOLD | self._card_type_attr(card),
            ))
            pile_labels = {
                "hand": self.t("Hand", "手牌"),
                "draw_pile": self.t("Draw pile", "抽牌堆"),
                "discard_pile": self.t("Discard pile", "弃牌堆"),
                "exhaust_pile": self.t("Exhaust pile", "消耗堆"),
                "play_pile": self.t("Play area", "打出区"),
            }
            if card.get("_pile") in pile_labels:
                lines.append((f"  {self.t('Destination', '加入位置')}：{pile_labels[card['_pile']]}", curses.color_pair(3)))
            details = []
            description = _description(
                card,
                in_combat=bool(card.get("_pile")),
                lang=self.lang,
            )
            if description:
                details.append(description)
            details.extend(_card_modifier_details(
                card,
                in_combat=bool(card.get("_pile")),
                lang=self.lang,
            ))
            details.extend(_card_keyword_details(card, self.t))
            effect = "\n".join(details)
            wrapped = _wrap(effect, content_w - 2) or [self.t("No additional effect", "没有额外效果")]
            lines.extend((f"  {line}", 0) for line in wrapped)
            if index + 1 < len(self.acquired_cards):
                lines.append(("", 0))

        if not lines:
            lines = [(self.t("No card data", "没有卡牌数据"), curses.A_DIM)]
        oh = min(h - 4, max(10, len(lines) + 4))
        oy, ox = (h - oh) // 2, (w - ow) // 2
        self.box(oy, ox, oh, ow, self.acquired_title)
        visible_lines = max(1, oh - 3)
        self.overlay_line_count = len(lines)
        self.overlay_cursor = min(self.overlay_cursor, max(0, len(lines) - visible_lines))
        start = min(self.overlay_cursor, max(0, len(lines) - visible_lines))
        for line_index, (line, attr) in enumerate(lines[start:start + visible_lines]):
            self.add(oy + 2 + line_index, ox + 3, line, attr, content_w)
        self._footer(self.t("Up/Down scroll | Esc/Enter close", "上下滚动 | Esc/回车关闭"))

    def _render_treasure_rewards_overlay(self):
        h, w = self.screen.getmaxyx()
        ow = min(w - 6, max(58, w * 4 // 5))
        content_w = max(20, ow - 6)
        lines = []
        if self.treasure_gold > 0:
            lines.append((f"{self.t('Gold', '金币')}  +{self.treasure_gold}", curses.A_BOLD | curses.color_pair(3)))
        for relic in self.treasure_relics:
            lines.append((f"{self.t('Relic', '遗物')}  {_name(relic.get('name'))}", curses.A_BOLD | curses.color_pair(5)))
            description = _description(relic, lang=self.lang)
            lines.extend((f"  {line}", 0) for line in (_wrap(description, content_w - 2) or [self.t("No description", "暂无描述")]))
        for potion in self.treasure_potions:
            lines.append((f"{self.t('Potion', '药水')}  {_name(potion.get('name'))}", curses.A_BOLD | curses.color_pair(4)))
            description = _description(potion, lang=self.lang)
            lines.extend((f"  {line}", 0) for line in (_wrap(description, content_w - 2) or [self.t("No description", "暂无描述")]))
        for card in self.treasure_cards:
            cost = _card_cost(card)
            lines.append((
                f"{self.t('Card', '卡牌')}  [{cost}] {_card_title(card)}",
                curses.A_BOLD | self._card_type_attr(card),
            ))
            description = _description(card, lang=self.lang)
            details = [description] if description else []
            details.extend(_card_modifier_details(card, lang=self.lang))
            details.extend(_card_keyword_details(card, self.t))
            detail_text = "\n".join(details)
            lines.extend((f"  {line}", 0) for line in (_wrap(detail_text, content_w - 2) or [self.t("No description", "暂无描述")]))

        if not lines:
            lines = [(self.t("No rewards obtained", "没有获得奖励"), curses.A_DIM)]
        oh = min(h - 4, max(10, len(lines) + 4))
        oy, ox = (h - oh) // 2, (w - ow) // 2
        self.box(oy, ox, oh, ow, self.treasure_title)
        visible_lines = max(1, oh - 3)
        self.overlay_line_count = len(lines)
        self.overlay_cursor = min(self.overlay_cursor, max(0, len(lines) - visible_lines))
        start = min(self.overlay_cursor, max(0, len(lines) - visible_lines))
        for line_index, (line, attr) in enumerate(lines[start:start + visible_lines]):
            self.add(oy + 2 + line_index, ox + 3, line, attr, content_w)
        self._footer(self.t("Up/Down scroll | Esc/Enter close", "上下滚动 | Esc/回车关闭"))

    def render(self):
        self.screen.erase()
        self._top_bar()
        if self.state.get("decision") == "combat_play":
            self._render_combat()
        elif self.state.get("decision") == "crystal_sphere":
            self._render_crystal_sphere()
        elif self.state.get("decision") == "game_over":
            self._render_game_over()
        elif self.state.get("decision") == "map_select":
            self._render_map_decision()
        else:
            self._render_decision()
        if self.overlay:
            self._render_overlay()
        self.screen.refresh()

    def _render_crystal_sphere(self):
        h, w = self.screen.getmaxyx()
        rows = self.state.get("rows") or []
        grid_h = self.state.get("height") or len(rows) or 1
        grid_w = self.state.get("width") or (len(rows[0]) if rows else 1)
        self.cursor %= max(1, grid_w * grid_h)
        cursor_x, cursor_y = self.cursor % grid_w, self.cursor // grid_w
        self.box(2, 2, h - 4, w - 4, _name(self.state.get("event_name") or self.t("Crystal Sphere", "水晶球")))

        remaining = self.state.get("remaining", 0)
        tool = self.state.get("tool", "Big")
        heading = f"{self.t('Divinations remaining', '剩余占卜次数')}：{remaining}"
        self.add(4, 5, heading, curses.A_BOLD | curses.color_pair(3), w - 10)
        small_attr = curses.color_pair(7) | curses.A_BOLD if tool == "Small" else curses.A_BOLD
        big_attr = curses.color_pair(7) | curses.A_BOLD if tool == "Big" else curses.A_BOLD
        self.add(5, 5, f"[1] {self.t('Small divination', '小幅占卜')}", small_attr, 22)
        self.add(5, 29, f"[2] {self.t('Big divination', '大幅占卜')}", big_attr, 22)

        token_w = 3
        grid_top = 7
        grid_left = max(5, (w - grid_w * token_w) // 2)
        selected_cell = rows[cursor_y][cursor_x] if rows and rows[cursor_y] else {}
        preview_cells = (
            _crystal_sphere_preview_cells(
                cursor_x, cursor_y, grid_w, grid_h, tool
            )
            if selected_cell.get("hidden", True)
            else {(cursor_x, cursor_y)}
        )
        for y, row in enumerate(rows[:grid_h]):
            if grid_top + y >= h - 3:
                break
            for x, cell in enumerate(row[:grid_w]):
                token = _crystal_sphere_token(cell)
                selected = x == cursor_x and y == cursor_y
                in_reveal_range = (x, y) in preview_cells and cell.get("hidden", True)
                rarity_attr = {
                    "Common": curses.color_pair(2),
                    "Uncommon": curses.color_pair(4),
                    "Rare": curses.color_pair(3),
                }.get(cell.get("rarity"))
                attr = curses.color_pair(7) | curses.A_BOLD if selected else (
                    curses.color_pair(6) | curses.A_BOLD if in_reveal_range else
                    rarity_attr if rarity_attr is not None else
                    curses.color_pair(2) if cell.get("is_good") else
                    curses.color_pair(1) if cell.get("is_good") is False else curses.A_DIM
                )
                self.add(grid_top + y, grid_left + x * token_w, f" {token} ", attr, token_w)

        legend_y = min(h - 3, grid_top + grid_h + 1)
        legend = self.t(
            "G Gold  R Relic  P Potion  C Common  U Uncommon  ★ Rare  X Curse",
            "G 金币  R 遗物  P 药水  C 普通卡  U 罕见卡  ★ 稀有卡  X 诅咒",
        )
        self.add(legend_y, 5, legend, curses.A_DIM, w - 10)
        self._footer(self.t(
            "Arrows move | 1 small | 2 big | Enter reveal highlighted area | D deck | A abandon | Q save & quit",
            "方向键移动 | 1 小幅 | 2 大幅 | 回车揭示高亮范围 | D 牌组 | A 放弃 | Q 保存退出",
        ))

    def _render_game_over(self):
        h, w = self.screen.getmaxyx()
        victory = self.state.get("victory", False)
        title = self.t("VICTORY", "胜利") if victory else self.t("DEFEAT", "战败")
        attr = curses.color_pair(2 if victory else 1) | curses.A_BOLD
        self.add(h // 2 - 2, max(2, (w - _wlen(title)) // 2), title, attr)
        p = self.state.get("player", {})
        summary = f"{_name(p.get('name'))}  HP {p.get('hp', 0)}/{p.get('max_hp', 0)}  {self.t('Gold', '金币')} {p.get('gold', 0)}"
        self.add(h // 2, max(2, (w - _wlen(summary)) // 2), summary)
        self._footer(self.t("N new run | Q quit", "N 新游戏 | Q 退出"))

    def _open_overlay(self, key):
        overlays = {ord("d"): "deck", ord("r"): "relics", ord("p"): "potions", ord("m"): "map"}
        self.overlay = overlays.get(key)
        self.overlay_cursor = 0
        if self.overlay == "map" and self.get_map:
            self._refresh_map()

    def _move(self, key, count):
        if not count:
            return
        delta = -1 if key in (curses.KEY_LEFT, curses.KEY_UP, ord("h"), ord("k")) else 1
        self.cursor = (self.cursor + delta) % count

    def _combat_key(self, key):
        hand = self.state.get("hand") or []
        enemies = self.state.get("enemies") or []
        if self.target_cursor is not None:
            if key == 27:
                self.target_cursor = None
                self.pending = None
            elif key in (curses.KEY_LEFT, curses.KEY_UP, ord("h"), ord("k")):
                self.target_cursor = (self.target_cursor - 1) % max(1, len(enemies))
            elif key in (curses.KEY_RIGHT, curses.KEY_DOWN, ord("l"), ord("j")):
                self.target_cursor = (self.target_cursor + 1) % max(1, len(enemies))
            elif key in (10, 13, curses.KEY_ENTER) and enemies:
                args = dict(self.pending[1])
                args["target_index"] = enemies[self.target_cursor]["index"]
                action = self.pending[0]
                self.target_cursor = None
                self.pending = None
                self._act(action, args)
            return
        if key in (ord("e"), ord("E"), ord(" ")):
            self.combat_focus = "hand"
            self._act("end_turn")
            return
        if key in (9, getattr(curses, "KEY_BTAB", -1)):
            if enemies:
                self.combat_focus = "enemy" if self.combat_focus == "hand" else "hand"
                if self.combat_focus == "enemy":
                    powers = enemies[self.enemy_cursor].get("powers") or []
                    self.enemy_power_cursor = min(self.enemy_power_cursor, max(0, len(powers) - 1))
            return
        if self.combat_focus == "enemy":
            if key == 27:
                self.combat_focus = "hand"
            elif key in (curses.KEY_UP, ord("k")) and enemies:
                self.enemy_cursor = (self.enemy_cursor - 1) % len(enemies)
                self.enemy_power_cursor = 0
            elif key in (curses.KEY_DOWN, ord("j")) and enemies:
                self.enemy_cursor = (self.enemy_cursor + 1) % len(enemies)
                self.enemy_power_cursor = 0
            elif key in (curses.KEY_LEFT, ord("h")) and enemies:
                powers = enemies[self.enemy_cursor].get("powers") or []
                if powers:
                    self.enemy_power_cursor = (self.enemy_power_cursor - 1) % len(powers)
            elif key in (curses.KEY_RIGHT, ord("l")) and enemies:
                powers = enemies[self.enemy_cursor].get("powers") or []
                if powers:
                    self.enemy_power_cursor = (self.enemy_power_cursor + 1) % len(powers)
            return
        if key in (curses.KEY_LEFT, curses.KEY_RIGHT, ord("h"), ord("l")):
            self._move(key, len(hand))
        elif key in (10, 13, curses.KEY_ENTER) and hand:
            card = hand[self.cursor % len(hand)]
            energy = self.state.get("energy", 0)
            if not _card_playable(card, energy):
                self.message = self.t("That card cannot be played", "这张牌现在无法打出")
                return
            args = {"card_index": card["index"]}
            if card.get("target_type") == "AnyEnemy" and len(enemies) > 1:
                self.pending = ("play_card", args)
                self.target_cursor = self.enemy_cursor
            else:
                if card.get("target_type") == "AnyEnemy" and enemies:
                    args["target_index"] = enemies[0]["index"]
                self._act("play_card", args)

    def _decision_key(self, key):
        dec = self.state.get("decision")
        items = self._decision_items()
        if dec == "crystal_sphere":
            grid_w = self.state.get("width", 11)
            grid_h = self.state.get("height", 11)
            x, y = self.cursor % grid_w, self.cursor // grid_w
            if key in (curses.KEY_LEFT, ord("h")):
                x = (x - 1) % grid_w
            elif key in (curses.KEY_RIGHT, ord("l")):
                x = (x + 1) % grid_w
            elif key in (curses.KEY_UP, ord("k")):
                y = (y - 1) % grid_h
            elif key in (curses.KEY_DOWN, ord("j")):
                y = (y + 1) % grid_h
            elif key == ord("1"):
                self._act("crystal_sphere_set_tool", {"tool": "small"})
                return
            elif key == ord("2"):
                self._act("crystal_sphere_set_tool", {"tool": "big"})
                return
            elif key in (10, 13, curses.KEY_ENTER):
                row = (self.state.get("rows") or [])[y]
                if not row[x].get("hidden", True):
                    self.message = self.t("That tile is already revealed", "这个图块已经揭开")
                    return
                self._act("crystal_sphere_reveal", {"x": x, "y": y})
                return
            else:
                return
            self.cursor = y * grid_w + x
            return
        if dec == "map_select":
            if key in (curses.KEY_UP, ord("k")):
                self.map_follow_current = False
                self.map_scroll = max(0, self.map_scroll - 2)
                return
            if key in (curses.KEY_DOWN, ord("j")):
                self.map_follow_current = False
                max_scroll = max(0, self.map_line_count - self.map_visible_lines)
                self.map_scroll = min(max_scroll, self.map_scroll + 2)
                return
            if key in (curses.KEY_LEFT, curses.KEY_RIGHT, ord("h"), ord("l")):
                self._move(key, len(items))
                return
        if key in (curses.KEY_LEFT, curses.KEY_UP, curses.KEY_RIGHT, curses.KEY_DOWN, ord("h"), ord("j"), ord("k"), ord("l")):
            self._move(key, len(items))
            return
        if key in (ord("f"), ord("F")) and dec == "card_reward" and self.state.get("can_reroll"):
            self._act("reroll_card_reward")
            return
        if key in (ord("x"), ord("X")) and dec == "card_reward" and self.state.get("alternatives"):
            alternative = self.state["alternatives"][0]
            self._act("select_card_reward_alternative", {"option_id": alternative["id"]})
            return
        if key in (ord("s"), ord("S")):
            if dec == "card_reward":
                self._act("skip_card_reward")
            elif dec == "card_select" and self.state.get("min_select", 1) == 0:
                self._act("skip_select")
            elif dec in ("event_choice", "shop"):
                self._act("leave_room")
            elif dec == "potion_replace":
                self._act("skip_potion_reward")
            return
        if not items:
            if key in (10, 13, curses.KEY_ENTER):
                self._act("proceed")
            return
        _, value = items[self.cursor % len(items)]
        if dec == "card_select" and key == ord(" "):
            idx = value["index"]
            if idx in self.selected:
                self.selected.remove(idx)
            elif len(self.selected) < self.state.get("max_select", 1):
                self.selected.add(idx)
            return
        if key not in (10, 13, curses.KEY_ENTER):
            return
        if dec == "map_select":
            self._act("select_map_node", {"col": value["col"], "row": value["row"]})
        elif dec == "card_reward":
            self._act("select_card_reward", {"card_index": value["index"]})
        elif dec == "bundle_select":
            self._act("select_bundle", {"bundle_index": value["index"]})
        elif dec == "card_select":
            if not self.selected and self.state.get("max_select", 1) == 1:
                self.selected.add(value["index"])
            mn, mx = self.state.get("min_select", 1), self.state.get("max_select", 1)
            if mn <= len(self.selected) <= mx:
                indices = ",".join(str(i) for i in sorted(self.selected))
                self._act("select_cards", {"indices": indices})
            else:
                self.message = self.t(f"Select {mn}-{mx} cards", f"请选择 {mn}-{mx} 张牌")
        elif dec in ("rest_site", "event_choice"):
            self._act("choose_option", {"option_index": value["index"]})
        elif dec == "potion_replace":
            self._act("replace_potion", {"potion_index": value["index"]})
        elif dec == "shop":
            kind, item = value
            actions = {
                "card": ("buy_card", "card_index"),
                "relic": ("buy_relic", "relic_index"),
                "potion": ("buy_potion", "potion_index"),
            }
            if kind == "remove":
                self._act("remove_card")
            else:
                action, arg = actions[kind]
                self._act(action, {arg: item["index"]})

    def _act(self, action, args=None):
        cmd = {"cmd": "action", "action": action}
        if args:
            cmd["args"] = args
        old_dec = self.state.get("decision")
        old_state = self.state
        selected_room_type = None
        if action == "select_map_node" and args:
            selected_room_type = next((
                choice.get("type")
                for choice in old_state.get("choices") or []
                if choice.get("col") == args.get("col") and choice.get("row") == args.get("row")
            ), None)
        new_state = self.send(cmd)
        if new_state:
            if new_state.get("type") == "error":
                self.message = _error_message(new_state.get("message"), self.t)
            else:
                removed_cards, added_cards = _deck_card_changes(old_state, new_state)
                combat_baseline = (
                    old_state if old_dec == "combat_play"
                    else getattr(self, "_combat_snapshot", None)
                )
                if not combat_baseline and new_state.get("decision") == "combat_play":
                    combat_baseline = {
                        "draw_pile": (old_state.get("player") or {}).get("deck") or [],
                    }
                all_combat_added = (
                    _added_combat_cards(combat_baseline, new_state)
                    if combat_baseline else []
                )
                combat_comparison_state = combat_baseline or old_state
                returned_cards = _returned_stolen_cards(
                    combat_comparison_state, new_state, added_cards, all_combat_added,
                )
                added_cards = _without_matching_cards(added_cards, returned_cards)
                added_relics = _added_player_items(old_state, new_state, "relics")
                added_potions = _added_player_items(old_state, new_state, "potions")
                old_gold = (old_state.get("player") or {}).get("gold", 0)
                new_gold = (new_state.get("player") or {}).get("gold", 0)
                gold_gained = max(0, new_gold - old_gold)
                is_treasure = selected_room_type == "Treasure"
                is_divination_reward = (
                    action == "crystal_sphere_reveal"
                    and old_dec == "crystal_sphere"
                    and new_state.get("decision") != "crystal_sphere"
                )
                combat_added = _without_matching_cards(all_combat_added, returned_cards)
                hostile_cards = [card for card in combat_added if card.get("type") in ("Status", "Curse")]
                stolen_cards = _newly_stolen_cards(combat_comparison_state, new_state)
                self.state = new_state
                self.message = (
                    _name(new_state.get("autosave_error"))
                    if new_state.get("autosave_error") else ""
                )
                self.target_cursor = None
                self.pending = None
                new_dec = self.state.get("decision")
                if new_dec == "combat_play":
                    self._combat_snapshot = new_state
                elif new_dec != "card_select":
                    self._combat_snapshot = None
                selection_decisions = {"card_select", "card_reward", "bundle_select", "potion_replace"}
                combat_reward_started = (
                    new_dec == "card_reward"
                    and new_state.get("gold_earned") is not None
                    and old_dec != "card_reward"
                )
                reward_batch_started = (
                    not self.pending_reward
                    and new_dec in selection_decisions
                    and action not in ("buy_card", "buy_relic", "buy_potion")
                    and (
                        combat_reward_started
                        or gold_gained
                        or added_relics
                        or added_potions
                        or added_cards
                    )
                )
                reward_batch_was_pending = self.pending_reward
                if reward_batch_started:
                    self.pending_reward = True
                    self.pending_reward_gold = max(
                        gold_gained,
                        (new_state.get("gold_earned") or 0) if combat_reward_started else 0,
                    )
                    self.pending_reward_relics = list(added_relics)
                    self.pending_reward_potions = list(added_potions)
                    self.pending_reward_cards = list(added_cards)
                    self.pending_reward_title = (
                        self.t("Combat rewards", "战斗奖励")
                        if combat_reward_started
                        else self.t("Divination rewards", "占卜奖励")
                        if is_divination_reward
                        else self.t("Treasure obtained", "宝箱奖励")
                        if is_treasure
                        else self.t("Rewards obtained", "获得奖励")
                    )
                elif reward_batch_was_pending:
                    self.pending_reward_gold += gold_gained
                    self.pending_reward_relics.extend(added_relics)
                    self.pending_reward_potions.extend(added_potions)
                    self.pending_reward_cards.extend(added_cards)
                reward_batch_finished = (
                    reward_batch_was_pending
                    and old_dec in selection_decisions
                    and new_dec not in selection_decisions
                )
                if new_dec == "combat_play":
                    self.combat_focus = "hand"
                    self._clamp_combat_cursors()
                if new_dec == "map_select":
                    self._refresh_map()
                if new_dec != old_dec or action == "end_turn" or old_dec not in ("combat_play", "crystal_sphere"):
                    self.cursor = 0
                    self.selected.clear()
                if new_dec == "crystal_sphere" and old_dec != "crystal_sphere":
                    grid_w = self.state.get("width", 11)
                    grid_h = self.state.get("height", 11)
                    self.cursor = (grid_h // 2) * grid_w + grid_w // 2
                if returned_cards:
                    self.acquired_cards = returned_cards + hostile_cards
                    self.acquired_previous_cards = []
                    self.acquired_title = (
                        self.t(
                            "Cards returned / Enemy added cards",
                            "卡牌已归还 / 敌人加入卡牌",
                        )
                        if hostile_cards else self.t("Cards returned", "卡牌已归还")
                    )
                    self.overlay = "acquired"
                    self.overlay_cursor = 0
                elif self.pending_reward and new_dec in selection_decisions:
                    # Resolve every chained card choice before showing the combined rewards.
                    pass
                elif reward_batch_finished:
                    self.treasure_gold = self.pending_reward_gold
                    self.treasure_relics = self.pending_reward_relics
                    self.treasure_potions = self.pending_reward_potions
                    self.treasure_cards = self.pending_reward_cards
                    self.treasure_title = self.pending_reward_title
                    self.overlay = "treasure-rewards"
                    self.overlay_cursor = 0
                    self.pending_reward = False
                    self.pending_reward_gold = 0
                    self.pending_reward_relics = []
                    self.pending_reward_potions = []
                    self.pending_reward_cards = []
                elif (is_treasure or is_divination_reward) and (gold_gained or added_relics or added_potions or added_cards):
                    self.treasure_gold = gold_gained
                    self.treasure_relics = added_relics
                    self.treasure_potions = added_potions
                    self.treasure_cards = added_cards
                    self.treasure_title = (
                        self.t("Divination rewards", "占卜奖励") if is_divination_reward
                        else self.t("Treasure obtained", "宝箱奖励")
                    )
                    self.overlay = "treasure-rewards"
                    self.overlay_cursor = 0
                elif removed_cards and added_cards and action == "select_cards":
                    same_ids = all(
                        (old.get("id") or old.get("name")) == (new.get("id") or new.get("name"))
                        for old, new in zip(removed_cards, added_cards)
                    )
                    upgraded = same_ids and any(
                        not old.get("upgraded") and new.get("upgraded")
                        for old, new in zip(removed_cards, added_cards)
                    )
                    enchanted = same_ids and any(
                        old.get("enchantment") != new.get("enchantment")
                        for old, new in zip(removed_cards, added_cards)
                    )
                    self.acquired_cards = added_cards
                    self.acquired_previous_cards = removed_cards
                    if upgraded:
                        self.acquired_title = self.t("Upgrade result", "升级结果")
                    elif enchanted:
                        self.acquired_title = self.t("Enchantment result", "附魔结果")
                    else:
                        self.acquired_title = self.t("Transform result", "变换结果")
                    self.overlay = "acquired"
                    self.overlay_cursor = 0
                elif stolen_cards:
                    self.acquired_cards = stolen_cards + hostile_cards
                    self.acquired_previous_cards = []
                    self.acquired_title = (
                        self.t(
                            "Card stolen / Enemy added cards",
                            "卡牌被偷走 / 敌人加入卡牌",
                        )
                        if hostile_cards else self.t("Card stolen", "卡牌被偷走")
                    )
                    self.overlay = "acquired"
                    self.overlay_cursor = 0
                elif (added_relics or added_potions) and action not in ("buy_relic", "buy_potion"):
                    self.treasure_gold = gold_gained
                    self.treasure_relics = added_relics
                    self.treasure_potions = added_potions
                    self.treasure_cards = added_cards
                    self.treasure_title = self.t("Rewards obtained", "获得奖励")
                    self.overlay = "treasure-rewards"
                    self.overlay_cursor = 0
                elif added_cards:
                    if action != "buy_card":
                        self.acquired_cards = added_cards
                        self.acquired_previous_cards = []
                        self.acquired_title = self.t("Cards obtained", "获得卡牌")
                        self.overlay = "acquired"
                        self.overlay_cursor = 0
                    else:
                        names = _card_name_counts(added_cards)
                        self.message = self.t(f"Added to deck: {names}", f"加入牌组：{names}")
                elif hostile_cards:
                    self.acquired_cards = hostile_cards
                    self.acquired_previous_cards = []
                    self.acquired_title = self.t("Enemy added cards", "敌人加入卡牌")
                    self.overlay = "acquired"
                    self.overlay_cursor = 0

    def run(self):
        while True:
            self.render()
            key = self.screen.getch()
            self.message = ""
            if self.overlay:
                if self.overlay == "abandon-confirm":
                    if key in (ord("y"), ord("Y")):
                        return "abandon"
                    if key in (ord("n"), ord("N"), 27, ord("q"), ord("Q")):
                        self.overlay = None
                elif self.overlay == "map":
                    if key in (curses.KEY_UP, ord("k")):
                        self.map_follow_current = False
                        self.map_scroll = max(0, self.map_scroll - 2)
                    elif key in (curses.KEY_DOWN, ord("j")):
                        self.map_follow_current = False
                        max_scroll = max(0, self.map_line_count - self.map_visible_lines)
                        self.map_scroll = min(max_scroll, self.map_scroll + 2)
                    elif key == curses.KEY_PPAGE:
                        self.map_follow_current = False
                        self.map_scroll = max(0, self.map_scroll - max(1, self.map_visible_lines - 2))
                    elif key == curses.KEY_NPAGE:
                        self.map_follow_current = False
                        max_scroll = max(0, self.map_line_count - self.map_visible_lines)
                        self.map_scroll = min(max_scroll, self.map_scroll + max(1, self.map_visible_lines - 2))
                    elif key in (27, 10, 13, curses.KEY_ENTER, ord("q"), ord("Q"), ord("m"), ord("M")):
                        self.overlay = None
                elif self.overlay == "potions-use":
                    pots = self.state.get("player", {}).get("potions") or []
                    if key in (curses.KEY_LEFT, curses.KEY_UP, ord("h"), ord("k")) and pots:
                        self.overlay_cursor = (self.overlay_cursor - 1) % len(pots)
                    elif key in (curses.KEY_RIGHT, curses.KEY_DOWN, ord("l"), ord("j")) and pots:
                        self.overlay_cursor = (self.overlay_cursor + 1) % len(pots)
                    elif key in (10, 13, curses.KEY_ENTER) and pots:
                        pot = pots[self.overlay_cursor]
                        args = {"potion_index": pot["index"]}
                        enemies = self.state.get("enemies") or []
                        self.overlay = None
                        if pot.get("target_type") == "AnyEnemy" and len(enemies) > 1:
                            self.pending = ("use_potion", args)
                            self.target_cursor = self.enemy_cursor
                        else:
                            if pot.get("target_type") == "AnyEnemy" and enemies:
                                args["target_index"] = enemies[0]["index"]
                            self._act("use_potion", args)
                    elif key in (27, ord("q"), ord("Q")):
                        self.overlay = None
                elif key in (curses.KEY_UP, ord("k")):
                    self.overlay_cursor = max(0, self.overlay_cursor - 1)
                elif key in (curses.KEY_DOWN, ord("j")):
                    self.overlay_cursor = min(max(0, self.overlay_line_count - 1), self.overlay_cursor + 1)
                elif key == curses.KEY_PPAGE:
                    self.overlay_cursor = max(0, self.overlay_cursor - 8)
                elif key == curses.KEY_NPAGE:
                    self.overlay_cursor = min(max(0, self.overlay_line_count - 1), self.overlay_cursor + 8)
                elif key in (27, 10, 13, curses.KEY_ENTER, ord("q"), ord("Q")):
                    if self.overlay == "acquired":
                        self.acquired_cards = []
                        self.acquired_previous_cards = []
                    elif self.overlay == "treasure-rewards":
                        self.treasure_gold = 0
                        self.treasure_relics = []
                        self.treasure_potions = []
                        self.treasure_cards = []
                    self.overlay = None
                continue
            if key in (ord("q"), ord("Q")):
                return "quit"
            if key in (ord("a"), ord("A")) and self.state.get("decision") != "game_over":
                self.overlay = "abandon-confirm"
                continue
            if self.state.get("decision") == "game_over":
                if key in (ord("n"), ord("N")):
                    return "restart"
                continue
            if key in (ord("d"), ord("D"), ord("r"), ord("R"), ord("m"), ord("M")):
                self._open_overlay(ord(chr(key).lower()))
                continue
            if key in (ord("p"), ord("P")):
                pots = self.state.get("player", {}).get("potions") or []
                if self.state.get("decision") == "combat_play" and pots:
                    self.overlay = "potions-use"
                    self.overlay_cursor = 0
                else:
                    self._open_overlay(ord("p"))
                continue
            if self.state.get("decision") == "combat_play":
                self._combat_key(key)
            else:
                self._decision_key(key)


def run_tui(initial_state, send, get_map=None, lang="zh", translate=None):
    """Run the full-screen UI and return ``quit``, ``abandon``, or ``restart``."""
    return curses.wrapper(
        lambda screen: Tui(screen, initial_state, send, get_map, lang, translate).run()
    )


def run_setup_tui(character="Ironclad", ascension=0, lang="zh", seed=None,
                  log_enabled=False, saves=None, active_save=None):
    """Show the pre-run settings screen and return selected settings or ``None``."""
    return curses.wrapper(
        lambda screen: SetupTui(
            screen, character, ascension, lang, seed, log_enabled,
            saves=saves, active_save=active_save
        ).run()
    )
