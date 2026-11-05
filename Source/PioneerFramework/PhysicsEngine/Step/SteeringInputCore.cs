using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PioneerFramework
{
    // Persist names, never localized labels. Append actions to retain configuration compatibility.
    internal enum SteeringAction
    {
        PitchUp, PitchDown, YawLeft, YawRight, RollLeft, RollRight,
        Forward, Back, Left, Right, Up, Down, Precision, ResetCamera, FocusControl,
        Group1, Group2, Group3, Group4, Group5, Group6, Group7, Group8, Group9
    }

    [Flags]
    internal enum SteeringModifiers { None = 0, Shift = 1, Control = 2, Alt = 4 }

    internal readonly record struct SteeringChord(int Key, SteeringModifiers Modifiers = SteeringModifiers.None)
    {
        public bool IsBound => Key != 0;
        public bool IsValid => ((int)Modifiers & ~7) == 0 && (Modifiers & ModifierFor(Key)) == 0 && (Key == 0 && Modifiers == 0
            || Key is 8 or 9 or 12 or 13 or 19 or 127 || Key >= 32 && Key <= 126 || Key >= 256 && Key <= 319);

        public static SteeringModifiers ModifierFor(int key) => key switch
        {
            303 or 304 => SteeringModifiers.Shift,
            305 or 306 => SteeringModifiers.Control,
            307 or 308 => SteeringModifiers.Alt,
            _ => SteeringModifiers.None
        };

        public bool Matches(int key, SteeringModifiers modifiers, SteeringModifiers ignored = 0)
            => IsBound && Key == key && (modifiers & ~ModifierFor(Key) & ~ignored) == (Modifiers & ~ignored);
    }

    internal sealed class SteeringBindingProfile
    {
        private readonly Dictionary<SteeringAction, SteeringChord> bindings = new();
        public readonly List<string> Issues = new();
        public static readonly SteeringAction[] Actions = Enum.GetValues<SteeringAction>();
        public SteeringChord this[SteeringAction action] => bindings[action];
        public static bool IsContinuous(SteeringAction action) => action >= SteeringAction.PitchUp && action <= SteeringAction.Down;

        public SteeringBindingProfile()
        {
            foreach (var action in Actions) bindings[action] = Default(action);
        }

        public static SteeringChord Default(SteeringAction action) => action switch
        {
            SteeringAction.PitchUp => new(119), SteeringAction.PitchDown => new(115),
            SteeringAction.YawLeft => new(97), SteeringAction.YawRight => new(100),
            SteeringAction.RollLeft => new(113), SteeringAction.RollRight => new(101),
            SteeringAction.Forward => new(114), SteeringAction.Back => new(102),
            SteeringAction.Left => new(122), SteeringAction.Right => new(99),
            SteeringAction.Up => new(116), SteeringAction.Down => new(103),
            SteeringAction.Precision => new(304), SteeringAction.ResetCamera => new(278),
            SteeringAction.FocusControl => new(104),
            >= SteeringAction.Group1 and <= SteeringAction.Group9 => new(49 + action - SteeringAction.Group1, SteeringModifiers.Control),
            _ => default
        };

        public bool Matches(SteeringAction action, int key, SteeringModifiers modifiers, bool precision)
        {
            var ignored = precision && IsContinuous(action) ? PrecisionModifier(bindings) : SteeringModifiers.None;
            return bindings[action].Matches(key, modifiers, ignored);
        }

        private static SteeringModifiers PrecisionModifier(Dictionary<SteeringAction, SteeringChord> table)
        {
            var chord = table[SteeringAction.Precision];
            return chord.IsBound ? chord.Modifiers | SteeringChord.ModifierFor(chord.Key) : SteeringModifiers.None;
        }

        private static bool Overlaps(SteeringAction a, SteeringAction b, Dictionary<SteeringAction, SteeringChord> table)
        {
            var first = table[a];
            var second = table[b];
            if (!first.IsBound || !second.IsBound || first.Key != second.Key) return false;
            if (first.Modifiers == second.Modifiers) return true;
            var precision = PrecisionModifier(table);
            return precision != 0 && (IsContinuous(a) || IsContinuous(b))
                && (first.Modifiers & ~precision) == (second.Modifiers & ~precision);
        }

        private static bool FindConflict(Dictionary<SteeringAction, SteeringChord> table,
            out SteeringAction first, out SteeringAction second)
        {
            for (int i = 0; i < Actions.Length; i++)
                for (int j = i + 1; j < Actions.Length; j++)
                    if (Overlaps(Actions[i], Actions[j], table))
                    { first = Actions[i]; second = Actions[j]; return true; }
            first = second = default;
            return false;
        }

        public bool TrySet(SteeringAction action, SteeringChord chord, out SteeringAction? conflict, bool swap = false)
        {
            conflict = null;
            if (!chord.IsValid || !bindings.ContainsKey(action)) return false;
            var staged = new Dictionary<SteeringAction, SteeringChord>(bindings) { [action] = chord };
            if (FindConflict(staged, out var first, out var second))
            {
                conflict = first == action ? second : first;
                if (!swap || first != action && second != action) return false;
                staged[conflict.Value] = bindings[action];
                if (FindConflict(staged, out _, out _)) return false;
            }
            foreach (var pair in staged) bindings[pair.Key] = pair.Value;
            return true;
        }

        public List<string> Serialize() => Actions.Select(a => a + ":" + bindings[a].Key.ToString(CultureInfo.InvariantCulture)
            + ":" + ((int)bindings[a].Modifiers).ToString(CultureInfo.InvariantCulture)).ToList();

        public static SteeringBindingProfile Restore(IEnumerable<string>? records, Func<int, bool>? validKey = null)
        {
            var profile = new SteeringBindingProfile();
            var seen = new HashSet<SteeringAction>();
            foreach (var record in records ?? Array.Empty<string>())
            {
                var fields = record?.Split(':');
                if (fields == null || fields.Length != 3 || !Enum.TryParse<SteeringAction>(fields[0], out var action)
                    || !Enum.IsDefined(action) || !seen.Add(action)) continue;
                if (!int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int key)
                    || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int modifiers))
                { profile.Issues.Add(action + ":Invalid"); continue; }
                var chord = new SteeringChord(key, (SteeringModifiers)modifiers);
                if (!chord.IsValid || key != 0 && validKey != null && !validKey(key))
                { profile.Issues.Add(action + ":Invalid"); continue; }
                profile.bindings[action] = chord;
            }
            while (FindConflict(profile.bindings, out var first, out var second))
            {
                profile.bindings[second] = default;
                profile.Issues.Add(second + ":Conflict:" + first);
            }
            return profile;
        }
    }

    /// <summary>Held-state and discrete-edge ownership survive modifier changes and focus restoration.</summary>
    internal sealed class SteeringReleaseGate
    {
        private HashSet<int> held = new();
        private readonly HashSet<int> blocked = new();
        private readonly HashSet<int> consumed = new();
        private bool rearm = true;
        private bool enabled;

        public void Rearm() => rearm = true;

        public void Update(IEnumerable<int> current, bool acceptsInput)
        {
            held = new HashSet<int>(current);
            blocked.RemoveWhere(key => !held.Contains(key));
            consumed.RemoveWhere(key => !held.Contains(key));
            if (!acceptsInput || rearm) blocked.UnionWith(held);
            enabled = acceptsInput;
            rearm = false;
        }

        public bool IsHeld(int key) => enabled && held.Contains(key) && !blocked.Contains(key);
        public bool WasConsumed(int key) => consumed.Contains(key);
        public bool TryPress(int key) => IsHeld(key) && consumed.Add(key);
    }

    internal sealed class SteeringPointerHold
    {
        public SteeringAction? Action { get; private set; }
        private bool enabled;
        private bool held;
        private bool blocked = true;

        public void Update(bool isHeld, bool acceptsInput)
        {
            held = isHeld;
            enabled = acceptsInput;
            if (!held) blocked = false;
            if (!enabled) { blocked = held; Action = null; }
            if (!held) Action = null;
        }

        public void Press(SteeringAction action)
        {
            if (enabled && held && !blocked && SteeringBindingProfile.IsContinuous(action)) Action = action;
        }

        public void Cancel() { Action = null; blocked = true; }
    }

    internal sealed class SteeringCapture
    {
        private bool waitingRelease = true;
        private bool complete;
        private int modifierKey;
        private SteeringModifiers modifierChord;
        public bool Cancelled { get; private set; }
        public bool WaitingRelease => waitingRelease;

        public SteeringChord? Update(IEnumerable<int> held, int key, bool down, bool up, SteeringModifiers modifiers)
        {
            if (complete || Cancelled) return null;
            if (key == 27 && down) { Cancelled = true; return null; }
            if (waitingRelease)
            {
                if (!held.Any()) waitingRelease = false;
                return null;
            }
            var ownModifier = SteeringChord.ModifierFor(key);
            if (down && ownModifier != 0)
            {
                modifierKey = key;
                modifierChord = modifiers & ~ownModifier;
                return null;
            }
            SteeringChord chord;
            if (up && key == modifierKey) chord = new(key, modifierChord);
            else if (down && ownModifier == 0) chord = new(key, modifiers);
            else return null;
            if (!chord.IsValid || !chord.IsBound) return null;
            complete = true;
            return chord;
        }
    }

    internal static class SteeringMath
    {
        public static DVector3 AngularCommand(double pitchUp, double yawLeft, double rollLeft, double maxRate, double scale)
        {
            if (!double.IsFinite(maxRate) || maxRate <= 0 || !double.IsFinite(scale)) return DVector3.zero;
            var command = new DVector3(-pitchUp, -yawLeft, rollLeft);
            double length = command.magnitude;
            if (!double.IsFinite(length) || length <= 0) return DVector3.zero;
            return command * (maxRate * Math.Clamp(scale, 0, 1) / Math.Max(1, length));
        }

        public static DVector3 ReleaseSpin(DVector3 spin, bool assisted, double dt)
        {
            if (!assisted || !double.IsFinite(dt) || dt <= 0) return spin;
            var result = spin * Math.Exp(-dt / .12);
            return result.magnitude < 1e-6 ? DVector3.zero : result;
        }
    }
}
