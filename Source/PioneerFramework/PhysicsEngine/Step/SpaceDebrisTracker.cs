using System.Collections.Generic;
using Verse;

namespace PioneerFramework
{
    /// <summary>
    /// **天体 → 太空垃圾质量（kg）**（2026-11-03）。只记账；消费方（UI / DMS:NF）后置。
    /// 累计口径 = 碎片退役时的「结构 + 罐内工质」（与分离核同口径）；`Scrap` 才记，`Recoverable` 不记。
    /// 同 tick 多笔由 <see cref="SpaceGameComp.RetireDueDebris"/> 按 groupId 升序调用 ⇒ 加法顺序确定。
    /// </summary>
    public class SpaceDebrisTracker : GameComponent, IExposable
    {
        private List<int> bodyIds = new();
        private List<double> massKg = new();

        public SpaceDebrisTracker(Game game)
        {
        }

        /// <summary>给某个天体增加垃圾质量（kg）；`kg ≤ 0`/非有限直接忽略。</summary>
        public void AddDebris(int celestialBodyId, double kg)
        {
            if (!(kg > 0.0) || !double.IsFinite(kg)) return;
            int idx = bodyIds.IndexOf(celestialBodyId);
            if (idx >= 0) massKg[idx] += kg;
            else
            {
                bodyIds.Add(celestialBodyId);
                massKg.Add(kg);
            }
        }

        /// <summary>某天体的垃圾质量（kg；未知 = 0）。</summary>
        public double GetDebrisMass(int celestialBodyId)
        {
            int idx = bodyIds.IndexOf(celestialBodyId);
            return idx >= 0 ? massKg[idx] : 0.0;
        }

        /// <summary>已记录的天体数（读数用）。</summary>
        public int Count => bodyIds.Count;

        public int BodyIdAt(int index) => bodyIds[index];

        public double MassAt(int index) => massKg[index];

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref bodyIds, "debrisBodyIds", LookMode.Value);
            Scribe_Collections.Look(ref massKg, "debrisMassKg", LookMode.Value);
            if (bodyIds == null) bodyIds = new List<int>();
            if (massKg == null) massKg = new List<double>();
        }
    }
}
