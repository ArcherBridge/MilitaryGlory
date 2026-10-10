using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Prefabs2;
using Bannerlord.UIExtenderEx.ViewModels;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.ViewModelCollection.Party;
using TaleWorlds.CampaignSystem.ViewModelCollection.Conversation;
using TaleWorlds.Core.ViewModelCollection.Information;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace MilitaryGlory
{
    public sealed class SoldierRowVM : ViewModel
    {
        private readonly SoldierRecord _record;
        private readonly Action<SoldierRecord, bool> _move;
        private readonly string _troopName;
        public HintViewModel TalkHint { get; }
        public SoldierRowVM(SoldierRecord record, CharacterObject currentTroop, bool inMainParty,
            Action<SoldierRecord, bool> move)
        {
            _record = record;
            _troopName = currentTroop?.Name?.ToString() ?? "";
            IsInMainParty = inMainParty;
            _move = move;
            TalkHint = new HintViewModel(new TextObject(MGText.Get("MG_UI_TALK", "Talk")), null);
        }
        [DataSourceProperty] public string TroopName => _troopName;
        [DataSourceProperty] public string Number => _record.Number.ToString();
        [DataSourceProperty] public string Defeats => _record.Defeats.ToString();
        [DataSourceProperty] public string Wounds => _record.Wounds.ToString();
        [DataSourceProperty] public string Status => _record.IsWounded ?
            MGText.Get("MG_STATUS_WOUNDED", "Wounded") : MGText.Get("MG_STATUS_READY", "Ready");
        [DataSourceProperty] public bool IsInMainParty { get; }
        [DataSourceProperty] public bool IsOutsideMainParty => !IsInMainParty;
        [DataSourceProperty] public bool CanTalk => true;
        [DataSourceProperty] public string TalkText => MGText.Get("MG_UI_TALK", "Talk");
        [DataSourceMethod] public void ExecuteMoveLeft() => _move?.Invoke(_record, true);
        [DataSourceMethod] public void ExecuteMoveRight() => _move?.Invoke(_record, false);
        [DataSourceMethod] public void ExecuteTalk() => PartyVMMixin.TalkToVeteran(_record);
    }

    [ViewModelMixin]
    public sealed class PartyVMMixin : BaseViewModelMixin<PartyVM>
    {
        private bool _isOpen;
        private string _selectedName = "";
        private PartyCharacterVM _selectedTroop;
        private PartyBase _sourceParty;
        private PartyBase _otherParty;
        private readonly Dictionary<string, int> _initialLeftRoster = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _initialRightRoster = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _loggedLeftDepartures = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _loggedRightDepartures = new Dictionary<string, int>();
        private static PartyVMMixin _active;
        internal static bool IsPartyScreenActive => _active != null;
        private MBBindingList<SoldierRowVM> _soldiers = new MBBindingList<SoldierRowVM>();
        public PartyVMMixin(PartyVM vm) : base(vm)
        {
            _active = this;
            if (vm.PartyScreenLogic != null)
            {
                _initialLeftRoster = Snapshot(vm.PartyScreenLogic.LeftOwnerParty?.MemberRoster);
                _initialRightRoster = Snapshot(vm.PartyScreenLogic.RightOwnerParty?.MemberRoster);
                vm.PartyScreenLogic.PartyScreenClosedEvent += OnPartyScreenClosed;
            }
        }

        private static Dictionary<string, int> Snapshot(TroopRoster roster) => roster?.GetTroopRoster()
            .Where(x => x.Character != null && !x.Character.IsHero && x.Number > 0)
            .GroupBy(x => x.Character.StringId).ToDictionary(x => x.Key, x => x.Sum(y => y.Number))
            ?? new Dictionary<string, int>();

        public override void OnFinalize()
        {
            if (ViewModel?.PartyScreenLogic != null) ViewModel.PartyScreenLogic.PartyScreenClosedEvent -= OnPartyScreenClosed;
            if (_active == this) _active = null;
            base.OnFinalize();
        }

        private void OnPartyScreenClosed(PartyBase leftOwnerParty, TroopRoster leftMemberRoster,
            TroopRoster leftPrisonRoster, PartyBase rightOwnerParty, TroopRoster rightMemberRoster,
            TroopRoster rightPrisonRoster, bool fromCancel)
        {
            if (!fromCancel)
            {
                MilitaryGloryBehavior.Instance?.AuthorizePartyScreenAdditions(leftOwnerParty,
                    leftMemberRoster, _initialLeftRoster, _loggedLeftDepartures,
                    rightOwnerParty, rightMemberRoster, _initialRightRoster,
                    _loggedRightDepartures);
                MilitaryGloryBehavior.Instance?.ApplyConfirmedPartyScreenDepartures(
                    leftOwnerParty, leftMemberRoster, _initialLeftRoster,
                    _loggedLeftDepartures, rightOwnerParty, rightMemberRoster,
                    _initialRightRoster, _loggedRightDepartures);
            }
            MilitaryGloryBehavior.Instance?.FinishPartyScreenTransaction(fromCancel);
        }

        internal static void LogNativeTransfer(PartyVM partyVm, PartyCharacterVM troop,
            int transferAmount, object fromSide)
        {
            var active = _active;
            if (active == null || active.ViewModel != partyVm || troop?.Character == null ||
                transferAmount <= 0 || fromSide == null) return;
            Dictionary<string, int> journal;
            string side = fromSide.ToString();
            if (side == "Left") journal = active._loggedLeftDepartures;
            else if (side == "Right") journal = active._loggedRightDepartures;
            else return;
            string characterId = troop.Character.StringId;
            journal[characterId] = journal.TryGetValue(characterId, out int existing)
                ? existing + transferAmount : transferAmount;
        }

        internal static void TalkToVeteran(SoldierRecord record)
        {
            var active = _active;
            if (active?._selectedTroop?.Character == null) return;
            try
            {
                CharacterObject troop = active._selectedTroop.Character;
                PartyBase party = active._sourceParty;
                if (party == null) return;
                if (active.ViewModel == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(MGText.Get("MG_PARTY_BLOCKED", "Resolve the pending party-screen operation first.")));
                    return;
                }
                active.MilitaryGloryIsOpen = false;
                // Use the exact native wanderer flow. The custom roster is only an extra
                // overlay: close it, register our dossier, then let PartyVM.ExecuteTalk
                // handle staged changes and open CampaignMission's conversation directly.
                MilitaryGloryBehavior.Instance?.StartVeteranConversation(record, troop, party);
                active.ViewModel.ExecuteTalk();
            }
            catch (Exception ex)
            {
                MilitaryGloryBehavior.Instance?.CancelPendingConversation();
                InformationManager.DisplayMessage(new InformationMessage(
                    MGText.Get("MG_CONVERSATION_FAILED", "Military Glory: conversation could not be opened (") +
                    ex.GetBaseException().Message + ")"));
            }
        }

        [DataSourceProperty] public bool MilitaryGloryIsOpen { get => _isOpen; set { if (_isOpen != value) { _isOpen = value; OnPropertyChangedWithValue(value); } } }
        [DataSourceProperty] public string MilitaryGlorySelectedName { get => _selectedName; set { if (_selectedName != value) { _selectedName = value; OnPropertyChangedWithValue(value); } } }
        [DataSourceProperty] public MBBindingList<SoldierRowVM> MilitaryGlorySoldiers => _soldiers;
        [DataSourceProperty] public string MilitaryGloryHeaderName => MGText.Get("MG_UI_NAME", "Troop");
        [DataSourceProperty] public string MilitaryGloryHeaderNumber => MGText.Get("MG_UI_NUMBER", "Number");
        [DataSourceProperty] public string MilitaryGloryHeaderKills => MGText.Get("MG_UI_KILLS", "Defeats");
        [DataSourceProperty] public string MilitaryGloryHeaderWounds => MGText.Get("MG_UI_WOUNDS", "Times Wounded");
        [DataSourceProperty] public string MilitaryGloryHeaderStatus => MGText.Get("MG_UI_STATUS", "Status");
        [DataSourceProperty] public string MilitaryGloryHeaderAction => MGText.Get("MG_UI_ACTION", "Action");

        [DataSourceMethod]
        public void ExecuteOpenMilitaryGlory()
        {
            OpenFor(ViewModel.CurrentCharacter);
        }

        internal static void OpenFor(PartyCharacterVM selectedTroop)
        {
            _active?.OpenForInternal(selectedTroop);
        }

        private void OpenForInternal(PartyCharacterVM selectedTroop)
        {
            _selectedTroop = selectedTroop;
            var character = _selectedTroop?.Character;
            // Prisoner stacks are not members of either party.  Looking them up by
            // character and owner party can otherwise display dossiers belonging to
            // enlisted troops of the same type, making an unrecruited captive appear
            // to have received a permanent number.
            if (character == null || character.IsHero || _selectedTroop.IsPrisoner) return;
            bool fromMain = ViewModel.MainPartyTroops.Contains(_selectedTroop);
            _sourceParty = fromMain ? MobileParty.MainParty?.Party : GetOtherParty();
            _otherParty = fromMain ? GetOtherParty() : MobileParty.MainParty?.Party;
            if (!MilitaryGloryBehavior.IsTrackedParty(_sourceParty)) return;
            MilitaryGloryBehavior.Instance?.SynchronizeAll();
            MilitaryGlorySelectedName = character.Name.ToString();
            RebuildRows();
            MilitaryGloryIsOpen = true;
        }

        private PartyBase GetOtherParty()
        {
            var logic = ViewModel.PartyScreenLogic;
            if (logic == null) return null;
            return logic.LeftOwnerParty != MobileParty.MainParty?.Party ? logic.LeftOwnerParty : logic.RightOwnerParty;
        }

        private void RebuildRows()
        {
            _soldiers.Clear();
            IEnumerable<SoldierRecord> records = MilitaryGloryBehavior.Instance?.GetRecords(_selectedTroop?.Character, _sourceParty, false)
                ?? Enumerable.Empty<SoldierRecord>();
            CharacterObject currentTroop = _selectedTroop?.Character;
            foreach (var record in records)
                _soldiers.Add(new SoldierRowVM(record, currentTroop, true, MoveSoldier));
        }

        private void MoveSoldier(SoldierRecord record, bool toMain)
        {
            PartyBase destination = toMain ? MobileParty.MainParty?.Party : _otherParty;
            if (record == null || destination == null || destination == _sourceParty ||
                ViewModel == null || _selectedTroop == null) return;
            try
            {
                MethodInfo method = typeof(PartyVM).GetMethod("OnTransferTroop",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var parameters = method?.GetParameters();
                if (parameters == null || parameters.Length != 4 || !parameters[3].ParameterType.IsEnum)
                {
                    InformationManager.DisplayMessage(new InformationMessage(MGText.Get("MG_TRANSFER_UNSUPPORTED", "Military Glory: this game version does not support individual transfers.")));
                    return;
                }
                // Native PartyScreen uses Right for the player's list and Left for the other party list.
                object fromSide = Enum.Parse(parameters[3].ParameterType,
                    _sourceParty == MobileParty.MainParty?.Party ? "Right" : "Left");
                // Let native code validate and perform the transfer, then correct only the
                // healthy/wounded split in its temporary rosters according to the selected
                // virtual dossier. This avoids depending on native's global transfer-priority
                // switch and preserves all normal capacity, cost and confirm/cancel handling.
                MethodInfo getRoster = typeof(PartyScreenLogic).GetMethod("GetRoster",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var rosterParameters = getRoster?.GetParameters();
                if (rosterParameters == null || rosterParameters.Length != 2 ||
                    !rosterParameters[0].ParameterType.IsEnum || !rosterParameters[1].ParameterType.IsEnum)
                {
                    InformationManager.DisplayMessage(new InformationMessage(MGText.Get("MG_TRANSFER_UNSUPPORTED", "Military Glory: this game version does not support individual transfers.")));
                    return;
                }
                object toSide = Enum.Parse(parameters[3].ParameterType,
                    _sourceParty == MobileParty.MainParty?.Party ? "Left" : "Right");
                object memberType = Enum.Parse(rosterParameters[1].ParameterType, "Member");
                var sourceRoster = getRoster.Invoke(ViewModel.PartyScreenLogic,
                    new[] { fromSide, memberType }) as TroopRoster;
                var destinationRoster = getRoster.Invoke(ViewModel.PartyScreenLogic,
                    new[] { toSide, memberType }) as TroopRoster;
                if (sourceRoster == null || destinationRoster == null)
                {
                    InformationManager.DisplayMessage(new InformationMessage(MGText.Get("MG_TRANSFER_UNSUPPORTED", "Military Glory: this game version does not support individual transfers.")));
                    return;
                }

                CharacterObject character = _selectedTroop.Character;
                int sourceWoundedBefore = sourceRoster.GetTroopRoster()
                    .Where(x => x.Character == character).Sum(x => x.WoundedNumber);
                int destinationWoundedBefore = destinationRoster.GetTroopRoster()
                    .Where(x => x.Character == character).Sum(x => x.WoundedNumber);
                method.Invoke(ViewModel, new[] { (object)_selectedTroop, 0, 1, fromSide });

                int desiredSourceWounded = sourceWoundedBefore - (record.IsWounded ? 1 : 0);
                int desiredDestinationWounded = destinationWoundedBefore + (record.IsWounded ? 1 : 0);
                int sourceWoundedAfter = sourceRoster.GetTroopRoster()
                    .Where(x => x.Character == character).Sum(x => x.WoundedNumber);
                int destinationWoundedAfter = destinationRoster.GetTroopRoster()
                    .Where(x => x.Character == character).Sum(x => x.WoundedNumber);
                if (sourceWoundedAfter != desiredSourceWounded)
                    sourceRoster.AddToCounts(character, 0, false, desiredSourceWounded - sourceWoundedAfter);
                if (destinationWoundedAfter != desiredDestinationWounded)
                    destinationRoster.AddToCounts(character, 0, false,
                        desiredDestinationWounded - destinationWoundedAfter);
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage(MGText.Get("MG_TRANSFER_FAILED", "Military Glory: transfer failed (") + ex.GetBaseException().Message + ")"));
                return;
            }
            MilitaryGloryBehavior.Instance?.MarkPendingTransfer(record, destination);
            RebuildRows();
            ViewModel.RefreshValues();
        }

        [DataSourceMethod] public void ExecuteCloseMilitaryGlory() => MilitaryGloryIsOpen = false;
    }

    [HarmonyPatch(typeof(PartyVM), "OnTransferTroop")]
    internal static class PartyTransferJournalPatch
    {
        private static void Postfix(PartyVM __instance, object[] __args)
        {
            if (__args == null || __args.Length != 4) return;
            PartyVMMixin.LogNativeTransfer(__instance, __args[0] as PartyCharacterVM,
                __args[2] is int amount ? amount : 0, __args[3]);
        }
    }

    [ViewModelMixin]
    public sealed class PartyCharacterMilitaryGloryMixin : BaseViewModelMixin<PartyCharacterVM>
    {
        public PartyCharacterMilitaryGloryMixin(PartyCharacterVM vm) : base(vm)
        {
            MilitaryGloryRosterHint = new HintViewModel(
                new TextObject(MGText.Get("MG_UI_ROSTER_HINT", "Military Glory Roster")), null);
        }
        [DataSourceProperty] public HintViewModel MilitaryGloryRosterHint { get; }
        [DataSourceProperty] public bool MilitaryGloryCanInspect => ViewModel?.Character != null &&
            !ViewModel.Character.IsHero && !ViewModel.IsPrisoner;
        [DataSourceMethod] public void ExecuteOpenMilitaryGloryRoster() => PartyVMMixin.OpenFor(ViewModel);
    }

    [PrefabExtension("PartyTroopTuple", "descendant::ScrollablePanel[@Id='UpgradesPanel']")]
    public sealed class PartyTroopTupleMilitaryGloryPatch : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Append;
        internal const string ButtonXml = @"
<ButtonWidget Id='MilitaryGloryInspectButton' WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedWidth='64' SuggestedHeight='50' MarginLeft='10' MarginRight='10' VerticalAlignment='Center' Brush='Party.TalkSlot.Background' IsVisible='@MilitaryGloryCanInspect' DoNotPassEventsToChildren='true' UpdateChildrenStates='true' Command.Click='ExecuteOpenMilitaryGloryRoster'><Children><Widget DoNotAcceptEvents='true' WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedWidth='42' SuggestedHeight='42' HorizontalAlignment='Center' VerticalAlignment='Center' Sprite='PartyScreen\talk_icon' HueFactor='10' SaturationFactor='60' ValueFactor='20'/><HintWidget DataSource='{MilitaryGloryRosterHint}' WidthSizePolicy='StretchToParent' HeightSizePolicy='StretchToParent' Command.HoverBegin='ExecuteBeginHint' Command.HoverEnd='ExecuteEndHint'/></Children></ButtonWidget>";
        [PrefabExtensionText]
        public string Xml => ButtonXml;
    }

    [PrefabExtension("PartyTroopTupleLeft", "descendant::BrushWidget[@Id='Main']/Children")]
    public sealed class PartyTroopTupleLeftMilitaryGloryPatch : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Append;
        [PrefabExtensionText]
        public string Xml => @"
