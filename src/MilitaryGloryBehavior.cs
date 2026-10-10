using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace MilitaryGlory
{
    public sealed class MilitaryGloryBehavior : CampaignBehaviorBase
    {
        private const string InactiveDead = "dead";
        private const string InactiveLeft = "left";
        private const string InactivePromoted = "promoted";
        private List<SoldierRecord> _records = new List<SoldierRecord>();
        private int _nextNumber = 1;
        private bool _trackingInitialized;
        private SoldierRecord _conversationRecord;
        private CharacterObject _conversationTroop;
        private PartyBase _conversationParty;
        private bool _promotionInProgress;
        private Dictionary<string, string> _promotedOrigins = new Dictionary<string, string>();
        // Troop upgrades are applied to PartyScreen's temporary roster before the owning
        // PartyBase roster is committed. Keep the original type so Cancel can roll the
        // dossiers back, and prevent normal roster reconciliation from undoing the staged
        // upgrade while the screen is still open.
        private readonly Dictionary<SoldierRecord, string> _pendingUpgradeOrigins =
            new Dictionary<SoldierRecord, string>();
        public static MilitaryGloryBehavior Instance { get; private set; }
        public MilitaryGloryBehavior() => Instance = this;

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, SynchronizeAll);
            CampaignEvents.PlayerUpgradedTroopsEvent.AddNonSerializedListener(this, OnPlayerUpgradedTroops);
            // RecruitmentVM.OnDone, tavern mercenary recruitment and the native main-party
            // prisoner conversion path all dispatch this player-facing event with the exact
            // troop type and confirmed count. OnTroopRecruitedEvent belongs to a different
            // generic recruitment path and is not reliably raised by the village UI.
            CampaignEvents.OnUnitRecruitedEvent.AddNonSerializedListener(this, OnUnitRecruited);
            CampaignEvents.MapEventStarted.AddNonSerializedListener(this, OnMapEventStarted);
            CampaignEvents.BattleStarted.AddNonSerializedListener(this, OnBattleStarted);
            CampaignEvents.BeforeMissionOpenedEvent.AddNonSerializedListener(this, OnBeforeMissionOpened);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, OnMapEventEnded);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("MilitaryGlory_Records", ref _records);
            dataStore.SyncData("MilitaryGlory_NextNumber", ref _nextNumber);
            dataStore.SyncData("MilitaryGlory_PromotedOrigins", ref _promotedOrigins);
            dataStore.SyncData("MilitaryGlory_TrackingInitialized", ref _trackingInitialized);
            _records ??= new List<SoldierRecord>();
            _promotedOrigins ??= new Dictionary<string, string>();
            // Never mutate campaign state while the save system is writing it. Validation
            // belongs exclusively to deserialization and leaves every valid record untouched.
            if (dataStore.IsLoading)
            {
                NormalizeLoadedRecords();
                // Older Military Glory saves have no marker, but any persisted dossier
                // proves that tracking was already initialized. Never bootstrap them again.
                if (_records.Count > 0) _trackingInitialized = true;
            }
            Instance = this;
        }

        private void NormalizeLoadedRecords()
        {
            _records.RemoveAll(x => x == null);
            foreach (var record in _records)
            {
                if (record.Defeats < 0) record.Defeats = 0;
                if (record.Wounds < 0) record.Wounds = 0;
            }

            if (_nextNumber < 1) _nextNumber = 1;
            // The persisted counter is a permanent high-water mark. A stale counter may be
            // moved forward, but a valid existing dossier number is never compacted or changed.
            int highestExisting = _records.Select(x => x.Number).DefaultIfEmpty(0).Max();
            if (highestExisting == int.MaxValue) _nextNumber = 0;
            else if (_nextNumber <= highestExisting) _nextNumber = highestExisting + 1;
            EnsureUniqueRecordNumbers();
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            // Upgrade old saves: records without an owner used to belong to the main party.
            PartyBase mainParty = MobileParty.MainParty?.Party;
            string mainId = GetLedgerId(mainParty);
            InitializeUntrackedSave(mainParty, mainId);
            foreach (var record in _records.Where(x => string.IsNullOrEmpty(x.PartyId))) record.PartyId = mainId;
            // Migrate saves that used the main party's transient PartyBase id.
            if (mainParty != null)
                foreach (var record in _records.Where(x => x.PartyId == mainParty.Id)) record.PartyId = mainId;
            // Numbered soldiers belong exclusively to the player-led party. Records left in
            // old garrison ledgers are historical departures; they must never reattach when a
            // garrison is recreated or when troops are taken back from it.
            foreach (var record in _records.Where(x => x.IsActive && x.PartyId != mainId).ToList())
                ArchiveRecord(record, InactiveLeft);
            // Party-screen transactions cannot survive a save/load. Treat leftovers as cancelled.
            foreach (var record in _records) record.PendingDestinationPartyId = null;
            SynchronizeAll();
            RegisterPromotionDialogs(starter);
            InformationManager.DisplayMessage(new InformationMessage(
                MGText.Get("MG_MODULE_ACTIVE", "Military Glory: tracking module loaded (build 1470).")));
        }

        private void InitializeUntrackedSave(PartyBase mainParty, string mainId)
        {
            if (_trackingInitialized) return;

            // One deliberate migration path for a campaign that has never had a ledger:
            // every ordinary soldier currently in the main party receives one dossier.
            if (mainParty?.MemberRoster != null)
            {
                string origin = MGText.Get("MG_ORIGIN_LEGACY_SAVE", "Legacy save initial party");
                foreach (var element in mainParty.MemberRoster.GetTroopRoster()
                             .Where(x => x.Character != null && !x.Character.IsHero && x.Number > 0))
                {
                    for (int i = 0; i < element.Number; i++)
                    {
                        _records.Add(new SoldierRecord(AllocateNumber(), element.Character.StringId,
                            mainId, null, origin)
                        {
                            IsWounded = i < element.WoundedNumber
                        });
                    }
                }
            }

            // An empty party is initialized too, preventing a later load from being mistaken
            // for another first-time migration.
            _trackingInitialized = true;
        }

        private void RegisterPromotionDialogs(CampaignGameStarter starter)
        {
            starter.AddDialogLine("mg_veteran_start", "start", "mg_veteran_talk",
                MGText.Get("MG_DLG_HELLO", "Commander, you wished to see me?"), IsVeteranConversation, null);
            starter.AddPlayerLine("mg_veteran_promote", "mg_veteran_talk", "mg_veteran_confirm",
                MGText.Get("MG_DLG_OFFER", "I wish to reward you for your services. Your military glory merits lordship."), CanOfferLordship, null);
            starter.AddPlayerLine("mg_veteran_promote_unqualified", "mg_veteran_talk", "mg_veteran_unqualified",
                MGText.Get("MG_DLG_OFFER_UNQUALIFIED", "I intend to make you a lord."), CanRequestLordshipBeforeQualified, null);
            starter.AddPlayerLine("mg_veteran_promote_no_authority", "mg_veteran_talk", "mg_veteran_no_authority",
                MGText.Get("MG_DLG_OFFER_UNQUALIFIED", "I intend to make you a lord."), LacksAuthorityToOfferLordship, null);
            starter.AddPlayerLine("mg_veteran_promote_no_fief", "mg_veteran_talk", "mg_veteran_no_fief",
                MGText.Get("MG_DLG_OFFER_UNQUALIFIED", "I intend to make you a lord."), LacksFiefToOfferLordship, null);
            starter.AddPlayerLine("mg_veteran_promote_no_gold", "mg_veteran_talk", "mg_veteran_no_gold",
                MGText.Get("MG_DLG_OFFER_UNQUALIFIED", "I intend to make you a lord."), LacksGoldToOfferLordship, null);
            starter.AddPlayerLine("mg_veteran_promote_no_influence", "mg_veteran_talk", "mg_veteran_no_influence",
                MGText.Get("MG_DLG_OFFER_UNQUALIFIED", "I intend to make you a lord."), LacksInfluenceToOfferLordship, null);
            starter.AddPlayerLine("mg_veteran_promote_no_resources", "mg_veteran_talk", "mg_veteran_no_resources",
                MGText.Get("MG_DLG_OFFER_UNQUALIFIED", "I intend to make you a lord."), LacksGoldAndInfluenceToOfferLordship, null);
            starter.AddPlayerLine("mg_veteran_leave", "mg_veteran_talk", "close_window",
                MGText.Get("MG_DLG_LEAVE", "Nothing. You may go."), null, ClearConversationCandidate);
            starter.AddDialogLine("mg_veteran_unqualified", "mg_veteran_unqualified", "mg_veteran_talk",
                MGText.Get("MG_DLG_INSUFFICIENT", "Commander, my ability is not yet sufficient to serve as a lord."), null, null);
            starter.AddDialogLine("mg_veteran_no_authority", "mg_veteran_no_authority", "mg_veteran_talk",
                MGText.Get("MG_DLG_NO_AUTHORITY", "Commander, you do not yet have the authority to ennoble me."), null, null);
            starter.AddDialogLine("mg_veteran_no_fief", "mg_veteran_no_fief", "mg_veteran_talk",
                MGText.Get("MG_DLG_NO_FIEF", "Commander, you have no town or castle to grant me for a new noble house."), null, null);
            starter.AddDialogLine("mg_veteran_no_gold", "mg_veteran_no_gold", "mg_veteran_talk",
                MGText.Get("MG_DLG_NO_GOLD", "Commander, you cannot yet provide the 20,000 denars needed to establish my house."), null, null);
            starter.AddDialogLine("mg_veteran_no_influence", "mg_veteran_no_influence", "mg_veteran_talk",
                MGText.Get("MG_DLG_NO_INFLUENCE", "Commander, your influence is not yet sufficient to ennoble me. You need 500 influence."), null, null);
            starter.AddDialogLine("mg_veteran_no_resources", "mg_veteran_no_resources", "mg_veteran_talk",
                MGText.Get("MG_DLG_NO_RESOURCES", "Commander, establishing my house requires 20,000 denars, and ennoblement requires 500 influence."), null, null);
            starter.AddDialogLine("mg_veteran_confirm", "mg_veteran_confirm", "mg_veteran_final",
                MGText.Get("MG_DLG_ACCEPT", "Yes. I would be honored to be considered."), null, null);
            starter.AddPlayerLine("mg_veteran_final_yes", "mg_veteran_final", "close_window",
                MGText.Get("MG_DLG_NAME", "Very well. I will grant you a fief and choose the name of your noble house."), null, BeginNaming);
            starter.AddPlayerLine("mg_veteran_final_no", "mg_veteran_final", "close_window",
                MGText.Get("MG_DLG_LATER", "We will discuss this another time."), null, ClearConversationCandidate);
        }

        private void OnUnitRecruited(CharacterObject troop, int count)
        {
            if (troop == null || troop.IsHero || count <= 0) return;
            // This event proves troop type and count, but it contains no source argument.
            // CurrentSettlement cannot distinguish village volunteers, tavern mercenaries
            // and prisoners recruited while the player is inside a settlement.
            string origin = MGText.Get("MG_ORIGIN_UNKNOWN", "Unknown origin");
            string mainId = GetLedgerId(MobileParty.MainParty?.Party);
            if (string.IsNullOrEmpty(mainId)) return;
            for (int i = 0; i < count; i++)
                _records.Add(new SoldierRecord(AllocateNumber(), troop.StringId,
                    mainId, null, origin));
        }

        internal void AuthorizePartyScreenAdditions(PartyBase leftOwnerParty, TroopRoster leftMemberRoster,
            IReadOnlyDictionary<string, int> initialLeftRoster,
            IReadOnlyDictionary<string, int> loggedLeftDepartures, PartyBase rightOwnerParty,
            TroopRoster rightMemberRoster, IReadOnlyDictionary<string, int> initialRightRoster,
            IReadOnlyDictionary<string, int> loggedRightDepartures)
        {
            PartyBase main = MobileParty.MainParty?.Party;
            if (main == null) return;

            PartyBase otherParty;
            TroopRoster finalMainRoster;
            IReadOnlyDictionary<string, int> initialMainRoster;
            IReadOnlyDictionary<string, int> loggedOtherDepartures;
            if (leftOwnerParty == main)
            {
                otherParty = rightOwnerParty;
                finalMainRoster = leftMemberRoster;
                initialMainRoster = initialLeftRoster;
                loggedOtherDepartures = loggedRightDepartures;
            }
            else if (rightOwnerParty == main)
            {
                otherParty = leftOwnerParty;
                finalMainRoster = rightMemberRoster;
                initialMainRoster = initialRightRoster;
                loggedOtherDepartures = loggedLeftDepartures;
            }
            else return;

            if (finalMainRoster == null) return;
            bool fromGarrison = otherParty?.MobileParty?.GarrisonPartyComponent?.Settlement != null;

            // A garrison settlement is an exact source container. A family party leader's
            // home or birthplace is not evidence of where an individual soldier joined.
            Settlement originSettlement = fromGarrison
                ? otherParty.MobileParty.GarrisonPartyComponent.Settlement
                : null;
            string originText = originSettlement?.Name?.ToString() ??
                MGText.Get("MG_ORIGIN_UNKNOWN", "Unknown origin");
            foreach (var element in finalMainRoster.GetTroopRoster()
                         .Where(x => x.Character != null && !x.Character.IsHero && x.Number > 0))
            {
                CharacterObject troop = element.Character;
                int mainBefore = initialMainRoster != null &&
                    initialMainRoster.TryGetValue(troop.StringId, out int savedMain) ? savedMain : 0;
                // Opening and confirmed closing snapshots prove the positive party-screen
                // addition. The transfer journal independently proves that the player
                // performed the matching operation from the other side.
                int loggedTransfer = loggedOtherDepartures != null &&
                    loggedOtherDepartures.TryGetValue(troop.StringId, out int savedLogged)
                    ? savedLogged : 0;
                int transferred = System.Math.Min(loggedTransfer,
                    System.Math.Max(0, element.Number - mainBefore));
                if (transferred <= 0) continue;

                // Every soldier entering the player-led party is a new recruit. No identity,
                // number or merit is ever restored from a garrison or another party.
                string mainId = GetLedgerId(main);
                while (transferred-- > 0)
                    _records.Add(new SoldierRecord(AllocateNumber(), troop.StringId,
                        mainId, originSettlement?.StringId, originText));
            }
        }

        internal void ApplyConfirmedPartyScreenDepartures(PartyBase leftOwnerParty,
            TroopRoster finalLeftRoster, IReadOnlyDictionary<string, int> initialLeftRoster,
            IReadOnlyDictionary<string, int> loggedLeftDepartures,
            PartyBase rightOwnerParty, TroopRoster finalRightRoster,
            IReadOnlyDictionary<string, int> initialRightRoster,
            IReadOnlyDictionary<string, int> loggedRightDepartures)
        {
            ApplyConfirmedDeparturesOneWay(leftOwnerParty, finalLeftRoster, initialLeftRoster,
                loggedLeftDepartures, rightOwnerParty, finalRightRoster, initialRightRoster);
            ApplyConfirmedDeparturesOneWay(rightOwnerParty, finalRightRoster, initialRightRoster,
                loggedRightDepartures, leftOwnerParty, finalLeftRoster, initialLeftRoster);
        }

        private void ApplyConfirmedDeparturesOneWay(PartyBase sourceParty, TroopRoster finalSourceRoster,
            IReadOnlyDictionary<string, int> initialSourceRoster,
            IReadOnlyDictionary<string, int> loggedSourceDepartures, PartyBase destinationParty,
            TroopRoster finalDestinationRoster, IReadOnlyDictionary<string, int> initialDestinationRoster)
        {
            if (!IsTrackedParty(sourceParty) || finalSourceRoster == null ||
                initialSourceRoster == null || loggedSourceDepartures == null) return;
            string sourceId = GetLedgerId(sourceParty);
            string destinationId = IsTrackedParty(destinationParty) ? GetLedgerId(destinationParty) : null;

            foreach (var logged in loggedSourceDepartures)
            {
                string characterId = logged.Key;
                int sourceBefore = initialSourceRoster.TryGetValue(characterId,
                    out int savedSource) ? savedSource : 0;
                int sourceAfter = finalSourceRoster.GetTroopRoster()
                    .Where(x => x.Character?.StringId == characterId).Sum(x => x.Number);
                int confirmedDepartures = System.Math.Min(logged.Value,
                    System.Math.Max(0, sourceBefore - sourceAfter));
                if (confirmedDepartures == 0) continue;

                // Individually selected Military Glory rows already carry their exact pending
                // destination. Leave those identities for FinishPartyScreenTransaction.
                int exactPending = _records.Count(x => x.IsActive && x.PartyId == sourceId &&
                    x.CharacterId == characterId && !string.IsNullOrEmpty(x.PendingDestinationPartyId));
                int anonymousDepartures = System.Math.Max(0, confirmedDepartures - exactPending);
                if (anonymousDepartures == 0) continue;

                int confirmedArrivals = 0;
                if (destinationId != null && finalDestinationRoster != null && initialDestinationRoster != null)
                {
                    int destinationBefore = initialDestinationRoster.TryGetValue(characterId,
                        out int savedDestination) ? savedDestination : 0;
                    int destinationAfter = finalDestinationRoster.GetTroopRoster()
                        .Where(x => x.Character?.StringId == characterId).Sum(x => x.Number);
                    confirmedArrivals = System.Math.Max(0, destinationAfter - destinationBefore - exactPending);
                }

                // Native bulk controls expose a confirmed count but no individual identity.
                // Permanent number order is the mod's deterministic selection rule.
                var departingRecords = _records.Where(x => x.IsActive && x.PartyId == sourceId &&
                        x.CharacterId == characterId && string.IsNullOrEmpty(x.PendingDestinationPartyId))
                    .OrderBy(x => x.Number).Take(anonymousDepartures).ToList();
                int transferCount = System.Math.Min(confirmedArrivals, departingRecords.Count);
                foreach (var record in departingRecords.Take(transferCount)) record.PartyId = destinationId;
                foreach (var record in departingRecords.Skip(transferCount))
                    ArchiveRecord(record, InactiveLeft);
            }
        }

        private void OnMapEventStarted(TaleWorlds.CampaignSystem.MapEvents.MapEvent mapEvent,
            PartyBase attacker, PartyBase defender)
        {
            // Freeze the pre-battle virtual roster before casualties are applied. This is
            // especially important for wounded dossiers, which must never enter the pool of
            // soldiers eligible to receive this battle's kills, wounds or deaths.
            SynchronizeAll();
            PartyBase main = MobileParty.MainParty?.Party;
            StartBattleTracking(mapEvent, true, attacker == main || defender == main);
        }

        private void OnBattleStarted(PartyBase attacker, PartyBase defender, object subject, bool showNotification)
        {
            PartyBase main = MobileParty.MainParty?.Party;
            StartBattleTracking(TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent,
                false, attacker == main || defender == main);
        }

        private void OnBeforeMissionOpened() => EnsureBattleTracking(
            TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent);

        internal void EnsureBattleTracking(TaleWorlds.CampaignSystem.MapEvents.MapEvent mapEvent)
        {
            if (!BattleResultTracker.IsArmed) StartBattleTracking(mapEvent, false, mapEvent != null);
        }

        internal void EnsureBattleTrackingForParty(PartyBase party)
        {
            if (BattleResultTracker.IsArmed) return;
            // This method is called only from an actual mission agent/hit carrying a
            // PartyAgentOrigin. A spawned agent from a tracked player party is stronger
            // evidence than MapEvent.PlayerMapEvent, which can be null in helper battles.
            bool hasEligibleDossier = IsTrackedParty(party) &&
                _records.Any(x => x.IsActive && x.PartyId == GetLedgerId(party) &&
                string.IsNullOrEmpty(x.PendingDestinationPartyId) &&
                party.MemberRoster.GetTroopRoster().Any(r =>
                    r.Character?.StringId == x.CharacterId && r.Number > 0));
            if (BattleResultTracker.BeginBattle(hasEligibleDossier, false))
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    MGText.Get("MG_BATTLE_TRACKING_STARTED", "Military Glory: battle merit tracking started.")));
            }
        }

        private void StartBattleTracking(TaleWorlds.CampaignSystem.MapEvents.MapEvent mapEvent,
            bool forceReset, bool knownPlayerBattle)
        {
            PartyBase mainParty = MobileParty.MainParty?.Party;
            bool playerRelated = mapEvent != null && (knownPlayerBattle || mapEvent.IsPlayerMapEvent ||
                                  mapEvent == TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent ||
                                  (mainParty != null && mapEvent.InvolvedParties.Contains(mainParty)));
            var involvedTrackedParties = mapEvent?.InvolvedParties
                .Where(IsTrackedParty).ToList() ?? new List<PartyBase>();
            bool shouldTrack = playerRelated && involvedTrackedParties.Any(party =>
                _records.Any(x => x.IsActive && x.PartyId == GetLedgerId(party) &&
                                  string.IsNullOrEmpty(x.PendingDestinationPartyId) &&
                                  party.MemberRoster.GetTroopRoster().Any(r =>
                                      r.Character?.StringId == x.CharacterId && r.Number > 0)));
            if (BattleResultTracker.BeginBattle(shouldTrack, forceReset))
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    MGText.Get("MG_BATTLE_TRACKING_STARTED", "Military Glory: battle merit tracking started.")));
            }
        }

        private void OnMapEventEnded(TaleWorlds.CampaignSystem.MapEvents.MapEvent mapEvent)
        {
            TryFinalizeBattle(mapEvent);
            BattleResultTracker.EndBattle();
            // MissionEnded fires before Bannerlord has necessarily committed the mission's
            // survivors back to PartyBase.MemberRoster.  Reconciliation during that window
            // can observe a temporarily reduced (or empty) troop stack, delete some or all
            // veteran dossiers in it, and later recreate those soldiers with fresh numbers
            // and zero merit.  A map
            // event has finished committing its casualties by this callback, so this is the
            // first safe point at which to reconcile the campaign roster.
            SynchronizeAll();
        }

        private void TryFinalizeBattle(TaleWorlds.CampaignSystem.MapEvents.MapEvent mapEvent)
        {
            // Mission battles settle in phase one at MissionEnded. Auto-resolve battles,
            // which have no mission callback, settle here at MapEventEnded.
            if (BattleResultTracker.WasDistributed || !BattleResultTracker.IsArmed) return;
            if (BattleResultTracker.TryDistribute(mapEvent, this, out int troopTypes, out int kills))
                InformationManager.DisplayMessage(new InformationMessage(
                    MGText.Get("MG_BATTLE_DISTRIBUTED", "Military Glory: distributed battle merit for ") + troopTypes +
                    MGText.Get("MG_BATTLE_TYPES", " troop types, ") + kills + MGText.Get("MG_BATTLE_KILLS", " defeats.")));
        }

        internal void ApplyBattleResult(CharacterObject troop, PartyBase party, int participated,
            int kills, int dead, int wounded, int seed)
        {
            if (troop == null || party == null || participated <= 0) return;
            var candidates = _records.Where(x => x.IsActive && x.CharacterId == troop.StringId &&
                                                  x.PartyId == GetLedgerId(party) &&
                                                  !x.IsWounded &&
                                                  string.IsNullOrEmpty(x.PendingDestinationPartyId)).ToList();
            if (candidates.Count == 0) return;

            var random = new System.Random(seed);
            Shuffle(candidates, random);
            var participants = candidates.Take(System.Math.Min(participated, candidates.Count)).ToList();
            if (participants.Count == 0) return;

            int[] killShares = CreateKillDistribution(participants.Count, kills, random);
            AssignKillSharesByPriorDefeats(participants, killShares, random);

            // Death and wound results are disjoint and are assigned only among participants.
            Shuffle(participants, random);
            int deadCount = System.Math.Min(dead, participants.Count);
            var fallen = participants.Take(deadCount).ToList();
            var survivors = participants.Skip(deadCount).ToList();
            int woundedCount = System.Math.Min(wounded, survivors.Count);
            foreach (var record in survivors.Take(woundedCount))
            {
                record.AddWound();
                record.IsWounded = true;
            }
            foreach (var record in fallen) ArchiveRecord(record, InactiveDead);
        }

        internal void ApplyFinalBattleResult(
            TaleWorlds.CampaignSystem.MapEvents.MapEventParty eventParty, int defeats, int seed)
        {
            PartyBase party = eventParty?.Party;
            if (party == null) return;
            string ledgerId = GetLedgerId(party);
            var troopResults = new List<System.Tuple<CharacterObject, int, int, int>>();
            var characterIds = new HashSet<string>(_records.Where(x => x.IsActive &&
                    x.PartyId == ledgerId && !x.IsWounded &&
                    string.IsNullOrEmpty(x.PendingDestinationPartyId))
                .Select(x => x.CharacterId));

            foreach (string characterId in characterIds)
            {
                CharacterObject troop = MBObjectManager.Instance.GetObject<CharacterObject>(characterId);
                if (troop == null || troop.IsHero) continue;
                int dead = eventParty.DiedInBattle.GetTroopCount(troop);
                int wounded = eventParty.WoundedInBattle.GetTroopCount(troop);
                int finalCount = party.MemberRoster.GetTroopCount(troop);
                int documented = _records.Count(x => x.IsActive && x.PartyId == ledgerId &&
                    x.CharacterId == characterId && !x.IsWounded &&
                    string.IsNullOrEmpty(x.PendingDestinationPartyId));
                int participated = System.Math.Min(documented, finalCount + dead);
                if (participated > 0 || dead > 0 || wounded > 0)
                    troopResults.Add(System.Tuple.Create(troop, participated, dead, wounded));
            }

            int participantTotal = troopResults.Sum(x => x.Item2);
            if (participantTotal <= 0) return;
            int[] individualShares = CreateKillDistribution(participantTotal, defeats,
                new System.Random(seed));
            int offset = 0;
            foreach (var result in troopResults)
            {
                int troopDefeats = 0;
                for (int i = 0; i < result.Item2; i++) troopDefeats += individualShares[offset++];
                ApplyBattleResult(result.Item1, party, result.Item2, troopDefeats,
                    result.Item3, result.Item4, seed ^ result.Item1.StringId.GetHashCode());
            }
        }

        private static void Shuffle<T>(IList<T> list, System.Random random)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                T value = list[i];
                list[i] = list[j];
                list[j] = value;
            }
        }

        private static void AssignKillSharesByPriorDefeats(
            IList<SoldierRecord> participants, IEnumerable<int> killShares, System.Random random)
        {
            // Participants have already been shuffled, so stable sorting also randomizes
            // ties instead of favouring an older dossier number. Starting with the best
            // pre-battle performer, each soldier has a 50% chance to take the largest
            // share still available. On a miss, only a lower share is selected, keeping
            // the advertised chance exact while leaving the result meaningfully random.
            var ranked = participants.OrderByDescending(x => x.Defeats).ToList();
            var available = killShares.OrderByDescending(x => x).ToList();
            for (int i = 0; i < ranked.Count && available.Count > 0; i++)
            {
                int selected = available.Count == 1 || random.NextDouble() < 0.50d
                    ? 0
                    : random.Next(1, available.Count);
                ranked[i].AddDefeats(available[selected]);
                available.RemoveAt(selected);
            }
        }

        internal static int[] CreateKillDistribution(int participantCount, int kills, System.Random random)
        {
            if (participantCount <= 0 || kills <= 0) return new int[System.Math.Max(0, participantCount)];

            // Give every participant a bell-shaped performance multiplier centred on 1.0.
            // The ordinary population is broad but bounded; a separate low-probability
            // long-tail step below can create one genuine standout without concentrating
            // the whole result in a handful of soldiers.
            var weights = new double[participantCount];
            double deviationTotal = 0d;
            for (int i = 0; i < participantCount; i++)
            {
                double u1 = System.Math.Max(double.Epsilon, random.NextDouble());
                double u2 = random.NextDouble();
                double normal = System.Math.Sqrt(-2d * System.Math.Log(u1)) *
                                System.Math.Cos(2d * System.Math.PI * u2);
                weights[i] = System.Math.Max(-0.55d, System.Math.Min(0.55d, normal * 0.30d));
                deviationTotal += weights[i];
            }
            // Re-centre this particular sample and rescale its deviations when necessary.
            // Consequently the sample mean is exactly 1.0 and every final multiplier stays
            // inside 0.45..1.55, even when there are only a handful of participants.
            double meanDeviation = deviationTotal / participantCount;
            double maxDeviation = weights.Max(x => System.Math.Abs(x - meanDeviation));
            double deviationScale = maxDeviation > 0.55d ? 0.55d / maxDeviation : 1d;
            for (int i = 0; i < participantCount; i++)
                weights[i] = 1d + (weights[i] - meanDeviation) * deviationScale;
            double weightTotal = participantCount;

            var result = new int[participantCount];
            var fractions = new double[participantCount];
            int assigned = 0;
            for (int i = 0; i < participantCount; i++)
            {
                double quota = kills * weights[i] / weightTotal;
                result[i] = (int)System.Math.Floor(quota);
                fractions[i] = quota - result[i];
                assigned += result[i];
            }

            // Randomized largest-remainder allocation preserves the exact total. Each
            // participant can receive at most one rounding kill, so rounding cannot create
            // a new outlier. When kills < participants, it also spreads those kills out.
            int remainder = kills - assigned;
            var available = Enumerable.Range(0, participantCount).ToList();
            while (remainder-- > 0 && available.Count > 0)
            {
                double total = available.Sum(x => fractions[x]);
                int selected;
                if (total <= double.Epsilon)
                {
                    selected = random.Next(available.Count);
                }
                else
                {
                    double roll = random.NextDouble() * total;
                    int position = 0;
                    while (position < available.Count - 1 && (roll -= fractions[available[position]]) > 0d)
                        position++;
                    selected = position;
                }
                int index = available[selected];
                result[index]++;
                available.RemoveAt(selected);
            }

            // Sparse results normally spread scarce kills across distinct participants.
            // In 15% of battles, use bounded weighted draws with replacement instead, so
            // a few soldiers can score twice (occasionally three times) and the result does
            // not mechanically become "30 kills = exactly 30 killers" every time.
            if (kills < participantCount && random.NextDouble() < 0.15d)
            {
                System.Array.Clear(result, 0, result.Length);
                for (int kill = 0; kill < kills; kill++)
                {
                    double roll = random.NextDouble() * weightTotal;
                    int selected = 0;
                    while (selected < participantCount - 1 && (roll -= weights[selected]) > 0d)
                        selected++;
                    result[selected]++;
                }
            }

            double mean = (double)kills / participantCount;
            // In 15% of sufficiently large troop results, create at most one standout.
            // For 120 participants and 500 kills this produces 20..25 kills. The chance
            // for any particular soldier to be that standout is only 0.15 / 120 = 0.125%.
            if (participantCount >= 20 && mean >= 2d && random.NextDouble() < 0.15d)
            {
                int standout = random.Next(participantCount);
                int targetMin = (int)System.Math.Ceiling(mean * 4.8d);
                int targetMax = (int)System.Math.Floor(mean * 6d);
                int target = targetMax > targetMin ? random.Next(targetMin, targetMax + 1) : targetMin;
                int donorFloor = System.Math.Max(1, (int)System.Math.Floor(mean * 0.35d));
                while (result[standout] < target)
                {
                    var donors = Enumerable.Range(0, participantCount)
                        .Where(x => x != standout && result[x] > donorFloor).ToList();
                    if (donors.Count == 0) break;
                    int donor = donors[random.Next(donors.Count)];
                    result[donor]--;
                    result[standout]++;
                }
            }

            // A perfectly even result is visually implausible. When rounding happens to
            // produce one, move a single kill while preserving the exact total.
            if (participantCount > 1 && result.All(x => x == result[0]) && result[0] > 0)
            {
                int receiver = random.Next(participantCount);
                int donor = (receiver + 1 + random.Next(participantCount - 1)) % participantCount;
                result[donor]--;
                result[receiver]++;
            }
            return result;
        }

        private bool IsVeteranConversation() => _conversationRecord != null && _conversationTroop != null &&
            Campaign.Current?.ConversationManager?.OneToOneConversationCharacter == _conversationTroop;

        private bool CanOfferLordship() => CanCheckMaterialRequirements() && HasGrantableFief() &&
            Hero.MainHero.Gold >= 20000 && Clan.PlayerClan.Influence >= 500f;

        private bool CanRequestLordshipBeforeQualified() => IsVeteranConversation() &&
            Hero.MainHero?.IsKingdomLeader == true &&
            _conversationRecord.CalculateMerit(_conversationTroop) < SoldierRecord.EnnoblementMeritRequired;

        private bool LacksAuthorityToOfferLordship() => IsVeteranConversation() &&
            Hero.MainHero?.IsKingdomLeader != true;

        private bool CanCheckMaterialRequirements() => IsVeteranConversation() &&
            Hero.MainHero?.IsKingdomLeader == true &&
            _conversationRecord.CalculateMerit(_conversationTroop) >= SoldierRecord.EnnoblementMeritRequired;

        private static bool HasGrantableFief() => Clan.PlayerClan != null && Settlement.All.Any(x =>
            x.OwnerClan == Clan.PlayerClan && (x.IsTown || x.IsCastle));

        private bool LacksFiefToOfferLordship() => CanCheckMaterialRequirements() && !HasGrantableFief();

        private bool LacksGoldToOfferLordship() => CanCheckMaterialRequirements() && HasGrantableFief() &&
            Hero.MainHero.Gold < 20000 && Clan.PlayerClan.Influence >= 500f;

        private bool LacksInfluenceToOfferLordship() => CanCheckMaterialRequirements() && HasGrantableFief() &&
            Hero.MainHero.Gold >= 20000 && Clan.PlayerClan.Influence < 500f;

        private bool LacksGoldAndInfluenceToOfferLordship() => CanCheckMaterialRequirements() && HasGrantableFief() &&
            Hero.MainHero.Gold < 20000 && Clan.PlayerClan.Influence < 500f;

        public void StartVeteranConversation(SoldierRecord record, CharacterObject troop, PartyBase party)
        {
            if (record == null || troop == null || party == null ||
                !_records.Contains(record) || !record.IsActive || !IsTrackedParty(party) ||
                record.CharacterId != troop.StringId || record.PartyId != GetLedgerId(party) ||
                !string.IsNullOrEmpty(record.PendingDestinationPartyId)) return;
            _conversationRecord = record;
            _conversationTroop = troop;
            _conversationParty = party;
        }

        public void CancelPendingConversation()
        {
            ClearConversationCandidate();
        }

        private void BeginNaming()
        {
            if (_conversationRecord == null) return;
            // The permanent identity is the numeric dossier number. Resolve the current
            // troop name only when presenting it, so upgrades and conversions cannot leave
            // a stale name in the save record.
            string currentTroopName = _conversationTroop?.Name?.ToString() ?? "";
            string defaultName = currentTroopName + _conversationRecord.Number;
            InformationManager.ShowTextInquiry(new TextInquiryData(MGText.Get("MG_ENNOBLE_TITLE", "Ennoble Soldier"), MGText.Get("MG_HERO_NAME_PROMPT", "Choose a name for this soldier:"),
                true, true, MGText.Get("MG_CONTINUE", "Continue"), MGText.Get("MG_CANCEL", "Cancel"), heroName => AskClanName(heroName), ClearConversationCandidate,
                false, ValidateName, defaultName, ""), true, false);
        }

        private void AskClanName(string heroName)
        {
            InformationManager.ShowTextInquiry(new TextInquiryData(MGText.Get("MG_CREATE_CLAN", "Create Clan"), MGText.Get("MG_CLAN_NAME_PROMPT", "Choose a name for the new clan:"),
                true, true, MGText.Get("MG_SELECT_FIEF", "Select Fief"), MGText.Get("MG_CANCEL", "Cancel"), clanName => AskFief(heroName, clanName),
                ClearConversationCandidate, false, ValidateName, "", ""), true, false);
        }

        private void AskFief(string heroName, string clanName)
        {
            var elements = Settlement.All
                .Where(x => x.OwnerClan == Clan.PlayerClan && (x.IsTown || x.IsCastle))
                .Select(x => new InquiryElement(x, x.Name.ToString(), null, true, MGText.Get("MG_GRANT_HINT", "Grant this settlement to the new lord")))
                .ToList();
            if (elements.Count == 0)
            {
                InformationManager.DisplayMessage(new InformationMessage(MGText.Get("MG_NEED_FIEF", "Ennoblement requires a town or castle owned by the player clan.")));
                ClearConversationCandidate();
                return;
            }

            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                MGText.Get("MG_SELECT_FIEF", "Select Fief"), MGText.Get("MG_SELECT_FIEF_PROMPT", "Select a town or castle to grant to the new lord. It will leave the player clan after ennoblement."),
                elements, false, 1, 1, MGText.Get("MG_ENNOBLE", "Ennoble"), MGText.Get("MG_CANCEL", "Cancel"),
                selected =>
                {
                    var fief = selected != null && selected.Count == 1 ? selected[0].Identifier as Settlement : null;
                    if (fief != null) ConfirmPromotionCost(heroName, clanName, fief);
                }, selected => ClearConversationCandidate(), "", false), true, false);
        }

        private void ConfirmPromotionCost(string heroName, string clanName, Settlement fief)
        {
            bool enoughGold = Hero.MainHero?.Gold >= 20000;
            bool enoughInfluence = Clan.PlayerClan?.Influence >= 500f;
            if (!enoughGold || !enoughInfluence)
            {
                string missing = !enoughGold && !enoughInfluence ? MGText.Get("MG_COST_BOTH", "20,000 denars and 500 influence") :
                    (!enoughGold ? MGText.Get("MG_COST_GOLD", "20,000 denars") : MGText.Get("MG_COST_INFLUENCE", "500 influence"));
                InformationManager.DisplayMessage(new InformationMessage(MGText.Get("MG_CANNOT_ENNOBLE", "Cannot ennoble: requires ") + missing + "."));
                ClearConversationCandidate();
                return;
            }

            InformationManager.ShowInquiry(new InquiryData(MGText.Get("MG_CONFIRM_ENNOBLE", "Confirm Ennoblement"),
                MGText.Get("MG_GRANT_PREFIX", "Grant ") + fief.Name + MGText.Get("MG_GRANT_SUFFIX", " and pay 20,000 denars and 500 influence. Continue?"),
                true, true, MGText.Get("MG_CONFIRM", "Confirm"), MGText.Get("MG_CANCEL", "Cancel"), () => PromoteVeteran(heroName, clanName, fief),
                ClearConversationCandidate), true, false);
        }

        private static System.Tuple<bool, string> ValidateName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new System.Tuple<bool, string>(false, MGText.Get("MG_NAME_EMPTY", "Name cannot be empty."));
            if (value.Length > 32) return new System.Tuple<bool, string>(false, MGText.Get("MG_NAME_LONG", "Name cannot exceed 32 characters."));
            if (value.IndexOfAny(new[] { '{', '}', '\r', '\n', '\t' }) >= 0)
                return new System.Tuple<bool, string>(false, MGText.Get("MG_NAME_INVALID", "Name contains unsupported characters."));
            return new System.Tuple<bool, string>(true, "");
        }

        private void PromoteVeteran(string heroName, string clanName, Settlement fief)
        {
            var record = _conversationRecord;
            var troop = _conversationTroop;
            var party = _conversationParty;
            if (_promotionInProgress || record == null || troop == null || party == null ||
                Hero.MainHero?.IsKingdomLeader != true || !_records.Contains(record) || !record.IsActive || fief == null ||
                record.CalculateMerit(troop) < SoldierRecord.EnnoblementMeritRequired ||
                record.CharacterId != troop.StringId || record.PartyId != GetLedgerId(party) ||
                !string.IsNullOrEmpty(record.PendingDestinationPartyId) || !IsTrackedParty(party) ||
                fief.OwnerClan != Clan.PlayerClan || (!fief.IsTown && !fief.IsCastle))
            {
                ClearConversationCandidate();
                return;
            }

            if (Hero.MainHero.Gold < 20000 || Clan.PlayerClan.Influence < 500f)
            {
                InformationManager.DisplayMessage(new InformationMessage(MGText.Get("MG_FAIL_RESOURCES", "Ennoblement failed: insufficient denars or influence.")));
                ClearConversationCandidate();
                return;
            }

            var rosterElement = party.MemberRoster.GetTroopRoster().FirstOrDefault(x => x.Character == troop);
            if (rosterElement.Character == null || rosterElement.Number <= 0)
            {
                InformationManager.DisplayMessage(new InformationMessage(MGText.Get("MG_FAIL_MISSING", "Ennoblement failed: the soldier is no longer in the original party.")));
                ClearConversationCandidate();
                return;
            }

            // Freeze the merit used for both qualification and generated skills against the
            // soldier's actual troop class at the moment of ennoblement.
            int merit = record.CalculateMerit(troop);
            CultureObject lordCulture = ResolveLordCulture(troop);
            Settlement birthplace = !string.IsNullOrEmpty(record.BirthplaceSettlementId)
                ? Settlement.All.FirstOrDefault(x => x.StringId == record.BirthplaceSettlementId)
                : null;
            // The Hero API requires a real settlement. Textual origins such as captive,
            // bandit and unknown remain in MilitaryGlory_PromotedOrigins.
            birthplace ??= Settlement.All.FirstOrDefault(x => x.Culture == lordCulture) ?? Hero.MainHero.CurrentSettlement;
            if (birthplace == null)
            {
                InformationManager.DisplayMessage(new InformationMessage(MGText.Get("MG_FAIL_BIRTHPLACE", "Ennoblement failed: no suitable birthplace exists for this culture.")));
                return;
            }

            _promotionInProgress = true;
            string promotionStage = "创建英雄";
            Hero createdHero = null;
            bool goldTransferred = false;
            try
            {
                Hero hero = HeroCreator.CreateSpecialHero(troop, birthplace, Clan.PlayerClan, Clan.PlayerClan, 25);
                createdHero = hero;
                SetCharacterCulture(hero.CharacterObject, lordCulture);
                var name = new TextObject(heroName);
                hero.SetName(name, name);
                // From this point onward, mirror CompanionRolesCampaignBehavior's vanilla
                // ClanNameSelectionIsDone sequence. Our only additions are the soldier's
                // chosen name/skills above and retiring his ordinary dossier at the end.
                AddCompanionAction.Apply(Clan.PlayerClan, hero);
                RemoveCompanionAction.ApplyByByTurningToLord(Clan.PlayerClan, hero);
                hero.SetNewOccupation(Occupation.Lord);
                ApplyVeteranSkills(hero, troop, merit, record.Number);
                promotionStage = "创建新家族";
                int bannerIconId = GetVanillaClanBannerIconId();
                Clan newClan = Clan.CreateCompanionToLordClan(hero, fief, new TextObject(clanName), bannerIconId);
                if (newClan == null) throw new System.InvalidOperationException(MGText.Get("MG_FAIL_CLAN", "The game could not create the new clan."));
                if (newClan != null) newClan.Culture = lordCulture;

                promotionStage = "建立领主部队";
                if (hero.PartyBelongedTo == MobileParty.MainParty)
                    MobileParty.MainParty.MemberRoster.AddToCounts(hero.CharacterObject, -1);
                MobileParty lordParty = hero.PartyBelongedTo;
                if (lordParty == null)
                {
                    lordParty = LordPartyComponent.CreateLordParty(hero.StringId, hero,
                        MobileParty.MainParty.Position, 3f, fief, hero);
                    lordParty.MemberRoster.AddToCounts(newClan.Culture.BasicTroop, MBRandom.RandomInt(12, 15));
                    lordParty.MemberRoster.AddToCounts(newClan.Culture.EliteBasicTroop, MBRandom.RandomInt(10, 15));
                }
                else
                {
                    lordParty.ActualClan = newClan;
                    lordParty.Party.SetVisualAsDirty();
                }
                promotionStage = "生成家族成员";
                ApplyVanillaCompanionLordExtras(hero, newClan, fief);
                promotionStage = "支付册封费用";
                GiveGoldAction.ApplyBetweenCharacters(Hero.MainHero, hero, 20000, false);
                goldTransferred = true;
                GainKingdomInfluenceAction.ApplyForDefault(Hero.MainHero, -500f);
                promotionStage = "完成关系与档案";
                ChangeRelationAction.ApplyPlayerRelation(hero, 50, true, true);
                _promotedOrigins[hero.StringId] = string.IsNullOrEmpty(record.OriginText) ? MGText.Get("MG_ORIGIN_UNKNOWN", "Unknown origin") : record.OriginText;

                // The new identity must exist completely before the old virtual identity is
                // retired. Until the Hero, clan, fief, mobile party and promoted-origin entry
                // all succeed, the source soldier and permanent numbered dossier remain intact.
                party.MemberRoster.AddToCounts(troop, -1, false, record.IsWounded ? -1 : 0);
                ArchiveRecord(record, InactivePromoted);
                InformationManager.DisplayMessage(new InformationMessage(heroName + MGText.Get("MG_SUCCESS_MIDDLE", " has been ennobled and founded clan ") + clanName +
                    MGText.Get("MG_SUCCESS_ORIGIN", ". (Origin: ") + _promotedOrigins[hero.StringId] + ")"));
                ClearConversationCandidate();
            }
            catch (System.Exception ex)
            {
                if (goldTransferred && createdHero != null)
                {
                    try { GiveGoldAction.ApplyBetweenCharacters(createdHero, Hero.MainHero, 20000, false); }
                    catch { }
                }
                InformationManager.DisplayMessage(new InformationMessage(
                    MGText.Get("MG_FAIL_PREFIX", "Ennoblement failed: ") + "[" + promotionStage + "] " +
                    ex.GetBaseException().Message));
                ClearConversationCandidate();
            }
            finally { _promotionInProgress = false; }
        }

        private static int GetVanillaClanBannerIconId()
        {
            var icons = Hero.MainHero?.MapFaction?.Culture?.PossibleClanBannerIconsIDs;
            return icons != null && icons.Count > 0 ? icons[MBRandom.RandomInt(icons.Count)] : 1;
        }

        private static void ApplyVanillaCompanionLordExtras(Hero hero, Clan clan, Settlement fief)
        {
            // These are private vanilla helpers. Calling the running campaign behavior keeps
            // equipment and the two generated relatives identical to native ennoblement.
            var behavior = Campaign.Current?.GetCampaignBehavior<CompanionRolesCampaignBehavior>();
            if (behavior == null) return;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            try
            {
                typeof(CompanionRolesCampaignBehavior).GetMethod("AdjustCompanionsEquipment", flags)
                    ?.Invoke(behavior, new object[] { hero });
                typeof(CompanionRolesCampaignBehavior).GetMethod("SpawnNewHeroesForNewCompanionClan", flags)
                    ?.Invoke(behavior, new object[] { hero, clan, fief });
            }
            catch (System.Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(
                    MGText.Get("MG_RELATIVE_FAIL", "The lord was ennobled, but native clan-member generation failed: ") + (ex.InnerException?.Message ?? ex.Message)));
            }
        }

        private static void ApplyVeteranSkills(Hero hero, CharacterObject troop, int merit, int seed)
        {
            SkillObject[] martial = { DefaultSkills.OneHanded, DefaultSkills.TwoHanded, DefaultSkills.Polearm,
                DefaultSkills.Bow, DefaultSkills.Crossbow, DefaultSkills.Throwing,
                DefaultSkills.Riding, DefaultSkills.Athletics, DefaultSkills.Crafting };
            foreach (var skill in martial) hero.SetSkillValue(skill, troop.GetSkillValue(skill));

            SkillObject[] mental = { DefaultSkills.Scouting, DefaultSkills.Tactics, DefaultSkills.Roguery,
                DefaultSkills.Charm, DefaultSkills.Leadership, DefaultSkills.Trade,
                DefaultSkills.Steward, DefaultSkills.Medicine, DefaultSkills.Engineering };
            var values = new int[mental.Length];
            var random = new System.Random(seed * 397 ^ merit);
            for (int i = 0; i < merit * 6; i++) values[random.Next(values.Length)]++;

            // Keep the random allocation, but prevent a generated lord from having one
            // mental skill more than three times another. Moving points after the draw
            // preserves the exact merit-derived total and introduces no fixed skill bias.
            while (values.Max() > values.Min() * 3)
            {
                int highest = values.Max();
                int lowest = values.Min();
                var donors = Enumerable.Range(0, values.Length).Where(x => values[x] == highest).ToList();
                var receivers = Enumerable.Range(0, values.Length).Where(x => values[x] == lowest).ToList();
                values[donors[random.Next(donors.Count)]]--;
                values[receivers[random.Next(receivers.Count)]]++;
            }
            for (int i = 0; i < mental.Length; i++) hero.SetSkillValue(mental[i], values[i]);
        }

        private static CultureObject ResolveLordCulture(CharacterObject troop)
        {
            CultureObject playerCulture = Clan.PlayerClan?.Culture ?? Hero.MainHero?.CharacterObject?.Culture;
            CultureObject troopCulture = troop?.Culture;
            bool usePlayerCulture = troop == null || troop.Occupation == Occupation.Bandit ||
                                    troop.Occupation == Occupation.Mercenary || troopCulture == null ||
                                    troopCulture.IsBandit || !troopCulture.IsMainCulture;
            return usePlayerCulture ? (playerCulture ?? troopCulture) : troopCulture;
        }

        private static void SetCharacterCulture(CharacterObject character, CultureObject culture)
        {
            if (character == null || culture == null) return;
            var property = typeof(CharacterObject).GetProperties(
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic)
                .Where(x => x.Name == "Culture" && x.PropertyType == typeof(CultureObject) &&
                            x.GetSetMethod(true) != null)
                .OrderByDescending(x => x.DeclaringType == typeof(CharacterObject))
                .FirstOrDefault();
            property?.SetValue(character, culture);
        }

        private void ClearConversationCandidate()
        {
            _conversationRecord = null;
            _conversationTroop = null;
            _conversationParty = null;
        }

        internal string ConversationNumberText => IsVeteranConversation() && _conversationRecord != null
            ? MGText.Get("MG_UI_NUMBER", "Number") + " " + _conversationRecord.Number
            : "";

        private void OnPlayerUpgradedTroops(CharacterObject oldTroop, CharacterObject newTroop, int count)
        {
            if (oldTroop == null || newTroop == null || count <= 0) return;
            string mainId = GetLedgerId(MobileParty.MainParty?.Party);
            foreach (var record in _records.Where(x => x.IsActive && x.PartyId == mainId &&
                                                       x.CharacterId == oldTroop.StringId &&
                                                       string.IsNullOrEmpty(x.PendingDestinationPartyId))
                         // The engine reports only how many members of a stack upgraded.
                         // The mod's canonical individual-selection rule is: highest kill
                         // count first, then lowest permanent number. Only CharacterId is
                         // changed, so every selected veteran keeps the same full dossier.
                         .OrderByDescending(x => x.Defeats).ThenBy(x => x.Number)
                         .Take(count).ToList())
            {
                if (PartyVMMixin.IsPartyScreenActive && !_pendingUpgradeOrigins.ContainsKey(record))
                    _pendingUpgradeOrigins.Add(record, record.CharacterId);
                record.CharacterId = newTroop.StringId;
            }
        }

        public static bool IsTrackedParty(PartyBase party)
        {
            return party != null && party == MobileParty.MainParty?.Party;
        }

        private static string GetLedgerId(PartyBase party)
        {
            if (party == null) return null;
            if (party == MobileParty.MainParty?.Party) return "main_party";
            return party.Id;
        }

        public void SynchronizeAll()
        {
            if (MobileParty.MainParty?.Party == null) return;
            // PartyBase rosters are not authoritative while a battle is armed. In
            // particular, mission teardown may temporarily expose too few (or zero) members
            // for a troop type. Preserve the frozen pre-battle dossiers until OnMapEventEnded.
            if (BattleResultTracker.IsArmed) return;
            // Only the party personally led by the player owns live dossiers. An explicit
            // death, confirmed departure, or completed promotion may retire one.
            // Roster counts are reconciliation evidence only. Identity must come from an
            // explicit recruitment, departure, promotion, or battle result.
            SynchronizeParty(MobileParty.MainParty.Party);
            EnsureNumberHighWaterMark();
        }

        public void FinishPartyScreenTransaction(bool fromCancel)
        {
            foreach (var record in _records.Where(x => x.IsActive && !string.IsNullOrEmpty(x.PendingDestinationPartyId)).ToList())
            {
                string destination = record.PendingDestinationPartyId;
                record.PendingDestinationPartyId = null;
                if (fromCancel) continue;
                if (destination == "__dismiss__") ArchiveRecord(record, InactiveLeft);
                else if (!string.IsNullOrEmpty(destination)) record.PartyId = destination;
            }

            if (fromCancel)
            {
                foreach (var upgrade in _pendingUpgradeOrigins)
                    upgrade.Key.CharacterId = upgrade.Value;
            }
            _pendingUpgradeOrigins.Clear();
            SynchronizeAll();
        }

        private void SynchronizeParty(PartyBase party)
        {
            // PartyScreen upgrades currently live only in its temporary roster. Comparing
            // them with PartyBase.MemberRoster here would see the old stack and convert the
            // dossiers straight back to their pre-upgrade type.
            string ledgerId = GetLedgerId(party);
            if (_pendingUpgradeOrigins.Keys.Any(x => x.PartyId == ledgerId)) return;

            var roster = party.MemberRoster.GetTroopRoster().Where(x => x.Character != null && !x.Character.IsHero).ToList();
            // Never infer an individual conversion from simultaneous stack-count changes.
            // A departing veteran and an unrelated recruit can otherwise look exactly like
            // a one-for-one conversion and transfer the veteran's number and merit. Dossiers
            // move between troop types only in OnPlayerUpgradedTroops, where the game has
            // explicitly reported a real upgrade.

            foreach (var element in roster)
            {
                var character = element.Character;
                // Pending transfers already changed the temporary native rosters, but their
                // dossier ownership must remain untouched until the party screen is confirmed.
                var records = _records.Where(x => x.IsActive && x.PartyId == ledgerId && x.CharacterId == character.StringId &&
                                                   string.IsNullOrEmpty(x.PendingDestinationPartyId))
                    .OrderBy(x => x.Number).ToList();
                // Never infer departure or death from a downward stack-count mismatch.
                // Legitimate changes are handled by their explicit events/transactions;
                // reconciliation is diagnostic and recovery-only.
                records = records.Take(element.Number).ToList();
                var wounded = records.Where(x => x.IsWounded).Take(element.WoundedNumber).ToList();
                if (wounded.Count < element.WoundedNumber)
                {
                    // The native roster exposes only a wounded count, not wounded identities.
                    // Preserve every still-valid wounded dossier, then randomly assign only
                    // the missing wounded slots among the remaining virtual individuals.
                    var available = records.Where(x => !wounded.Contains(x)).ToList();
                    var random = new System.Random(ledgerId.GetHashCode() ^ character.StringId.GetHashCode() ^
                                                   element.WoundedNumber ^ records.Count);
                    Shuffle(available, random);
                    wounded.AddRange(available.Take(element.WoundedNumber - wounded.Count));
                }
                foreach (var record in records) record.IsWounded = wounded.Contains(record);
            }

            // A troop type disappearing from the roster is not, by itself, authority to
            // retire its dossiers. Battle deaths, confirmed dismissals/transfers and loss
            // of a tracked settlement have their own explicit paths. Keeping the records
            // active here prevents a transient native count from becoming permanent data loss.
        }

        public IReadOnlyList<SoldierRecord> GetRecords(CharacterObject character, PartyBase party, bool synchronize = true)
        {
            if (synchronize) SynchronizeAll();
            if (character == null || party == null) return new List<SoldierRecord>();
            // A list is always limited to the currently selected troop type and selected party side.
            return _records.Where(x => x.IsActive && x.CharacterId == character.StringId && x.PartyId == GetLedgerId(party) && string.IsNullOrEmpty(x.PendingDestinationPartyId))
                .OrderBy(x => x.Number).ToList();
        }

        internal IReadOnlyList<SoldierRecord> GetAutoResolveCandidates(CharacterObject character, PartyBase party) =>
            GetRecords(character, party, false);

        public void MarkPendingTransfer(SoldierRecord record, PartyBase destination)
        {
            if (record != null && _records.Contains(record))
                record.PendingDestinationPartyId = IsTrackedParty(destination) ? GetLedgerId(destination) : "__dismiss__";
        }

        private static void ArchiveRecord(SoldierRecord record, string status)
        {
            if (record == null || !record.IsActive) return;
            record.InactiveStatus = status;
            record.PendingDestinationPartyId = null;
        }

        private int AllocateNumber()
        {
            // Deleted records never lower this counter, so their numbers cannot be reused.
            if (_nextNumber <= 0)
                throw new System.InvalidOperationException("Military Glory permanent number space is exhausted.");
            while (_records.Any(x => x.Number == _nextNumber))
            {
                if (_nextNumber == int.MaxValue)
                    throw new System.InvalidOperationException("Military Glory permanent number space is exhausted.");
                _nextNumber++;
            }
            int allocated = _nextNumber;
            if (_nextNumber == int.MaxValue) _nextNumber = 0;
            else _nextNumber++;
            return allocated;
        }

        private void EnsureNumberHighWaterMark()
        {
            // Runtime guard: no normal gameplay operation is allowed to renumber a record.
            // Keep only the allocator above every number ever issued, including inactive
            // historical dossiers, so a save/load or integer drift can never reuse one.
            int highest = _records.Where(x => x != null).Select(x => x.Number)
                .DefaultIfEmpty(0).Max();
            if (highest == int.MaxValue) _nextNumber = 0;
            else if (_nextNumber <= highest) _nextNumber = highest + 1;
        }

        private void EnsureUniqueRecordNumbers()
        {
            var issued = new HashSet<int>();
            int highest = _records.Where(x => x != null).Select(x => x.Number).DefaultIfEmpty(0).Max();
            if (highest == int.MaxValue) _nextNumber = 0;
            else if (_nextNumber <= highest) _nextNumber = highest + 1;

            // Keep the first owner of a valid number. Invalid or duplicate legacy records
            // receive fresh numbers above the permanent high-water mark.
            foreach (var record in _records.Where(x => x != null))
            {
                if (record.Number > 0 && issued.Add(record.Number)) continue;
                if (_nextNumber <= 0)
                    throw new System.InvalidOperationException("Military Glory permanent number space is exhausted.");
                while (issued.Contains(_nextNumber))
                {
                    if (_nextNumber == int.MaxValue)
                        throw new System.InvalidOperationException("Military Glory permanent number space is exhausted.");
                    _nextNumber++;
                }
                record.Number = _nextNumber++;
                issued.Add(record.Number);
            }
        }

    }
}
