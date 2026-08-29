using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Audio.Debug;

namespace Sts2Headless;

/// <summary>
/// Synchronization context that executes continuations inline immediately.
/// Task.Yield() posts to SynchronizationContext.Current — by executing inline,
/// the yield becomes a no-op and the entire async chain runs synchronously.
/// Uses a recursion guard to queue nested posts and drain them after.
/// </summary>
internal class InlineSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback, object?)> _queue = new();
    private bool _executing;

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_executing)
        {
            _queue.Enqueue((d, state));
            return;
        }
        // removed debug log

        // Execute inline immediately, then drain any nested posts
        _executing = true;
        try
        {
            d(state);
            // Drain any callbacks that were queued during execution
            while (_queue.Count > 0)
            {
                var (cb, st) = _queue.Dequeue();
                cb(st);
            }
        }
        finally
        {
            _executing = false;
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        d(state);
    }

    public void Pump()
    {
        // Drain any remaining queued callbacks
        while (_queue.Count > 0)
        {
            var (cb, st) = _queue.Dequeue();
            _executing = true;
            try { cb(st); }
            finally { _executing = false; }
        }
    }
}

/// <summary>
/// Bilingual localization lookup — loads eng/zhs JSON files for display names.
/// </summary>
internal class LocLookup
{
    private readonly Dictionary<string, Dictionary<string, string>> _eng = new();
    private readonly Dictionary<string, Dictionary<string, string>> _zhs = new();

    public LocLookup()
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");
        Load(Path.Combine(baseDir, "localization_eng"), _eng);
        Load(Path.Combine(baseDir, "localization_zhs"), _zhs);
    }

    private static void Load(string dir, Dictionary<string, Dictionary<string, string>> target)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (data != null) target[name] = data;
            }
            catch { }
        }
    }

    /// <summary>Get bilingual name: "English / 中文" or just the key if not found.</summary>
    public string Name(string table, string key)
    {
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);
        var zh = _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);
        if (en != null && zh != null && en != zh) return $"{en} / {zh}";
        return en ?? zh ?? key;
    }

    public string? En(string table, string key) => _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);
    public string? Zh(string table, string key) => _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);

    /// <summary>Strip BBCode tags like [gold], [/blue], [b], [sine], etc.</summary>
    private static string StripBBCode(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-zA-Z_][a-zA-Z0-9_=]*\]", "");
    }

    /// <summary>Language for JSON output: "en" or "zh". Default: "en".</summary>
    public string Lang { get; set; } = "en";

    /// <summary>Return localized string for JSON output based on Lang setting.</summary>
    public string Bilingual(string table, string key)
    {
        if (Lang == "zh")
        {
            var zh = _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);
            if (zh != null) return StripBBCode(zh);
        }
        var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key) ?? key;
        return StripBBCode(en);
    }

    // Convenience helpers using ModelId
    public string Card(string entry) => Bilingual("cards", entry + ".title");
    public string Monster(string entry)
    {
        var key = entry + ".name";
        var result = Bilingual("monsters", key);
        // If no dedicated entry, fall back to the base segment key (e.g. DECIMILLIPEDE_SEGMENT_FRONT → DECIMILLIPEDE_SEGMENT)
        if (result == key)
        {
            var lastUnderscore = entry.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var baseEntry = entry[..lastUnderscore];
                var baseKey = baseEntry + ".name";
                var baseResult = Bilingual("monsters", baseKey);
                if (baseResult != baseKey) return baseResult;
            }
        }
        return result;
    }
    public string Relic(string entry) => Bilingual("relics", entry + ".title");
    public string Potion(string entry) => Bilingual("potions", entry + ".title");
    public string Power(string entry)
    {
        var key = entry + ".title";
        var result = Bilingual("powers", key);
        if (result == key && entry.EndsWith("_POWER", StringComparison.Ordinal))
        {
            var potionEntry = entry[..^"_POWER".Length];
            var potionKey = potionEntry + ".title";
            var potionResult = Bilingual("potions", potionKey);
            if (potionResult != potionKey) return potionResult;
        }
        return result;
    }

    public string PowerDescription(string entry)
    {
        var key = entry + ".description";
        var result = Bilingual("powers", key);
        if (result == key && entry.EndsWith("_POWER", StringComparison.Ordinal))
        {
            var potionEntry = entry[..^"_POWER".Length];
            var potionKey = potionEntry + ".description";
            var potionResult = Bilingual("potions", potionKey);
            if (potionResult != potionKey) return potionResult;
        }
        return result;
    }
    public string Event(string entry) => Bilingual("events", entry + ".title");
    public string Act(string entry) => Bilingual("acts", entry + ".title");

    /// <summary>Resolve a full loc key like "TABLE.KEY.SUB" by searching all tables.</summary>
    public string BilingualFromKey(string locKey)
    {
        if (Lang == "zh")
        {
            foreach (var tableName in _zhs.Keys)
            {
                var zh = _zhs.GetValueOrDefault(tableName)?.GetValueOrDefault(locKey);
                if (zh != null) return StripBBCode(zh);
            }
        }
        foreach (var tableName in _eng.Keys)
        {
            var en = _eng.GetValueOrDefault(tableName)?.GetValueOrDefault(locKey);
            if (en != null) return StripBBCode(en);
        }
        return locKey;
    }

    public string LocalizeModelName(string value)
    {
        // Some StringVars already contain a localization key (for example
        // THIS_OR_THAT's Curse value is "CLUMSY.title"), not an English model name.
        var localizedKey = BilingualFromKey(value);
        if (localizedKey != value) return localizedKey;

        var entry = value.Contains('.', StringComparison.Ordinal)
            ? value[(value.LastIndexOf('.') + 1)..]
            : value;
        foreach (var tableName in new[] { "cards", "relics", "potions", "monsters", "enchantments" })
        {
            var directKey = entry + ".title";
            var direct = Bilingual(tableName, directKey);
            if (direct != directKey) return direct;

            if (!_eng.TryGetValue(tableName, out var entries)) continue;
            foreach (var (key, english) in entries)
            {
                if (!key.EndsWith(".title", StringComparison.Ordinal)
                    && !key.EndsWith(".name", StringComparison.Ordinal))
                    continue;
                if (string.Equals(english, value, StringComparison.Ordinal))
                    return Bilingual(tableName, key);
            }
        }
        return value;
    }

    public string? FindTitleEntry(string table, object? localizedTitle)
    {
        var title = Convert.ToString(localizedTitle);
        if (string.IsNullOrEmpty(title)) return null;

        var directKey = title.EndsWith(".title", StringComparison.Ordinal) ? title : null;
        if (directKey != null &&
            (_eng.GetValueOrDefault(table)?.ContainsKey(directKey) == true ||
             _zhs.GetValueOrDefault(table)?.ContainsKey(directKey) == true))
            return directKey[..^".title".Length];

        var keys = (_eng.GetValueOrDefault(table)?.Keys ?? Enumerable.Empty<string>())
            .Concat(_zhs.GetValueOrDefault(table)?.Keys ?? Enumerable.Empty<string>())
            .Where(key => key.EndsWith(".title", StringComparison.Ordinal))
            .Distinct();
        foreach (var key in keys)
        {
            var en = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key);
            var zh = _zhs.GetValueOrDefault(table)?.GetValueOrDefault(key);
            if (string.Equals(en == null ? null : StripBBCode(en), title, StringComparison.Ordinal) ||
                string.Equals(zh == null ? null : StripBBCode(zh), title, StringComparison.Ordinal))
                return key[..^".title".Length];
        }
        return null;
    }

    public bool IsLoaded => _eng.Count > 0;
}

/// <summary>
/// Full run simulator — manages the game lifecycle from character selection
/// through map navigation, combat, events, rest sites, shops, and act transitions.
/// Drives the engine forward until it hits a "decision point" requiring external input.
/// </summary>
public class RunSimulator
{
    private static int? _expectedSaveSchemaVersion;
    private static bool _expectedSaveSchemaVersionReady;
    private static readonly object _expectedSaveSchemaVersionLock = new();

    private RunState? _runState;
    private static bool _modelDbInitialized;
    private static readonly InlineSynchronizationContext _syncCtx = new();
    private readonly ManualResetEventSlim _turnStarted = new(false);
    private readonly ManualResetEventSlim _combatEnded = new(false);
    private static readonly LocLookup _loc = new();

    private static object DynamicVarDisplayValue(DynamicVar dynamicVar)
    {
        if (dynamicVar is StringVar stringVar)
            return _loc.LocalizeModelName(stringVar.StringValue);
        return (int)dynamicVar.BaseValue;
    }

    private static object? LocVariableDisplayValue(object? value)
    {
        return value switch
        {
            null => null,
            DynamicVar dynamicVar => DynamicVarDisplayValue(dynamicVar),
            string text => _loc.LocalizeModelName(text),
            _ => value,
        };
    }

    private static Dictionary<string, object?> LocVariables(
        LocString? locString, Dictionary<string, object?>? fallback = null)
    {
        var result = fallback != null
            ? new Dictionary<string, object?>(fallback)
            : new Dictionary<string, object?>();
        if (locString?.Variables == null)
            return result;
        foreach (var (name, value) in locString.Variables)
            result[name] = LocVariableDisplayValue(value);
        return result;
    }
    private bool _eventOptionChosen;
    private int _lastEventOptionCount;
    private bool _shopCardRemovalUsed;

    // Pending rewards for card selection (populated after combat, before proceeding)
    private List<Reward>? _pendingRewards;
    private CardReward? _pendingCardReward;
    private bool _rewardsProcessed;
    private int _goldBeforeCombat;
    private int _lastKnownHp;
    private readonly HeadlessCardSelector _cardSelector = new();
    private Dictionary<string, object?>? _pendingSelectionInfo;
    private string? _pendingSelectionKind;
    // A room/relic action can pause inside the headless selector. Keep its task so the
    // response after select_cards/card_reward is serialized only after that action resumes.
    private Task? _pendingSelectionTask;
    private string? _pendingSelectionTaskSource;
    // Pending bundle selection (Scroll Boxes: pick 1 of N packs)
    private IReadOnlyList<IReadOnlyList<CardModel>>? _pendingBundles;
    private TaskCompletionSource<IEnumerable<CardModel>>? _pendingBundleTcs;
    private CrystalSphereMinigame? _pendingCrystalSphere;
    private PotionReward? _pendingPotionReward;
    private TaskCompletionSource<int?>? _pendingPotionChoice;
    // The native game saves after a map node is chosen but before its room starts.
    // Keep that exact pre-room snapshot so quitting mid-combat cannot persist combat HP/RNG.
    private string? _roomStartCheckpointJson;

    public Dictionary<string, object?> StartRun(string character, int ascension = 0, string? seed = null, string lang = "en")
    {
        try
        {
            _pendingCrystalSphere = null;
            _pendingPotionReward = null;
            _pendingPotionChoice = null;
            _loc.Lang = lang;
            EnsureModelDbInitialized();

            var player = CreatePlayer(character);
            if (player == null)
                return Error($"Unknown character: {character}");

            var seedStr = seed ?? "headless_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Log($"Creating RunState with seed={seedStr}");

            // Use CreateForTest which properly handles mutable copies internally
            _runState = RunState.CreateForTest(
                players: new[] { player },
                ascensionLevel: ascension,
                seed: seedStr
            );

            // Set up RunManager with test mode
            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpTest(_runState, netService);
            LocalContext.NetId = netService.NetId;

            // Force Neow event (blessing selection at start)
            _runState.ExtraFields.StartedWithNeow = true;

            // Generate rooms for all acts
            RunManager.Instance.GenerateRooms();
            Log("Rooms generated");

            // Launch the run
            RunManager.Instance.Launch();
            Log("Run launched");

            // Register event handlers for combat turn transitions
            CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
            CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();

            // Finalize starting relics
            RunManager.Instance.FinalizeStartingRelics().GetAwaiter().GetResult();
            Log("Starting relics finalized");

            // Enter first act (generates map)
            RunManager.Instance.EnterAct(0, doTransition: false).GetAwaiter().GetResult();
            Log("Entered Act 0");

            // Ancient is created by EnterAct, so serialize its untouched event state after
            // entry. Before EnterAct its map point still has RoomType.Unassigned.
            _roomStartCheckpointJson = SaveManager.ToJson(
                RunManager.Instance.ToSave(_runState.CurrentRoom));

            // Register card selector for cards that need player choice
            CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;
            ConfigureRewardSelector();

            // Now we should be at the map — detect decision point
            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("StartRun failed", ex);
        }
    }

    // ─── Test/Debug commands ───