<ButtonWidget Id='MilitaryGloryInspectButtonLeft' WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedWidth='44' SuggestedHeight='44' MarginRight='130' HorizontalAlignment='Right' VerticalAlignment='Center' Brush='Party.TalkSlot.Background' IsVisible='@MilitaryGloryCanInspect' DoNotPassEventsToChildren='true' UpdateChildrenStates='true' Command.Click='ExecuteOpenMilitaryGloryRoster'><Children><Widget DoNotAcceptEvents='true' WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedWidth='34' SuggestedHeight='34' HorizontalAlignment='Center' VerticalAlignment='Center' Sprite='PartyScreen\talk_icon' HueFactor='10' SaturationFactor='60' ValueFactor='20'/><HintWidget DataSource='{MilitaryGloryRosterHint}' WidthSizePolicy='StretchToParent' HeightSizePolicy='StretchToParent' Command.HoverBegin='ExecuteBeginHint' Command.HoverEnd='ExecuteEndHint'/></Children></ButtonWidget>";
    }

    [ViewModelMixin]
    public sealed class ConversationMilitaryGloryMixin : BaseViewModelMixin<MissionConversationVM>
    {
        public ConversationMilitaryGloryMixin(MissionConversationVM vm) : base(vm) { }
        [DataSourceProperty] public string MilitaryGloryConversationNumber =>
            MilitaryGloryBehavior.Instance?.ConversationNumberText ?? "";
        [DataSourceProperty] public bool MilitaryGloryConversationHasNumber =>
            !string.IsNullOrEmpty(MilitaryGloryConversationNumber);
    }

    [PrefabExtension("SPConversation", "descendant::Widget[@Id='CharacterNameContainer']/Children")]
    public sealed class ConversationNumberPatch : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Append;
        [PrefabExtensionText]
        public string Xml => @"
