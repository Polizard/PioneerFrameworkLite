using System;
using System.Collections.Generic;

namespace PioneerFramework.SpaceAI
{
    /// <summary>Linear scan, four bounded candidates; no sorting allocations or identity heuristics.</summary>
    public static class AiCandidateShortlist
    {
        public static void Fill(in AiShipState ship, IReadOnlyList<AiContact> input, double now, List<AiContact> output)
        {
            output.Clear();
            for (int i = 0; i < input.Count; i++)
            {
                var candidate = input[i]; if (!candidate.Valid) continue;
                double score = AiDecisionCore.Score(ship,candidate,now);
                int at = 0;
                while (at < output.Count)
                {
                    double other = AiDecisionCore.Score(ship,output[at],now);
                    if (score > other || score == other && candidate.TrackId < output[at].TrackId) break;
                    at++;
                }
                if (at >= 4) continue;
                output.Insert(at,candidate);
                if (output.Count > 4) output.RemoveAt(4);
            }
        }
    }
}