    private static readonly System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    /// <summary>Get the backing List&lt;T&gt; behind an IReadOnlyList property via reflection.</summary>
    private static List<T>? GetBackingList<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        return field?.GetValue(obj) as List<T>;
    }

    private static void SetField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        field?.SetValue(obj, value);
    }

    public Dictionary<string, object?> SetPlayer(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];

            if (args.TryGetValue("hp", out var hpEl) && player.Creature != null)
                SetField(player.Creature, "_currentHp", hpEl.GetInt32());
            if (args.TryGetValue("max_hp", out var mhpEl) && player.Creature != null)
                SetField(player.Creature, "_maxHp", mhpEl.GetInt32());
            if (args.TryGetValue("gold", out var goldEl))
                player.Gold = goldEl.GetInt32();

            if (args.TryGetValue("relics", out var relicsEl))
            {
                var list = GetBackingList<RelicModel>(player, "_relics");
                if (list != null)
                {
                    list.Clear();
                    foreach (var rEl in relicsEl.EnumerateArray())
                    {
                        var id = rEl.GetString();
                        if (id == null) continue;
                        var model = ModelDb.GetById<RelicModel>(new ModelId("RELIC", id));
                        if (model != null) list.Add(model.ToMutable());
                    }
                }
            }
            if (args.TryGetValue("deck", out var deckEl))
            {
                // Remove existing cards from RunState tracking
                foreach (var c in player.Deck.Cards.ToList())
                    _runState.RemoveCard(c);
                player.Deck.Clear(silent: true);
                // Add new cards via RunState.CreateCard (sets Owner + registers)
                foreach (var cEl in deckEl.EnumerateArray())
                {
                    var id = cEl.GetString();
                    if (id == null) continue;
                    var canonical = ModelDb.GetById<CardModel>(new ModelId("CARD", id));
                    if (canonical != null)
                    {
                        var card = _runState.CreateCard(canonical, player);
                        player.Deck.AddInternal(card, silent: true);
                    }
                }
            }
            if (args.TryGetValue("potions", out var potionsEl))
            {
                var slots = GetBackingList<PotionModel>(player, "_potionSlots")
                         ?? GetBackingList<PotionModel?>(player, "_potionSlots") as System.Collections.IList;
                if (slots != null)
                {
                    for (int i = 0; i < slots.Count; i++) slots[i] = null;
                    int idx = 0;
                    foreach (var pEl in potionsEl.EnumerateArray())
                    {
                        if (idx >= slots.Count) break;
                        var id = pEl.GetString();
                        if (id != null)
                        {
                            var model = ModelDb.GetById<PotionModel>(new ModelId("POTION", id));
                            // Inject a mutable instance (not the canonical model — that throws
                            // CanonicalModelException when the game reads potion.Owner) and set its
                            // Owner, or UsePotionAction fails with "without an owner!".
                            if (model != null)
                            {
                                var mutable = model.ToMutable();
                                mutable.Owner = player;
                                slots[idx] = mutable;
                            }
                        }
                        idx++;
                    }
                }
            }

            Log($"SetPlayer: hp={player.Creature?.CurrentHp} gold={player.Gold} relics={player.Relics.Count} deck={player.Deck?.Cards?.Count}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["player"] = PlayerSummary(player),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetPlayer failed", ex); }
    }

    public Dictionary<string, object?> EnterRoom(string roomType, string? encounter, string? eventId)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var runState = _runState;
            Log($"EnterRoom: type={roomType} encounter={encounter} event={eventId}");

            AbstractRoom room;
            switch (roomType.ToLowerInvariant())
            {
                case "combat":
                case "monster":
                case "elite":
                {
                    if (string.IsNullOrEmpty(encounter))
                        encounter = "SHRINKER_BEETLE_WEAK"; // default encounter
                    var encModel = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", encounter));
                    if (encModel == null) return Error($"Unknown encounter: {encounter}");
                    room = new CombatRoom(encModel.ToMutable(), runState);
                    break;
                }
                case "shop":
                    room = new MerchantRoom();
                    break;
                case "rest":
                case "rest_site":
                    room = new RestSiteRoom();
                    break;
                case "event":
                {
                    if (string.IsNullOrEmpty(eventId))
                        return Error("event requires 'event' parameter (e.g. CHANGELING_GROVE)");
                    var evModel = ModelDb.GetById<EventModel>(new ModelId("EVENT", eventId));
                    if (evModel == null) return Error($"Unknown event: {eventId}");
                    room = new EventRoom(evModel);
                    break;
                }
                case "treasure":
                    room = new TreasureRoom(_runState.CurrentActIndex);
                    break;
                default:
                    return Error($"Unknown room type: {roomType}");
            }

            RunManager.Instance.EnterRoom(room).GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        catch (Exception ex) { return ErrorWithTrace("EnterRoom failed", ex); }
    }

    public Dictionary<string, object?> SetDrawOrder(List<string> cardIds)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];
            var pcs = player.PlayerCombatState;
            if (pcs?.DrawPile == null) return Error("Not in combat");

            var drawList = GetBackingList<CardModel>(pcs.DrawPile, "_cards");
            if (drawList == null) return Error("Cannot access draw pile");

            var newOrder = new List<CardModel>();
            var available = new List<CardModel>(drawList);
            foreach (var cardId in cardIds)
            {
                var match = available.FirstOrDefault(c =>
                    c.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    newOrder.Add(match);
                    available.Remove(match);
                }
            }
            newOrder.AddRange(available);

            drawList.Clear();
            drawList.AddRange(newOrder);

            Log($"SetDrawOrder: {newOrder.Count} cards, top={newOrder.FirstOrDefault()?.Id.Entry}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["draw_pile_count"] = drawList.Count,
                ["top_cards"] = newOrder.Take(5).Select(c => _loc.Card(c.Id.Entry)).ToList(),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetDrawOrder failed", ex); }
    }

    // ─── Game actions ───
    public Dictionary<string, object?> LoadSave(string saveJson, string lang = "en")
    {
        try
        {
            _pendingCrystalSphere = null;
            _pendingPotionReward = null;
            _pendingPotionChoice = null;
            _roomStartCheckpointJson = null;
            _loc.Lang = lang;
            EnsureModelDbInitialized();

            Log("Loading save file...");

            if (!ValidateSaveSchemaVersion(saveJson, out var schemaError))
                return Error($"Save schema mismatch: {schemaError}");

            var readResult = SaveManager.FromJson<SerializableRun>(saveJson);
            if (!readResult.Success || readResult.SaveData == null)
                return Error($"Failed to parse save file: {readResult.Status} {readResult.ErrorMessage}");

            var save = readResult.SaveData;
            Log($"Save loaded: seed={save.SerializableRng?.Seed}, act={save.CurrentActIndex}, ascension={save.Ascension}");

            _runState = RunState.FromSerializable(save);
            if (_runState == null)
                return Error("Failed to create RunState from save");

            Log($"RunState created, players={_runState.Players?.Count}");

            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpSavedSingleplayer(_runState, save);
            LocalContext.NetId = netService.NetId;

            CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
            CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();
            CardSelectCmd.UseSelector(_cardSelector);
            LocPatches._bundleSimRef = this;
            ConfigureRewardSelector();

            var savedRoom = _runState.CurrentRoom;
            var pendingRoomCoord = GetPendingRoomCoord(saveJson);

            // Save visited coords before Launch (EnterAct will clear them)
            var savedVisitedCoords = _runState.VisitedMapCoords?.ToList() ?? new List<MapCoord>();
            var shouldResumeInitialNeow = IsInitialNeowSave(saveJson);
            Log($"Save has {savedVisitedCoords.Count} visited coords");

            RunManager.Instance.Launch();
            Log("Run launched");

            if (savedRoom is MapRoom || savedRoom == null)
            {
                // Preserve Neow for saves created before the first blessing choice.
                // Once the run has visited at least one map node, re-entering Act 1
                // should not send the player back through the Ancient start node.
                if (_runState.CurrentActIndex == 0 && savedVisitedCoords.Count > 0 && !shouldResumeInitialNeow)
                    _runState.ExtraFields.StartedWithNeow = false;
                RunManager.Instance.EnterAct(_runState.CurrentActIndex, doTransition: false).GetAwaiter().GetResult();
                _syncCtx.Pump();
                Log($"Entered Act {_runState.CurrentActIndex}");

                if (shouldResumeInitialNeow && _runState.Map?.StartingMapPoint != null &&
                    _runState.CurrentRoom is not EventRoom)
                {
                    Log("Restoring initial Neow event");
                    RunManager.Instance.EnterMapCoord(_runState.Map.StartingMapPoint.coord).GetAwaiter().GetResult();
                    _syncCtx.Pump();
                }

                // A pending-room save includes the selected node as the last visited coord.
                // Restore only completed nodes, then enter the selected node again so the
                // same pre-room RNG chooses the same encounter.
                var completedCoords = pendingRoomCoord.HasValue
                    ? savedVisitedCoords.Take(savedVisitedCoords.Count - 1).ToList()
                    : savedVisitedCoords;

                // EnterAct clears visited coords and ActFloor — restore completed nodes.
                if (completedCoords.Count > 0)
                {
                    if (_runState.VisitedMapCoords == null || _runState.VisitedMapCoords.Count == 0)
                    {
                        foreach (var coord in completedCoords)
                            _runState.AddVisitedMapCoord(coord);
                    }
                    _runState.ActFloor = completedCoords.Count;
                    var last = completedCoords[^1];
                    Log($"Restored map position: floor={_runState.ActFloor}, coord=({last.col},{last.row})");
                }

                if (pendingRoomCoord.HasValue)
                {
                    _roomStartCheckpointJson = saveJson;
                    var coord = pendingRoomCoord.Value;
                    Log($"Restarting saved room at ({coord.col},{coord.row})");
                    RunManager.Instance.EnterMapCoord(coord).GetAwaiter().GetResult();
                    _syncCtx.Pump();
                    WaitForActionExecutor();
                }
            }
            else
            {
                Log($"Preserving saved room: {savedRoom.GetType().Name}");
            }

            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("LoadSave failed", ex);
        }
    }

    /// <summary>
    /// Expected run save <c>schema_version</c> (lazy: first load_save only, so StartRun never fails on reflection).
    /// Order: <c>STS2_SAVE_SCHEMA_VERSION</c> env → reflect sts2.dll → unknown, defer to SaveManager.
    /// </summary>
    private static int? GetExpectedSaveSchemaVersion()
    {
        if (_expectedSaveSchemaVersionReady)
            return _expectedSaveSchemaVersion;
        lock (_expectedSaveSchemaVersionLock)
        {
            if (_expectedSaveSchemaVersionReady)
                return _expectedSaveSchemaVersion;
            _expectedSaveSchemaVersion = ResolveExpectedSaveSchemaVersion();
            _expectedSaveSchemaVersionReady = true;
            return _expectedSaveSchemaVersion;
        }
    }

    private static int? ResolveExpectedSaveSchemaVersion()
    {
        var env = Environment.GetEnvironmentVariable("STS2_SAVE_SCHEMA_VERSION");
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out var envVer))
            return envVer;

        var reflected = TryReflectLatestSaveSchemaVersion();
        if (reflected.HasValue)
            return reflected.Value;

        Console.Error.WriteLine(
            "[Sts2Headless] Could not read save schema from sts2.dll; deferring schema compatibility " +
            "to SaveManager.FromJson. Set STS2_SAVE_SCHEMA_VERSION to enforce a specific version.");
        return null;
    }

    /// <summary>Find static parameterless GetLatestSchemaVersion (or close) on sts2; supports int/uint/long.</summary>
    private static int? TryReflectLatestSaveSchemaVersion()
    {
        var asm = typeof(SerializableRun).Assembly;
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }

        var candidates = new List<(int score, string typeName, int value)>();
        foreach (var t in types)
        {
            MethodInfo? m;
            try
            {
                foreach (var name in new[] { "GetLatestSchemaVersion", "GetLatestVersion" })
                {
                    m = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, Type.EmptyTypes, null);
                    if (m == null) continue;
                    var tn = t.FullName ?? "";
                    // Avoid unrelated static GetLatestVersion() elsewhere in the assembly.
                    if (name == "GetLatestVersion" && !tn.Contains("Saves", StringComparison.Ordinal))
                        continue;

                    var conv = TryConvertSchemaNumber(m.Invoke(null, null));
                    if (!conv.HasValue) continue;

                    var score = name == "GetLatestSchemaVersion" ? 100 : 0;
                    if (tn.Contains("Saves", StringComparison.Ordinal)) score += 50;
                    if (tn.Contains("Schema", StringComparison.Ordinal) || tn.Contains("Migration", StringComparison.Ordinal))
                        score += 25;
                    candidates.Add((score, tn, conv.Value));
                }
            }
            catch
            {
                // type may not support full reflection on this runtime
            }
        }

        if (candidates.Count == 0)
            return null;

        var best = candidates.OrderByDescending(c => c.score).ThenBy(c => c.typeName).First();
        return best.value;
    }

    private static int? TryConvertSchemaNumber(object? value) => value switch
    {
        int i => i,
        uint u => u <= int.MaxValue ? (int)u : null,
        long l => l >= int.MinValue && l <= int.MaxValue ? (int)l : null,
        short s => s,
        ushort us => us,
        byte b => b,
        _ => null,
    };

    private static bool ValidateSaveSchemaVersion(string saveJson, out string error)
    {
        error = "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("schema_version", out var versionElem))
            {
                error = "missing schema_version";
                return false;
            }

            if (versionElem.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !versionElem.TryGetInt32(out var schemaVersion))
            {
                error = "schema_version is not a valid integer";
                return false;
            }

            var expected = GetExpectedSaveSchemaVersion();
            if (expected.HasValue && schemaVersion != expected.Value)
            {
                error = $"expected v{expected.Value}, got v{schemaVersion}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"could not inspect save: {ex.Message}";
            return false;
        }
    }

    private static bool TrySetPropertyValue(object target, string propertyName, object? value)
    {
        var prop = target.GetType().GetProperty(propertyName);
        if (prop?.CanWrite != true)
            return false;
        prop.SetValue(target, value);
        return true;
    }

    private static bool IsInitialNeowSave(string saveJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("current_act_index", out var actIndexElem) || actIndexElem.GetInt32() != 0)
                return false;

            var startedWithNeow = root.TryGetProperty("extra_fields", out var extraFieldsElem)
                && extraFieldsElem.ValueKind == System.Text.Json.JsonValueKind.Object
                && extraFieldsElem.TryGetProperty("started_with_neow", out var startedElem)
                && startedElem.ValueKind == System.Text.Json.JsonValueKind.True;
            if (!startedWithNeow)
                return false;

            var hasVisitedCoords = root.TryGetProperty("visited_map_coords", out var visitedElem)
                                && visitedElem.ValueKind == System.Text.Json.JsonValueKind.Array
                                && visitedElem.GetArrayLength() > 0;
            if (!hasVisitedCoords)
                return true;

            // A checkpoint taken on the untouched Ancient screen already contains its
            // starting coordinate and unfinished room, unlike a pre-EnterAct game save.
            return root.TryGetProperty("pre_finished_room", out var roomElem)
                && roomElem.ValueKind == System.Text.Json.JsonValueKind.Object
                && roomElem.TryGetProperty("event_id", out var eventElem)
                && string.Equals(eventElem.GetString(), "EVENT.NEOW", StringComparison.Ordinal)
                && roomElem.TryGetProperty("is_pre_finished", out var finishedElem)
                && finishedElem.ValueKind == System.Text.Json.JsonValueKind.False;
        }
        catch
        {
            return false;
        }
    }

    private static MapCoord? GetPendingRoomCoord(string saveJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(saveJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("current_act_index", out var actElem) ||
                !actElem.TryGetInt32(out var actIndex) ||
                !root.TryGetProperty("visited_map_coords", out var visited) ||
                visited.ValueKind != System.Text.Json.JsonValueKind.Array ||
                visited.GetArrayLength() == 0)
                return null;

            var completedCount = 0;
            if (root.TryGetProperty("map_point_history", out var histories) &&
                histories.ValueKind == System.Text.Json.JsonValueKind.Array &&
                actIndex >= 0 && actIndex < histories.GetArrayLength())
            {
                var currentActHistory = histories[actIndex];
                if (currentActHistory.ValueKind == System.Text.Json.JsonValueKind.Array)
                    completedCount = currentActHistory.GetArrayLength();
            }

            // Native room-start saves have exactly one selected-but-unfinished node.
            if (visited.GetArrayLength() != completedCount + 1)
                return null;

            var last = visited[visited.GetArrayLength() - 1];
            if (!last.TryGetProperty("col", out var colElem) ||
                !last.TryGetProperty("row", out var rowElem))
                return null;
            return new MapCoord(colElem.GetByte(), rowElem.GetByte());
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySetSerializedVisitedCoords(
        SerializableRun serializableRun, List<object?> visitedItems, out string error)
    {
        error = "";

        var saveType = serializableRun.GetType();
        var visitedProp = saveType.GetProperty("VisitedMapCoords");
        if (visitedProp == null)
        {
            error = "Save data is missing VisitedMapCoords";
            return false;
        }

        var visitedType = visitedProp.PropertyType;
        if (visitedType.IsArray)
        {
            var elementType = visitedType.GetElementType()!;
            var array = Array.CreateInstance(elementType, visitedItems.Count);
            for (int i = 0; i < visitedItems.Count; i++)
                array.SetValue(visitedItems[i], i);
            visitedProp.SetValue(serializableRun, array);
        }
        else if (visitedType.IsGenericType)
        {
            var elementType = visitedType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            foreach (var item in visitedItems)
                list.Add(item);
            visitedProp.SetValue(serializableRun, list);
        }
        else
        {
            error = $"Unsupported VisitedMapCoords type: {visitedType.Name}";
            return false;
        }

        TrySetPropertyValue(serializableRun, "ActFloor", visitedItems.Count);
        TrySetPropertyValue(serializableRun, "CurrentMapCoord", visitedItems.Count > 0 ? visitedItems[^1] : null);
        return true;
    }

    private bool TryCaptureRoomStartCheckpoint(MapCoord selectedCoord, out string error)
    {
        error = "";
        if (_runState == null)
        {
            error = "No active run";
            return false;
        }

        try
        {
            var serializableRun = RunManager.Instance.ToSave(_runState.CurrentRoom ?? new MapRoom());
            var visitedProp = serializableRun.GetType().GetProperty("VisitedMapCoords");
            if (visitedProp == null)
            {
                error = "Save data is missing VisitedMapCoords";
                return false;
            }

            var visitedItems = new List<object?>();
            if (visitedProp.GetValue(serializableRun) is System.Collections.IEnumerable visited)
            {
                foreach (var item in visited)
                    visitedItems.Add(item);
            }
            visitedItems.Add(selectedCoord);

            if (!TrySetSerializedVisitedCoords(serializableRun, visitedItems, out error))
                return false;

            TrySetPropertyValue(serializableRun, "PreFinishedRoom", null);
            TrySetPropertyValue(serializableRun, "CurrentRoom", null);
            _roomStartCheckpointJson = SaveManager.ToJson(serializableRun);
            Log($"Captured room-start checkpoint at ({selectedCoord.col},{selectedCoord.row})");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public Dictionary<string, object?> SaveCheckpoint(string? outputPath)
    {
        try
        {
            if (_runState == null)
                return Error("No active run to save");

            if (string.IsNullOrEmpty(outputPath))
                return Error("No output path specified for quit save");

            var currentRoom = _runState.CurrentRoom;
            string saveJson;

            if (currentRoom is MapRoom || currentRoom == null)
            {
                Log($"Saving map checkpoint (room={currentRoom?.GetType().Name ?? "null"}, outputPath={outputPath})...");
                saveJson = SaveManager.ToJson(RunManager.Instance.ToSave(currentRoom));
            }
            else
            {
                Log($"Saving pre-room checkpoint from {currentRoom.GetType().Name} (outputPath={outputPath})...");
                if (string.IsNullOrEmpty(_roomStartCheckpointJson))
                    return Error("Cannot save checkpoint: no room-start snapshot is available");
                saveJson = _roomStartCheckpointJson;
            }

            Log($"Serialized save: {saveJson.Length} chars");

            var dir = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);
            var tempPath = outputPath + ".tmp";
            try
            {
                System.IO.File.WriteAllText(tempPath, saveJson);
                System.IO.File.Move(tempPath, outputPath, overwrite: true);
            }
            finally
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
            Log($"Save written to: {outputPath}");

            return new Dictionary<string, object?>
            {
                ["type"] = "save_result",
                ["success"] = true,
                ["path"] = outputPath,
                ["size"] = saveJson.Length,
                ["room_type"] = currentRoom?.GetType().Name,
            };
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("SaveCheckpoint failed", ex);
        }
    }
    public Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, object?>? args)
    {
        try
        {
            if (_runState == null)
                return Error("No run in progress");

            var player = _runState.Players[0];

            switch (action)
            {
                case "select_map_node":
                    return DoMapSelect(player, args);
                case "play_card":
                    return DoPlayCard(player, args);
                case "end_turn":
                    return DoEndTurn(player);
                case "choose_option":
                    return DoChooseOption(player, args);
                case "select_card_reward":
                    return DoSelectCardReward(player, args);
                case "reroll_card_reward":
                    return DoRerollCardReward(player);
                case "select_card_reward_alternative":
                    return DoSelectCardRewardAlternative(player, args);
                case "skip_card_reward":
                    return DoSkipCardReward(player);
                case "buy_card":
                    return DoBuyCard(player, args);
                case "buy_relic":
                    return DoBuyRelic(player, args);
                case "buy_potion":
                    return DoBuyPotion(player, args);
                case "remove_card":
                    return DoRemoveCard(player);
                case "select_bundle":
                    return DoSelectBundle(player, args);
                case "select_cards":
                    return DoSelectCards(player, args);
                case "skip_select":
                    return DoSkipSelect(player);
                case "crystal_sphere_set_tool":
                    return DoCrystalSphereSetTool(args);
                case "crystal_sphere_reveal":
                    return DoCrystalSphereReveal(args);
                case "use_potion":
                    return DoUsePotion(player, args);
                case "discard_potion":
                    return DoDiscardPotion(player, args);
                case "replace_potion":
                    return DoResolvePotionReward(args);
                case "skip_potion_reward":
                    return DoResolvePotionReward(null);
                case "leave_room":
                    return DoLeaveRoom(player);
                case "proceed":
                    return DoProceed(player);
                default:
                    return Error($"Unknown action: {action}");
            }
        }
        catch (Exception ex)
        {
            return ErrorWithTrace($"Action '{action}' failed", ex);
        }
    }

    #region Actions

    private Dictionary<string, object?> DoMapSelect(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("col") || !args.ContainsKey("row"))
            return Error("select_map_node requires 'col' and 'row'");

        // Reset tracking for new room
        _rewardsProcessed = false;
        _pendingCardReward = null;
        _eventOptionChosen = false;
        _lastEventOptionCount = 0;
        _shopCardRemovalUsed = false;
        _pendingRewards = null;
        _pendingSelectionInfo = null;
        _pendingSelectionKind = null;
        _pendingSelectionTask = null;
        _pendingSelectionTaskSource = null;
        _pendingCrystalSphere = null;
        _lastKnownHp = player.Creature?.CurrentHp ?? 0;

        var col = Convert.ToInt32(args["col"]);
        var row = Convert.ToInt32(args["row"]);
        var coord = new MapCoord((byte)col, (byte)row);

        Log($"Moving to map coord ({col},{row})");

        // BUG-013: Wait for any pending actions (relic sessions, etc.) to complete before entering new room
        WaitForActionExecutor();
        _syncCtx.Pump();

        if (!TryCaptureRoomStartCheckpoint(coord, out var checkpointError))
            return Error($"Cannot enter room without a restart checkpoint: {checkpointError}");

        // Call EnterMapCoord directly (same as what MoveToMapCoordAction does in TestMode)
        // This avoids the action executor which can swallow errors silently.
        RunManager.Instance.EnterMapCoord(coord).GetAwaiter().GetResult();
        _syncCtx.Pump();
        WaitForActionExecutor();

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoPlayCard(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("card_index"))
            return Error("play_card requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var pcs = player.PlayerCombatState;
        if (pcs == null)
            return Error("Not in combat");

        var hand = pcs.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return Error($"Invalid card index {cardIndex}, hand has {hand.Count} cards");

        var card = hand[cardIndex];

        // Determine target based on card's TargetType first
        // Self/None/All cards: target = null (game handles internally)
        // AnyEnemy cards: use target_index or auto-pick first alive enemy
        Creature? target = null;
        var cardTargetType = card.TargetType;
        if (cardTargetType == TargetType.AnyEnemy)
        {
            // Use caller's target_index if provided
            if (args.TryGetValue("target_index", out var targetObj) && targetObj != null)
            {
                var targetIndex = Convert.ToInt32(targetObj);
                var state = CombatManager.Instance.DebugOnlyGetState();
                if (state != null)
                {
                    var enemies = state.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIndex >= 0 && targetIndex < enemies.Count)
                        target = enemies[targetIndex];
                }
            }
            // No target_index given: only auto-target when the choice is unambiguous
            // (a single alive enemy). With multiple enemies, picking one is a real game
            // decision — return an error instead of silently targeting enemy 0 (#79).
            if (target == null)
            {
                var state = CombatManager.Instance.DebugOnlyGetState();
                var alive = state?.Enemies?.Where(e => e != null && e.IsAlive).ToList() ?? new();
                if (alive.Count == 1)
                    target = alive[0];
                else if (alive.Count > 1)
                    return Error($"Card {card.Id.Entry} targets a single enemy (AnyEnemy); " +
                                 $"'target_index' is required when multiple enemies are alive ({alive.Count}).");
            }
        }
        // All other target types (None, All, etc.) → leave target as null

        // Check if card can be played
        if (!card.CanPlay(out var reason, out var _))
        {
            return Error($"Cannot play card {card.GetType().Name}: {reason}");
        }

        Log($"Playing card {card.GetType().Name} (index {cardIndex}) targeting {(target != null ? target.Monster?.GetType().Name ?? "creature" : "none")}");

        var handCountBefore = hand.Count;

        var playAction = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(playAction);
        WaitForActionExecutor();

        // Some card effects call the selector without preserving their source model in
        // CardSelectCmd.FromHand. The action is paused here, so the card that initiated
        // the pending choice is still the most reliable context for the UI.
        if (_cardSelector.HasPending && card.Id.Entry == "ARMAMENTS")
            _pendingSelectionKind = "upgrade";

        // Check if card play had no effect (hand unchanged, same card still at same index)
        var handAfter = pcs.Hand.Cards;
        if (handAfter.Count == handCountBefore && cardIndex < handAfter.Count && handAfter[cardIndex] == card)
        {
            return Error($"Card could not be played (still in hand after action): {card.GetType().Name} [{card.Id}]");
        }

        return DetectDecisionPoint();
    }

    // STS2 build 23372702 removed CombatManager.IsPlayPhase (global) in favor of a
    // per-player PlayerCombatState.Phase. Headless is single-player, so the local
    // player (Players[0]) being in the Play phase is the equivalent signal.
    private bool IsPlayPhase()
    {
        var p = (_runState != null && _runState.Players.Count > 0) ? _runState.Players[0] : null;
        return p?.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play;
    }

    private bool HasLivingEnemies()
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        return combatState?.Enemies?.Any(enemy => enemy != null && enemy.IsAlive) == true;
    }

    private Dictionary<string, object?> DoEndTurn(Player player)
    {
        // A pending card / card-reward / bundle selection is an unresolved prompt; ending
        // the turn here would silently mutate combat instead. Surface the prompt unchanged
        // and let the caller resolve it first (#61).
        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
        {
            Log("end_turn ignored: a card selection is pending");
            return DetectDecisionPoint();
        }

        if (!IsPlayPhase())
        {
            // Might be between phases — pump and check
            _syncCtx.Pump();
            if (!IsPlayPhase())
            {
                if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                    return DetectDecisionPoint();
                // Brief wait for ThreadPool if sync context didn't catch it
                Thread.Sleep(100);
                _syncCtx.Pump();
                if (!IsPlayPhase())
                    return DetectDecisionPoint();
            }
        }

        // Ensure no actions are still running before ending turn
        WaitForActionExecutor();

        Log($"Ending turn (round={CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0})");
        _turnStarted.Reset();
        _combatEnded.Reset();

        // Enable SuppressYield so Task.Yield() runs inline during enemy turn processing.
        // This prevents deadlocks during boss fights (e.g., Vantom) where continuations
        // would otherwise be posted to ThreadPool and never complete.
        // Keep SuppressYield=true through the initial fallback wait loop — multi-hit
        // attacks (e.g., 10x2) have continuations between hits that also need suppression.
        YieldPatches.SuppressYield = true;
        try
        {
            PlayerCmd.EndTurn(player, canBackOut: false);
            _syncCtx.Pump();

            // Fallback: if turn didn't complete synchronously, keep pumping with SuppressYield on
            if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
            {
                for (int i = 0; i < 50; i++)
                {
                    _syncCtx.Pump();
                    if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null) break;
                    if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                    if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                    if (IsPlayPhase()) break;
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            YieldPatches.SuppressYield = false;
        }

        // Some enemies pause their turn for a real player decision. Knowledge Demon, for
        // example, presents Disintegration vs Mind Rot through FromChooseACardScreen. Return
        // that pending prompt instead of treating the intentionally paused enemy turn as a
        // deadlock and cancelling the ActionExecutor.
        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
        {
            Log("Enemy turn paused for player selection");
            return DetectDecisionPoint();
        }

        // Second fallback: if still stuck after SuppressYield window, cancel and retry.
        // The WaitUntilQueue TCS is likely deadlocked.
        if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
        {
            Log("EndTurn stuck, cancelling and retrying with SuppressYield...");
            try
            {
                RunManager.Instance.ActionExecutor.Cancel();
                _syncCtx.Pump();
                Thread.Sleep(50);
                _syncCtx.Pump();

                // Reset the player ready state and try again with SuppressYield
                CombatManager.Instance.UndoReadyToEndTurn(player);
                _syncCtx.Pump();

                YieldPatches.SuppressYield = true;
                try
                {
                    PlayerCmd.EndTurn(player, canBackOut: false);
                    _syncCtx.Pump();
                }
                finally
                {
                    YieldPatches.SuppressYield = false;
                }

                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                    if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                    if (IsPlayPhase()) break;
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex) { Log($"Cancel retry: {ex.Message}"); }

            // NUCLEAR OPTION: If STILL stuck after 2 attempts, use ThreadPool to force
            // the enemy turn processing to complete with SuppressYield permanently on.
            if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
            {
                var stuckState = CombatManager.Instance.DebugOnlyGetState();
                var stuckEnemies = stuckState?.Enemies?.Where(e => e != null && e.IsAlive)
                    .Select(e => $"{e.Monster?.GetType().Name}(hp={e.CurrentHp})").ToList();
                Log($"EndTurn STILL stuck after retry — nuclear fallback. Round={stuckState?.RoundNumber}, " +
                    $"Enemies=[{string.Join(",", stuckEnemies ?? new())}], " +
                    $"IsPlayPhase={IsPlayPhase()}, " +
                    $"IsInProgress={CombatManager.Instance.IsInProgress}, " +
                    $"ActionExecutor.IsRunning={RunManager.Instance.ActionExecutor.IsRunning}");
                try
                {
                    // Cancel again and undo
                    RunManager.Instance.ActionExecutor.Cancel();
                    _syncCtx.Pump();
                    CombatManager.Instance.UndoReadyToEndTurn(player);
                    _syncCtx.Pump();
                    Thread.Sleep(50);

                    // Run EndTurn on ThreadPool with SuppressYield permanently on
                    YieldPatches.SuppressYield = true;
                    var endTurnTask = Task.Run(() =>
                    {
                        PlayerCmd.EndTurn(player, canBackOut: false);
                    });

                    // Aggressively pump sync context while waiting (up to 5 seconds)
                    for (int i = 0; i < 500; i++)
                    {
                        _syncCtx.Pump();
                        if (endTurnTask.IsCompleted) break;
                        if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                        if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                        if (IsPlayPhase()) break;
                        Thread.Sleep(10);
                    }
                    YieldPatches.SuppressYield = false;

                    // If still not play phase, try just waiting a bit more
                    if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            _syncCtx.Pump();
                            Thread.Sleep(10);
                            if (IsPlayPhase() || !CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                                break;
                        }
                    }

                    if (IsPlayPhase())
                        Log("Nuclear fallback SUCCEEDED — play phase resumed");
                    else
                    {
                        Log("Nuclear fallback FAILED — forcing game_over to escape deadlock");
                        return GameOverState(false);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Nuclear fallback error: {ex.Message}");
                    YieldPatches.SuppressYield = false;
                }
            }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCardReward(Player player, Dictionary<string, object?>? args)
    {
        // Handle event-triggered card reward (blocking GetSelectedCardReward)
        if (_cardSelector.HasPendingReward)
        {
            if (args == null || !args.ContainsKey("card_index"))
                return Error("select_card_reward requires 'card_index'");
            var idx = Convert.ToInt32(args["card_index"]);
            Log($"Resolving event card reward: index {idx}");
            var releasedRewardCards = _cardSelector.PendingRewardCards;
            _cardSelector.ResolveReward(idx);
            ResumePendingSelectionTask(releasedRewardCards);
            return DetectDecisionPoint();
        }

        if (_pendingCardReward == null)
            return Error("No pending card reward");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("select_card_reward requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var cards = _pendingCardReward.Cards.ToList();
        if (cardIndex < 0 || cardIndex >= cards.Count)
            return Error($"Invalid card index {cardIndex}, {cards.Count} cards available");

        var card = cards[cardIndex];
        Log($"Selected card reward: {card.GetType().Name}");

        // Add card to deck
        try
        {
            MegaCrit.Sts2.Core.Commands.CardPileCmd
                .Add(card, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck)
                .GetAwaiter().GetResult();
            _syncCtx.Pump();
            RunManager.Instance.RewardSynchronizer.SyncLocalObtainedCard(card);
        }
        catch (Exception ex) { Log($"Add card to deck: {ex.Message}"); }

        _pendingCardReward = null;
        // Check if more rewards pending
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipCardReward(Player player)
    {
        if (_cardSelector.HasPendingReward)
        {
            Log("Skipping event card reward");
            var releasedRewardCards = _cardSelector.PendingRewardCards;
            _cardSelector.SkipReward();
            ResumePendingSelectionTask(releasedRewardCards);
            return DetectDecisionPoint();
        }
        if (_pendingCardReward != null)
        {
            Log("Skipping card reward");
            _pendingCardReward.OnSkipped();
            _pendingCardReward = null;
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoRerollCardReward(Player player)
    {
        if (_cardSelector.HasPendingReward)
        {
            if (!_cardSelector.CanRerollPendingReward)
                return Error("This card reward cannot be rerolled");

            Log("Rerolling event card reward");
            var releasedRewardCards = _cardSelector.PendingRewardCards;
            _cardSelector.ResolveRewardAlternative("REROLL");
            ResumePendingSelectionTask(releasedRewardCards);
            return DetectDecisionPoint();
        }

        if (_pendingCardReward == null)
            return Error("No pending card reward");
        if (!_pendingCardReward.CanReroll)
            return Error("This card reward cannot be rerolled");

        Log("Rerolling card reward");
        _pendingCardReward.Reroll();
        _syncCtx.Pump();
        return CardRewardState(player, _runState?.CurrentRoom as CombatRoom);
    }

    private Dictionary<string, object?> DoSelectCardRewardAlternative(
        Player player,
        Dictionary<string, object?>? args)
    {
        if (args == null || !args.TryGetValue("option_id", out var rawOptionId))
            return Error("select_card_reward_alternative requires 'option_id'");

        var optionId = Convert.ToString(rawOptionId);
        if (string.IsNullOrWhiteSpace(optionId))
            return Error("select_card_reward_alternative requires a non-empty 'option_id'");

        if (_cardSelector.HasPendingReward)
        {
            var releasedRewardCards = _cardSelector.PendingRewardCards;
            if (!_cardSelector.ResolveRewardAlternative(optionId))
                return Error($"Card reward alternative '{optionId}' is not available");
            Log($"Selected event card reward alternative: {optionId}");
            ResumePendingSelectionTask(releasedRewardCards);
            return DetectDecisionPoint();
        }

        if (_pendingCardReward == null)
            return Error("No pending card reward");

        var alternative = GetCardRewardAlternatives(_pendingCardReward)
            .FirstOrDefault(candidate => string.Equals(
                candidate.OptionId,
                optionId,
                StringComparison.OrdinalIgnoreCase));
        if (alternative == null)
            return Error($"Card reward alternative '{optionId}' is not available");

        try
        {
            Log($"Selected card reward alternative: {alternative.OptionId}");
            alternative.OnSelect().GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace($"Card reward alternative '{optionId}' failed", ex);
        }

        if (alternative.AfterSelected == MegaCrit.Sts2.Core.Entities.Rewards.PostAlternateCardRewardAction.EndSelectionAndCompleteReward)
            _pendingCardReward = null;

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyCard(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("buy_card requires 'card_index'");

        var idx = Convert.ToInt32(args["card_index"]);
        var allEntries = merchantRoom.GetLocalInventory().CharacterCardEntries
            .Concat(merchantRoom.GetLocalInventory().ColorlessCardEntries).ToList();
        if (idx < 0 || idx >= allEntries.Count)
            return Error($"Invalid card index {idx}");

        var entry = allEntries[idx];
        if (!entry.IsStocked) return Error("Card already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.GetLocalInventory()).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought card: {entry.CreationResult?.Card?.GetType().Name ?? "?"} for {entry.Cost}g");
        }
        catch (Exception ex) { return Error($"Buy card failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyRelic(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("relic_index"))
            return Error("buy_relic requires 'relic_index'");

        var idx = Convert.ToInt32(args["relic_index"]);
        var entries = merchantRoom.GetLocalInventory().RelicEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid relic index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Relic already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            // The pickup effect can open a card_select (e.g. KIFUDA → enchant up to 3 with
            // Adroit, #80). Run the purchase on a background task and yield as soon as a
            // pending selection appears so the caller can resolve it; the background task
            // continues once the selector's TCS is fed by select_cards.
            var inv = merchantRoom.GetLocalInventory();
            var relicName = entry.Model?.GetType().Name ?? "?";
            _pendingSelectionKind = null;
            var task = Task.Run(() => entry.OnTryPurchaseWrapper(inv));
            // Keep pumping until the purchase has either completed or exposed an actual
            // selection. Returning while the task is merely still running serializes the old
            // gold and inventory, even though the purchase may finish moments later.
            for (int i = 0; i < 3000; i++)
            {
                _syncCtx.Pump();
                if (_cardSelector.HasPending || _cardSelector.HasPendingReward) break;
                if (_pendingBundles != null) break;
                if (task.IsCompleted) break;
                Thread.Sleep(10);
            }
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
            {
                RememberPendingSelectionTask(task, $"shop relic {relicName}");
                Log($"Buy relic {relicName}: yielded for pending selection");
                return DetectDecisionPoint();
            }
            if (!task.IsCompleted)
                return Error($"Buy relic {relicName} timed out before inventory updated");
            task.GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought relic: {relicName} for {entry.Cost}g");
        }
        catch (Exception ex) { return Error($"Buy relic failed: {ex.Message}"); }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyPotion(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("buy_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var entries = merchantRoom.GetLocalInventory().PotionEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid potion index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Potion already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        try
        {
            entry.OnTryPurchaseWrapper(merchantRoom.GetLocalInventory()).GetAwaiter().GetResult();
            _syncCtx.Pump();
            Log($"Bought potion: {entry.Model.GetType().Name} for {entry.Cost}g");
        }
        catch (Exception ex)
        {
            // Potion purchase sometimes NullRefs in headless (missing potion slot UI)
            Log($"Buy potion failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoRemoveCard(Player player)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");

        var removal = merchantRoom.GetLocalInventory().CardRemovalEntry;
        if (removal == null) return Error("No card removal available");
        if (IsShopCardRemovalUsed(removal))
            return Error("Card removal has already been used in this shop");
        if (player.Gold < removal.Cost) return Error("Not enough gold");

        try
        {
            _shopCardRemovalUsed = true;
            // Run on background thread so card selection can pause (same pattern as event options)
            _pendingSelectionKind = "remove";
            var task = Task.Run(() => removal.OnTryPurchaseWrapper(merchantRoom.GetLocalInventory()));
            for (int i = 0; i < 100; i++)
            {
                _syncCtx.Pump();
                if (_cardSelector.HasPending) break;
                if (task.IsCompleted) break;
                Thread.Sleep(10);
            }
            if (_cardSelector.HasPending)
            {
                RememberPendingSelectionTask(task, "shop card removal");
                WaitForActionExecutor();
                return DetectDecisionPoint();
            }
            if (!task.IsCompleted) task.Wait(2000);
            _syncCtx.Pump();
            Log($"Removed card for {removal.Cost}g");
        }
        catch (Exception ex)
        {
            _shopCardRemovalUsed = false;
            return Error($"Remove card failed: {ex.Message}");
        }

        return DetectDecisionPoint();
    }

    private bool IsShopCardRemovalUsed(object removal)
    {
        if (_shopCardRemovalUsed) return true;

        // Preserve the once-per-shop rule after loading a save made inside the shop.
        foreach (var propertyName in new[] { "IsUsed", "Used", "WasUsed" })
        {
            try
            {
                var property = removal.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.PropertyType == typeof(bool) && property.GetValue(removal) is true)
                    return true;
            }
            catch { }
        }
        return false;
    }

    private Dictionary<string, object?> DoSelectBundle(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingBundleTcs == null || _pendingBundles == null)
            return Error("No pending bundle selection");
        if (args == null || !args.ContainsKey("bundle_index"))
            return Error("select_bundle requires 'bundle_index'");

        var idx = Convert.ToInt32(args["bundle_index"]);
        Log($"Bundle selection: pack {idx}");
        var bundles = _pendingBundles;
        var tcs = _pendingBundleTcs;
        _pendingBundles = null;
        _pendingBundleTcs = null;

        // Set result directly (no ContinueWith/ThreadPool)
        var selected = (idx >= 0 && idx < bundles.Count) ? bundles[idx] : bundles[0];
        var deckCountBefore = player.Deck?.Cards?.Count ?? 0;
        tcs.TrySetResult(selected);
        ResumePendingSelectionTask();

        // Some bundle providers are not owned by a tracked room task. Retain the old
        // deck-count fallback for those callers, but do not return while the cards are
        // still being added.
        if (_pendingSelectionTask == null)
        {
            for (int i = 0; i < 200; i++)
            {
                _syncCtx.Pump();
                var deckCountNow = player.Deck?.Cards?.Count ?? 0;
                if (deckCountNow >= deckCountBefore + selected.Count)
                    break;
                Thread.Sleep(10);
            }
            WaitForActionExecutor();
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCards(Player player, Dictionary<string, object?>? args)
    {
        if (!_cardSelector.HasPending)
            return Error("No pending card selection");
        if (args == null || !args.ContainsKey("indices"))
            return Error("select_cards requires 'indices' (comma-separated card indices)");

        var indicesStr = args["indices"]?.ToString() ?? "";
        var indices = indicesStr.Split(',')
            .Select(s => int.TryParse(s.Trim(), out var v) ? v : -1)
            .Where(i => i >= 0)
            .Distinct()
            .ToArray();

        var requiredMin = IsKnowledgeDemonCurseSelection() ? 1 : _cardSelector.PendingMinSelect;
        if (indices.Length < requiredMin || indices.Length > _cardSelector.PendingMaxSelect)
            return Error($"Select {requiredMin}-{_cardSelector.PendingMaxSelect} cards");

        Log($"Card selection: indices [{string.Join(",", indices)}]");
        _cardSelector.ResolvePendingByIndices(indices);
        ResumePendingSelectionTask();

        // Extra wait for rest-site SMITH: the background ChooseLocalOption task
        // needs time to complete the upgrade after card selection resolves.
        if (_runState?.CurrentRoom is RestSiteRoom)
        {
            Thread.Sleep(200);
            _syncCtx.Pump();
            WaitForActionExecutor();
            // Force to map after SMITH completes (same pattern as HEAL)
            Log("Card selection in rest site (SMITH), forcing to map");
            ForceToMap();
            return MapSelectState();
        }

        // Extra wait for shop card removal: the purchase task needs to finish
        if (_runState?.CurrentRoom is MerchantRoom)
        {
            Thread.Sleep(200);
            _syncCtx.Pump();
            WaitForActionExecutor();
            Log("Card selection in shop (card removal), refreshing shop state");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipSelect(Player player)
    {
        if (_cardSelector.HasPending)
        {
            Log("Skipping card selection");
            _cardSelector.CancelPending();
            ResumePendingSelectionTask();
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoCrystalSphereSetTool(Dictionary<string, object?>? args)
    {
        var game = _pendingCrystalSphere;
        if (game == null || game.IsFinished)
            return Error("No Crystal Sphere divination is active");
        if (args == null || !args.TryGetValue("tool", out var rawTool))
            return Error("crystal_sphere_set_tool requires 'tool'");

        var toolName = rawTool?.ToString() ?? "";
        var tool = toolName.Equals("small", StringComparison.OrdinalIgnoreCase)
            ? CrystalSphereMinigame.CrystalSphereToolType.Small
            : toolName.Equals("big", StringComparison.OrdinalIgnoreCase)
                ? CrystalSphereMinigame.CrystalSphereToolType.Big
                : CrystalSphereMinigame.CrystalSphereToolType.None;
        if (tool == CrystalSphereMinigame.CrystalSphereToolType.None)
            return Error("Crystal Sphere tool must be 'small' or 'big'");

        game.SetTool(tool);
        return CrystalSphereState(_runState!.Players[0]);
    }

    private Dictionary<string, object?> DoCrystalSphereReveal(Dictionary<string, object?>? args)
    {
        var game = _pendingCrystalSphere;
        if (game == null || game.IsFinished)
            return Error("No Crystal Sphere divination is active");
        if (args == null || !args.ContainsKey("x") || !args.ContainsKey("y"))
            return Error("crystal_sphere_reveal requires 'x' and 'y'");

        var x = Convert.ToInt32(args["x"]);
        var y = Convert.ToInt32(args["y"]);
        if (x < 0 || y < 0 || x >= game.cells.GetLength(0) || y >= game.cells.GetLength(1))
            return Error($"Crystal Sphere cell ({x},{y}) is outside the grid");
        var cell = game.cells[x, y];
        if (!cell.IsHidden)
            return Error($"Crystal Sphere cell ({x},{y}) is already revealed");

        try
        {
            game.CellClicked(cell).GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("Crystal Sphere reveal failed", ex);
        }

        if (!game.IsFinished)
            return CrystalSphereState(_runState!.Players[0]);

        _pendingCrystalSphere = null;
        ResumePendingSelectionTask();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoUsePotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("use_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        // Determine target based on potion's TargetType first, then fall back to target_index
        Creature? target = null;
        var potionTargetType = potion.TargetType;

        // Self-targeting potions (Flex, Fortifier, etc.) ALWAYS target the player
        // regardless of any target_index the caller provides
        if (potionTargetType == TargetType.Self || potionTargetType == TargetType.TargetedNoCreature)
        {
            target = player.Creature;
        }
        else if (potionTargetType == TargetType.AnyEnemy)
        {
            // Use caller's target_index if provided, otherwise pick first alive enemy
            if (args.TryGetValue("target_index", out var tObj) && tObj != null)
            {
                var targetIdx = Convert.ToInt32(tObj);
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                if (combatState != null)
                {
                    var enemies = combatState.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIdx >= 0 && targetIdx < enemies.Count)
                        target = enemies[targetIdx];
                }
            }
            // Same single-enemy rule as play_card: only auto-target when unambiguous (#79).
            if (target == null && CombatManager.Instance.IsInProgress)
            {
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                var alive = combatState?.Enemies?.Where(e => e != null && e.IsAlive).ToList() ?? new();
                if (alive.Count == 1)
                    target = alive[0];
                else if (alive.Count > 1)
                    return Error($"Potion {potion.Id.Entry} targets a single enemy (AnyEnemy); " +
                                 $"'target_index' is required when multiple enemies are alive ({alive.Count}).");
            }
        }
        // All other target types (None, All, etc.) → leave target as null

        Log($"Using potion: {potion.GetType().Name} at slot {idx} target={target?.GetType().Name ?? "none"}");
        try
        {
            var action = new MegaCrit.Sts2.Core.GameActions.UsePotionAction(potion, target, CombatManager.Instance.IsInProgress);
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
            WaitForActionExecutor();
            _syncCtx.Pump();

            // Effect may require card_select before the potion slot clears — do not discard as "stuck".
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward)
                return DetectDecisionPoint();

            // Verify potion was consumed
            var afterPotions = player.Potions?.ToList() ?? new();
            if (afterPotions.Contains(potion))
            {
                // Potion wasn't consumed — manually discard it
                Log("Potion not consumed by action, manually discarding");
                MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
        }
        catch (Exception ex)
        {
            Log($"Use potion failed: {ex.Message}");
            // Try manual discard as fallback
            try { MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult(); } catch { }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoDiscardPotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("discard_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
        _syncCtx.Pump();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoResolvePotionReward(Dictionary<string, object?>? args)
    {
        var choice = _pendingPotionChoice;
        var reward = _pendingPotionReward;
        if (reward == null || choice == null)
            return Error("No potion reward is waiting for a slot");

        int? potionIndex = null;
        if (args != null)
        {
            if (!args.TryGetValue("potion_index", out var rawIndex))
                return Error("replace_potion requires 'potion_index'");
            potionIndex = Convert.ToInt32(rawIndex);
            var potions = _runState!.Players[0].Potions?.ToList() ?? new();
            if (potionIndex < 0 || potionIndex >= potions.Count)
                return Error($"Invalid potion index {potionIndex}, inventory has {potions.Count} potions");
        }

        Log(potionIndex.HasValue
            ? $"Replacing potion {potionIndex.Value} for {reward.Potion?.Id.Entry ?? "?"}"
            : $"Skipping potion reward {reward.Potion?.Id.Entry ?? "?"}");
        _pendingPotionReward = null;
        _pendingPotionChoice = null;
        choice.TrySetResult(potionIndex);
        ResumePendingSelectionTask();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoChooseOption(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("option_index"))
            return Error("choose_option requires 'option_index'");

        var optionIndex = Convert.ToInt32(args["option_index"]);
        Log($"Choosing option {optionIndex}");

        // Dispatch based on ROOM TYPE (not event state) to avoid cross-contamination
        if (_runState?.CurrentRoom is RestSiteRoom restSiteRoom)
        {
            Log($"Rest site: choosing option {optionIndex}");
            try
            {
                // Run on background thread so Smith card selection can pause
                _pendingSelectionKind = optionIndex >= 0 && optionIndex < restSiteRoom.Options.Count &&
                    restSiteRoom.Options[optionIndex].OptionId == "SMITH"
                    ? "upgrade"
                    : null;
                var task = Task.Run(() => RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(optionIndex));
                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    if (_cardSelector.HasPending) break;
                    if (task.IsCompleted) break;
                    Thread.Sleep(10);
                }
                if (_cardSelector.HasPending)
                {
                    RememberPendingSelectionTask(task, "rest site option");
                    WaitForActionExecutor();
                    return DetectDecisionPoint();
                }
                if (!task.IsCompleted) task.Wait(2000);
                _syncCtx.Pump();
            }
            catch (Exception ex)
            {
                Log($"Rest site ChooseLocalOption failed: {ex.Message}");
            }

            // After non-Smith rest site options (HEAL, etc.), the options may not clear.
            // Wait for the action to complete (heal/dig), then force transition to map.
            if (!_cardSelector.HasPending)
            {
                Log("Rest site: option chosen (non-Smith), waiting for action then forcing to map");
                // Give the action time to complete (heal HP, dig for relic, etc.)
                WaitForActionExecutor();
                _syncCtx.Pump();
                Thread.Sleep(200);
                _syncCtx.Pump();
                WaitForActionExecutor();
                ForceToMap();
                return MapSelectState();
            }
        }
        // For events — use EventSynchronizer
        // Run Chosen() on a background thread so card selections can pause
        else if (_runState?.CurrentRoom is EventRoom)
        {
            var eventSync = RunManager.Instance.EventSynchronizer;
            var localEvent = eventSync?.GetLocalEvent();
            if (localEvent != null && !localEvent.IsFinished)
            {
                var options = localEvent.CurrentOptions;
                var optCountBefore = options?.Count ?? 0;
                if (options != null && optionIndex >= 0 && optionIndex < options.Count)
                {
                    if (localEvent.Id?.Entry == "CRYSTAL_SPHERE")
                        return BeginCrystalSphereEvent(player, localEvent, options[optionIndex].TextKey);
                    try
                    {
                        _pendingSelectionInfo = null;
                        _pendingSelectionKind = null;
                        var eventVars = new Dictionary<string, object?>();
                        try
                        {
                            if (localEvent.DynamicVars?.Values != null)
                                foreach (var dv in localEvent.DynamicVars.Values)
                                    eventVars[dv.Name] = DynamicVarDisplayValue(dv);
                        }
                        catch { }
                        _pendingSelectionInfo = EventEnchantmentInfo(
                            options[optionIndex].TextKey, eventVars);
                        _eventOptionChosen = true;
                        _lastEventOptionCount = options.Count;
                        // Run on thread pool so GetSelectedCards/GetSelectedCardReward can block
                        var task = Task.Run(() => options[optionIndex].Chosen());
                        for (int i = 0; i < 500; i++)
                        {
                            _syncCtx.Pump();
                            if (_cardSelector.HasPending || _cardSelector.HasPendingReward) break;
                            if (_pendingBundles != null) break;
                            if (_pendingCrystalSphere != null) break;
                            if (_pendingPotionChoice != null) break;
                            if (task.IsCompleted) break;
                            // Event effects commonly await an ActionExecutor command before
                            // changing page. Drive it while the event task is still pending;
                            // waiting on the task alone leaves both sides blocked.
                            if (i == 100) WaitForActionExecutor();
                            Thread.Sleep(10);
                        }
                        if (_cardSelector.HasPending || _cardSelector.HasPendingReward ||
                            _pendingBundles != null || _pendingCrystalSphere != null ||
                            _pendingPotionChoice != null)
                        {
                            RememberPendingSelectionTask(task, $"event {localEvent.GetType().Name}");
                            WaitForActionExecutor();
                            return DetectDecisionPoint();
                        }
                        if (!task.IsCompleted)
                        {
                            RememberPendingSelectionTask(task, $"event {localEvent.GetType().Name}");
                            return Error($"Event {localEvent.GetType().Name} is still processing");
                        }
                        task.GetAwaiter().GetResult();
                        _syncCtx.Pump();
                        _pendingSelectionInfo = null;
                    }
                    catch (Exception ex)
                    {
                        _pendingSelectionInfo = null;
                        Log($"Event choose failed: {ex}");
                    }
                }

                // Note: do NOT force-finish on `optCountAfter == optCountBefore`. Events can
                // legitimately stay interactive with the same option count (Slippery Bridge
                // Hold On loops until Overcome is chosen, #59). Trust IsFinished and let the
                // next DetectDecisionPoint return the current options for the next choice.
            }
        }

        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> BeginCrystalSphereEvent(
        Player player, EventModel localEvent, string? optionKey)
    {
        try
        {
            int ReadVar(string name, int fallback)
            {
                try { return Convert.ToInt32(localEvent.DynamicVars[name].BaseValue); }
                catch { return fallback; }
            }

            int count;
            if (optionKey?.EndsWith(".UNCOVER_FUTURE", StringComparison.Ordinal) == true)
            {
                var cost = ReadVar("UncoverFutureCost", 100);
                if (player.Gold < cost)
                    return Error("Not enough gold for Crystal Sphere divination");
                PlayerCmd.LoseGold((decimal)cost, player,
                    (MegaCrit.Sts2.Core.Entities.Gold.GoldLossType)1).GetAwaiter().GetResult();
                count = ReadVar("UncoverFutureProphesizeCount", 3);
            }
            else if (optionKey?.EndsWith(".PAYMENT_PLAN", StringComparison.Ordinal) == true)
            {
                CardPileCmd.AddCurseToDeck<MegaCrit.Sts2.Core.Models.Cards.Debt>(player)
                    .GetAwaiter().GetResult();
                count = ReadVar("PaymentPlanCount", 6);
            }
            else
            {
                return Error("Unknown Crystal Sphere option");
            }

            var game = new CrystalSphereMinigame(player, localEvent.Rng, count);
            _pendingCrystalSphere = game;
            var task = Task.Run(async () =>
            {
                await game.PlayMinigame();
                var finish = typeof(EventModel).GetMethod("SetEventFinished",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                finish?.Invoke(localEvent, new object[]
                {
                    new LocString("events", "CRYSTAL_SPHERE.pages.FINISH.description"),
                });
            });
            RememberPendingSelectionTask(task, "Crystal Sphere event");
            return CrystalSphereState(player);
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("Crystal Sphere setup failed", ex);
        }
    }

    private Dictionary<string, object?> DoLeaveRoom(Player player)
    {
        Log("Leaving room");
        try { RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult(); }
        catch { }
        _syncCtx.Pump();
        WaitForActionExecutor();

        // If still in a non-combat room, force to map
        var room = _runState?.CurrentRoom;
        if (room is RestSiteRoom || room is MerchantRoom || room is EventRoom || room is TreasureRoom)
        {
            Log("Force leaving non-combat room to map");
            try
            {
                RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex) { Log($"Force leave: {ex.Message}"); }
        }
        return DetectDecisionPoint();
    }

    /// <summary>
    /// Headless mode skips Godot transition callbacks that normally trigger between-act healing
    /// (AncientEventModel.BeforeEventStarted). Replicates the original sts2.dll formula:
    ///   healAmount = MaxHp - CurrentHp  (i.e. heal to full)
    ///   if Ascension >= 2: healAmount *= 0.8
    /// No-op if the engine already healed (missingHp &lt;= 0), so this is safe alongside any
    /// future engine path that does fire the callback.
    /// Adapted from PR #83 commit cf75bec by @tianyumyum.
    /// </summary>
    private void HealBetweenActs()
    {
        if (_runState == null) return;
        var player = _runState.Players[0];
        if (player.Creature == null) return;

        var currentHp = player.Creature.CurrentHp;
        var maxHp = player.Creature.MaxHp;
        var missingHp = maxHp - currentHp;
        if (missingHp <= 0) return;

        decimal healAmount = missingHp;
        if (RunManager.Instance.HasAscension((AscensionLevel)2))
            healAmount *= 0.8m;

        var newHp = currentHp + (int)Math.Ceiling(healAmount);
        if (newHp > maxHp) newHp = maxHp;
        SetField(player.Creature, "_currentHp", newHp);
        Log($"Between-act heal: {currentHp} → {newHp} (missing={missingHp}, ascension2+={RunManager.Instance.HasAscension((AscensionLevel)2)})");
    }

    private void EnterActAncientIfNeeded()
    {
        if (_runState?.Map?.StartingMapPoint == null)
            return;
        if (_runState.CurrentRoom is EventRoom)
            return;

        var startPoint = _runState.Map.StartingMapPoint;
        if (!string.Equals(startPoint.PointType.ToString(), "Ancient", StringComparison.OrdinalIgnoreCase))
            return;

        Log($"Entering Act {_runState.CurrentActIndex + 1} Ancient reward node");
        if (!TryCaptureRoomStartCheckpoint(startPoint.coord, out var checkpointError))
            throw new InvalidOperationException($"Cannot checkpoint Ancient room: {checkpointError}");
        RunManager.Instance.EnterMapCoord(startPoint.coord).GetAwaiter().GetResult();
        _syncCtx.Pump();
        WaitForActionExecutor();
    }

    private Dictionary<string, object?> DoProceed(Player player)
    {
        Log("Proceeding");

        // Check if we need to move to next act (boss defeated)
        var room = _runState?.CurrentRoom;
        if (room is CombatRoom combatRoom && combatRoom.RoomType == RoomType.Boss)
        {
            if (combatRoom.IsPreFinished || !CombatManager.Instance.IsInProgress)
            {
                // Final act boss → victory (same rule as DetectPostCombatState, #81).
                if (_runState != null && _runState.CurrentActIndex >= 2)
                {
                    Log($"Final boss defeated via Proceed (Act {_runState.CurrentActIndex + 1}), reporting victory");
                    return GameOverState(true);
                }
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                WaitForActionExecutor();
                HealBetweenActs();
                EnterActAncientIfNeeded();
                return DetectDecisionPoint();
            }
        }

        RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    #endregion

    #region Decision Point Detection

    private Dictionary<string, object?> DetectDecisionPoint()
    {
        if (_runState == null)
            return Error("No run in progress");

        var player = _runState.Players[0];

        // Check game over (death)
        if (player.Creature != null && player.Creature.IsDead)
        {
            return GameOverState(false);
        }

        if (_pendingPotionReward != null && _pendingPotionChoice != null)
        {
            return PotionReplaceState(player, _pendingPotionReward);
        }

        if (_pendingCrystalSphere != null)
        {
            if (!_pendingCrystalSphere.IsFinished)
                return CrystalSphereState(player);
            _pendingCrystalSphere = null;
        }

        // Check if there's a pending bundle selection (Scroll Boxes: pick 1 of N packs)
        if (_pendingBundles != null && _pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted)
        {
            var bundles = _pendingBundles.Select((bundle, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["cards"] = bundle.Select(card => CardSummary(card)).ToList(),
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "bundle_select",
                ["context"] = RunContext(),
                ["bundles"] = bundles,
                ["player"] = PlayerSummary(player),
            };
        }

        // Check if there's a pending card reward from event (GetSelectedCardReward blocking)
        if (_cardSelector.HasPendingReward)
        {
            var rewardCards = _cardSelector.PendingRewardCards!;
            var cards = rewardCards.Select((cr, i) =>
            {
                var cardInfo = CardSummary(cr.Card, includeAfterUpgrade: true);
                cardInfo["index"] = i;
                return cardInfo;
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_reward",
                ["context"] = RunContext(),
                ["cards"] = cards,
                ["can_skip"] = true,
                ["can_reroll"] = _cardSelector.CanRerollPendingReward,
                ["alternatives"] = RewardAlternativeSummaries(_cardSelector.PendingRewardAlternatives),
                ["from_event"] = true,
                ["player"] = PlayerSummary(_runState!.Players[0]),
            };
        }

        // Check if there's a pending card selection (upgrade, remove, transform, start-of-turn powers)
        checkCardSelect:
        if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
        {
            var isKnowledgeCurse = IsKnowledgeDemonCurseSelection();
            var opts = _cardSelector.PendingOptions.Select((card, i) =>
            {
                var cardInfo = CardSummary(card, includeAfterUpgrade: true);
                cardInfo["index"] = i;
                return cardInfo;
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_select",
                ["context"] = RunContext(),
                ["selection_title"] = isKnowledgeCurse
                    ? _loc.Bilingual("monsters", "KNOWLEDGE_DEMON.moves.CURSE_OF_KNOWLEDGE.title")
                    : null,
                ["selection_kind"] = _pendingSelectionKind,
                ["cards"] = opts,
                ["min_select"] = isKnowledgeCurse ? 1 : _cardSelector.PendingMinSelect,
                ["max_select"] = _cardSelector.PendingMaxSelect,
                ["selection_info"] = _pendingSelectionInfo,
                ["player"] = PlayerSummary(player),
            };
        }

        // Check if there's a pending card reward
        if (_pendingCardReward != null)
        {
            return CardRewardState(player, _runState.CurrentRoom as CombatRoom);
        }

        // Check if RunManager reports game over (victory)
        if (RunManager.Instance.IsGameOver)
        {
            return GameOverState(true);
        }

        var room = _runState.CurrentRoom;

        // Map room — need to select a node
        if (room is MapRoom || room == null)
        {
            return MapSelectState();
        }

        // Combat room
        if (room is CombatRoom combatRoom)
        {
            // With Task.Yield() patched, combat init should be synchronous
            _syncCtx.Pump();
            WaitForActionExecutor();

            // Re-check for pending card selections AFTER pump (BUG-024: start-of-turn effects
            // like Tools of Trade create card selections during Pump, AFTER the initial HasPending check)
            if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
            {
                goto checkCardSelect;  // Jump back to card_select handling
            }

            // The engine can briefly leave the player in Play phase after an enemy dies
            // while death, summon, revival, and combat-end continuations are settling. Give
            // those continuations time to surface their next decision point. An empty enemy
            // list is not itself proof that combat ended: Test Subject intentionally remains
            // dead until the player ends the turn and its Respawn move runs.
            if (CombatManager.Instance.IsInProgress && !HasLivingEnemies())
            {
                Log("Combat has no living enemies while still marked in progress; waiting for resolution");
                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
                        goto checkCardSelect;
                    if (_cardSelector.HasPendingReward ||
                        (_pendingBundles != null && _pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted))
                        return DetectDecisionPoint();
                    if (HasLivingEnemies() || !CombatManager.Instance.IsInProgress ||
                        (player.Creature != null && player.Creature.IsDead))
                        break;

                    if (RunManager.Instance.ActionExecutor.IsRunning)
                        WaitForActionExecutor();
                    Thread.Sleep(5);
                }
            }

            if (player.Creature != null && player.Creature.IsDead)
                return GameOverState(false);

            if (!HasLivingEnemies())
            {
                if (CombatManager.Instance.IsInProgress && !combatRoom.IsPreFinished)
                {
                    Log("No living enemies, but combat is awaiting death/revival resolution");
                    return CombatPlayState(player);
                }

                Log("No living enemies remain and combat has ended; advancing to post-combat state");
                return DetectPostCombatState(player, combatRoom);
            }

            if (CombatManager.Instance.IsInProgress && IsPlayPhase())
            {
                return CombatPlayState(player);
            }
            if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
            {
                return DetectPostCombatState(player, combatRoom);
            }
            // Fallback: brief wait
            for (int i = 0; i < 20; i++)
            {
                _syncCtx.Pump();
                Thread.Sleep(5);
                if (IsPlayPhase() && HasLivingEnemies()) return CombatPlayState(player);
                if (!CombatManager.Instance.IsInProgress) return DetectPostCombatState(player, combatRoom);
            }
            if (!HasLivingEnemies() &&
                (!CombatManager.Instance.IsInProgress || combatRoom.IsPreFinished))
                return DetectPostCombatState(player, combatRoom);
            return CombatPlayState(player);
        }

        // Event room
        if (room is EventRoom eventRoom)
        {
            return EventChoiceState(eventRoom);
        }

        // Rest site
        if (room is RestSiteRoom restRoom)
        {
            return RestSiteState(restRoom);
        }

        // Merchant/Shop
        if (room is MerchantRoom merchantRoom)
        {
            return ShopState(merchantRoom, player);
        }

        // Treasure room
        if (room is TreasureRoom treasureRoom)
        {
            return TreasureState(treasureRoom);
        }

        // Fallback
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "unknown",
            ["context"] = RunContext(),
            ["room_type"] = room?.GetType().Name,
            ["message"] = "Unknown room type or state",
        };
    }

    private Dictionary<string, object?> CrystalSphereState(Player player)
    {
        var game = _pendingCrystalSphere!;
        var width = game.cells.GetLength(0);
        var height = game.cells.GetLength(1);
        var rows = new List<List<Dictionary<string, object?>>>(height);
        for (int y = 0; y < height; y++)
        {
            var row = new List<Dictionary<string, object?>>(width);
            for (int x = 0; x < width; x++)
            {
                var cell = game.cells[x, y];
                var item = !cell.IsHidden ? cell.Item : null;
                var serializedItem = item?.ToSerializable();
                row.Add(new Dictionary<string, object?>
                {
                    ["x"] = x,
                    ["y"] = y,
                    ["hidden"] = cell.IsHidden,
                    ["item"] = item?.GetType().Name.Replace("CrystalSphere", ""),
                    ["is_good"] = item?.IsGood,
                    ["rarity"] = serializedItem?.type == CrystalSphereItemType.CardReward
                        ? serializedItem.cardRarity.ToString()
                        : null,
                });
            }
            rows.Add(row);
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "crystal_sphere",
            ["context"] = RunContext(),
            ["event_name"] = _loc.Event("CRYSTAL_SPHERE"),
            ["remaining"] = game.DivinationCount,
            ["tool"] = game.CrystalSphereTool.ToString(),
            ["width"] = width,
            ["height"] = height,
            ["rows"] = rows,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> MapSelectState()
    {
        var map = _runState?.Map;
        if (map == null)
        {
            Log("Map is null, generating...");
            try
            {
                RunManager.Instance.GenerateMap().GetAwaiter().GetResult();
                _syncCtx.Pump();
                map = _runState?.Map;
            }
            catch (Exception ex)
            {
                Log($"GenerateMap failed: {ex.Message}");
            }
            if (map == null)
                return Error("No map available");
        }
        var currentCoord = _runState!.CurrentMapCoord;

        List<Dictionary<string, object?>> choices;
        if (currentCoord.HasValue)
        {
            var currentPoint = map.GetPoint(currentCoord.Value);
            if (currentPoint == null)
            {
                Log($"GetPoint returned null for coord ({currentCoord.Value.col},{currentCoord.Value.row}), falling back to start");
                // Current coord is invalid (stale after forced room transition); treat as no position
                choices = new List<Dictionary<string, object?>>();
                var sp = map.StartingMapPoint;
                if (sp?.Children != null)
                {
                    foreach (var child in sp.Children)
                    {
                        choices.Add(new Dictionary<string, object?>
                        {
                            ["col"] = (int)child.coord.col,
                            ["row"] = (int)child.coord.row,
                            ["type"] = child.PointType.ToString(),
                        });
                    }
                }
            }
            else
            {
                choices = (currentPoint.Children ?? Enumerable.Empty<MapPoint>())
                    .Select(child => new Dictionary<string, object?>
                    {
                        ["col"] = (int)child.coord.col,
                        ["row"] = (int)child.coord.row,
                        ["type"] = child.PointType.ToString(),
                    })
                    .ToList();
            }
        }
        else
        {
            // The first node of every act is the mandatory Ancient reward. Exposing its
            // children here allowed callers to skip it and enter a monster room directly.
            var startPoint = map.StartingMapPoint;
            choices = new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["col"] = (int)startPoint.coord.col,
                    ["row"] = (int)startPoint.coord.row,
                    ["type"] = startPoint.PointType.ToString(),
                }
            };
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "map_select",
            ["context"] = RunContext(),
            ["choices"] = choices,
            ["player"] = PlayerSummary(_runState!.Players[0]),
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
        };
    }

    private bool IsKnowledgeDemonCurseSelection()
    {
        var options = _cardSelector.PendingOptions;
        if (options == null || options.Count != 2) return false;
        var ids = options.Select(card => card.Id.Entry).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ids.Contains("DISINTEGRATION") && ids.Contains("MIND_ROT");
    }

    private Dictionary<string, object?> CardStats(CardModel card, bool resolvePreview)
    {
        var stats = new Dictionary<string, object?>();
        var previewResolved = false;
        try
        {
            if (resolvePreview)
            {
                card.DynamicVars.ClearPreview();
                card.UpdateDynamicVarPreview(
                    MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.Normal,
                    card.CurrentTarget, card.DynamicVars);
                previewResolved = true;
            }
        }
        catch { }
        try
        {
            foreach (var dv in card.DynamicVars.Values)
                stats[dv.Name.ToLowerInvariant()] = (int)(previewResolved ? dv.PreviewValue : dv.BaseValue);
        }
        catch { }
        finally
        {
            if (resolvePreview)
            {
                try { card.DynamicVars.ClearPreview(); } catch { }
            }
        }

        if (resolvePreview)
            ApplyCardEnchantmentStatFallback(card, stats);
        return stats;
    }

    private void ApplyCardEnchantmentStatFallback(
        CardModel card, Dictionary<string, object?> stats)
    {
        // Some preview paths leave additive enchantments at the card's base value. Apply the
        // documented modifier only when the preview is still exactly base, preventing a double
        // count when the game has already resolved it.
        if (card.Enchantment != null)
        {
            var entry = card.Enchantment.Id.Entry;
            var amount = 0;
            try { amount = card.Enchantment.Amount; } catch { }
            var affectedStats = entry switch
            {
                "SHARP" => new[] { "damage", "calculateddamage", "ostydamage" },
                "NIMBLE" => new[] { "block" },
                _ => Array.Empty<string>(),
            };
            foreach (var affectedStat in affectedStats)
            {
                if (amount == 0 || !stats.TryGetValue(affectedStat, out var rawValue))
                    continue;
                try
                {
                    var baseValue = (int)card.DynamicVars.Values
                        .First(dv => string.Equals(dv.Name, affectedStat, StringComparison.OrdinalIgnoreCase))
                        .BaseValue;
                    var currentValue = Convert.ToInt32(rawValue);
                    if (currentValue == baseValue)
                        stats[affectedStat] = currentValue + amount;
                }
                catch { }
            }
        }
    }

    private Dictionary<string, object?> CardModifierInfo(object modifier, string table, string entry, int amount)
    {
        var vars = new Dictionary<string, object?> { ["Amount"] = amount };
        try
        {
            var dynamicVars = modifier.GetType().GetProperty("DynamicVars")?.GetValue(modifier);
            var values = dynamicVars?.GetType().GetProperty("Values")?.GetValue(dynamicVars)
                as System.Collections.IEnumerable;
            if (values != null)
            {
                foreach (var value in values)
                {
                    if (value == null) continue;
                    var name = value.GetType().GetProperty("Name")?.GetValue(value)?.ToString();
                    var baseValue = value.GetType().GetProperty("BaseValue")?.GetValue(value);
                    if (!string.IsNullOrEmpty(name) && baseValue != null)
                        vars[name] = Convert.ToInt32(baseValue);
                }
            }
        }
        catch { }
        return new Dictionary<string, object?>
        {
            ["name"] = _loc.Bilingual(table, entry + ".title"),
            ["description"] = _loc.Bilingual(table, entry + ".description"),
            ["vars"] = vars.Count > 0 ? vars : null,
            ["amount"] = amount,
        };
    }

    private void AddCardModifierInfo(Dictionary<string, object?> cardInfo, CardModel card)
    {
        if (card.Enchantment != null)
        {
            var entry = card.Enchantment.Id.Entry;
            var amount = 0;
            try { amount = card.Enchantment.Amount; } catch { }
            var info = CardModifierInfo(card.Enchantment, "enchantments", entry, amount);
            cardInfo["enchantment"] = info["name"];
            if (amount != 0) cardInfo["enchantment_amount"] = amount;
            cardInfo["enchantment_info"] = info;
        }
        if (card.Affliction != null)
        {
            var entry = card.Affliction.Id.Entry;
            var amount = 0;
            try { amount = card.Affliction.Amount; } catch { }
            var info = CardModifierInfo(card.Affliction, "afflictions", entry, amount);
            cardInfo["affliction"] = info["name"];
            if (amount != 0) cardInfo["affliction_amount"] = amount;
            cardInfo["affliction_info"] = info;
        }
    }

    private Dictionary<string, object?> CardSummary(
        CardModel card, bool includeAfterUpgrade = false, bool resolvePreview = true)
    {
        var stats = CardStats(card, resolvePreview);
        string? cardVariant = null;
        if (card.Id.Entry == "MAD_SCIENCE")
        {
            try
            {
                var rider = card.GetType().GetProperty("TinkerTimeRider")?.GetValue(card);
                var riderName = rider?.ToString();
                var hasRider = !string.IsNullOrEmpty(riderName)
                    && !string.Equals(riderName, "None", StringComparison.OrdinalIgnoreCase);
                stats["cardtype"] = card.Type.ToString();
                stats["hasrider"] = hasRider ? 1 : 0;
                if (hasRider)
                {
                    stats[riderName!.ToLowerInvariant()] = 1;
                    cardVariant = riderName;
                }
            }
            catch (Exception ex) { Log($"Read Mad Science rider: {ex.Message}"); }
        }
        var keywords = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
        var cardInfo = new Dictionary<string, object?>
        {
            ["id"] = card.Id.ToString(),
            ["name"] = _loc.Card(card.Id.Entry),
            ["cost"] = card.EnergyCost?.GetResolved() ?? 0,
            ["cost_is_x"] = card.EnergyCost?.CostsX ?? false,
            ["type"] = card.Type.ToString(),
            ["rarity"] = card.Rarity.ToString(),
            ["upgraded"] = card.IsUpgraded,
            ["description"] = _loc.Bilingual("cards", card.Id.Entry + ".description"),
            ["stats"] = stats.Count > 0 ? stats : null,
            ["keywords"] = keywords?.Count > 0 ? keywords : null,
        };
        if (cardVariant != null)
            cardInfo["variant"] = cardVariant;
        if (includeAfterUpgrade)
            cardInfo["after_upgrade"] = GetUpgradedInfo(card);
        AddCardModifierInfo(cardInfo, card);
        return cardInfo;
    }

    private Dictionary<string, object?> CombatPileCardSummary(CardModel card)
    {
        return CardSummary(card);
    }

    private Dictionary<string, object?> CombatPlayState(Player player)
    {
        var pcs = player.PlayerCombatState;
        var combatState = CombatManager.Instance.DebugOnlyGetState();

        // Track last known HP for accurate game_over reporting (BUG-005)
        if (player.Creature != null && player.Creature.CurrentHp > 0)
            _lastKnownHp = player.Creature.CurrentHp;

        // Alive enemies in the same order play_card's AnyEnemy targeting uses, so
        // damage_by_target[i].target_index aligns with the target_index clients pass.
        var aliveEnemiesForTargeting = combatState?.Enemies?
            .Where(e => e != null && e.IsAlive).ToList() ?? new();

        var hand = pcs?.Hand?.Cards?.Select((c, i) =>
        {
            // Export the *currently resolved* stat values, not the card base: refresh the
            // DynamicVar previews (mirrors NCard.UpdateVisuals) so damage reflects Strength/Weak,
            // block reflects Frail, calculateddamage reflects current Block, etc. ClearPreview
            // resets PreviewValue to BaseValue, and only damage/block/calculated vars override it,
            // so reading PreviewValue uniformly is safe. Issues #65 #69 #70 #71 #74 #75.
            var stats = CardStats(c, resolvePreview: true);

            // Per-target resolved damage for attack cards: the scalar `stats` above use the
            // card's CurrentTarget, but a single value can't capture target-specific modifiers
            // (Vulnerable #60, Slow #77) or conditional hit counts (Dismantle #78, X-cost
            // Whirlwind #82). Re-run the preview per enemy via MultiCreatureTargeting, the same
            // path the game uses to draw multi-target previews, and read the resolved vars.
            List<Dictionary<string, object?>>? damageByTarget = null;
            if (c.Type == CardType.Attack && aliveEnemiesForTargeting.Count > 0
                && (c.TargetType == TargetType.AnyEnemy || c.TargetType == TargetType.AllEnemies))
            {
                damageByTarget = new List<Dictionary<string, object?>>();
                for (int ti = 0; ti < aliveEnemiesForTargeting.Count; ti++)
                {
                    var tgt = aliveEnemiesForTargeting[ti];
                    try
                    {
                        c.DynamicVars.ClearPreview();
                        c.UpdateDynamicVarPreview(
                            MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.MultiCreatureTargeting,
                            tgt, c.DynamicVars);
                        var tstats = new Dictionary<string, object?>();
                        foreach (var dv in c.DynamicVars.Values)
                            tstats[dv.Name.ToLowerInvariant()] = (int)dv.PreviewValue;
                        ApplyCardEnchantmentStatFallback(c, tstats);

                        // Per-hit damage = calculateddamage (override cards) or damage.
                        int? perHit = tstats.TryGetValue("calculateddamage", out var cdv) && cdv is int cdi && cdi > 0
                            ? cdi
                            : (tstats.TryGetValue("damage", out var dv2) && dv2 is int di ? di : (int?)null);
                        // Hit count: most cards use `repeat`; Tear Asunder exposes its
                        // HP-loss-scaled count as `CalculatedHits` instead. X-cost attacks use
                        // the current X (= available energy), e.g. Whirlwind (#82).
                        int repeat = tstats.TryGetValue("repeat", out var rv) && rv is int ri && ri > 0
                            ? ri
                            : tstats.TryGetValue("calculatedhits", out var chv) && chv is int chi && chi > 0
                                ? chi
                                : 1;
                        if (repeat == 1 && c.EnergyCost?.CostsX == true && pcs != null)
                            repeat = pcs.Energy;
                        // Dismantle hits twice when the target is Vulnerable (#78). The doubled
                        // hit count lives in Dismantle.OnPlay, not in any DynamicVar preview or
                        // the Hook.ModifyAttackHitCount path (which needs an AttackCommand we
                        // don't have at preview time), so it's special-cased by card entry.
                        if (repeat == 1 && c.Id.Entry == "DISMANTLE" && tgt.Powers != null
                            && tgt.Powers.Any(p => p?.Id.Entry == "VULNERABLE_POWER"))
                            repeat = 2;

                        var row = new Dictionary<string, object?>
                        {
                            ["target_index"] = ti,
                            ["name"] = _loc.Monster(tgt.Monster?.Id.Entry ?? "UNKNOWN"),
                        };
                        if (perHit != null) row["damage"] = perHit;
                        if (repeat > 1)
                        {
                            row["repeat"] = repeat;
                            if (perHit != null) row["total_damage"] = perHit * repeat;
                        }
                        damageByTarget.Add(row);
                    }
                    catch { }
                    finally { c.DynamicVars.ClearPreview(); }
                }
            }

            // Use CurrentStarCost (combat-modified) for UI/can_play; BaseStarCost ignores temporary reductions.
            var starCost = c.CurrentStarCost;
            var cardInfo = CardSummary(c, resolvePreview: false);
            cardInfo["index"] = i;
            cardInfo["can_play"] = c.CanPlay(out _, out _);
            cardInfo["target_type"] = c.TargetType.ToString();
            cardInfo["stats"] = stats.Count > 0 ? stats : null;
            if (starCost > 0)
            {
                cardInfo["star_cost"] = starCost;
                // BUG-007: Override can_play for star-cost cards when player lacks stars
                if (pcs != null && pcs.Stars < starCost)
                    cardInfo["can_play"] = false;
            }
            if (damageByTarget != null && damageByTarget.Count > 0)
                cardInfo["damage_by_target"] = damageByTarget;
            return cardInfo;
        }).ToList() ?? new();

        var playerCreatures = combatState?.PlayerCreatures?.ToList();

        var enemies = combatState?.Enemies?
            .Where(e => e != null && e.IsAlive)
            .Select((e, i) =>
            {
                // Extract detailed intent info
                var intents = new List<Dictionary<string, object?>>();
                try
                {
                    if (e.Monster?.NextMove?.Intents != null)
                    {
                        foreach (var intent in e.Monster.NextMove.Intents)
                        {
                            var intentInfo = new Dictionary<string, object?>
                            {
                                ["type"] = intent.IntentType.ToString(),
                            };
                            // Get damage for attack intents
                            if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk && playerCreatures != null)
                            {
                                try
                                {
                                    // For multi-hit attacks, expose per-hit `damage` (matching the
                                    // game's intent description, which pairs GetSingleDamage with
                                    // Repeat) plus an explicit `total_damage`. Reporting the total in
                                    // `damage` while also reporting `hits` let clients compute
                                    // damage*hits and double-count incoming damage (#67).
                                    var hits = atk.Repeats;
                                    if (hits > 1)
                                    {
                                        intentInfo["damage"] = atk.GetSingleDamage(playerCreatures, e);
                                        intentInfo["hits"] = hits;
                                        intentInfo["total_damage"] = atk.GetTotalDamage(playerCreatures, e);
                                    }
                                    else
                                    {
                                        intentInfo["damage"] = atk.GetTotalDamage(playerCreatures, e);
                                    }
                                }
                                catch { }
                            }
                            intents.Add(intentInfo);
                        }
                    }
                }
                catch { }

                // Enemy powers
                var ePowers = e.Powers?.Select(PowerSummary).ToList();

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Monster(e.Monster?.Id.Entry ?? "UNKNOWN"),
                    ["hp"] = e.CurrentHp,
                    ["max_hp"] = e.MaxHp,
                    ["block"] = e.Block,
                    ["intents"] = intents.Count > 0 ? intents : null,
                    ["intends_attack"] = e.Monster?.IntendsToAttack ?? false,
                    ["powers"] = ePowers?.Count > 0 ? ePowers : null,
                };
            }).ToList() ?? new();

        // Player powers/buffs
        var playerPowers = player.Creature?.Powers?.Select(PowerSummary).ToList();

        var result = new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "combat_play",
            ["context"] = RunContext(),
            ["round"] = combatState?.RoundNumber ?? 0,
            ["energy"] = pcs?.Energy ?? 0,
            ["max_energy"] = pcs?.MaxEnergy ?? 0,
            ["hand"] = hand,
            ["enemies"] = enemies,
            ["player"] = PlayerSummary(player),
            ["player_powers"] = playerPowers?.Count > 0 ? playerPowers : null,
            ["draw_pile"] = pcs?.DrawPile?.Cards?.Select(CombatPileCardSummary).ToList() ?? new(),
            ["discard_pile"] = pcs?.DiscardPile?.Cards?.Select(CombatPileCardSummary).ToList() ?? new(),
            ["exhaust_pile"] = pcs?.ExhaustPile?.Cards?.Select(CombatPileCardSummary).ToList() ?? new(),
            ["play_pile"] = pcs?.PlayPile?.Cards?.Select(CombatPileCardSummary).ToList() ?? new(),
            ["draw_pile_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
            ["discard_pile_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
            ["exhaust_pile_count"] = pcs?.ExhaustPile?.Cards?.Count ?? 0,
        };

        // Character-specific mechanics
        try
        {
            // Defect: Orbs
            var orbQueue = pcs?.OrbQueue;
            if (orbQueue?.Orbs?.Count > 0)
            {
                result["orbs"] = orbQueue.Orbs.Select((orb, i) => new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Bilingual("orbs", orb.Id.Entry + ".title"),
                    ["type"] = orb.GetType().Name.Replace("Orb", ""),
                    ["passive"] = (int)orb.PassiveVal,
                    ["evoke"] = (int)orb.EvokeVal,
                }).ToList();
                result["orb_slots"] = orbQueue.Capacity;
            }

            // Regent: Stars
            if (pcs != null && pcs.Stars >= 0 && player.Character?.Id.Entry == "REGENT")
            {
                result["stars"] = pcs.Stars;
            }

            // Necrobinder: Osty (minion)
            var osty = player.Osty;
            if (osty != null)
            {
                result["osty"] = new Dictionary<string, object?>
                {
                    ["name"] = _loc.Monster(osty.Monster?.Id.Entry ?? "OSTY"),
                    ["hp"] = osty.CurrentHp,
                    ["max_hp"] = osty.MaxHp,
                    ["block"] = osty.Block,
                    ["alive"] = osty.IsAlive,
                };
            }
            else if (player.Character?.Id.Entry == "NECROBINDER")
            {
                result["osty"] = new Dictionary<string, object?> { ["alive"] = false };
            }
        }
        catch (Exception ex)
        {
            Log($"Character-specific data: {ex.Message}");
        }

        return result;
    }

    private Dictionary<string, object?> PowerSummary(PowerModel power)
    {
        var description = _loc.PowerDescription(power.Id.Entry);
        var amount = power.Amount;
        var displayAmount = Math.Abs(amount);
        var vars = new Dictionary<string, object?> { ["Amount"] = displayAmount };

        // Potion-backed temporary powers have no entry in powers.json. Their potion
        // descriptions use variables such as DexterityPower or StrengthPower.
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(description, @"\{([A-Za-z0-9_]+)(?::[^}]*)?\}"))
        {
            var name = match.Groups[1].Value;
            if (name.EndsWith("Power", StringComparison.Ordinal))
                vars.TryAdd(name, displayAmount);
        }

        var summary = new Dictionary<string, object?>
        {
            ["name"] = _loc.Power(power.Id.Entry),
            ["description"] = description,
            ["amount"] = amount,
            ["vars"] = vars,
        };

        // SwipePower keeps the removed card on its StolenCard property until the
        // enemy dies. Expose it so clients can identify what was actually stolen.
        try
        {
            var stolenCard = power.GetType()
                .GetProperty("StolenCard", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
                .GetValue(power) as CardModel;
            if (stolenCard != null)
                summary["stolen_card"] = CardSummary(stolenCard);
        }
        catch (Exception ex)
        {
            Log($"Read stolen card from {power.GetType().Name}: {ex.Message}");
        }

        return summary;
    }

    private Dictionary<string, object?> DetectPostCombatState(Player player, CombatRoom combatRoom)
    {
        Log($"Post-combat: RoomType={combatRoom.RoomType}, IsPreFinished={combatRoom.IsPreFinished}");
        _syncCtx.Pump();

        // Generate rewards manually instead of using TestMode auto-accept
        if (_pendingRewards == null && !_rewardsProcessed)
        {
            _goldBeforeCombat = player.Gold;
            try
            {
                var rewardsSet = new RewardsSet(player).WithRewardsFromRoom(combatRoom);
                // build 23372702: GenerateWithoutOffering() now returns Task (void);
                // generated rewards live on rewardsSet.Rewards afterwards.
                rewardsSet.GenerateWithoutOffering().GetAwaiter().GetResult();
                var rewards = rewardsSet.Rewards;
                _syncCtx.Pump();

                // Auto-collect non-choice rewards. SpecialCardReward includes encounter
                // returns such as the card stolen by Thieving Hopper.
                var cardRewards = new List<CardReward>();
                foreach (var reward in rewards)
                {
                    Log($"Generated combat reward: {reward.GetType().FullName}");
                    if (reward is GoldReward || reward is MegaCrit.Sts2.Core.Rewards.RelicReward
                        || reward is MegaCrit.Sts2.Core.Rewards.PotionReward
                        || reward is MegaCrit.Sts2.Core.Rewards.SpecialCardReward)
                    {
                        try { reward.SelectUnsynchronized().GetAwaiter().GetResult(); _syncCtx.Pump(); }
                        catch (Exception ex) { Log($"Auto-collect reward: {ex.Message}"); }
                    }
                    else if (reward is CardReward cr)
                    {
                        cardRewards.Add(cr);
                    }
                }

                if (cardRewards.Count > 0)
                {
                    _pendingCardReward = cardRewards[0];
                    _pendingRewards = rewards;
                    return CardRewardState(player, combatRoom);
                }

                _pendingRewards = null;
            }
            catch (Exception ex) { Log($"Generate rewards: {ex.Message}"); }
        }

        // No more pending rewards — proceed
        _pendingCardReward = null;
        _pendingRewards = null;
        _rewardsProcessed = true;

        // Boss → next act, OR final victory after the last act's boss (#81). Act index is
        // 0-based and STS2 has 3 acts (0/1/2); killing the Act-3 (index 2) boss has no next
        // act — EnterNextAct NREs and DetectDecisionPoint falls through to an empty
        // map_select. Report victory directly in that case.
        if (combatRoom.RoomType == RoomType.Boss)
        {
            if (_runState != null && _runState.CurrentActIndex >= 2)
            {
                Log($"Final boss defeated (Act {_runState.CurrentActIndex + 1}), reporting victory");
                return GameOverState(true);
            }
            Log("Boss defeated, entering next act");
            try
            {
                RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
                HealBetweenActs();
                EnterActAncientIfNeeded();
            }
            catch (Exception ex) { Log($"EnterNextAct: {ex.Message}"); }
            return DetectDecisionPoint();
        }

        // Normal → go to map
        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> CardRewardState(Player player, CombatRoom? combatRoom)
    {
        if (_pendingCardReward == null)
            return DetectPostCombatState(player, combatRoom ?? (_runState?.CurrentRoom as CombatRoom)!);

        var cards = _pendingCardReward.Cards.Select((c, i) =>
        {
            var cardInfo = CardSummary(c, includeAfterUpgrade: true);
            cardInfo["index"] = i;
            return cardInfo;
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "card_reward",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["can_skip"] = _pendingCardReward.CanSkip,
            ["can_reroll"] = _pendingCardReward.CanReroll,
            ["alternatives"] = RewardAlternativeSummaries(GetCardRewardAlternatives(_pendingCardReward)),
            ["gold_earned"] = _runState!.Players[0].Gold - _goldBeforeCombat,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private Dictionary<string, object?> PotionReplaceState(Player player, PotionReward reward)
    {
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "potion_replace",
            ["context"] = RunContext(),
            ["incoming_potion"] = PotionSummary(reward.Potion!),
            ["potions"] = player.Potions?.Select((potion, index) =>
            {
                var summary = PotionSummary(potion!);
                summary["index"] = index;
                return summary;
            }).ToList() ?? new(),
            ["can_skip"] = true,
            ["player"] = PlayerSummary(player),
        };
    }

    private IReadOnlyList<CardRewardAlternative> GetCardRewardAlternatives(CardReward cardReward)
    {
        try
        {
            return CardRewardAlternative.Generate(cardReward);
        }
        catch (Exception ex)
        {
            Log($"Generate card reward alternatives: {ex.Message}");
            return Array.Empty<CardRewardAlternative>();
        }
    }

    private List<Dictionary<string, object?>> RewardAlternativeSummaries(
        IEnumerable<CardRewardAlternative>? alternatives)
    {
        return (alternatives ?? Array.Empty<CardRewardAlternative>())
            .Where(alternative =>
                !string.Equals(alternative.OptionId, "REROLL", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(alternative.OptionId, "SKIP", StringComparison.OrdinalIgnoreCase))
            .GroupBy(alternative => alternative.OptionId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(alternative => new Dictionary<string, object?>
            {
                ["id"] = alternative.OptionId,
                ["name"] = _loc.Bilingual(
                    alternative.Title.LocTable,
                    alternative.Title.LocEntryKey),
                ["hotkey"] = alternative.Hotkey,
            })
            .ToList();
    }

    private void ForceToMap()
    {
        try
        {
            RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch { }

        if (_runState?.CurrentRoom is not MapRoom)
        {
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch (Exception ex) { Log($"ForceToMap: {ex.Message}"); }
        }
    }

    private Dictionary<string, object?> EventChoiceState(EventRoom eventRoom)
    {
        var localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
        _syncCtx.Pump();

        // Reset the choice-tracking flag once we re-export the event state. Earlier this
        // block force-finished events whose option count was unchanged, but that incorrectly
        // killed legitimate loops like Slippery Bridge Hold On (#59). Rely on IsFinished
        // instead and let DetectDecisionPoint show the current options for the next choice.
        if (_eventOptionChosen) _eventOptionChosen = false;

        // If event is finished, proceed to map
        if (localEvent == null || localEvent.IsFinished)
        {
            Log($"Event {localEvent?.GetType().Name ?? "null"} finished, proceeding");
            try
            {
                RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch { }
            // Force to map if still in event room
            if (_runState?.CurrentRoom is EventRoom)
            {
                try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
                catch { }
            }
            return _runState?.CurrentRoom is MapRoom ? MapSelectState() : DetectDecisionPoint();
        }

        var currentOptions = localEvent.CurrentOptions;
        if (currentOptions == null || currentOptions.Count == 0)
        {
            Log($"Event {localEvent.GetType().Name} has no options, auto-skipping");
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch { }
            return MapSelectState();
        }

        var options = currentOptions
            .Select((opt, i) =>
            {
                // Try to resolve title via loc tables
                string? title = null;
                if (opt.Title != null)
                {
                    var t = _loc.Bilingual(opt.Title.LocTable, opt.Title.LocEntryKey);
                    // Check if we actually found a translation (not just the key echoed back)
                    if (t != opt.Title.LocEntryKey)
                        title = t;
                }
                // Fallback: try to extract option ID from the key and look up as relic/card/potion
                if (title == null && opt.TextKey != null)
                {
                    // TextKey like "NEOW.pages.INITIAL.options.STONE_HUMIDIFIER" → extract "STONE_HUMIDIFIER"
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    // Try relic, then card, then just use the optionId
                    var relic = _loc.Relic(optionId);
                    if (relic != optionId + ".title")
                        title = relic;
                    else
                    {
                        var card = _loc.Card(optionId);
                        if (card != optionId + ".title")
                            title = card;
                        else
                            title = optionId.Replace("_", " ");
                    }
                }
                title ??= $"option_{i}";

                // Description: try loc table first
                string? optDesc = null;
                if (opt.Description != null && !string.IsNullOrEmpty(opt.Description.LocEntryKey))
                {
                    var d = _loc.Bilingual(opt.Description.LocTable, opt.Description.LocEntryKey);
                    if (d != opt.Description.LocEntryKey)
                        optDesc = d;
                }
                // Fallback: try relic/card description
                if (optDesc == null && opt.TextKey != null)
                {
                    var parts = opt.TextKey.Split('.');
                    var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                    var rd = _loc.Bilingual("relics", optionId + ".description");
                    if (rd != optionId + ".description")
                        optDesc = rd;
                }

                // Extract vars: try event's own DynamicVars first, then relic
                Dictionary<string, object?>? optVars = null;
                try
                {
                    // Event's DynamicVars (covers Gold, HpLoss, Heal, etc.)
                    if (localEvent.DynamicVars?.Values != null)
                    {
                        optVars = new Dictionary<string, object?>();
                        foreach (var dv in localEvent.DynamicVars.Values)
                            optVars[dv.Name] = DynamicVarDisplayValue(dv);
                    }
                }
                catch { }
                // Also try relic vars (for Neow options)
                if (opt.TextKey != null)
                {
                    try
                    {
                        var parts = opt.TextKey.Split('.');
                        var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                        var relicModel = ModelDb.GetById<RelicModel>(new ModelId("RELIC", optionId));
                        if (relicModel != null)
                        {
                            optVars ??= new Dictionary<string, object?>();
                            var mutable = relicModel.ToMutable();
                            foreach (var dv in mutable.DynamicVars.Values)
                                optVars[dv.Name] = DynamicVarDisplayValue(dv);
                        }
                    }
                    catch { }
                }

                // Event options can carry different values for the same placeholder in
                // their title and description. The Future of Potions, for example, uses
                // potion rarity in the title but card rarity in the description.
                var titleVars = LocVariables(opt.Title, optVars);
                var descriptionVars = LocVariables(opt.Description, optVars);

                // `RandomCard` (Slippery Bridge / Overcome) carries a *deck index*, but the
                // description template `{RandomCard}` should render that deck card's name. Resolve
                // it to the localized name so clients can substitute it (#58). Scoped to this var
                // name on purpose: other card vars (e.g. Wood Carvings' BirdCard/ToricCard) index
                // a transform-target pool, not the deck, so a generic rule would mis-resolve them.
                if (optVars != null && optVars.TryGetValue("RandomCard", out var rcVal) && rcVal is int rcIdx)
                {
                    try
                    {
                        var deck = _runState?.Players?[0]?.Deck?.Cards;
                        if (deck != null && rcIdx >= 0 && rcIdx < deck.Count && deck[rcIdx] != null)
                            optVars["RandomCard"] = _loc.Card(deck[rcIdx].Id.Entry);
                    }
                    catch { }
                }

                var effect = EventEnchantmentInfo(opt.TextKey, descriptionVars)
                    ?? EventRelicInfo(descriptionVars);
                var effects = EventRelicTradeEffects(opt.TextKey, descriptionVars);

                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["title"] = title,
                    ["description"] = optDesc,
                    ["text_key"] = opt.TextKey,
                    ["is_locked"] = opt.IsLocked,
                    ["vars"] = descriptionVars.Count > 0 ? descriptionVars : null,
                    ["title_vars"] = titleVars.Count > 0 ? titleVars : null,
                    ["description_vars"] = descriptionVars.Count > 0 ? descriptionVars : null,
                    ["effect"] = effect,
                    ["effects"] = effects.Count > 0 ? effects : null,
                };
            }).ToList();

        // Resolve event name — try ancients table first (for Neow), then events
        var eventEntry = localEvent.Id?.Entry ?? localEvent.GetType().Name.ToUpperInvariant();
        var eventName = _loc.Bilingual("ancients", eventEntry + ".title");
        if (eventName == eventEntry + ".title")
            eventName = _loc.Event(eventEntry);

        // Resolve event description, suppress if key not found
        string? eventDesc = null;
        if (localEvent.Description != null)
        {
            var d = _loc.Bilingual(localEvent.Description.LocTable, localEvent.Description.LocEntryKey);
            if (d != localEvent.Description.LocEntryKey)
                eventDesc = d;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "event_choice",
            ["context"] = RunContext(),
            ["event_name"] = eventName,
            ["description"] = eventDesc,
            ["options"] = options,
            ["player"] = PlayerSummary(_runState!.Players[0]),
        };
    }

    private Dictionary<string, object?>? EventEnchantmentInfo(
        string? textKey, Dictionary<string, object?>? vars)
    {
        if (string.IsNullOrEmpty(textKey)) return null;

        string? entry = null;
        string? nameVar = null;
        string? amountVar = null;
        if (textKey.StartsWith("SELF_HELP_BOOK.") && textKey.EndsWith(".READ_THE_BACK"))
            (entry, nameVar, amountVar) = ("SHARP", "Enchantment1", "Enchantment1Amount");
        else if (textKey.StartsWith("SELF_HELP_BOOK.") && textKey.EndsWith(".READ_PASSAGE"))
            (entry, nameVar, amountVar) = ("NIMBLE", "Enchantment2", "Enchantment2Amount");
        else if (textKey.StartsWith("SELF_HELP_BOOK.") && textKey.EndsWith(".READ_ENTIRE_BOOK"))
            (entry, nameVar, amountVar) = ("SWIFT", "Enchantment3", "Enchantment3Amount");
        else if (textKey.StartsWith("FIELD_OF_MAN_SIZED_HOLES.") && textKey.EndsWith(".ENTER_YOUR_HOLE"))
            (entry, nameVar) = ("PERFECT_FIT", "Enchantment");
        else if (textKey.StartsWith("SPIRALING_WHIRLPOOL.") && textKey.EndsWith(".OBSERVE"))
            entry = "SPIRAL";
        else if (textKey.StartsWith("STONE_OF_ALL_TIME.") && textKey.EndsWith(".PUSH"))
            (entry, amountVar) = ("VIGOROUS", "PushVigorousAmount");
        else if (textKey.StartsWith("WATERLOGGED_SCRIPTORIUM.") &&
                 (textKey.EndsWith(".PRICKLY_SPONGE") || textKey.EndsWith(".TENTACLE_QUILL")))
            entry = "STEADY";

        if (entry == null && vars != null)
        {
            foreach (var (varName, value) in vars)
            {
                if (!varName.Contains("Enchantment", StringComparison.OrdinalIgnoreCase) ||
                    varName.EndsWith("Amount", StringComparison.OrdinalIgnoreCase))
                    continue;
                var resolvedEntry = _loc.FindTitleEntry("enchantments", value);
                if (resolvedEntry == null) continue;
                entry = resolvedEntry;
                nameVar = varName;
                var possibleAmount = varName + "Amount";
                if (vars.ContainsKey(possibleAmount)) amountVar = possibleAmount;
                break;
            }
        }
        if (entry == null) return null;

        int? amount = null;
        if (amountVar != null && vars != null && vars.TryGetValue(amountVar, out var rawAmount))
        {
            try { amount = Convert.ToInt32(rawAmount); } catch { }
        }
        var name = _loc.Bilingual("enchantments", entry + ".title");
        if (vars != null && nameVar != null)
            vars[nameVar] = name;

        var effectVars = new Dictionary<string, object?>();
        if (amount.HasValue) effectVars["Amount"] = amount.Value;

        return new Dictionary<string, object?>
        {
            ["kind"] = "enchantment",
            ["name"] = name,
            ["description"] = _loc.Bilingual("enchantments", entry + ".description"),
            ["vars"] = effectVars.Count > 0 ? effectVars : null,
            ["amount"] = amount,
        };
    }

    private Dictionary<string, object?>? EventRelicInfo(Dictionary<string, object?>? vars)
    {
        if (vars == null) return null;
        var relicValue = vars.FirstOrDefault(pair =>
            string.Equals(pair.Key, "Relic", StringComparison.OrdinalIgnoreCase)).Value;
        return RelicEffectInfo(relicValue);
    }

    private List<Dictionary<string, object?>> EventRelicTradeEffects(
        string? textKey, Dictionary<string, object?>? vars)
    {
        if (vars == null || textKey?.StartsWith(
                "RELIC_TRADER.", StringComparison.OrdinalIgnoreCase) != true)
            return new();

        var slot = new[] { "Top", "Middle", "Bottom" }
            .FirstOrDefault(name => textKey.EndsWith(
                $".{name}", StringComparison.OrdinalIgnoreCase));
        if (slot == null) return new();

        var effects = new List<Dictionary<string, object?>>();
        var owned = RelicEffectInfo(vars.GetValueOrDefault($"{slot}RelicOwned"), "give");
        var received = RelicEffectInfo(vars.GetValueOrDefault($"{slot}RelicNew"), "receive");
        if (owned != null) effects.Add(owned);
        if (received != null) effects.Add(received);
        return effects;
    }

    private Dictionary<string, object?>? RelicEffectInfo(object? relicValue, string? role = null)
    {
        if (relicValue == null) return null;

        var entry = _loc.FindTitleEntry("relics", relicValue);
        if (entry == null) return null;

        var relicVars = new Dictionary<string, object?>();
        try
        {
            var relic = ModelDb.GetById<RelicModel>(new ModelId("RELIC", entry)).ToMutable();
            foreach (var dynamicVar in relic.DynamicVars.Values)
                relicVars[dynamicVar.Name] = DynamicVarDisplayValue(dynamicVar);
        }
        catch { }

        return new Dictionary<string, object?>
        {
            ["kind"] = "relic",
            ["role"] = role,
            ["name"] = _loc.Relic(entry),
            ["description"] = _loc.Bilingual("relics", entry + ".description"),
            ["vars"] = relicVars.Count > 0 ? relicVars : null,
        };
    }

    private Dictionary<string, object?> RestSiteState(RestSiteRoom restRoom)
    {
        var options = restRoom.Options;
        var player = _runState!.Players[0];

        if (options == null || options.Count == 0)
        {
            // Options empty = choice already made (synchronizer cleared them), go to map
            Log("Rest site: options empty, proceeding to map");
            ForceToMap();
            return MapSelectState();
        }

        var optionList = options.Select((opt, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["option_id"] = opt.OptionId,
            ["name"] = opt.GetType().Name,
            ["is_enabled"] = opt.IsEnabled,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "rest_site",
            ["context"] = RunContext(),
            ["options"] = optionList,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> ShopState(MerchantRoom merchantRoom, Player player)
    {
        var inv = merchantRoom.GetLocalInventory();
        if (inv == null) { ForceToMap(); return MapSelectState(); }

        var cards = inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries)
            .Select((e, i) =>
            {
                var card = e.CreationResult?.Card;
                var entry = card?.Id.Entry ?? "?";
                var stats = new Dictionary<string, object?>();
                int cardCost = 0;
                try
                {
                    if (card != null)
                    {
                        cardCost = card.EnergyCost?.GetResolved() ?? 0;
                        // The shop entry's card can have uninitialized DynamicVars (stats: null
                        // while after_upgrade is populated, #68). Read base stats from a fresh
                        // ModelDb clone at the card's current upgrade level, like GetUpgradedInfo.
                        var fresh = ModelDb.GetById<CardModel>(card.Id).ToMutable();
                        for (int u = 0; u < card.CurrentUpgradeLevel; u++)
                        {
                            fresh.UpgradeInternal();
                            fresh.FinalizeUpgradeInternal();
                        }
                        foreach (var dv in fresh.DynamicVars.Values)
                            stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue;
                    }
                }
                catch { }
                var shopkws = card?.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["name"] = _loc.Card(entry),
                    ["type"] = card?.Type.ToString() ?? "?",
                    ["rarity"] = card?.Rarity.ToString() ?? "?",
                    ["card_cost"] = cardCost,
                    ["card_cost_is_x"] = card?.EnergyCost?.CostsX ?? false,
                    ["description"] = _loc.Bilingual("cards", entry + ".description"),
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["keywords"] = shopkws?.Count > 0 ? shopkws : null,
                    ["after_upgrade"] = card != null ? GetUpgradedInfo(card) : null,
                    ["cost"] = e.Cost,
                    ["is_stocked"] = e.IsStocked,
                    ["on_sale"] = e.IsOnSale,
                };
            }).ToList();

        var relics = inv.RelicEntries.Select((e, i) =>
        {
            var vars = new Dictionary<string, object?>();
            try
            {
                if (e.Model?.DynamicVars?.Values != null)
                    foreach (var dv in e.Model.DynamicVars.Values)
                        vars[dv.Name] = DynamicVarDisplayValue(dv);
            }
            catch { }
            return new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = _loc.Relic(e.Model?.Id.Entry ?? "?"),
                ["description"] = _loc.Bilingual("relics", (e.Model?.Id.Entry ?? "?") + ".description"),
                ["vars"] = vars.Count > 0 ? vars : null,
                ["cost"] = e.Cost,
                ["is_stocked"] = e.IsStocked,
            };
        }).ToList();

        var potions = inv.PotionEntries.Select((e, i) =>
        {
            var vars = new Dictionary<string, object?>();
            try
            {
                if (e.Model?.DynamicVars?.Values != null)
                    foreach (var dv in e.Model.DynamicVars.Values)
                        vars[dv.Name] = DynamicVarDisplayValue(dv);
            }
            catch { }
            return new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = _loc.Potion(e.Model?.Id.Entry ?? "?"),
                ["description"] = _loc.Bilingual("potions", (e.Model?.Id.Entry ?? "?") + ".description"),
                ["vars"] = vars.Count > 0 ? vars : null,
                ["cost"] = e.Cost,
                ["is_stocked"] = e.IsStocked,
            };
        }).ToList();

        var removal = merchantRoom.GetLocalInventory().CardRemovalEntry;

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "shop",
            ["context"] = RunContext(),
            ["cards"] = cards,
            ["relics"] = relics,
            ["potions"] = potions,
            ["card_removal_cost"] = removal != null && !IsShopCardRemovalUsed(removal) ? removal.Cost : null,
            ["player"] = PlayerSummary(player),
        };
    }

    private Dictionary<string, object?> TreasureState(TreasureRoom treasureRoom)
    {
        // Treasure rooms give relics via TreasureRoomRelicSynchronizer
        Log("Treasure room — collecting rewards");

        WaitForActionExecutor();
        _syncCtx.Pump();

        // TreasureRoom.EnterInternal opens a relic-picking session (BeginRelicPicking) that the
        // headless must drive, otherwise (a) no relic is ever awarded and (b) the session stays
        // open so the NEXT treasure room's BeginRelicPicking throws "relic picking session while
        // one was already occurring!" (issue #56). Auto-pick the first offered relic: PickRelicLocally
        // enqueues a PickRelicAction whose execution awards the relic and ends the session
        // (OnPicked -> EndRelicVoting). An empty offer is closed with CompleteWithNoRelics.
        try
        {
            var relicSync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            if (relicSync?.CurrentRelics != null)
            {
                // The actual relic grant normally lives in the UI node's RelicsAwarded handler
                // (RelicCmd.Obtain per result), which is absent in headless. Capture the awarded
                // results, then grant them ourselves after the pick resolves (granting inside the
                // event — mid action-execution — risks re-entrancy).
                List<MegaCrit.Sts2.Core.Entities.TreasureRelicPicking.RelicPickingResult>? awarded = null;
                Action<List<MegaCrit.Sts2.Core.Entities.TreasureRelicPicking.RelicPickingResult>> capture = r => awarded = r;
                relicSync.RelicsAwarded += capture;
                try
                {
                    if (relicSync.CurrentRelics.Count > 0)
                    {
                        Log($"Auto-picking treasure relic 0 of {relicSync.CurrentRelics.Count}");
                        relicSync.PickRelicLocally(0);
                    }
                    else
                    {
                        relicSync.CompleteWithNoRelics();
                    }
                    _syncCtx.Pump();
                    WaitForActionExecutor();
                    _syncCtx.Pump();
                }
                finally { relicSync.RelicsAwarded -= capture; }

                if (awarded != null)
                {
                    foreach (var res in awarded)
                    {
                        if (res.relic != null && res.player != null)
                            RelicCmd.Obtain(res.relic.ToMutable(), res.player).GetAwaiter().GetResult();
                    }
                    _syncCtx.Pump();
                    WaitForActionExecutor();
                    _syncCtx.Pump();
                }
            }
        }
        catch (Exception ex) { Log($"Treasure relic pick: {ex.Message}"); }

        try
        {
            treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
            _syncCtx.Pump();
            treasureRoom.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("relic picking session"))
        {
            // BUG-013: Relic session conflict — wait for pending session then retry
            Log($"Relic session conflict, waiting and retrying: {ex.Message}");
            WaitForActionExecutor();
            _syncCtx.Pump();
            try
            {
                treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
                _syncCtx.Pump();
                treasureRoom.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch (Exception retryEx) { Log($"Treasure rewards retry failed: {retryEx.Message}"); }
        }
        catch (Exception ex) { Log($"Treasure rewards: {ex.Message}"); }

        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> GameOverState(bool isVictory)
    {
        var player = _runState!.Players[0];
        var summary = PlayerSummary(player);
        // BUG-005: When player died, the engine resets HP to max. Use last known HP instead.
        if (!isVictory)
            summary["hp"] = _lastKnownHp > 0 ? 0 : (player.Creature?.CurrentHp ?? 0);
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "game_over",
            ["context"] = RunContext(),
            ["victory"] = isVictory,
            ["player"] = summary,
            ["act"] = _runState.CurrentActIndex + 1,
            ["floor"] = _runState.ActFloor,
        };
    }

    #endregion

    #region Helpers

    private void ConfigureRewardSelector()
    {
        var selectorField = typeof(RewardsSet).GetField(
            "testSelector", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (selectorField == null)
        {
            Log("RewardsSet.testSelector was not found; full potion rewards cannot pause");
            return;
        }
        selectorField.SetValue(null, (Func<RewardsSet, Task>)SelectRewardsForHeadless);
    }

    private async Task SelectRewardsForHeadless(RewardsSet rewardsSet)
    {
        var synchronizer = typeof(RewardsSet).GetField(
            "_synchronizer", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(rewardsSet) as RewardsSetSynchronizer
            ?? throw new InvalidOperationException("RewardsSet synchronizer was not found");

        foreach (var reward in rewardsSet.Rewards)
        {
            if (reward.SuccessfullySelected) continue;

            if (reward is PotionReward potionReward && !rewardsSet.Player.HasOpenPotionSlots)
            {
                var choice = new TaskCompletionSource<int?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingPotionReward = potionReward;
                _pendingPotionChoice = choice;
                Log($"Potion slots full; waiting to place {potionReward.Potion?.Id.Entry ?? "?"}");

                var potionIndex = await choice.Task.ConfigureAwait(false);
                if (potionIndex.HasValue)
                {
                    var currentPotions = rewardsSet.Player.Potions?.ToList() ?? new();
                    if (potionIndex.Value < 0 || potionIndex.Value >= currentPotions.Count)
                        throw new InvalidOperationException($"Potion slot {potionIndex.Value} is no longer valid");
                    await PotionCmd.Discard(currentPotions[potionIndex.Value]).ConfigureAwait(false);
                    if (!await synchronizer.SelectLocalReward(potionReward).ConfigureAwait(false))
                        throw new InvalidOperationException($"Could not procure potion {potionReward.Potion?.Id.Entry ?? "?"}");
                }
                else
                {
                    synchronizer.SkipLocalRewardsSet();
                    return;
                }
                continue;
            }

            await synchronizer.SelectLocalReward(reward).ConfigureAwait(false);
        }
    }

    private void RememberPendingSelectionTask(Task task, string source)
    {
        _pendingSelectionTask = task;
        _pendingSelectionTaskSource = source;
        Log($"{source}: background action paused for player selection");
    }

    private void ResumePendingSelectionTask(object? releasedRewardCards = null)
    {
        var task = _pendingSelectionTask;
        if (task == null)
        {
            if (releasedRewardCards != null)
            {
                for (int i = 0; i < 100 &&
                    ReferenceEquals(_cardSelector.PendingRewardCards, releasedRewardCards); i++)
                {
                    _syncCtx.Pump();
                    Thread.Sleep(10);
                }
            }
            _syncCtx.Pump();
            WaitForActionExecutor();
            _pendingSelectionInfo = null;
            _pendingSelectionKind = null;
            return;
        }

        // Resolving the selector's TCS schedules the rest of the room/relic action back on
        // the thread pool. Pump until it either completes or exposes another real prompt.
        for (int i = 0; i < 500; i++)
        {
            _syncCtx.Pump();
            var stillReleasingReward = releasedRewardCards != null &&
                ReferenceEquals(_cardSelector.PendingRewardCards, releasedRewardCards);
            if (_cardSelector.HasPending || _pendingBundles != null || _pendingPotionChoice != null ||
                (_cardSelector.HasPendingReward && !stillReleasingReward))
                return;
            if (task.IsCompleted)
                break;
            Thread.Sleep(10);
        }

        if (!task.IsCompleted)
        {
            Log($"{_pendingSelectionTaskSource ?? "selection action"}: still running after selection resolved");
            return;
        }

        try { task.GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Log($"{_pendingSelectionTaskSource ?? "selection action"}: completion failed: {ex.Message}");
        }

        _pendingSelectionTask = null;
        _pendingSelectionTaskSource = null;
        _pendingSelectionInfo = null;
        _pendingSelectionKind = null;
        _syncCtx.Pump();
        WaitForActionExecutor();
    }

    private void WaitForActionExecutor()
    {
        try
        {
            // Ensure sync context is set for this thread
            SynchronizationContext.SetSynchronizationContext(_syncCtx);

            // Pump the synchronization context to execute any pending continuations
            _syncCtx.Pump();

            // Executor may stay "running" while the game awaits headless card selection / reward (e.g. Attack Potion).
            // Spinning here would time out and downstream code could mis-handle an in-flight potion use (BUG-026).
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingPotionChoice != null)
                return;

            var executor = RunManager.Instance.ActionExecutor;
            if (executor.IsRunning)
            {
                // Pump while waiting for executor
                int maxPumps = 1000;
                for (int i = 0; i < maxPumps; i++)
                {
                    _syncCtx.Pump();
                    if (!executor.IsRunning) break;
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception ex)
        {
            Log($"WaitForActionExecutor exception: {ex.Message}");
        }
    }

    private void SpinWaitForCombatStable()
    {
        int maxIterations = 200;
        for (int i = 0; i < maxIterations; i++)
        {
            _syncCtx.Pump();
            if (!CombatManager.Instance.IsInProgress) return;
            if (IsPlayPhase()) return;
            WaitForActionExecutor();
            if (IsPlayPhase() || !CombatManager.Instance.IsInProgress) return;
            Thread.Sleep(5);
        }
    }

    /// <summary>Compute what a card would look like after upgrading (stats + cost + description).</summary>
    private Dictionary<string, object?>? GetUpgradedInfo(CardModel card)
    {
        if (!card.IsUpgradable) return null;
        try
        {
            var clone = ModelDb.GetById<CardModel>(card.Id).ToMutable();
            // Apply existing upgrades first
            for (int i = 0; i < card.CurrentUpgradeLevel; i++)
            {
                clone.UpgradeInternal();
                clone.FinalizeUpgradeInternal();
            }
            // Apply one more upgrade
            clone.UpgradeInternal();
            clone.FinalizeUpgradeInternal();

            var stats = new Dictionary<string, object?>();
            try { foreach (var dv in clone.DynamicVars.Values) stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue; } catch { }

            // Compare keywords before/after upgrade
            var oldKws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
            var newKws = clone.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
            var addedKws = newKws.Except(oldKws).ToList();
            var removedKws = oldKws.Except(newKws).ToList();

            return new Dictionary<string, object?>
            {
                ["cost"] = clone.EnergyCost?.GetResolved() ?? 0,
                ["cost_is_x"] = clone.EnergyCost?.CostsX ?? false,
                ["stats"] = stats.Count > 0 ? stats : null,
                ["description"] = _loc.Bilingual("cards", card.Id.Entry + ".description"),
                ["added_keywords"] = addedKws.Count > 0 ? addedKws : null,
                ["removed_keywords"] = removedKws.Count > 0 ? removedKws : null,
            };
        }
        catch { return null; }
    }

    private Dictionary<string, object?> PotionSummary(PotionModel potion)
    {
        var vars = new Dictionary<string, object?>();
        try
        {
            foreach (var dynamicVar in potion.DynamicVars.Values)
                vars[dynamicVar.Name] = DynamicVarDisplayValue(dynamicVar);
        }
        catch { }
        return new Dictionary<string, object?>
        {
            ["id"] = potion.Id.ToString(),
            ["name"] = _loc.Potion(potion.Id.Entry),
            ["description"] = _loc.Bilingual("potions", potion.Id.Entry + ".description"),
            ["vars"] = vars.Count > 0 ? vars : null,
            ["target_type"] = potion.TargetType.ToString(),
        };
    }

    private Dictionary<string, object?> PlayerSummary(Player player)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = _loc.Bilingual("characters", (player.Character?.Id.Entry ?? "IRONCLAD") + ".title"),
            ["hp"] = player.Creature?.CurrentHp ?? 0,
            ["max_hp"] = player.Creature?.MaxHp ?? 0,
            ["block"] = player.Creature?.Block ?? 0,
            ["gold"] = player.Gold,
            ["relics"] = player.Relics?.Select(r =>
            {
                var vars = new Dictionary<string, object?>();
                try { foreach (var dv in r.DynamicVars.Values) vars[dv.Name] = DynamicVarDisplayValue(dv); } catch { }
                return new Dictionary<string, object?>
                {
                    ["name"] = _loc.Relic(r.Id.Entry),
                    ["description"] = _loc.Bilingual("relics", r.Id.Entry + ".description"),
                    ["vars"] = vars.Count > 0 ? vars : null,
                };
            }).ToList(),
            ["potions"] = player.Potions?.Select((p, i) =>
            {
                if (p == null) return null;
                var summary = PotionSummary(p);
                summary["index"] = i;
                return summary;
            }).Where(x => x != null).ToList(),
            ["deck_size"] = player.Deck?.Cards?.Count(c => c != null) ?? 0,
            ["deck"] = player.Deck?.Cards?.Where(c => c != null)
                .Select(c => CardSummary(c, includeAfterUpgrade: true)).ToList(),
        };
    }

    /// <summary>Common context added to every decision point.</summary>
    private Dictionary<string, object?> RunContext()
    {
        if (_runState == null) return new();
        var ctx = new Dictionary<string, object?>
        {
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
            ["room_type"] = _runState.CurrentRoom?.RoomType.ToString(),
        };

        // Boss encounter info — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                var monsterKey = bossIdEntry.EndsWith("_BOSS") ? bossIdEntry[..^5] : bossIdEntry;
                // Handle special mappings
                if (monsterKey == "THE_KIN") monsterKey = "KIN_PRIEST";
                ctx["boss"] = new Dictionary<string, object?>
                {
                    ["id"] = bossIdEntry,
                    ["name"] = _loc.Monster(monsterKey),
                };
            }
        }
        catch { }

        return ctx;
    }

    private static void EnsureModelDbInitialized()
    {
        if (_modelDbInitialized) return;
        _modelDbInitialized = true;

        TestMode.IsOn = true;

        // Install inline sync context on main thread
        SynchronizationContext.SetSynchronizationContext(_syncCtx);

        // Initialize PlatformServices before anything touches PlatformUtil
        try
        {
            // Try to access PlatformUtil to trigger its static init
            // If it fails, it won't be available but most code checks SteamInitializer.Initialized
            var _ = MegaCrit.Sts2.Core.Platform.PlatformUtil.PrimaryPlatform;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] PlatformUtil init: {ex.Message}");
        }

        // Initialize SaveManager with a dummy profile for save/load support
        try { SaveManager.Instance.InitProfileId(0); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitProfileId: {ex.Message}"); }

        // Initialize PrefsSave (FastMode etc.). build 23372702 reads PrefsSave.FastMode
        // from many gameplay paths (e.g. Slice.OnPlay anim delay); without this the
        // PrefsSave getter returns null and those paths NRE.
        try { SaveManager.Instance.InitPrefsDataForTest(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitPrefsDataForTest: {ex.Message}"); }

        // Initialize progress data for epoch/timeline tracking
        try { SaveManager.Instance.InitProgressData(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] InitProgressData: {ex.Message}"); }

        // Install the Task.Yield patch but keep SuppressYield=false by default.
        // SuppressYield is toggled to true only during EndTurn to prevent boss fight deadlocks.
        PatchTaskYield();

        // Patch visual waits to be no-ops in headless mode. Without a Godot scene tree,
        // both regular waits and custom scaled event waits never complete.
        PatchCmdWait();

        // Some effects offer an empty custom reward set after running hooks. The game UI
        // normally opens and immediately closes that screen; headless has no screen to close.
        PatchEmptyCustomRewards();

        // Patch TalkCmd.Play to a no-op (issue #64). Monster speech-bubble VFX during
        // moves (e.g. BygoneEffigy.WakeMove) NRE in headless and break the enemy turn.
        PatchTalkCmd();

        // Debug audio is not initialized without a Godot scene tree. Some events call it
        // without a null check before applying their actual reward.
        PatchDebugAudio();

        // Event-only screen shake is another visual call whose singleton is unavailable
        // without a Godot scene tree (for example Dense Vegetation after resting).
        PatchDenseVegetationScreenRumble();

        // Trial.Accept updates the event state after several portrait-only Godot calls.
        // Suppress those local visuals so the guilty/innocent choice remains playable.
        PatchTrialVisuals();

        // Initialize localization system (needed for events, cards, etc.)
        InitLocManager();

        // STS2 v0.107.1+: ReflectionHelper.ModTypes requires ModManager
        // to have completed initialization before model subtype discovery.
        try
        {
            var stateProp = typeof(ModManager).GetProperty(
                "State",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
            );

            var stateType = stateProp?.PropertyType;
            if (stateProp != null && stateType != null)
            {
                var initialized = Enum.Parse(stateType, "Initialized");
                stateProp.SetValue(null, initialized);
                Console.Error.WriteLine("[INFO] ModManager marked initialized for headless mode");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] ModManager init shim: {ex.Message}");
        }

        var subtypes = MegaCrit.Sts2.Core.Models.AbstractModelSubtypes.All;
        int registered = 0, failed = 0;
        for (int i = 0; i < subtypes.Count; i++)
        {
            try
            {
                ModelDb.Inject(subtypes[i]);
                registered++;
            }
            catch (Exception ex)
            {
                failed++;
                // Only log first few failures to reduce noise
                if (failed <= 5)
                    Console.Error.WriteLine($"[WARN] Failed to register {subtypes[i].Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.Error.WriteLine($"[INFO] ModelDb: {registered} registered, {failed} failed out of {subtypes.Count}");

        // Initialize net ID serialization cache (needed for combat actions)
        try
        {
            ModelIdSerializationCache.Init();
            Console.Error.WriteLine("[INFO] ModelIdSerializationCache initialized");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] ModelIdSerializationCache.Init: {ex.Message}");
        }
    }

    private Player? CreatePlayer(string characterName)
    {
        return characterName.ToLowerInvariant() switch
        {
            "ironclad" => Player.CreateForNewRun<Ironclad>(UnlockState.all, 1uL),
            "silent" => Player.CreateForNewRun<Silent>(UnlockState.all, 1uL),
            "defect" => Player.CreateForNewRun<Defect>(UnlockState.all, 1uL),
            "regent" => Player.CreateForNewRun<Regent>(UnlockState.all, 1uL),
            "necrobinder" => Player.CreateForNewRun<Necrobinder>(UnlockState.all, 1uL),
            _ => null
        };
    }

    private static void PatchCmdWait()
    {
        try
        {
            var harmony = new Harmony("sts2headless.cmdwait");
            // Find Cmd via CardPileCmd's assembly (both are in the same namespace).
            var cmdPileType = typeof(MegaCrit.Sts2.Core.Commands.CardPileCmd);
            var cmdAsm = cmdPileType.Assembly;
            Type? cmdType = cmdAsm.GetType("MegaCrit.Sts2.Core.Commands.Cmd");
            if (cmdType == null)
            {
                foreach (var t in cmdAsm.GetTypes())
                {
                    if (t.Namespace == "MegaCrit.Sts2.Core.Commands")
                    {
                        var waitM = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                            .Where(m => m.Name == "Wait" || m.Name == "CustomScaledWait")
                            .ToList();
                        if (waitM.Count > 0)
                        {
                            cmdType = t;
                            break;
                        }
                    }
                }
            }
            if (cmdType != null)
            {
                var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                    BindingFlags.Static | BindingFlags.Public);
                var waitMethods = cmdType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => (m.Name == "Wait" || m.Name == "CustomScaledWait") &&
                                typeof(Task).IsAssignableFrom(m.ReturnType))
                    .ToList();
                foreach (var method in waitMethods)
                {
                    if (prefix != null)
                    {
                        harmony.Patch(method, new HarmonyMethod(prefix));
                        Console.Error.WriteLine($"[INFO] Patched Cmd.{method.Name}() to no-op");
                    }
                }
            }
            else
            {
                Console.Error.WriteLine("[WARN] Could not find MegaCrit.Sts2.Core.Commands.Cmd type");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Cmd.Wait: {ex.Message}");
        }
    }

    private static void PatchTalkCmd()
    {
        try
        {
            var harmony = new Harmony("sts2headless.talkpatch");
            var talkType = typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.Commands.TalkCmd");
            var playMethod = talkType?.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Play");
            if (playMethod == null)
            {
                Console.Error.WriteLine("[WARN] Could not find TalkCmd.Play to patch");
                return;
            }
            var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.TalkCmdPlayPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (prefix != null)
            {
                harmony.Patch(playMethod, new HarmonyMethod(prefix));
                Console.Error.WriteLine("[INFO] Patched TalkCmd.Play() to no-op (prevents enemy-move VFX crash, issue #64)");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch TalkCmd.Play: {ex.Message}");
        }
    }

    private static void PatchTrialVisuals()
    {
        try
        {
            var harmony = new Harmony("sts2headless.trialvisuals");
            var accept = AccessTools.DeclaredMethod(
                typeof(MegaCrit.Sts2.Core.Models.Events.Trial), "Accept");
            var isMe = AccessTools.Method(
                typeof(LocalContext), nameof(LocalContext.IsMe), new[] { typeof(Player) });
            if (accept == null || isMe == null)
            {
                Console.Error.WriteLine("[WARN] Could not find Trial.Accept or LocalContext.IsMe to patch");
                return;
            }

            harmony.Patch(
                accept,
                prefix: new HarmonyMethod(typeof(YieldPatches), nameof(YieldPatches.TrialAcceptPrefix)),
                postfix: new HarmonyMethod(typeof(YieldPatches), nameof(YieldPatches.TrialAcceptPostfix)),
                finalizer: new HarmonyMethod(typeof(YieldPatches), nameof(YieldPatches.TrialAcceptFinalizer)));
            harmony.Patch(
                isMe,
                prefix: new HarmonyMethod(typeof(YieldPatches), nameof(YieldPatches.TrialIsMePrefix)));
            Console.Error.WriteLine("[INFO] Patched Trial portrait visuals for headless mode");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Trial portrait visuals: {ex.Message}");
        }
    }

    private static void PatchEmptyCustomRewards()
    {
        try
        {
            var harmony = new Harmony("sts2headless.emptycustomrewards");
            var method = typeof(RewardsCmd).GetMethod(
                "OfferCustom", BindingFlags.Static | BindingFlags.Public,
                null, new[] { typeof(Player), typeof(List<Reward>) }, null);
            var prefix = typeof(YieldPatches).GetMethod(
                nameof(YieldPatches.EmptyCustomRewardsPrefix),
                BindingFlags.Static | BindingFlags.Public);
            if (method != null && prefix != null)
            {
                harmony.Patch(method, new HarmonyMethod(prefix));
                Console.Error.WriteLine("[INFO] Patched empty custom reward screens for headless mode");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch empty custom rewards: {ex.Message}");
        }
    }

    private static void PatchDebugAudio()
    {
        try
        {
            var harmony = new Harmony("sts2headless.debugaudio");
            var instanceGetter = typeof(NDebugAudioManager).GetProperty(
                nameof(NDebugAudioManager.Instance),
                BindingFlags.Static | BindingFlags.Public
            )?.GetMethod;
            var instancePrefix = typeof(YieldPatches).GetMethod(
                nameof(YieldPatches.DebugAudioInstancePrefix),
                BindingFlags.Static | BindingFlags.Public
            );
            if (instanceGetter != null && instancePrefix != null)
                harmony.Patch(instanceGetter, new HarmonyMethod(instancePrefix));

            var playPrefix = typeof(YieldPatches).GetMethod(
                nameof(YieldPatches.DebugAudioPlayPrefix),
                BindingFlags.Static | BindingFlags.Public
            );
            var playMethods = typeof(NDebugAudioManager).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == "Play" && method.ReturnType == typeof(int));
            var patched = 0;
            if (playPrefix != null)
            {
                foreach (var method in playMethods)
                {
                    harmony.Patch(method, new HarmonyMethod(playPrefix));
                    patched++;
                }
            }
            var stopPrefix = typeof(YieldPatches).GetMethod(
                nameof(YieldPatches.DebugAudioStopPrefix),
                BindingFlags.Static | BindingFlags.Public
            );
            var stopMethods = typeof(NDebugAudioManager).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name == "Stop" && method.ReturnType == typeof(void));
            if (stopPrefix != null)
            {
                foreach (var method in stopMethods)
                {
                    harmony.Patch(method, new HarmonyMethod(stopPrefix));
                    patched++;
                }
            }
            Console.Error.WriteLine($"[INFO] Patched debug audio for headless mode ({patched} methods)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch debug audio: {ex.Message}");
        }
    }

    private static void PatchDenseVegetationScreenRumble()
    {
        try
        {
            var restMethod = typeof(MegaCrit.Sts2.Core.Models.Events.DenseVegetation)
                .GetMethod("Rest", BindingFlags.Instance | BindingFlags.NonPublic);
            var stateMachineType = restMethod?
                .GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?
                .StateMachineType;
            var moveNext = stateMachineType?.GetMethod(
                "MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var transpiler = typeof(YieldPatches).GetMethod(
                nameof(YieldPatches.DenseVegetationRestTranspiler),
                BindingFlags.Static | BindingFlags.Public);
            if (moveNext != null && transpiler != null)
            {
                var harmony = new Harmony("sts2headless.densevegetationrumble");
                harmony.Patch(moveNext, transpiler: new HarmonyMethod(transpiler));
                Console.Error.WriteLine("[INFO] Patched Dense Vegetation screen rumble for headless mode");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Dense Vegetation screen rumble: {ex.Message}");
        }
    }

    private static void PatchTaskYield()
    {
        try
        {
            var harmony = new Harmony("sts2headless.yieldpatch");

            // Patch YieldAwaitable.YieldAwaiter.IsCompleted to return true
            // This makes `await Task.Yield()` execute synchronously (continuation runs inline)
            var yieldAwaiterType = typeof(System.Runtime.CompilerServices.YieldAwaitable)
                .GetNestedType("YieldAwaiter");
            if (yieldAwaiterType != null)
            {
                var isCompletedProp = yieldAwaiterType.GetProperty("IsCompleted");
                if (isCompletedProp != null)
                {
                    var getter = isCompletedProp.GetGetMethod();
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.IsCompletedPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (getter != null && prefix != null)
                    {
                        harmony.Patch(getter, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Task.Yield() to be synchronous");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Task.Yield: {ex.Message}");
        }
    }

    /// <summary>
    /// Card selector for headless mode — picks first available card for any selection prompt.
    /// Used by cards like Headbutt, Armaments, etc. that need player to choose a card.
    /// </summary>
    /// <summary>
    /// Card selector that creates a pending selection decision point.
    /// When the game needs the player to choose cards (upgrade, remove, transform, bundle pick),
    /// this stores the options and waits for the main loop to provide the answer.
    /// </summary>
    internal class HeadlessCardSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        // Pending card selection — set by game engine, read by main loop
        public List<CardModel>? PendingOptions { get; private set; }
        public int PendingMinSelect { get; private set; }
        public int PendingMaxSelect { get; private set; }
        public string PendingPrompt { get; private set; } = "";
        private TaskCompletionSource<IEnumerable<CardModel>>? _pendingTcs;

        public bool HasPending => _pendingTcs != null && !_pendingTcs.Task.IsCompleted;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            var optList = options.ToList();
            if (optList.Count == 0)
                return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());

            // Store pending selection and wait
            PendingOptions = optList;
            PendingMinSelect = minSelect;
            PendingMaxSelect = maxSelect;
            _pendingTcs = new TaskCompletionSource<IEnumerable<CardModel>>();

            Console.Error.WriteLine($"[SIM] Card selection pending: {optList.Count} options, select {minSelect}-{maxSelect}");

            // Return the task — the main loop will complete it
            return _pendingTcs.Task;
        }

        public void ResolvePending(IEnumerable<CardModel> selected)
        {
            _pendingTcs?.TrySetResult(selected);
            PendingOptions = null;
            _pendingTcs = null;
        }

        public void ResolvePendingByIndices(int[] indices)
        {
            if (PendingOptions == null) return;
            var selected = indices
                .Where(i => i >= 0 && i < PendingOptions.Count)
                .Select(i => PendingOptions[i])
                .ToList();
            ResolvePending(selected);
        }

        public void CancelPending()
        {
            _pendingTcs?.TrySetResult(Array.Empty<CardModel>());
            PendingOptions = null;
            _pendingTcs = null;
        }

        // Pending card reward from events (GetSelectedCardReward blocks until resolved)
        public List<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult>? PendingRewardCards { get; private set; }
        public IReadOnlyList<CardRewardAlternative>? PendingRewardAlternatives { get; private set; }
        private ManualResetEventSlim? _rewardWait;
        private int _rewardChoice = -1;
        private CardRewardAlternative? _rewardAlternative;

        // NOTE: STS2 build 23372702 changed ICardSelector.GetSelectedCardReward to return
        // a CardRewardSelection struct { CardModel card; CardRewardAlternative alternative }.
        // CardReward.OnSelect interprets: alternative != null → pick that alternative
        // (Skip/Reroll); else card != null → take that card; both null → skip (no card kept).
        public MegaCrit.Sts2.Core.TestSupport.CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            if (options.Count == 0) return default;  // Skip

            // Store pending and block until main loop resolves
            PendingRewardCards = options.ToList();
            PendingRewardAlternatives = alternatives;
            _rewardChoice = -1;
            _rewardAlternative = null;
            _rewardWait = new ManualResetEventSlim(false);

            Console.Error.WriteLine($"[SIM] Card reward pending: {options.Count} cards (blocking)");
            _rewardWait.Wait(TimeSpan.FromSeconds(300)); // Wait up to 5 min

            var choice = _rewardChoice;
            var alternative = _rewardAlternative;
            PendingRewardCards = null;
            PendingRewardAlternatives = null;
            _rewardWait = null;
            _rewardAlternative = null;

            if (alternative != null)
                return new MegaCrit.Sts2.Core.TestSupport.CardRewardSelection { alternative = alternative };
            if (choice >= 0 && choice < options.Count)
                return new MegaCrit.Sts2.Core.TestSupport.CardRewardSelection { card = options[choice].Card };
            return default;  // Skip (card=null, alternative=null)
        }

        public bool HasPendingReward => PendingRewardCards != null && _rewardWait != null;
        public bool CanRerollPendingReward =>
            PendingRewardAlternatives?.Any(alternative => string.Equals(
                alternative.OptionId, "REROLL", StringComparison.OrdinalIgnoreCase)) == true;

        public void ResolveReward(int index)
        {
            _rewardChoice = index;
            _rewardWait?.Set();
        }

        public void SkipReward()
        {
            _rewardChoice = -1;
            _rewardWait?.Set();
        }

        public bool ResolveRewardAlternative(string optionId)
        {
            _rewardAlternative = PendingRewardAlternatives?
                .FirstOrDefault(alternative => string.Equals(
                    alternative.OptionId,
                    optionId,
                    StringComparison.OrdinalIgnoreCase));
            if (_rewardAlternative != null)
            {
                _rewardWait?.Set();
                return true;
            }
            return false;
        }
    }

    internal static class YieldPatches
    {
        private static NDebugAudioManager? _headlessDebugAudioManager;

        [ThreadStatic]
        private static bool _suppressTrialVisuals;

        public static void TrialAcceptPrefix() => _suppressTrialVisuals = true;

        public static void TrialAcceptPostfix() => _suppressTrialVisuals = false;

        public static Exception? TrialAcceptFinalizer(Exception? __exception)
        {
            _suppressTrialVisuals = false;
            return __exception;
        }

        public static bool TrialIsMePrefix(ref bool __result)
        {
            if (!_suppressTrialVisuals) return true;
            __result = false;
            return false;
        }

        // Only suppress Task.Yield() when this flag is set (during end_turn processing)
        public static volatile bool SuppressYield;

        public static bool IsCompletedPrefix(ref bool __result)
        {
            if (SuppressYield)
            {
                __result = true;
                return false;
            }
            return true; // Let normal Yield behavior run
        }

        /// <summary>Harmony prefix: make visual Cmd waits complete immediately in headless mode.</summary>
        public static bool CmdWaitPrefix(ref Task __result)
        {
            __result = Task.CompletedTask;
            return false; // Skip original method
        }

        public static bool EmptyCustomRewardsPrefix(
            List<Reward> rewards, ref Task __result)
        {
            if (rewards.Count > 0) return true;
            __result = Task.CompletedTask;
            return false;
        }

        /// <summary>
        /// Harmony prefix: no-op TalkCmd.Play (issue #64). The speech-bubble VFX
        /// (NSpeechBubbleVfx.Create + GetVfxContainer().AddChildSafely) NREs in headless,
        /// which derails enemy moves like BygoneEffigy.WakeMove mid enemy turn and forces
        /// the EndTurn nuclear fallback / false game_over. The bubble is purely cosmetic and
        /// its return value is ignored by callers, so returning null is safe.
        /// </summary>
        public static bool TalkCmdPlayPrefix(ref MegaCrit.Sts2.Core.Nodes.Vfx.NSpeechBubbleVfx? __result)
        {
            __result = null;
            return false; // Skip original method
        }

        public static bool DebugAudioInstancePrefix(ref NDebugAudioManager __result)
        {
            _headlessDebugAudioManager ??= (NDebugAudioManager)
                System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                    typeof(NDebugAudioManager));
            __result = _headlessDebugAudioManager;
            return false;
        }

        public static bool DebugAudioPlayPrefix(ref int __result)
        {
            __result = -1;
            return false;
        }

        public static bool DebugAudioStopPrefix() => false;

        public static IEnumerable<CodeInstruction> DenseVegetationRestTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var rumble = AccessTools.Method(
                typeof(MegaCrit.Sts2.Core.Nodes.NGame), "ScreenRumble");
            var noOp = AccessTools.Method(
                typeof(YieldPatches), nameof(DenseVegetationScreenRumbleNoOp));
            foreach (var instruction in instructions)
            {
                if (rumble != null && noOp != null && instruction.Calls(rumble))
                {
                    instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                    instruction.operand = noOp;
                }
                yield return instruction;
            }
        }

        public static void DenseVegetationScreenRumbleNoOp(
            MegaCrit.Sts2.Core.Nodes.NGame? game,
            MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.ShakeStrength strength,
            MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.ShakeDuration duration,
            MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.RumbleStyle style)
        {
        }
    }

    private static void InitLocManager()
    {
        // Create a LocManager instance with stub tables via reflection.
        // LocManager.Initialize() fails because PlatformUtil isn't available,
        // and Harmony can't patch some LocString methods due to JIT issues.
        // Solution: create an uninitialized LocManager, set its _tables, and
        // use Harmony only for the simple LocTable.GetRawText fallback.
        try
        {
            // Create uninitialized LocManager and set Instance
            var instanceProp = typeof(LocManager).GetProperty("Instance",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
            instanceProp!.SetValue(null, instance);

            // Load REAL localization data from localization_eng/ JSON files
            var tablesField = typeof(LocManager).GetField("_tables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tables = new Dictionary<string, LocTable>();

            var locDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "localization_eng");
            if (Directory.Exists(locDir))
            {
                foreach (var file in Directory.GetFiles(locDir, "*.json"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(file));
                        if (data != null)
                            tables[name] = new LocTable(name, data);
                    }
                    catch { }
                }
                Console.Error.WriteLine($"[INFO] Loaded {tables.Count} localization tables from {locDir}");
            }
            else
            {
                Console.Error.WriteLine($"[WARN] Localization dir not found: {locDir}");
                // Fallback: empty tables
                var tableNames = new[] {
                    "achievements","acts","afflictions","ancients","ascension",
                    "bestiary","card_keywords","card_library","card_reward_ui",
                    "card_selection","cards","characters","combat_messages",
                    "credits","enchantments","encounters","epochs","eras",
                    "events","ftues","game_over_screen","gameplay_ui",
                    "inspect_relic_screen","intents","main_menu_ui","map",
                    "merchant_room","modifiers","monsters","orbs","potion_lab",
                    "potions","powers","relic_collection","relics","rest_site_ui",
                    "run_history","settings_ui","static_hover_tips","stats_screen",
                    "timeline","vfx"
                };
                foreach (var name in tableNames)
                    tables[name] = new LocTable(name, new Dictionary<string, string>());
            }
            tablesField!.SetValue(instance, tables);

            // Set Language
            var langProp = typeof(LocManager).GetProperty("Language",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { langProp?.SetValue(instance, "eng"); } catch { }

            // Set CultureInfo
            var cultureProp = typeof(LocManager).GetProperty("CultureInfo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { cultureProp?.SetValue(instance, System.Globalization.CultureInfo.InvariantCulture); } catch { }

            // Initialize _smartFormatter — the game uses `new SmartFormatter()`
            try
            {
                var sfField = typeof(LocManager).GetField("_smartFormatter",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                // Dump ALL fields (instance + static)
                foreach (var f in typeof(LocManager).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                    Console.Error.WriteLine($"[DEBUG] LocManager {(f.IsStatic?"static":"inst")} field: {f.Name} ({f.FieldType.Name})");
                Console.Error.WriteLine($"[DEBUG] sfField: {sfField?.Name ?? "null"} type: {sfField?.FieldType?.Name ?? "null"}");
                if (sfField != null)
                {
                    try
                    {
                        // List constructors to find the right one
                        var ctors = sfField.FieldType.GetConstructors(
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        Console.Error.WriteLine($"[DEBUG] SmartFormatter has {ctors.Length} constructors:");
                        foreach (var ctor in ctors)
                        {
                            var ps = ctor.GetParameters();
                            Console.Error.WriteLine($"  ({string.Join(", ", ps.Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                        }
                        // Try the one with fewest params
                        var bestCtor = ctors.OrderBy(c => c.GetParameters().Length).First();
                        var args2 = bestCtor.GetParameters().Select(p =>
                            p.HasDefaultValue ? p.DefaultValue :
                            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
                        ).ToArray();
                        var sf = bestCtor.Invoke(args2);
                        // Register extensions using the game's own LoadLocFormatters logic
                        // Call it via reflection on LocManager instance
                        try
                        {
                            var loadMethod = typeof(LocManager).GetMethod("LoadLocFormatters",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            if (loadMethod != null)
                            {
                                loadMethod.Invoke(instance, null);
                                Console.Error.WriteLine("[INFO] SmartFormatter initialized via LoadLocFormatters");
                            }
                            else
                            {
                                sfField.SetValue(null, sf);
                                Console.Error.WriteLine("[INFO] SmartFormatter set (no LoadLocFormatters found)");
                            }
                        }
                        catch (Exception lfEx)
                        {
                            sfField.SetValue(null, sf);
                            Console.Error.WriteLine($"[WARN] LoadLocFormatters failed: {lfEx.InnerException?.Message ?? lfEx.Message}");
                        }
                    }
                    catch (Exception sfEx)
                    {
                        Console.Error.WriteLine($"[WARN] SmartFormatter create failed: {sfEx.GetType().Name}: {sfEx.Message}");
                        if (sfEx.InnerException != null)
                            Console.Error.WriteLine($"  Inner: {sfEx.InnerException.GetType().Name}: {sfEx.InnerException.Message}");
                    }
                }
                else
                {
                    Console.Error.WriteLine("[WARN] _smartFormatter field not found in LocManager");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] _smartFormatter init: {ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.Message}"); }

            // Initialize _engTables to point to _tables (avoid null ref in fallback)
            try
            {
                var engTablesField = typeof(LocManager).GetField("_engTables",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                engTablesField?.SetValue(instance, tables);
            }
            catch { }

            Console.Error.WriteLine("[INFO] LocManager initialized with stub tables");

            // Use Harmony to patch methods that need fallback behavior
            var harmony = new Harmony("sts2headless.locpatch");

            // With real loc data loaded, we only need fallback patches for:
            // 1. LocTable.GetRawText — return key for missing entries instead of throwing
            // 2. LocManager.SmartFormat — _smartFormatter is null, return raw text instead
            // We do NOT patch GetFormattedText/GetRawText on LocString anymore
            // so the real localization pipeline works (needed for Neow event etc.)

            var getRawText = typeof(LocTable).GetMethod("GetRawText",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(string) }, null);
            var prefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetRawTextPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getRawText != null && prefix != null)
            {
                harmony.Patch(getRawText, new HarmonyMethod(prefix));
                Console.Error.WriteLine("[INFO] Patched LocTable.GetRawText");
            }

            // Patch GetLocString to not throw
            var getLocString = typeof(LocTable).GetMethod("GetLocString");
            var glsPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.GetLocStringPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (getLocString != null && glsPrefix != null)
            {
                try { harmony.Patch(getLocString, new HarmonyMethod(glsPrefix)); }
                catch (Exception ex4) { Console.Error.WriteLine($"[WARN] Failed to patch GetLocString: {ex4.Message}"); }
            }

            // Patch FromChooseABundleScreen to use our card selector
            try
            {
                var bundleMethod = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethod("FromChooseABundleScreen",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var bundlePrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.BundleScreenPrefix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                if (bundleMethod != null && bundlePrefix != null)
                {
                    harmony.Patch(bundleMethod, new HarmonyMethod(bundlePrefix));
                    Console.Error.WriteLine("[INFO] Patched FromChooseABundleScreen");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Bundle patch: {ex.Message}"); }

            try
            {
                var fromHandMethod = typeof(CardSelectCmd).GetMethod(
                    "FromHand", BindingFlags.Static | BindingFlags.Public);
                var fromHandPrefix = typeof(LocPatches).GetMethod(
                    nameof(LocPatches.CardSelectFromHandPrefix),
                    BindingFlags.Static | BindingFlags.Public);
                if (fromHandMethod != null && fromHandPrefix != null)
                {
                    harmony.Patch(fromHandMethod, new HarmonyMethod(fromHandPrefix));
                    Console.Error.WriteLine("[INFO] Patched hand selection context for TUI prompts");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Hand selection context patch: {ex.Message}"); }

            // Crystal Sphere normally opens a Godot-only tile screen. Replace that screen wait
            // with a headless task; the TUI drives SetTool/CellClicked on the captured minigame.
            PatchMethod(harmony, typeof(CrystalSphereMinigame), "PlayMinigame",
                nameof(LocPatches.CrystalSpherePlayPrefix));

            // Patch Neutralize.OnPlay to avoid NullRef in DamageCmd.Attack().Execute()
            try
            {
                var neutralizeType = typeof(MegaCrit.Sts2.Core.Models.Cards.Neutralize);
                var neutralizeOnPlay = neutralizeType.GetMethod("OnPlay",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (neutralizeOnPlay != null)
                {
                    var neutPrefix = typeof(LocPatches).GetMethod(nameof(LocPatches.NeutralizePrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (neutPrefix != null)
                    {
                        harmony.Patch(neutralizeOnPlay, new HarmonyMethod(neutPrefix));
                        Console.Error.WriteLine("[INFO] Patched Neutralize.OnPlay");
                    }
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Neutralize patch: {ex.Message}"); }

            // Patch HasEntry to always return true
            PatchMethod(harmony, typeof(LocTable), "HasEntry", nameof(LocPatches.HasEntryPrefix));

            // Patch IsLocalKey to always return true
            PatchMethod(harmony, typeof(LocTable), "IsLocalKey", nameof(LocPatches.HasEntryPrefix));

            // Patch LocString.Exists (static) to always return true
            var locStringExists = typeof(LocString).GetMethod("Exists",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (locStringExists != null)
            {
                PatchMethod(harmony, locStringExists, nameof(LocPatches.HasEntryPrefix));
            }

            // Patch LocTable.GetLocStringsWithPrefix to return empty list
            PatchMethod(harmony, typeof(LocTable), "GetLocStringsWithPrefix", nameof(LocPatches.GetLocStringsWithPrefixPrefix));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] InitLocManager failed: {ex.Message}");
        }
    }

    private static void PatchMethod(Harmony harmony, Type type, string methodName, string patchName)
    {
        try
        {
            var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            PatchMethod(harmony, method, patchName);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Failed to patch {type.Name}.{methodName}: {ex.Message}"); }
    }

    private static void PatchMethod(Harmony harmony, System.Reflection.MethodInfo? method, string patchName)
    {
        if (method == null) return;
        try
        {
            var prefix = typeof(LocPatches).GetMethod(patchName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (prefix != null) harmony.Patch(method, new HarmonyMethod(prefix));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Failed to patch {method.Name}: {ex.Message}"); }
    }

    internal static class LocPatches
    {
        public static void CardSelectFromHandPrefix(
            CardSelectorPrefs prefs, AbstractModel source)
        {
            var sim = _bundleSimRef;
            if (sim == null) return;

            var isDiscard = false;
            var isUpgrade = false;
            try
            {
                var prompt = prefs.Prompt;
                var discardPrompt = CardSelectorPrefs.DiscardSelectionPrompt;
                isDiscard = prompt != null && discardPrompt != null &&
                    string.Equals(prompt.LocTable, discardPrompt.LocTable, StringComparison.Ordinal) &&
                    string.Equals(prompt.LocEntryKey, discardPrompt.LocEntryKey, StringComparison.Ordinal);
                isDiscard |= prompt?.LocEntryKey?.Contains(
                    "discard", StringComparison.OrdinalIgnoreCase) == true;
                isUpgrade = prompt?.LocEntryKey?.Contains(
                    "upgrade", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch { }

            var sourceEntry = source?.Id.Entry;
            if (sourceEntry is "GAMBLERS_BREW" or "GAMBLING_CHIP")
                isDiscard = true;
            if (sourceEntry == "ARMAMENTS")
                isUpgrade = true;
            sim._pendingSelectionKind = isDiscard ? "discard" : isUpgrade ? "upgrade" : null;
        }

        public static bool CrystalSpherePlayPrefix(
            CrystalSphereMinigame __instance, ref Task __result)
        {
            var sim = _bundleSimRef;
            if (sim == null)
                return true;
            sim._pendingCrystalSphere = __instance;
            Console.Error.WriteLine($"[SIM] Crystal Sphere pending: {__instance.DivinationCount} divinations");
            __result = RunCrystalSphereHeadless(__instance);
            return false;
        }

        private static async Task RunCrystalSphereHeadless(CrystalSphereMinigame game)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var completionField = typeof(CrystalSphereMinigame).GetField("_completionSource", flags);
            var completion = completionField?.GetValue(game) as TaskCompletionSource;
            if (completion == null)
                throw new InvalidOperationException("Crystal Sphere completion source was not found");
            await completion.Task;

            var completeMethod = typeof(CrystalSphereMinigame).GetMethod("CompleteMinigame", flags);
            if (completeMethod?.Invoke(game, null) is Task completeTask)
                await completeTask;
            else
                throw new InvalidOperationException("Crystal Sphere completion method was not found");
        }

        public static bool GetRawTextPrefix(LocTable __instance, string key, ref string __result)
        {
            // Return key as fallback "translation"
            __result = key;
            return false;
        }

        public static bool GetFormattedTextPrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }

        public static bool GetRawTextInstancePrefix(LocString __instance, ref string __result)
        {
            __result = __instance?.LocEntryKey ?? "";
            return false;
        }


        /// <summary>Harmony prefix: replace Neutralize.OnPlay with safe damage+weak.</summary>
        public static bool NeutralizePrefix(CardModel __instance, ref Task __result,
            PlayerChoiceContext choiceContext, CardPlay cardPlay)
        {
            if (cardPlay.Target == null) { __result = Task.CompletedTask; return false; }
            __result = NeutralizeSafe(__instance, choiceContext, cardPlay);
            return false;
        }

        private static async Task NeutralizeSafe(CardModel card, PlayerChoiceContext ctx, CardPlay play)
        {
            try
            {
                await CreatureCmd.Damage(ctx, play.Target!, card.DynamicVars.Damage.BaseValue,
                    MegaCrit.Sts2.Core.ValueProps.ValueProp.Move, card);
                await PowerCmd.Apply<WeakPower>(ctx, play.Target!, card.DynamicVars["WeakPower"].BaseValue,
                    card.Owner.Creature, card, false);
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] Neutralize safe: {ex.Message}"); }
        }

        public static bool HasEntryPrefix(ref bool __result)
        {
            __result = true;
            return false;
        }

        public static bool GetLocStringPrefix(LocTable __instance, string key, ref LocString __result)
        {
            var nameField = typeof(LocTable).GetField("_name",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tableName = nameField?.GetValue(__instance) as string ?? "_unknown";
            __result = new LocString(tableName, key);
            return false;
        }

        /// <summary>
        /// Intercept bundle selection — store bundles and wait for player to pick a pack index.
        /// </summary>
        public static bool BundleScreenPrefix(
            MegaCrit.Sts2.Core.Entities.Players.Player player,
            IReadOnlyList<IReadOnlyList<CardModel>> bundles,
            ref Task<IEnumerable<CardModel>> __result)
        {
            if (bundles.Count == 0)
            {
                __result = Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
                return false;
            }

            // Store pending bundles for the main loop to present
            var sim = _bundleSimRef;
            if (sim != null)
            {
                sim._pendingBundles = bundles;
                sim._pendingBundleTcs = new TaskCompletionSource<IEnumerable<CardModel>>();
                Console.Error.WriteLine($"[SIM] Bundle selection pending: {bundles.Count} packs");

                __result = sim._pendingBundleTcs.Task;
                return false;
            }

            __result = Task.FromResult<IEnumerable<CardModel>>(bundles[0]);
            return false;
        }

        // Static reference so Harmony patch can access the simulator instance
        internal static RunSimulator? _bundleSimRef;

        public static bool GetLocStringsWithPrefixPrefix(ref IReadOnlyList<LocString> __result)
        {
            __result = new List<LocString>();
            return false;
        }
    }

    private static void Log(string message)
    {
        Console.Error.WriteLine($"[SIM] {message}");
    }

    private static Dictionary<string, object?> Error(string message) =>
        new() { ["type"] = "error", ["message"] = message };

    private static Dictionary<string, object?> ErrorWithTrace(string context, Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["message"] = $"{context}: {inner.GetType().Name}: {inner.Message}",
            ["stack_trace"] = inner.StackTrace,
        };
    }

    public Dictionary<string, object?> GetFullMap()
    {
        if (_runState?.Map == null)
            return Error("No map available");

        var map = _runState.Map;
        var rows = new List<List<Dictionary<string, object?>>>();
        var currentCoord = _runState.CurrentMapCoord;
        var visited = _runState.VisitedMapCoords;

        for (int row = 0; row < map.GetRowCount(); row++)
        {
            var rowNodes = new List<Dictionary<string, object?>>();
            foreach (var point in map.GetPointsInRow(row))
            {
                if (point == null) continue;
                var children = point.Children?.Select(ch => new Dictionary<string, object?>
                {
                    ["col"] = (int)ch.coord.col,
                    ["row"] = (int)ch.coord.row,
                }).ToList();

                var isVisited = visited?.Any(v => v.col == point.coord.col && v.row == point.coord.row) ?? false;
                var isCurrent = currentCoord.HasValue &&
                    currentCoord.Value.col == point.coord.col && currentCoord.Value.row == point.coord.row;

                rowNodes.Add(new Dictionary<string, object?>
                {
                    ["col"] = (int)point.coord.col,
                    ["row"] = (int)point.coord.row,
                    ["type"] = point.PointType.ToString(),
                    ["children"] = children,
                    ["visited"] = isVisited,
                    ["current"] = isCurrent,
                });
            }
            if (rowNodes.Count > 0)
                rows.Add(rowNodes);
        }

        // Boss node
        var bossNode = new Dictionary<string, object?>
        {
            ["col"] = (int)map.BossMapPoint.coord.col,
            ["row"] = (int)map.BossMapPoint.coord.row,
            ["type"] = map.BossMapPoint.PointType.ToString(),
        };

        // Add boss name/id — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                var monsterKey = bossIdEntry.EndsWith("_BOSS") ? bossIdEntry[..^5] : bossIdEntry;
                if (monsterKey == "THE_KIN") monsterKey = "KIN_PRIEST";
                bossNode["id"] = bossIdEntry;
                bossNode["name"] = _loc.Monster(monsterKey);
            }
        }
        catch { }

        return new Dictionary<string, object?>
        {
            ["type"] = "map",
            ["context"] = RunContext(),
            ["rows"] = rows,
            ["boss"] = bossNode,
            ["current_coord"] = currentCoord.HasValue ? new Dictionary<string, object?>
            {
                ["col"] = (int)currentCoord.Value.col,
                ["row"] = (int)currentCoord.Value.row,
            } : null,
        };
    }

    public void CleanUp()
    {
        try
        {
            if (RunManager.Instance.IsInProgress)
                RunManager.Instance.CleanUp(graceful: true);
            _runState = null;
        }
        catch (Exception ex)
        {
            Log($"CleanUp exception: {ex.Message}");
        }
    }

    #endregion
}