<TextWidget DoNotAcceptEvents='true' WidthSizePolicy='CoverChildren' HeightSizePolicy='Fixed' SuggestedHeight='70' HorizontalAlignment='Right' MarginRight='25' Brush='Conversation.HeaderText' Brush.TextHorizontalAlignment='Right' Text='@MilitaryGloryConversationNumber' IsVisible='@MilitaryGloryConversationHasNumber'/>";
    }

    [PrefabExtension("PartyScreen", "descendant::PartyScreenWidget[@Id='PartyScreen']/Children/*[last()]")]
    public sealed class PartyScreenPatch : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Append;
        [PrefabExtensionText]
        public string Xml => @"
<Widget WidthSizePolicy='StretchToParent' HeightSizePolicy='StretchToParent' IsVisible='@MilitaryGloryIsOpen'><Children>
  <Widget WidthSizePolicy='StretchToParent' HeightSizePolicy='StretchToParent' Sprite='BlankWhiteSquare' Color='#000000B0'/>
    <Widget WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedWidth='900' SuggestedHeight='620' HorizontalAlignment='Center' VerticalAlignment='Center' Sprite='inventory_tooltip_9' ExtendTop='10' ExtendLeft='10' ExtendRight='10' ExtendBottom='10'><Children>
      <TextWidget WidthSizePolicy='StretchToParent' HeightSizePolicy='Fixed' SuggestedHeight='52' MarginLeft='24' MarginRight='70' MarginTop='16' Text='@MilitaryGlorySelectedName' Brush='InventoryDefaultFontBrush' Brush.FontSize='26' Brush.TextColor='#E1E1E1FF' Brush.TextHorizontalAlignment='Center' Brush.TextVerticalAlignment='Center'/>
      <ButtonWidget WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedWidth='42' SuggestedHeight='42' HorizontalAlignment='Right' VerticalAlignment='Top' MarginRight='14' MarginTop='12' Brush='Inventory.ItemPreview.CloseButton' Command.Click='ExecuteCloseMilitaryGlory'/>
      <ListPanel WidthSizePolicy='StretchToParent' HeightSizePolicy='Fixed' SuggestedHeight='38' MarginLeft='35' MarginRight='35' MarginTop='78' StackLayout.LayoutMethod='HorizontalLeftToRight'><Children><TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='280' Text='@MilitaryGloryHeaderName' Brush='Party.Text.Tuple'/><TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='70' Text='@MilitaryGloryHeaderNumber' Brush='Party.Text.Tuple'/><TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='80' Text='@MilitaryGloryHeaderKills' Brush='Party.Text.Tuple'/><TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='90' Text='@MilitaryGloryHeaderWounds' Brush='Party.Text.Tuple'/><TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='90' Text='@MilitaryGloryHeaderStatus' Brush='Party.Text.Tuple'/><TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='220' Text='@MilitaryGloryHeaderAction' Brush='Party.Text.Tuple'/></Children></ListPanel>
      <ScrollablePanel WidthSizePolicy='StretchToParent' HeightSizePolicy='Fixed' SuggestedHeight='465' MarginLeft='30' MarginRight='45' MarginTop='120' VerticalAlignment='Top' AutoHideScrollBars='true' MouseScrollAxis='Vertical' OnlyAcceptScrollEventIfCanScroll='true' ClipContents='true' ClipRect='MilitaryGloryClip' InnerPanel='MilitaryGloryClip\MilitaryGloryList' VerticalScrollbar='..\MilitaryGloryScrollbar\Scrollbar'><Children>
        <Widget Id='MilitaryGloryClip' WidthSizePolicy='StretchToParent' HeightSizePolicy='StretchToParent' ClipContents='true'><Children><ListPanel Id='MilitaryGloryList' DataSource='{MilitaryGlorySoldiers}' WidthSizePolicy='StretchToParent' HeightSizePolicy='CoverChildren' StackLayout.LayoutMethod='VerticalTopToBottom'><ItemTemplate>
          <Widget WidthSizePolicy='StretchToParent' HeightSizePolicy='Fixed' SuggestedHeight='38' Sprite='BlankWhiteSquare_9' Color='#332719CC' MarginBottom='2'><Children>
            <TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='270' MarginLeft='10' Text='@TroopName' Brush='Party.Text.Tuple'/><TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='70' MarginLeft='280' Text='@Number' Brush='Party.Text.Tuple'/><TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='80' MarginLeft='350' Text='@Defeats' Brush='Party.Text.Tuple'/><TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='90' MarginLeft='430' Text='@Wounds' Brush='Party.Text.Tuple'/><TextWidget WidthSizePolicy='Fixed' HeightSizePolicy='StretchToParent' SuggestedWidth='90' MarginLeft='520' Text='@Status' Brush='Party.Text.Tuple'/>
            <ButtonWidget WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedWidth='42' SuggestedHeight='30' MarginLeft='620' VerticalAlignment='Center' IsVisible='@IsOutsideMainParty' Brush='ButtonLeftArrowBrush1' Command.Click='ExecuteMoveLeft'/>
            <ButtonWidget WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedWidth='42' SuggestedHeight='30' MarginLeft='666' VerticalAlignment='Center' IsVisible='@IsInMainParty' Brush='ButtonRightArrowBrush1' Command.Click='ExecuteMoveRight'/>
            <ButtonWidget WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedWidth='64' SuggestedHeight='50' MarginLeft='734' VerticalAlignment='Center' IsVisible='@CanTalk' Brush='Party.TalkSlot.Background' DoNotPassEventsToChildren='true' UpdateChildrenStates='true' Command.Click='ExecuteTalk'><Children><Widget DoNotAcceptEvents='true' WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedWidth='42' SuggestedHeight='42' HorizontalAlignment='Center' VerticalAlignment='Center' Sprite='PartyScreen\talk_icon' HueFactor='10' SaturationFactor='60' ValueFactor='20'/><HintWidget DataSource='{TalkHint}' WidthSizePolicy='StretchToParent' HeightSizePolicy='StretchToParent' Command.HoverBegin='ExecuteBeginHint' Command.HoverEnd='ExecuteEndHint'/></Children></ButtonWidget>
          </Children></Widget>
        </ItemTemplate></ListPanel></Children></Widget>
      </Children></ScrollablePanel>
      <Standard.VerticalScrollbar Id='MilitaryGloryScrollbar' WidthSizePolicy='Fixed' HeightSizePolicy='Fixed' SuggestedHeight='465' HorizontalAlignment='Right' VerticalAlignment='Top' MarginRight='25' MarginTop='120'/>
    </Children></Widget>
</Children></Widget>";
    }
}
