using System;
using System.Collections.Generic;
using System.Linq;
using boston_timing_system.Models;

namespace boston_timing_system.Helpers
{
    /// <summary>
    /// Helper terpusat untuk menghitung ranking peserta berdasarkan catatan waktu FinishTime.
    /// Digunakan bersama oleh RaceTimingEngine (live engine) dan HeatModel (data model).
    /// </summary>
    public static class RankingHelper
    {
        /// <summary>
        /// Menghitung ranking peserta berdasarkan FinishTime untuk lane yang berstatus Finished.
        /// Lane yang belum selesai atau berstatus selain Finished (DNS, DNF, DQ, Ready, Running) akan direset Rank-nya menjadi null.
        /// Catatan waktu yang sama persis (tie) akan mendapatkan rank yang sama.
        /// </summary>
        /// <param name="lanes">Koleksi lane yang akan dihitung ranking-nya.</param>
        public static void CalculateRanks(IEnumerable<LaneModel>? lanes)
        {
            if (lanes == null) return;

            foreach (var lane in lanes)
            {
                if (lane.Status != LaneStatus.Finished)
                {
                    lane.Rank = null;
                }
            }

            var finishedLanes = lanes
                .Where(l => l.Status == LaneStatus.Finished && l.FinishTime.HasValue)
                .OrderBy(l => l.FinishTime!.Value)
                .ToList();

            int currentRank = 1;
            for (int i = 0; i < finishedLanes.Count; i++)
            {
                if (i > 0 && finishedLanes[i].FinishTime == finishedLanes[i - 1].FinishTime)
                {
                    finishedLanes[i].Rank = finishedLanes[i - 1].Rank;
                }
                else
                {
                    finishedLanes[i].Rank = currentRank;
                }
                currentRank++;
            }
        }
    }
}
