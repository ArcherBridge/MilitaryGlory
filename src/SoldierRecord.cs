using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;
using TaleWorlds.SaveSystem;

namespace MilitaryGlory
{
    public sealed class SoldierRecord
    {
        public const int EnnoblementMeritRequired = 100;

        [SaveableField(1)] public int Number;
        [SaveableField(2)] public string CharacterId;
        // Legacy save field. Kept so existing saves remain readable, but troop names are
        // presentation data and must be resolved from CharacterId when they are displayed.
        [SaveableField(3)] public string TroopName;
        [SaveableField(4)] public int Defeats;
        [SaveableField(5)] public int Wounds;
        [SaveableField(6)] public string PartyId;
        [SaveableField(7)] public bool IsWounded;
        [SaveableField(8)] public string PendingDestinationPartyId;
        [SaveableField(9)] public string BirthplaceSettlementId;
        [SaveableField(10)] public string OriginText;
        // Null means the soldier is still present in a tracked roster. Historical records
        // are retained permanently and merely hidden from the live roster UI.
        [SaveableField(11)] public string InactiveStatus;

        public bool IsActive => string.IsNullOrEmpty(InactiveStatus);

        // Merit is computed from the soldier's current troop class rather than saved, so troop
        // upgrades and existing saves automatically use the current rule.
        public int Merit
        {
            get => CalculateMerit(MBObjectManager.Instance?.GetObject<CharacterObject>(CharacterId));
        }

        public int CalculateMerit(CharacterObject troop)
        {
            FormationClass formationClass = troop?.DefaultFormationClass ?? FormationClass.Infantry;
            bool mounted = formationClass == FormationClass.Cavalry ||
                           formationClass == FormationClass.HorseArcher || troop?.IsMounted == true;
            bool usesCrossbow = HasBattleWeapon(troop, WeaponClass.Crossbow);
            bool usesBow = HasBattleWeapon(troop, WeaponClass.Bow);
            bool usesThrowing = HasBattleWeapon(troop, WeaponClass.Javelin) ||
                                HasBattleWeapon(troop, WeaponClass.ThrowingAxe) ||
                                HasBattleWeapon(troop, WeaponClass.ThrowingKnife) ||
                                HasBattleWeapon(troop, WeaponClass.Stone);
            bool usesShield = HasBattleWeapon(troop, WeaponClass.SmallShield) ||
                              HasBattleWeapon(troop, WeaponClass.LargeShield);
            long merit;
            if (usesCrossbow)
            {
                merit = (long)Defeats + 4L * Wounds;
            }
            else if (usesBow)
            {
                merit = (long)Defeats + (mounted ? 3L : 2L) * Wounds;
            }
            else if (usesThrowing && mounted)
            {
                merit = (long)Defeats + 3L * Wounds;
            }
            else if (formationClass == FormationClass.Cavalry)
            {
                merit = 13L * Defeats / 10L + 3L * Wounds;
            }
            else if (formationClass == FormationClass.HorseArcher)
            {
                // Keep modded mounted ranged troops without recognizable vanilla weapons
                // on the horse-archer rule rather than treating them as melee cavalry.
                merit = (long)Defeats + 3L * Wounds;
            }
            else if (formationClass == FormationClass.Ranged && !usesThrowing)
            {
                // Preserve the bow rule as a safe fallback for modded ranged weapons.
                merit = (long)Defeats + 2L * Wounds;
            }
            else
            {
                // Foot troops are divided by shield equipment, regardless of whether they
                // also carry throwing weapons.
                merit = usesShield
                    ? 15L * Defeats / 10L + 4L * Wounds
                    : (long)Defeats + 4L * Wounds;
            }
            return merit >= int.MaxValue ? int.MaxValue : (int)merit;
        }

        private static bool HasBattleWeapon(CharacterObject troop, WeaponClass weaponClass) =>
            troop?.BattleEquipments?.Any(equipment => equipment != null &&
                equipment.HasWeaponOfClass(weaponClass)) == true;

        public void AddDefeats(int count)
        {
            if (count <= 0) return;
            long total = (long)Defeats + count;
            Defeats = total >= int.MaxValue ? int.MaxValue : (int)total;
        }

        public void AddWound()
        {
            if (Wounds < int.MaxValue) Wounds++;
        }

        public SoldierRecord() { }

        public SoldierRecord(int number, string characterId, string partyId,
            string birthplaceSettlementId, string originText)
        {
            Number = number;
            CharacterId = characterId;
            PartyId = partyId;
            BirthplaceSettlementId = birthplaceSettlementId;
            OriginText = originText;
        }
    }
}
