using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;

namespace MilitaryGlory
{
    internal static class BattleResultTracker
    {
        internal static bool IsArmed { get; private set; }
        internal static bool WasDistributed { get; private set; }

        internal static bool BeginBattle(bool shouldTrack, bool forceReset)
        {
            if (!forceReset && IsArmed) return false;
            IsArmed = shouldTrack;
            WasDistributed = false;
            return IsArmed;
        }

        internal static void EndBattle()
        {
            IsArmed = false;
            WasDistributed = false;
        }

        internal static void ContinueAfterMissionRound()
        {
            // No mission data is recorded. Keep the final-settlement tracker armed for the
            // next round of the same MapEvent.
            WasDistributed = false;
        }

        internal static bool TryDistribute(MapEvent mapEvent, MilitaryGloryBehavior behavior,
            out int partyCount, out int totalDefeats)
        {
            partyCount = 0;
            totalDefeats = 0;
            if (!IsArmed || WasDistributed || behavior == null || mapEvent == null) return false;

            DistributeSide(mapEvent.AttackerSide.Parties.ToList(),
                mapEvent.DefenderSide.Parties.ToList(), behavior, mapEvent,
                ref partyCount, ref totalDefeats);
            DistributeSide(mapEvent.DefenderSide.Parties.ToList(),
                mapEvent.AttackerSide.Parties.ToList(), behavior, mapEvent,
                ref partyCount, ref totalDefeats);

            WasDistributed = partyCount > 0;
            return WasDistributed;
        }

        private static void DistributeSide(IReadOnlyList<MapEventParty> friendlyParties,
            IReadOnlyList<MapEventParty> enemyParties, MilitaryGloryBehavior behavior,
            MapEvent mapEvent, ref int partyCount, ref int totalDefeats)
        {
            int enemyDefeats = enemyParties.Sum(CountFinalCasualties);
            if (enemyDefeats <= 0) return;

            int totalContribution = friendlyParties.Sum(x => System.Math.Max(0, x.ContributionToBattle));
            int totalStrength = friendlyParties.Sum(CountFinalPartyStrength);
            int denominator = totalContribution > 0 ? totalContribution : totalStrength;
            if (denominator <= 0) return;

            var allocations = new Dictionary<MapEventParty, int>();
            var fractions = new List<System.Tuple<MapEventParty, double>>();
            int assigned = 0;
            foreach (var party in friendlyParties)
            {
                int numerator = totalContribution > 0
                    ? System.Math.Max(0, party.ContributionToBattle)
                    : CountFinalPartyStrength(party);
                double quota = (double)enemyDefeats * numerator / denominator;
                int whole = (int)System.Math.Floor(quota);
                allocations[party] = whole;
                fractions.Add(System.Tuple.Create(party, quota - whole));
                assigned += whole;
            }
            foreach (var remainder in fractions.OrderByDescending(x => x.Item2)
                         .ThenBy(x => x.Item1.Party.Id).Take(enemyDefeats - assigned))
                allocations[remainder.Item1]++;

            foreach (var eventParty in friendlyParties.Where(x =>
                         MilitaryGloryBehavior.IsTrackedParty(x.Party)))
            {
                int defeats = allocations[eventParty];
                behavior.ApplyFinalBattleResult(eventParty, defeats,
                    (mapEvent.GetHashCode() * 397) ^ eventParty.Party.Id.GetHashCode());
                partyCount++;
                totalDefeats += defeats;
            }
        }

        private static int CountFinalCasualties(MapEventParty party) =>
            party.DiedInBattle.GetTroopRoster().Sum(x => x.Number) +
            party.WoundedInBattle.GetTroopRoster().Sum(x => x.Number);

        private static int CountFinalPartyStrength(MapEventParty party) =>
            // TotalManCount already includes wounded troops. Only the dead are absent from
            // the final member roster and must be added back to reconstruct battle strength.
            party.Party.MemberRoster.TotalManCount +
            party.DiedInBattle.GetTroopRoster().Sum(x => x.Number);
    }
}
